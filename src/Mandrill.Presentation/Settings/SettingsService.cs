using Mandrill.Core.State;
using Microsoft.Extensions.Logging;

namespace Mandrill.Presentation.Settings;

/// <summary>
/// The settings in effect (AC-1.34, AC-1.3b) and their atomic save. <see cref="LayoutDefaults"/> is what
/// <c>LayoutManager</c> reads on every Add/Remove pane; a language change takes effect after a restart. UI thread.
/// </summary>
public sealed class SettingsService
{
    private readonly StateStore<AppSettings> _store;
    private readonly ILogger _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="current">Settings loaded at startup.</param>
    /// <param name="store">Store for settings.json.</param>
    /// <param name="logger">Logger.</param>
    public SettingsService(AppSettings current, StateStore<AppSettings> store, ILogger<SettingsService> logger)
    {
        Current = current ?? throw new ArgumentNullException(nameof(current));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Raised after <see cref="Current"/> changed.</summary>
    public event Action? Changed;

    /// <summary>The settings in effect.</summary>
    public AppSettings Current { get; private set; }

    /// <summary>Default arrangements for 2 and 3 panes (pass <c>() => settings.LayoutDefaults</c> to LayoutManager).</summary>
    public LayoutDefaults LayoutDefaults => Current.LayoutDefaults;

    /// <summary>
    /// Takes the settings another instance saved (architecture §18 D3): in effect at once through <see cref="Changed"/>,
    /// nothing written. A changed language still applies only after this window restarts.
    /// </summary>
    /// <param name="settings">The reloaded, normalized settings.</param>
    public void ApplyExternal(AppSettings settings)
    {
        Current = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger.LogInformation("Settings changed by another instance (theme {Theme}, favorites bar {Bar}, layouts {Two}/{Three})", settings.Theme, settings.ShowFavoritesBar, settings.LayoutDefaults.TwoPanes, settings.LayoutDefaults.ThreePanes);
        Changed?.Invoke();
    }

    /// <summary>
    /// Saves a language choice on top of <see cref="Current"/> at this moment, so settings another window saved while the
    /// picker was open are kept (architecture §18 D3). See <see cref="SaveAsync"/>.
    /// </summary>
    /// <param name="language">"auto" or a language code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when written.</returns>
    public Task<bool> SaveLanguageAsync(string language, CancellationToken ct) => SaveAsync(Current with { Language = language }, ct);

    /// <summary>
    /// Applies <paramref name="next"/> at once and writes settings.json (temp file + replace). A failed write is logged and
    /// reported; the new values stay in effect for this session.
    /// </summary>
    /// <param name="next">New settings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when written.</returns>
    public async Task<bool> SaveAsync(AppSettings next, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(next);
        var fixes = new List<string>();
        next = next.Normalize(fixes);
        if (fixes.Count > 0)
        {
            _logger.LogWarning("Settings to save had {Count} invalid value(s), replaced with defaults: {Fixes}", fixes.Count, string.Join("; ", fixes));
        }

        bool languageChanged = !string.Equals(next.Language, Current.Language, StringComparison.Ordinal);
        Current = next;
        Changed?.Invoke();
        try
        {
            // JsonStore creates the directory and opens the file before its first await: keep that off the UI thread.
            await Task.Run(() => _store.SaveAsync(next, ct), ct).ConfigureAwait(true);
            _logger.LogInformation("Settings saved (language {Language}{Restart}, theme {Theme}, favorites bar {Bar}, layouts {Two}/{Three})", next.Language, languageChanged ? ", applies after restart" : string.Empty, next.Theme, next.ShowFavoritesBar, next.LayoutDefaults.TwoPanes, next.LayoutDefaults.ThreePanes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Saving settings to {File} failed", _store.FilePath);
            return false;
        }
    }
}
