using System.Diagnostics;
using System.Globalization;
using Mandrill.Core.Diagnostics;
using Mandrill.Core.IO;
using Mandrill.Core.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Core.Scanning;

/// <summary>
/// Per-pane scan pipeline (architecture §3, §4, §7). Each <see cref="Scan"/> gets a process-wide id and cancels the previous
/// job; every sink call carries the id so the consumer can drop stale results (AC-1.12, E-1.10). Work runs on the thread
/// pool. A cold listing publishes its first snapshot after 200 entries or 30 ms (sorting only those entries), then a
/// cumulative snapshot every 250 ms, then a final one; batches merge into the sorted array in O(n) (AC-1.10, E-1.5).
/// Threading: <see cref="Scan"/>, <see cref="Resort"/> and <see cref="Cancel"/> are called from one thread (the pane's UI
/// thread); jobs run on the thread pool. Sink calls are serialized, and no snapshot of an older request is delivered
/// after a snapshot of a newer one, but a consumer must still compare ids with <see cref="CurrentRequestId"/>.
/// </summary>
public sealed class PaneScanner : IDisposable
{
    /// <summary>Entries in the first snapshot of a cold listing.</summary>
    public const int FirstSnapshotEntries = 200;

    /// <summary>Most per-item changes a published diff may carry; above it the consumer resets (architecture §5).</summary>
    public const int MaxDiffChanges = 200;

    /// <summary>Time after which the first snapshot is published even with fewer entries.</summary>
    public static readonly TimeSpan FirstSnapshotDelay = TimeSpan.FromMilliseconds(30);

    /// <summary>Interval between cumulative snapshots after the first.</summary>
    public static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);

    private const int ErrorPathNotFound = 3;

    private static long s_requestIds;
    private static long s_versions;

    private readonly IDirectoryLister _lister;
    private readonly DirectoryCache _cache;
    private readonly EntrySorter _sorter;
    private readonly ILogger _logger;
    private readonly ILogger _perf;
    private readonly Lock _gate = new();

    // Held across "is this id still current" and the sink call, so deliveries are ordered (an old job cannot deliver
    // after a newer job). Sinks must therefore not block (they store and post, architecture §4).
    private readonly Lock _publishGate = new();

    private CancellationTokenSource? _cts;
    private long _currentId;
    private long _runningId;
    private ScanRequest? _lastRequest;

    // What the pane shows: the last snapshot published while its request was current, and the full listing of the folder
    // it belongs to. The listing carries its own path: during a cold scan of B the shown snapshot is already B while no
    // complete listing of B exists yet, so a Partial/Reconcile for B must not use A's entries (review M1).
    private EntrySnapshot? _shown;
    private CachedListing? _shownListing;

    // CancellationTokenSources are not disposed: they have no timers or registrations, and a worker may still observe the
    // token after the scanner moved on (disposing could make its checks throw ObjectDisposedException).

    /// <summary>Creates a scanner for one pane.</summary>
    /// <param name="lister">Directory lister.</param>
    /// <param name="cache">Listing cache shared by all panes.</param>
    /// <param name="sorter">Sorter (culture and type names).</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public PaneScanner(IDirectoryLister lister, DirectoryCache cache, EntrySorter sorter, ILoggerFactory? loggerFactory = null)
    {
        _lister = lister ?? throw new ArgumentNullException(nameof(lister));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _sorter = sorter ?? throw new ArgumentNullException(nameof(sorter));
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<PaneScanner>();
        _perf = factory.CreateLogger(MandrillLogging.PerfCategory);
    }

    /// <summary>Id of the newest request, or 0 when cancelled.</summary>
    public long CurrentRequestId
    {
        get
        {
            lock (_gate)
            {
                return _currentId;
            }
        }
    }

    /// <summary>Starts a request and cancels the one in flight. Results arrive on <paramref name="sink"/> from a worker.</summary>
    /// <param name="request">The request.</param>
    /// <param name="sink">Receiver.</param>
    /// <returns>The request id.</returns>
    public long Scan(ScanRequest request, IScanSink sink)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        long id = Interlocked.Increment(ref s_requestIds);
        var cts = new CancellationTokenSource();
        EntrySnapshot? shown;
        CachedListing? listing;
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _cts;
            _cts = cts;
            _currentId = id;
            _runningId = id;
            _lastRequest = request;
            shown = _shown;
            listing = _shownListing;
        }

        previous?.Cancel();
        var job = new Job(this, id, request, sink, shown, listing, cts.Token);
        _ = Task.Run(job.Run, CancellationToken.None);
        return id;
    }

    /// <summary>
    /// Re-filters and re-sorts the shown folder for a new view without I/O. If a scan is still running, it is restarted
    /// with the new view instead (its listing is not complete yet).
    /// </summary>
    /// <param name="view">New view.</param>
    /// <param name="sink">Receiver.</param>
    /// <returns>The request id.</returns>
    /// <exception cref="InvalidOperationException">Nothing has been listed yet.</exception>
    public long Resort(ViewSpec view, IScanSink sink)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(sink);
        ScanRequest? last;
        bool running;
        CachedListing? listing;
        EntrySnapshot? shown;
        lock (_gate)
        {
            last = _lastRequest;
            running = _runningId != 0;
            listing = _shownListing;
            shown = _shown;
        }

        if (last is null)
        {
            throw new InvalidOperationException("Nothing to re-sort: no folder has been scanned.");
        }

        if (running || shown is null || !HasCompleteListing(shown, listing, shown.Path))
        {
            return Scan(last with { View = view }, sink);
        }

        long id = Interlocked.Increment(ref s_requestIds);
        var cts = new CancellationTokenSource();
        var request = new ScanRequest(shown.Path, view, ScanKind.Reconcile);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _cts;
            _cts = cts;
            _currentId = id;
            _runningId = id;
            _lastRequest = request;
        }

        previous?.Cancel();
        var job = new Job(this, id, request, sink, shown, listing, cts.Token);
        _ = Task.Run(job.RunResort, CancellationToken.None);
        return id;
    }

    /// <summary>Cancels the request in flight; its results will not be published.</summary>
    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _currentId = 0;
        }

        cts?.Cancel();
    }

    /// <inheritdoc />
    public void Dispose() => Cancel();

    // True when the shown snapshot is complete (final or from cache) and the full listing belongs to the same folder.
    private static bool HasCompleteListing(EntrySnapshot? shown, CachedListing? listing, string? path)
        => shown is not null && listing is not null && path is not null
           && (shown.IsFinal || shown.FromCache)
           && PathUtil.PathEquals(shown.Path, path)
           && PathUtil.PathEquals(listing.Path, path);

    private bool TryPublish(long id, IScanSink sink, EntrySnapshot snapshot, FileEntry[]? all, CancellationToken ct)
    {
        lock (_publishGate)
        {
            lock (_gate)
            {
                if (ct.IsCancellationRequested || _currentId != id)
                {
                    return false;
                }

                _shown = snapshot;
                if (all is not null)
                {
                    _shownListing = new CachedListing(snapshot.Path, all, DateTime.UtcNow);
                }
                else if (_shownListing is not null && !PathUtil.PathEquals(_shownListing.Path, snapshot.Path))
                {
                    // A progressive snapshot of another folder: the old full listing no longer matches what is shown.
                    _shownListing = null;
                }

                // The listing is complete once the final snapshot is out: a Resort right after it must not re-list.
                if (snapshot.IsFinal && _runningId == id)
                {
                    _runningId = 0;
                }
            }

            try
            {
                sink.OnSnapshot(id, snapshot);
            }
            catch (Exception ex)
            {
                // A faulty consumer must not kill the worker silently.
                _logger.LogError(ex, "Scan sink failed for request {Id}", id);
            }
        }

        return true;
    }

    private void Fail(long id, IScanSink sink, FsError error, CancellationToken ct)
    {
        lock (_publishGate)
        {
            lock (_gate)
            {
                if (ct.IsCancellationRequested || _currentId != id)
                {
                    return;
                }
            }

            try
            {
                sink.OnFailed(id, error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan sink failed for request {Id}", id);
            }
        }
    }

    private void Finished(long id)
    {
        lock (_gate)
        {
            if (_runningId == id)
            {
                _runningId = 0;
            }
        }
    }

    private static long NextVersion() => Interlocked.Increment(ref s_versions);

    private sealed class Job
    {
        private readonly PaneScanner _owner;
        private readonly long _id;
        private readonly ScanRequest _request;
        private readonly IScanSink _sink;
        private readonly EntrySnapshot? _shown;
        private readonly CachedListing? _listing;
        private readonly CancellationToken _ct;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public Job(PaneScanner owner, long id, ScanRequest request, IScanSink sink, EntrySnapshot? shown, CachedListing? listing, CancellationToken ct)
        {
            _owner = owner;
            _id = id;
            _request = request;
            _sink = sink;
            _shown = shown;
            _listing = listing;
            _ct = ct;
        }

        private EntrySorter Sorter => _owner._sorter;

        public void Run()
        {
            try
            {
                bool sameFolder = HasCompleteListing(_shown, _listing, _request.Path);
                switch (_request.Kind)
                {
                    case ScanKind.Partial when sameFolder && _request.Names is not null:
                        RunPartial();
                        break;
                    case ScanKind.Reconcile or ScanKind.Partial when sameFolder:
                        RunReconcile(_request.Path, null, _shown!);
                        break;
                    default:
                        RunCold();
                        break;
                }
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                _owner._logger.LogDebug("Scan {Id} cancelled after {Ms} ms", _id, _sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _owner._logger.LogError(ex, "Scan {Id} of {Path} failed unexpectedly", _id, _request.Path);
                _owner.Fail(_id, _sink, new FsError(FsErrorKind.Other, _request.Path, 0), _ct);
            }
            finally
            {
                _owner.Finished(_id);
            }
        }

        public void RunResort()
        {
            try
            {
                var visible = Sorter.FilterAndSort(_listing!.Entries, _request.View, out int total);
                Publish(new EntrySnapshot(_shown!.Path, _shown.RedirectedFrom, NextVersion(), visible, true, null, 0, _sw.Elapsed, false, total), _listing.Entries);
                _owner._perf.LogDebug("Resort {Id} {Path}: {Count} entries in {Ms} ms", _id, _shown.Path, visible.Length, _sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                // The consumer waits for this id: report the failure instead of going silent.
                _owner._logger.LogError(ex, "Resort {Id} failed unexpectedly", _id);
                _owner.Fail(_id, _sink, new FsError(FsErrorKind.Other, _request.Path, 0), _ct);
            }
            finally
            {
                _owner.Finished(_id);
            }
        }

        private bool Publish(EntrySnapshot snapshot, FileEntry[]? all) => _owner.TryPublish(_id, _sink, snapshot, all, _ct);

        private void RunCold()
        {
            string path = _request.Path;
            string? redirectedFrom = null;
            if (_request.FallbackToAncestor)
            {
                string? existing = _owner._lister.NearestExistingDirectory(path);
                if (existing is null)
                {
                    _owner.Fail(_id, _sink, new FsError(FsErrorKind.NotFound, path, ErrorPathNotFound), _ct);
                    return;
                }

                if (!PathUtil.PathEquals(existing, path))
                {
                    redirectedFrom = path;
                    path = existing;
                }
            }

            if (_owner._cache.TryGet(path, out var cached))
            {
                // AC-1.13: show the cached listing at once, then re-list and publish a diff against it.
                var visible = Sorter.FilterAndSort(cached.Entries, _request.View, out int total);
                var snapshot = new EntrySnapshot(path, redirectedFrom, NextVersion(), visible, false, null, 0, _sw.Elapsed, true, total);
                if (!Publish(snapshot, cached.Entries))
                {
                    return;
                }

                RunReconcile(path, redirectedFrom, snapshot);
                return;
            }

            RunProgressive(path, redirectedFrom);
        }

        private void RunProgressive(string path, string? redirectedFrom)
        {
            var all = new List<FileEntry>();
            var pending = new List<FileEntry>();
            FileEntry[] sorted = [];
            IComparer<FileEntry>? comparer = null;
            bool first = true;
            long lastPublishMs = 0;
            long firstMs = -1;
            int firstCount = 0;
            int total = 0; // shown by the hidden/system rule, before the name filter
            var view = _request.View;

            void PublishProgress(int takeFromPending)
            {
                var take = new FileEntry[takeFromPending];
                pending.CopyTo(0, take, 0, takeFromPending);
                pending.RemoveRange(0, takeFromPending);
                comparer ??= Sorter.CreateComparer(view, take);
                Array.Sort(take, comparer);
                sorted = sorted.Length == 0 ? take : EntrySorter.Merge(sorted, take, comparer);
                Publish(new EntrySnapshot(path, redirectedFrom, NextVersion(), sorted, false, null, 0, _sw.Elapsed, false, total), null);
                lastPublishMs = _sw.ElapsedMilliseconds;
            }

            var result = _owner._lister.List(path, dirsOnly: false, batch =>
            {
                _ct.ThrowIfCancellationRequested();
                all.AddRange(batch);
                foreach (var e in batch)
                {
                    if (e.IsVisible(view.ShowHidden, view.ShowSystem))
                    {
                        total++;
                        if (view.NameFilter?.IsMatch(e.Name) != false)
                        {
                            pending.Add(e);
                        }
                    }
                }

                if (first)
                {
                    if (all.Count >= FirstSnapshotEntries || _sw.Elapsed >= FirstSnapshotDelay)
                    {
                        // The first snapshot sorts only its own entries (S2: sorting everything first cost 2.5 s).
                        first = false;
                        firstCount = Math.Min(pending.Count, FirstSnapshotEntries);
                        PublishProgress(firstCount);
                        firstMs = _sw.ElapsedMilliseconds;
                    }
                }
                else if (_sw.ElapsedMilliseconds - lastPublishMs >= PublishInterval.TotalMilliseconds)
                {
                    PublishProgress(pending.Count);
                }
            }, _ct);

            _ct.ThrowIfCancellationRequested();
            if (result.Error is not null)
            {
                _owner.Fail(_id, _sink, result.Error, _ct);
                return;
            }

            var allArray = all.ToArray();
            var sortWatch = Stopwatch.StartNew();
            FileEntry[] final;
            if (comparer is null || view.Column == SortColumn.Type)
            {
                // Type ranks depend on every extension present, so the final order is computed once over everything.
                final = Sorter.FilterAndSort(allArray, view, out total);
            }
            else
            {
                var rest = pending.ToArray();
                Array.Sort(rest, comparer);
                final = sorted.Length == 0 ? rest : EntrySorter.Merge(sorted, rest, comparer);
            }

            long sortMs = sortWatch.ElapsedMilliseconds;
            _ct.ThrowIfCancellationRequested();
            if (Publish(new EntrySnapshot(path, redirectedFrom, NextVersion(), final, true, null, result.SkippedEntries, _sw.Elapsed, false, total), allArray))
            {
                _owner._cache.Put(path, new CachedListing(path, allArray, DateTime.UtcNow));
                _owner._perf.LogInformation(
                    "Scan {Id} cold {Path}: first snapshot {FirstMs} ms ({FirstCount} entries), final {TotalMs} ms, {Count} entries, {Visible} visible, final sort/merge {SortMs} ms",
                    _id, path, firstMs, firstCount, _sw.ElapsedMilliseconds, allArray.Length, final.Length, sortMs);
            }
        }

        private void RunReconcile(string path, string? redirectedFrom, EntrySnapshot baseSnapshot)
        {
            var all = new List<FileEntry>();
            var result = _owner._lister.List(path, dirsOnly: false, batch =>
            {
                _ct.ThrowIfCancellationRequested();
                all.AddRange(batch);
            }, _ct);

            _ct.ThrowIfCancellationRequested();
            if (result.Error is not null)
            {
                _owner._cache.Remove(path);
                _owner.Fail(_id, _sink, result.Error, _ct);
                return;
            }

            var allArray = all.ToArray();
            PublishWithDiff(path, redirectedFrom ?? baseSnapshot.RedirectedFrom, baseSnapshot, allArray, result.SkippedEntries, "reconcile");
        }

        private void RunPartial()
        {
            string path = _request.Path;
            var names = new HashSet<string>(_request.Names!, PathUtil.Comparer);
            var (present, _) = _owner._lister.StatNames(path, names);
            _ct.ThrowIfCancellationRequested();

            var merged = new List<FileEntry>(_listing!.Entries.Length + present.Count);
            foreach (var e in _listing.Entries)
            {
                if (!names.Contains(e.Name))
                {
                    merged.Add(e);
                }
            }

            merged.AddRange(present);
            PublishWithDiff(path, _shown!.RedirectedFrom, _shown, merged.ToArray(), 0, "partial");
        }

        private void PublishWithDiff(string path, string? redirectedFrom, EntrySnapshot baseSnapshot, FileEntry[] all, int skipped, string kind)
        {
            var visible = Sorter.FilterAndSort(all, _request.View, out int total);
            var diff = EntryDiffer.Compute(baseSnapshot.Version, baseSnapshot.Visible, visible, MaxDiffChanges);
            _ct.ThrowIfCancellationRequested();
            if (Publish(new EntrySnapshot(path, redirectedFrom, NextVersion(), visible, true, diff, skipped, _sw.Elapsed, false, total), all))
            {
                _owner._cache.Put(path, new CachedListing(path, all, DateTime.UtcNow));
                _owner._perf.LogInformation(
                    "Scan {Id} {Kind} {Path}: {TotalMs} ms, {Count} entries, diff {Changes}",
                    _id, kind, path, _sw.ElapsedMilliseconds, all.Length, diff is null ? "reset" : diff.ChangeCount.ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}
