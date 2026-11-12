namespace Mandrill.Core.Watching;

/// <summary>
/// Folder change notifications shared by all panes and the index (ADR-0003). Subscribing is UI-thread-safe (no I/O on the
/// caller's thread); callbacks run on thread-pool threads.
/// </summary>
public interface IChangeFeed
{
    /// <summary>
    /// Watches one folder. On a fixed drive, changes are coalesced per 250 ms window; on other drives the folder gets
    /// <c>DirChange(dir, null, false)</c> every max(2 s, 10 × <paramref name="lastScanDuration"/>) and no handle is held.
    /// </summary>
    /// <param name="dir">Absolute drive-letter folder path; '/' and a trailing '\' are normalized away, and <see cref="DirChange.Dir"/> reports that form.</param>
    /// <param name="onChange">Receives coalesced changes; must not block.</param>
    /// <param name="lastScanDuration">Duration of the folder's last scan (sets the polling interval).</param>
    /// <returns>Disposing it ends the subscription; no callback starts after Dispose returns.</returns>
    /// <exception cref="ArgumentException"><paramref name="dir"/> is not an absolute drive-letter path.</exception>
    IDisposable WatchDirectory(string dir, Action<DirChange> onChange, Func<TimeSpan> lastScanDuration);

    /// <summary>Receives every raw change under a fixed drive root, once per 250 ms window (index, Phase 2).</summary>
    /// <param name="root">Drive root, e.g. "C:\".</param>
    /// <param name="onChanges">Raw changes of one window, in arrival order (read-only, shared by all root subscribers); must not block.</param>
    /// <param name="onOverflow">Events were lost (buffer overflow or watcher failure); the subscriber must rescan.</param>
    /// <returns>Disposing it ends the subscription; no callback starts after Dispose returns.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is not a drive root.</exception>
    IDisposable WatchRoot(string root, Action<IReadOnlyList<RawChange>> onChanges, Action onOverflow);
}
