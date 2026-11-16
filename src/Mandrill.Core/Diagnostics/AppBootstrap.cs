using System.Globalization;
using Mandrill.Core.Localization;
using Mandrill.Core.Platform;
using Mandrill.Core.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.System.Diagnostics.Debug;

namespace Mandrill.Core.Diagnostics;

/// <summary>
/// Services created by <see cref="AppBootstrap.StartAsync"/>. The caller owns and disposes <see cref="LoggerFactory"/>;
/// <see cref="Dispose"/> releases the instance number (the app keeps it for its lifetime, the OS frees it at exit).
/// </summary>
public sealed class BootstrapResult : IDisposable
{
    /// <summary>Creates the result.</summary>
    /// <param name="paths">Resolved application paths.</param>
    /// <param name="loggerFactory">Logger factory (dispose on shutdown to flush).</param>
    /// <param name="localizer">Loaded localizer (already installed as <see cref="Localizer.Current"/>).</param>
    /// <param name="fileLoggingEnabled">False when the data directory could not be created.</param>
    /// <param name="state">Loaded settings, session and favorites.</param>
    public BootstrapResult(AppPaths paths, ILoggerFactory loggerFactory, Localizer localizer, bool fileLoggingEnabled, StartupState state)
    {
        Paths = paths;
        LoggerFactory = loggerFactory;
        Localizer = localizer;
        FileLoggingEnabled = fileLoggingEnabled;
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>Loaded settings, session and favorites.</summary>
    public StartupState State { get; }

    /// <summary>Resolved application paths.</summary>
    public AppPaths Paths { get; }

    /// <summary>Logger factory (dispose on shutdown to flush).</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>Loaded localizer (already installed as <see cref="Localizer.Current"/>).</summary>
    public Localizer Localizer { get; }

    /// <summary>False when the data directory could not be created; there is no log file and nothing can be saved.</summary>
    public bool FileLoggingEnabled { get; }

    /// <summary>The supported languages whose locale file is installed (the language picker's list).</summary>
    public IReadOnlyList<LanguageInfo> Languages { get; init; } = [];

    /// <summary>The language "auto" resolves to on this PC (the picker's Auto cell).</summary>
    public string AutoLanguage { get; init; } = Localizer.English;

    /// <summary>This process's instance number: the lowest free 1..8 among running instances, 0 when none was free.</summary>
    public int InstanceSlot { get; init; }

    // instance-<n>.lock held open without sharing: the number stays taken until disposed or the process ends.
    internal FileStream? InstanceLock { get; init; }

    /// <summary>Releases the instance number.</summary>
    public void Dispose() => InstanceLock?.Dispose();
}

/// <summary>Settings and session loaded at startup (AC-1.33, AC-1.34), with the stores that save them later.</summary>
/// <param name="Settings">Normalized settings (missing → defaults; corrupt → .bak + defaults).</param>
/// <param name="Session">Normalized session (same rules).</param>
/// <param name="FirstRun">True when no session file existed (first-run tree defaults, AC-1.3b).</param>
/// <param name="SettingsStore">Store for settings.json (atomic writes).</param>
/// <param name="SessionStore">Store for session.json (atomic writes).</param>
/// <param name="Favorites">Normalized favorites (same rules; WP-1.12 shows them).</param>
/// <param name="FavoritesStore">Store for favorites.json (atomic writes; <see cref="StateStore{T}.SaveAsync"/> runs off the UI thread).</param>
public sealed record StartupState(
    AppSettings Settings,
    SessionState Session,
    bool FirstRun,
    StateStore<AppSettings> SettingsStore,
    StateStore<SessionState> SessionStore,
    FavoritesDocument Favorites,
    StateStore<FavoritesDocument> FavoritesStore)
{
    /// <summary>Whether the favorites bar is shown (Settings, AC-1.34); the bar's view model starts from this value.</summary>
    public bool ShowFavoritesBar => Settings.ShowFavoritesBar;
}

/// <summary>Startup I/O done off the UI thread: data/log directories, logging, settings and session, locale tables.</summary>
public static class AppBootstrap
{
    /// <summary>
    /// Full startup (worker thread): directories and logging, then settings.json and session.json (a corrupt file is
    /// renamed to .bak and defaults are used, so the app always starts), then the locale tables for the effective language
    /// (MANDRILL_LANG developer override, else the saved setting, else the OS UI language).
    /// </summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The services and the loaded state.</returns>
    public static async Task<BootstrapResult> StartAsync(AppPaths paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        // Not-ready removable media (empty card reader) must fail the call, not show a modal "insert a disk" box (SetErrorMode
        // docs: best practice for every application).
        PInvoke.SetErrorMode((THREAD_ERROR_MODE)PInvoke.GetErrorMode() | THREAD_ERROR_MODE.SEM_FAILCRITICALERRORS);
        var (factory, fileLogging, log, slot, slotLock) = StartLogging(paths);
        var settingsStore = new StateStore<AppSettings>(paths.SettingsFile, factory.CreateLogger("Mandrill.State.Settings"));
        var sessionStore = new StateStore<SessionState>(paths.SessionFile, factory.CreateLogger("Mandrill.State.Session"));
        var favoritesStore = new StateStore<FavoritesDocument>(paths.FavoritesFile, factory.CreateLogger("Mandrill.State.Favorites"));
        AppSettings settings = AppSettings.CreateDefault();
        SessionState session = SessionState.CreateDefault();
        FavoritesDocument favorites = FavoritesDocument.CreateDefault();
        bool firstRun = !File.Exists(paths.SessionFile);
        try
        {
            settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
            session = await sessionStore.LoadAsync(ct).ConfigureAwait(false);
            favorites = await favoritesStore.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The stores already turn unreadable or corrupt files into defaults; anything else must not stop the app either.
            log.LogError(ex, "Loading settings, session or favorites failed; using defaults");
        }

        var (localizer, languages, auto) = LoadLocalizer(paths, factory, log, UiLanguage.EffectiveSetting(settings.Language));
        return new BootstrapResult(paths, factory, localizer, fileLogging, new StartupState(settings, session, firstRun, settingsStore, sessionStore, favorites, favoritesStore))
        {
            Languages = languages,
            AutoLanguage = auto,
            InstanceSlot = slot,
            InstanceLock = slotLock,
        };
    }

    private static (ILoggerFactory Factory, bool FileLogging, ILogger Log, int Slot, FileStream? SlotLock) StartLogging(AppPaths paths)
    {
        ILoggerFactory factory;
        bool fileLogging = true;
        int slot = 0;
        FileStream? slotLock = null;
        try
        {
            paths.EnsureCreated();
            (slot, slotLock) = TakeInstanceSlot(paths.DataDirectory);
            ProbeLogFile(paths.LogFile);
            factory = MandrillLogging.Create(paths.LogFile, MandrillLogging.IsDebugEnabled, slot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Without a writable data directory the app still runs; there is just no log file. (The log mutex name taken by
            // another kind of kernel object is all but impossible with a hashed name; it must still not stop the app.)
            factory = NullLoggerFactory.Instance;
            fileLogging = false;
        }

        var log = factory.CreateLogger(typeof(AppBootstrap).FullName!);
        log.LogInformation("Mandrill starting: version {Version}, OS {OS}, pid {Pid}, instance {Slot}, data {Data}", typeof(AppBootstrap).Assembly.GetName().Version, Environment.OSVersion, Environment.ProcessId, slot, paths.DataDirectory);
        if (paths.IsOverridden)
        {
            log.LogInformation("Data directory overridden by {Variable}: {Data}", AppPaths.DataDirectoryVariable, paths.DataDirectory);
        }
        else if (paths.IgnoredOverride is { } ignored)
        {
            log.LogWarning("{Variable} '{Value}' is not a valid absolute path; using {Data}", AppPaths.DataDirectoryVariable, ignored, paths.DataDirectory);
        }

        if (paths.UsesFallbackDataDirectory)
        {
            // CLAUDE.md fixes %APPDATA%\mandrill; a different location must never be silent.
            log.LogWarning("%APPDATA% is unavailable; settings and logs are stored in {Data} instead", paths.DataDirectory);
        }

        return (factory, fileLogging, log, slot, slotLock);
    }

    // Lowest free instance-<n>.lock in 1..8 (architecture §18 D1); counting processes would race when two start at once.
    // The empty lock files stay in the data folder on purpose: DeleteOnClose would leave a delete-pending file that the
    // next instance cannot open, so it would skip that number.
    private static (int Slot, FileStream? Lock) TakeInstanceSlot(string dataDirectory)
    {
        for (int n = 1; n <= 8; n++)
        {
            try
            {
                string file = Path.Combine(dataDirectory, string.Create(CultureInfo.InvariantCulture, $"instance-{n}.lock"));
                return (n, new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held by a running instance (or not writable): try the next number.
            }
        }

        return (0, null);
    }

    // "Cannot write" is for a real failure only: a viewer holding the file (sharing violation) just delays the writer.
    private static void ProbeLogFile(string logFile)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                SharedFileLoggerProvider.Open(logFile).Dispose();
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException && attempt < 3)
            {
                Thread.Sleep(100);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
            {
                return; // sharing or lock violation: a viewer holds the file; every other failure is real
            }
        }
    }

    private static (Localizer Localizer, LanguageInfo[] Languages, string AutoLanguage) LoadLocalizer(AppPaths paths, ILoggerFactory factory, ILogger log, string setting)
    {
        string culture = UiLanguage.UserDefaultCultureName();
        bool HasLocale(string code) => code == Localizer.English || File.Exists(Path.Combine(paths.LocalesDirectory, code + ".json"));
        var languages = LanguageCatalog.All.Where(l => HasLocale(l.Code)).ToArray();
        string language = Localizer.ResolveLanguage(setting, culture, HasLocale);
        string auto = Localizer.ResolveLanguage(LanguageCatalog.Auto, culture, HasLocale);
        log.LogInformation("Language setting {Setting}, OS UI culture {Culture} -> {Language} (auto {Auto}; available {Available})", setting, culture, language, auto, string.Join(",", languages.Select(l => l.Code)));
        var localizer = Localizer.Load(paths.LocalesDirectory, language, factory.CreateLogger(typeof(Localizer).FullName!));
        Localizer.SetCurrent(localizer);
        return (localizer, languages, auto);
    }
}
