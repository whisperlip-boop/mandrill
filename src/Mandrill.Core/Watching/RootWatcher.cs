namespace Mandrill.Core.Watching;

/// <summary>Starts a recursive watcher on a drive root (seam for tests; the real one wraps FileSystemWatcher).</summary>
internal interface IRootWatcherFactory
{
    /// <summary>Starts watching <paramref name="root"/>; disposing the result stops it. May block briefly (opens a handle).</summary>
    /// <param name="root">Folder to watch recursively.</param>
    /// <param name="onChange">Raw change callback (any thread).</param>
    /// <param name="onError">Error callback; <see cref="InternalBufferOverflowException"/> means events were lost.</param>
    /// <returns>The running watcher.</returns>
    IDisposable Start(string root, Action<RawChange> onChange, Action<Exception> onError);
}

/// <summary>FileSystemWatcher per ADR-0003: recursive, 64 KB buffer, name/last-write/size filters.</summary>
internal sealed class FileSystemRootWatcherFactory : IRootWatcherFactory
{
    private const int BufferSize = 64 * 1024;

    /// <inheritdoc/>
    public IDisposable Start(string root, Action<RawChange> onChange, Action<Exception> onError)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = BufferSize,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Created += (_, e) => onChange(new RawChange(ChangeKind.Created, e.FullPath));
        watcher.Deleted += (_, e) => onChange(new RawChange(ChangeKind.Deleted, e.FullPath));
        watcher.Changed += (_, e) => onChange(new RawChange(ChangeKind.Changed, e.FullPath));
        watcher.Renamed += (_, e) => onChange(new RawChange(ChangeKind.Renamed, e.FullPath, e.OldFullPath));
        watcher.Error += (_, e) => onError(e.GetException());
        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }

        return watcher;
    }
}
