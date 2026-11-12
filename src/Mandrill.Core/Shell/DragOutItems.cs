using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Windows.Storage;

namespace Mandrill.Core.Shell;

/// <summary>
/// The StorageItems a WinUI drag hands to its target (WP-3.4, ADR-0011, S3): resolved lazily, only when a target asks, and
/// never on the UI thread. The time for the whole list goes to the perf log, so the 2 s rule for a raw FileDrop has a number.
/// Known limitation (QA m5, E-1.11): the Storage APIs cannot open a name ending in a space or a dot, in the plain or the \\?\
/// form, so such items are skipped with a warning when dragged to another application; drags between panes keep them
/// (Mandrill.Paths).
/// </summary>
public static class DragOutItems
{
    /// <summary>How long a target waits for the items before it gets those resolved so far (lead: a dead network drive).</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(10);

    /// <summary>Resolves <paramref name="paths"/> to files and folders, skipping (and logging) items that are gone.</summary>
    /// <param name="paths">Full paths of the dragged items.</param>
    /// <param name="logger">Logger (perf line and skipped items).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The items, in the order given.</returns>
    public static Task<IReadOnlyList<IStorageItem>> ToStorageItemsAsync(IReadOnlyList<string> paths, ILogger logger, CancellationToken ct)
        => ToStorageItemsAsync(paths, logger, Timeout.InfiniteTimeSpan, ct);

    /// <summary>
    /// Resolves <paramref name="paths"/> like <see cref="ToStorageItemsAsync(IReadOnlyList{string}, ILogger, CancellationToken)"/>,
    /// stopping after <paramref name="limit"/>: the items resolved by then are returned and the rest is logged as left out.
    /// </summary>
    /// <param name="paths">Full paths of the dragged items.</param>
    /// <param name="logger">Logger (perf line, skipped items, the limit).</param>
    /// <param name="limit">Time for the whole list (<see cref="DefaultLimit"/> for a drag; infinite for none).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The items resolved in time, in the order given.</returns>
    public static async Task<IReadOnlyList<IStorageItem>> ToStorageItemsAsync(IReadOnlyList<string> paths, ILogger logger, TimeSpan limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        var clock = Stopwatch.StartNew();
        var items = new List<IStorageItem>(paths.Count);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (limit == TimeSpan.Zero)
        {
            timeout.Cancel(); // CancelAfter(0) fires on a timer thread, so it would race the first item
        }
        else
        {
            timeout.CancelAfter(limit);
        }
        foreach (string path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Directory.Exists on a dead network path blocks for the SMB timeout and cannot be cancelled: it is waited
                // for only up to the limit (the blocked worker finishes on its own).
                bool folder = await Task.Run(() => Directory.Exists(path), CancellationToken.None).WaitAsync(timeout.Token).ConfigureAwait(false);
                items.Add(folder
                    ? await StorageFolder.GetFolderFromPathAsync(path).AsTask(timeout.Token).ConfigureAwait(false)
                    : await StorageFile.GetFileFromPathAsync(path).AsTask(timeout.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Drag-out stopped after {Ms} ms at {Path}: {Count} of {Total} item(s) provided",
                    clock.ElapsedMilliseconds,
                    path,
                    items.Count,
                    paths.Count);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One item the shell cannot resolve (gone, locked, a dead network path) never empties the whole drag.
                logger.LogWarning("Drag-out skips {Path}: {Error}", path, ex.Message);
            }
        }

        logger.LogDebug("Drag-out StorageItems for {Count} item(s) in {Ms} ms", items.Count, clock.ElapsedMilliseconds);
        return items;
    }
}
