using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Mandrill.Core.State;

/// <summary>
/// Tells a running instance that another instance saved settings.json or favorites.json (architecture §18 D3): a
/// non-recursive watcher on the data folder reports the rename from <c>&lt;file&gt;.tmp.&lt;pid&gt;</c> (JsonStore's save) when
/// the pid is not this process. A lost-events error reports both files. Events arrive on the thread pool.
/// </summary>
public sealed class StateFileWatcher : IDisposable
{
    /// <summary>File name of AppPaths.SettingsFile.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>File name of AppPaths.FavoritesFile.</summary>
    public const string FavoritesFileName = "favorites.json";

    // session.json is not shared: each window keeps its own layout (§18 D4).
    private static readonly string[] Watched = [SettingsFileName, FavoritesFileName];
    private readonly FileSystemWatcher _watcher;
    private readonly ILogger _logger;

    /// <summary>Starts watching. Call from a worker thread (it checks the folder).</summary>
    /// <param name="dataDirectory">The data folder that holds settings.json and favorites.json.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentException">The folder does not exist.</exception>
    public StateFileWatcher(string dataDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _watcher = new FileSystemWatcher(dataDirectory) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Raised with the file name ("settings.json" or "favorites.json") another instance just saved.</summary>
    public event Action<string>? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }

    /// <summary>The watched file another process saved with this rename, or null (this process, another file, not a save).</summary>
    internal static string? SavedByOtherProcess(string? oldName, string? newName, int ownPid)
    {
        string? file = Watched.FirstOrDefault(f => string.Equals(f, newName, StringComparison.OrdinalIgnoreCase));
        string prefix = file + ".tmp.";
        return file is not null
            && oldName is not null
            && oldName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(oldName.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            && pid != ownPid
            ? file
            : null;
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (SavedByOtherProcess(e.OldName, e.Name, Environment.ProcessId) is { } file)
        {
            Raise(file);
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "The data folder watcher lost events; reloading settings and favorites");
        foreach (string file in Watched)
        {
            Raise(file);
        }
    }

    // A watcher callback that throws would end the process (thread-pool thread).
    private void Raise(string file)
    {
        try
        {
            Changed?.Invoke(file);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling a change of {File} failed", file);
        }
    }
}
