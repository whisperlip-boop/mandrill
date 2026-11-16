namespace Mandrill.Core.Index;

/// <summary>State of the index (status-bar indicator, AC-2.9).</summary>
public enum IndexState
{
    /// <summary>Reading the saved index at startup.</summary>
    Loading,

    /// <summary>Building or verifying a drive.</summary>
    Scanning,

    /// <summary>Every fixed drive is indexed.</summary>
    Ready,

    /// <summary>A build waits while a search runs.</summary>
    Paused,

    /// <summary>The index thread failed; the loaded segments stay searchable.</summary>
    Error,

    /// <summary>Another instance owns the index; this one loads it on its first search (architecture §18 D2).</summary>
    Standby,
}

/// <summary>A snapshot of the index state.</summary>
/// <param name="State">State.</param>
/// <param name="Items">Items in all published segments.</param>
/// <param name="Percent">Progress of the running build when the drive's previous size is known; otherwise null.</param>
/// <param name="LastUpdatedUtc">When the index last changed (build, verify, applied change).</param>
/// <param name="SaveFailed">The last write of an index file failed; the index lives on in memory (E-2.14).</param>
/// <param name="Message">The drive being scanned, or the error; null otherwise.</param>
public sealed record IndexStatus(IndexState State, long Items, int? Percent, DateTime? LastUpdatedUtc, bool SaveFailed, string? Message);

/// <summary>
/// The search index as a service (architecture §3, §8.5, ADR-0008): one low-priority background thread owns all segments
/// and is their only writer. It loads the saved index first, builds missing drives, verifies periodically, saves, and
/// follows drive arrival and removal. Presentation sees only this interface.
/// </summary>
public interface IIndexService : ISearchIndex, IDisposable
{
    /// <summary>Current status.</summary>
    IndexStatus Status { get; }

    /// <summary>Raised on the index thread when the status changes; consumers marshal to the UI.</summary>
    event Action<IndexStatus>? StatusChanged;

    /// <summary>The published segment of a drive.</summary>
    /// <param name="root">Drive root, e.g. "C:\".</param>
    /// <param name="segment">The segment, or null.</param>
    /// <returns>True when the drive has a segment.</returns>
    bool TryGetSegment(string root, out DriveSegment? segment);

    /// <summary>
    /// The settings changed (AC-2.10): an added exclude removes that subtree, a removed one schedules a scan of its drive,
    /// and a new RescanHours re-arms the verify timer. Applied on the index thread.
    /// </summary>
    void ApplySettings();

    /// <summary>
    /// Call when a search opens; completes when the index is usable for it. A Standby reader loads the saved index (calls
    /// until that load ends share its task); a loaded reader refreshes the drives only the owner can bring up to date (one
    /// refresh queued at a time, shared by the calls meanwhile); the owner completes at once, or after its start-up load.
    /// Any thread; never faults.
    /// </summary>
    /// <returns>A task that completes when the load or refresh this call waits for is done.</returns>
    Task EnsureLoadedAsync();

    /// <summary>Starts the index thread (idempotent).</summary>
    void Start();

    /// <summary>
    /// Stops the thread: cancels a running build and saves what changed, waiting at most 3 s (§4 shutdown). A timeout is
    /// logged, not thrown; a cancelled <paramref name="ct"/> throws <see cref="OperationCanceledException"/> (the stop goes on).
    /// </summary>
    /// <param name="ct">Cancellation token for the wait.</param>
    /// <returns>A task that completes when the thread stopped or the wait timed out.</returns>
    Task StopAsync(CancellationToken ct);
}
