namespace Mandrill.Core.Search;

/// <summary>A filename search (architecture §3 Core.Search, AC-2.14–2.16).</summary>
/// <param name="Scope">Folder searched, with all its subfolders (AC-2.15).</param>
/// <param name="Text">Query: a substring, or a whole-name pattern with <c>*</c>/<c>?</c> (AC-2.16).</param>
/// <param name="Cap">Most hits delivered; one more found sets <see cref="SearchOutcome.Capped"/> (AC-2.18).</param>
/// <param name="ShowHidden">Explorer "show hidden files": hidden items and folders are found (and entered) only then.</param>
/// <param name="ShowSystem">Explorer "show protected operating system files" (hidden + system items).</param>
public sealed record SearchQuery(string Scope, string Text, int Cap = 100_000, bool ShowHidden = false, bool ShowSystem = false);

/// <summary>One search result. <see cref="Directory"/> strings are shared by all hits of a folder within one search.</summary>
/// <param name="Name">File or folder name.</param>
/// <param name="Directory">Folder that contains it.</param>
/// <param name="Size">Size in bytes (0 for folders).</param>
/// <param name="Modified">Last write time, UTC.</param>
/// <param name="IsDirectory">True for a folder.</param>
public sealed record SearchHit(string Name, string Directory, long Size, DateTime Modified, bool IsDirectory);

/// <summary>How a search ended.</summary>
/// <param name="Found">Hits delivered (at most the cap).</param>
/// <param name="Capped">More hits exist than the cap (the walk stopped).</param>
/// <param name="LiveUsed">Part of the scope was listed from disk because the index does not cover it (indexing notice).</param>
/// <param name="AccessDenied">Folders that could not be listed for lack of access (E-2.13).</param>
/// <param name="Partial">Folders vanished or could not be read while listing (e.g. a removed drive, E-2.10).</param>
/// <param name="Elapsed">Wall time of the search.</param>
public sealed record SearchOutcome(int Found, bool Capped, bool LiveUsed, int AccessDenied, bool Partial, TimeSpan Elapsed);

/// <summary>
/// The state flags of a running search, so the popup can show the indexing strip and the skipped-folder count while a long
/// live listing runs and keep them after Stop (ui-spec §3, AC-2.24).
/// </summary>
/// <param name="LiveUsed">Part of the scope is being listed from disk.</param>
/// <param name="AccessDenied">Folders skipped for lack of access so far.</param>
/// <param name="Partial">Folders vanished or could not be read so far.</param>
public sealed record SearchProgress(bool LiveUsed, int AccessDenied, bool Partial);

/// <summary>Scoped filename search over the index with a live listing where the index does not reach (§8.4).</summary>
public interface ISearchService
{
    /// <summary>
    /// Runs a search on a worker thread. Hits stream to <paramref name="sink"/> on that thread in batches of at most 2,000
    /// or every 100 ms; the task completes after the last batch.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="sink">Receives hit batches (worker thread; marshal to the UI yourself).</param>
    /// <param name="progress">
    /// Receives the state flags when they change: a live listing or a vanished folder at once, the access-denied count at
    /// most with each batch (worker thread); null for none.
    /// </param>
    /// <param name="ct">Cancels the search (the task is cancelled).</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentException">The scope is not a valid folder path, the text is empty, or the cap is not positive.</exception>
    Task<SearchOutcome> SearchAsync(SearchQuery query, Action<IReadOnlyList<SearchHit>> sink, Action<SearchProgress>? progress, CancellationToken ct);
}
