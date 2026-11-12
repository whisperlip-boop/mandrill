using Mandrill.Core.IO;
using Mandrill.Core.Scanning;
using Mandrill.Presentation.Common;

namespace Mandrill.Presentation.Panes;

/// <summary>
/// Scan results → UI thread (architecture §4 "Batched delivery"). Keeps only the latest snapshot (Interlocked.Exchange) and
/// normally has at most one callback queued, so delivery coalesces itself. A request's first snapshot is posted at Normal
/// priority, later ones at Low. A failure is posted too and first drains the pending snapshot, so callbacks keep their order.
/// Called on scanner worker threads; never blocks.
/// </summary>
public sealed class PaneScanSink : IScanSink
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<long, EntrySnapshot> _onSnapshot;
    private readonly Action<long, FsError> _onFailed;
    private Pending? _pending;
    private int _posted;
    private long _lastId;

    /// <summary>Creates a sink that delivers on the UI thread.</summary>
    /// <param name="dispatcher">UI dispatcher.</param>
    /// <param name="onSnapshot">Receives the latest snapshot on the UI thread.</param>
    /// <param name="onFailed">Receives a failure on the UI thread.</param>
    public PaneScanSink(IUiDispatcher dispatcher, Action<long, EntrySnapshot> onSnapshot, Action<long, FsError> onFailed)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _onSnapshot = onSnapshot ?? throw new ArgumentNullException(nameof(onSnapshot));
        _onFailed = onFailed ?? throw new ArgumentNullException(nameof(onFailed));
    }

    /// <inheritdoc/>
    public void OnSnapshot(long requestId, EntrySnapshot snapshot)
    {
        Interlocked.Exchange(ref _pending, new Pending(requestId, snapshot));

        // The scanner serializes sink calls. A new request's first snapshot gets its own Normal-priority drain even when a
        // Low drain of the previous request is still queued (whichever drain runs second finds nothing and returns).
        bool first = Interlocked.Exchange(ref _lastId, requestId) != requestId;
        if (Interlocked.Exchange(ref _posted, 1) == 0 || first)
        {
            _dispatcher.Post(Drain, first ? UiPriority.Normal : UiPriority.Low);
        }
    }

    /// <inheritdoc/>
    public void OnFailed(long requestId, FsError error)
        => _dispatcher.Post(
            () =>
            {
                Drain();
                _onFailed(requestId, error);
            });

    private void Drain()
    {
        // Reset the flag before taking the snapshot: a snapshot arriving in between posts a new (harmless) drain.
        Interlocked.Exchange(ref _posted, 0);
        if (Interlocked.Exchange(ref _pending, null) is { } p)
        {
            _onSnapshot(p.Id, p.Snapshot);
        }
    }

    private sealed record Pending(long Id, EntrySnapshot Snapshot);
}
