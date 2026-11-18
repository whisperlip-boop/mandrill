using Mandrill.Core.IO;
using Mandrill.Core.Search;
using Mandrill.Core.State;

namespace Mandrill.Core.Scanning;

/// <summary>Kind of scan request (architecture §7).</summary>
public enum ScanKind
{
    /// <summary>Navigate to a folder: cached snapshot first if any, then a full listing (AC-1.10, AC-1.13).</summary>
    Cold,

    /// <summary>Re-list the shown folder and publish a diff against the shown version (refresh, many changes).</summary>
    Reconcile,

    /// <summary>Stat only the given names and diff (≤200 change events).</summary>
    Partial,
}

/// <summary>How entries are filtered and ordered.</summary>
/// <param name="Column">Sort column.</param>
/// <param name="Descending">True for descending (folders still come first, AC-1.22).</param>
/// <param name="ShowHidden">Explorer "show hidden files".</param>
/// <param name="ShowSystem">Explorer "show protected operating system files".</param>
/// <param name="NameFilter">The pane's quick filter (ui-spec §2b): only matching names are shown; null shows all.</param>
public sealed record ViewSpec(SortColumn Column, bool Descending, bool ShowHidden, bool ShowSystem, NameMatcher? NameFilter = null)
{
    /// <summary>Name ascending, hidden files hidden (Explorer defaults).</summary>
    public static ViewSpec Default { get; } = new(SortColumn.Name, false, false, false);
}

/// <summary>One scan request.</summary>
/// <param name="Path">Normalized folder path.</param>
/// <param name="View">Filter and order.</param>
/// <param name="Kind">Scan kind.</param>
/// <param name="Names">Changed names for <see cref="ScanKind.Partial"/>.</param>
/// <param name="FallbackToAncestor">If the folder is missing, show its nearest existing ancestor (restore, E-1.3).</param>
public sealed record ScanRequest(string Path, ViewSpec View, ScanKind Kind, IReadOnlyCollection<string>? Names = null, bool FallbackToAncestor = false);

/// <summary>
/// Receives scan results. Called on worker threads, one call at a time per scanner, and never blocking (the scanner holds
/// its publish lock during the call): store the result and post to the UI. Every call carries the request id (AC-1.12);
/// no result of an older request arrives after a result of a newer one, but the consumer still drops ids that are not its
/// current one (E-1.10).
/// </summary>
public interface IScanSink
{
    /// <summary>A cumulative snapshot: a later snapshot for the same id replaces earlier ones.</summary>
    /// <param name="requestId">Request id from <see cref="PaneScanner.Scan"/>.</param>
    /// <param name="snapshot">The snapshot.</param>
    void OnSnapshot(long requestId, EntrySnapshot snapshot);

    /// <summary>The request failed. Snapshots already delivered for it are partial.</summary>
    /// <param name="requestId">Request id.</param>
    /// <param name="error">The error.</param>
    void OnFailed(long requestId, FsError error);
}
