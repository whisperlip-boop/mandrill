using Mandrill.Core.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Presentation.Tree;

/// <summary>Subfolders of one folder, in Explorer name order.</summary>
/// <param name="Path">Listed folder.</param>
/// <param name="Folders">Visible subfolder names.</param>
/// <param name="Error">Listing failure (the node then has no children); else null.</param>
public sealed record TreeListing(string Path, IReadOnlyList<string> Folders, FsError? Error);

/// <summary>
/// Folder-only listings for tree expansion, shared by all panes (architecture §4: thread pool, at most 2 at a time).
/// Never runs on the caller's thread. Failures are logged once per path and kind (FsErrorLog) and returned in the listing.
/// </summary>
public sealed class TreeLoader
{
    /// <summary>Listings that may run at the same time.</summary>
    public const int MaxConcurrent = 2;

    private readonly IDirectoryLister _lister;
    private readonly SemaphoreSlim _gate = new(MaxConcurrent, MaxConcurrent);
    private readonly FsErrorLog _errors;

    // The semaphore is never disposed: it lives as long as the app, and a late listing may still release it.

    /// <summary>Creates the loader.</summary>
    /// <param name="lister">Directory lister.</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public TreeLoader(IDirectoryLister lister, ILoggerFactory? loggerFactory = null)
    {
        _lister = lister ?? throw new ArgumentNullException(nameof(lister));
        _errors = new FsErrorLog((loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TreeLoader>());
    }

    /// <summary>Lists the visible subfolders of <paramref name="dir"/> on the thread pool.</summary>
    /// <param name="dir">Normalized folder path.</param>
    /// <param name="showHidden">Show hidden folders (same rule as the file list, ui-spec §2).</param>
    /// <param name="showSystem">Show protected operating system folders.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The listing; completes on a pool thread.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public Task<TreeListing> ListAsync(string dir, bool showHidden, bool showSystem, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dir);
        return Task.Run(
            async () =>
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var names = new List<string>();
                    ListResult result = _lister.List(
                        dir,
                        dirsOnly: true,
                        batch =>
                        {
                            ct.ThrowIfCancellationRequested();
                            foreach (FileEntry e in batch)
                            {
                                if (e.IsDirectory && e.IsVisible(showHidden, showSystem))
                                {
                                    names.Add(e.Name);
                                }
                            }
                        },
                        ct);
                    if (result.Error is { } error)
                    {
                        _errors.Report("Tree", error);
                        return new TreeListing(dir, [], error);
                    }

                    names.Sort(FileNameComparer.Instance);
                    return new TreeListing(dir, names, null);
                }
                finally
                {
                    _gate.Release();
                }
            },
            ct);
    }
}
