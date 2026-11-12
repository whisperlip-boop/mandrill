namespace Mandrill.Core.Diagnostics;

/// <summary>
/// Well-known locations: the per-user data directory (%APPDATA%\mandrill) and resources shipped next to the executable.
/// Path composition is pure; only <see cref="EnsureCreated"/> touches the disk.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Creates paths rooted at explicit directories (tests, alternate profiles).</summary>
    /// <param name="dataDirectory">Per-user data directory.</param>
    /// <param name="resourceDirectory">Directory that contains Assets\ and Locales\.</param>
    /// <param name="usesFallbackDataDirectory">True when %APPDATA% was unavailable and %LOCALAPPDATA% is used instead.</param>
    /// <param name="isOverridden">True when <see cref="DataDirectoryVariable"/> chose the data directory.</param>
    public AppPaths(string dataDirectory, string resourceDirectory, bool usesFallbackDataDirectory = false, bool isOverridden = false)
    {
        DataDirectory = dataDirectory;
        ResourceDirectory = resourceDirectory;
        UsesFallbackDataDirectory = usesFallbackDataDirectory;
        IsOverridden = isOverridden;
    }

    /// <summary>True when the data directory comes from <see cref="DataDirectoryVariable"/> (developer and perf runs).</summary>
    public bool IsOverridden { get; }

    /// <summary>A <see cref="DataDirectoryVariable"/> value that was set but rejected (the default was used); null otherwise.</summary>
    public string? IgnoredOverride { get; private init; }

    /// <summary>Default locations: %APPDATA%\mandrill (or %LOCALAPPDATA%\mandrill) and the executable's directory.</summary>
    /// <returns>The default paths.</returns>
    /// <exception cref="InvalidOperationException">Neither profile folder is available.</exception>
    public static AppPaths CreateDefault()
    {
        // Developer/perf override: measurement runs (tools/perf) use their own data so the user's session is never touched.
        string? requested = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (DataDirectoryOverride(requested) is { } custom)
        {
            return new AppPaths(custom, AppContext.BaseDirectory, isOverridden: true);
        }

        // Logging does not exist yet: remember a rejected value so bootstrap can report it.
        string dir = ResolveDataDirectory(Environment.GetFolderPath, out bool fallback);
        return new AppPaths(dir, AppContext.BaseDirectory, fallback)
        {
            IgnoredOverride = string.IsNullOrWhiteSpace(requested) ? null : requested,
        };
    }

    /// <summary>Environment variable that points the data directory elsewhere (developer and perf runs only).</summary>
    public const string DataDirectoryVariable = "MANDRILL_DATA_DIR";

    /// <summary>
    /// The override directory when <paramref name="value"/> is a fully qualified path (drive, UNC or \\?\ form; a developer
    /// setting, not validated further); otherwise null, so a typo never stops the app before logging exists.
    /// </summary>
    /// <param name="value">Value of <see cref="DataDirectoryVariable"/>.</param>
    /// <returns>The directory, or null.</returns>
    public static string? DataDirectoryOverride(string? value)
    {
        string? trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || HasReservedCharacters(trimmed) || !Path.IsPathFullyQualified(trimmed))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // Characters Windows forbids in a path (.NET's GetInvalidPathChars lists only '|' and control characters); the "\\?\"
    // prefix itself is allowed.
    private static bool HasReservedCharacters(string path)
    {
        string body = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        return body.IndexOfAny(['"', '<', '>', '|', '?', '*']) >= 0 || body.Any(char.IsControl);
    }

    /// <summary>
    /// %APPDATA%\mandrill, falling back to %LOCALAPPDATA%\mandrill. Never returns a relative path:
    /// GetFolderPath returns "" when a folder is unavailable, and Path.Combine would then point at the working directory.
    /// </summary>
    /// <param name="getFolderPath">Folder lookup (Environment.GetFolderPath; injectable for tests).</param>
    /// <param name="usedFallback">True when %LOCALAPPDATA% had to be used.</param>
    /// <returns>Absolute data directory.</returns>
    /// <exception cref="InvalidOperationException">Neither profile folder is available.</exception>
    public static string ResolveDataDirectory(Func<Environment.SpecialFolder, string> getFolderPath, out bool usedFallback)
    {
        ArgumentNullException.ThrowIfNull(getFolderPath);
        usedFallback = false;
        foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            string root = getFolderPath(folder);
            if (!string.IsNullOrEmpty(root) && Path.IsPathFullyQualified(root))
            {
                usedFallback = folder != Environment.SpecialFolder.ApplicationData;
                return Path.Combine(root, "mandrill");
            }
        }

        throw new InvalidOperationException("Neither %APPDATA% nor %LOCALAPPDATA% is available for Mandrill's data directory.");
    }

    /// <summary>Per-user data directory (settings, session, favorites, index, logs).</summary>
    public string DataDirectory { get; }

    /// <summary>True when %APPDATA% was unavailable and the data directory is under %LOCALAPPDATA% (logged at startup).</summary>
    public bool UsesFallbackDataDirectory { get; }

    /// <summary>Directory that contains shipped resources.</summary>
    public string ResourceDirectory { get; }

    /// <summary>Log directory (logs\ under the data directory).</summary>
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Main rolling log file.</summary>
    public string LogFile => Path.Combine(LogDirectory, "mandrill.log");

    /// <summary>settings.json.</summary>
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>session.json.</summary>
    public string SessionFile => Path.Combine(DataDirectory, "session.json");

    /// <summary>favorites.json.</summary>
    public string FavoritesFile => Path.Combine(DataDirectory, "favorites.json");

    /// <summary>The search index folder: manifest.json and one file per drive (architecture §8.3).</summary>
    public string IndexDirectory => Path.Combine(DataDirectory, "index");

    /// <summary>Directory with the locale JSON files.</summary>
    public string LocalesDirectory => Resource("Locales");

    /// <summary>The app icon (window title bars, About).</summary>
    public string AppIcon => Resource(Path.Combine("Assets", "mandrill.ico"));

    /// <summary>The search popup's title bar icon: the toolbar magnifier (tools/icons/Export-SearchIco.ps1).</summary>
    public string SearchIcon => Resource(@"Assets\search.ico"); // not Path.Combine: its lone search-dot-ico literal would read as a locale key (LocaleKeyCoverageTests)

    /// <summary>Absolute path of a shipped resource.</summary>
    /// <param name="relativePath">Path relative to the executable's directory.</param>
    /// <returns>The absolute path.</returns>
    public string Resource(string relativePath) => Path.Combine(ResourceDirectory, relativePath);

    /// <summary>Creates the data and log directories if missing. Call from a worker thread.</summary>
    /// <exception cref="IOException">The directories cannot be created.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the data directory is denied.</exception>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
