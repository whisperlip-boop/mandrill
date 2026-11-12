using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using Mandrill.Core.Diagnostics;
using Mandrill.Core.IO;
using Mandrill.Core.Platform;
using Mandrill.Core.State;
using Mandrill.Core.Watching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Mandrill.Core.Index;

/// <summary>Settings and seams of the <see cref="IndexService"/>; the defaults are the product values (architecture §8.3, §8.5).</summary>
public sealed class IndexServiceOptions
{
    /// <summary>The index folder (<c>AppPaths.IndexDirectory</c>).</summary>
    public required string IndexDirectory { get; init; }

    /// <summary>Drive arrival and removal; only <see cref="DriveKind.Fixed"/> drives are indexed (AC-2.1).</summary>
    public required IDriveMonitor Drives { get; init; }

    /// <summary>Directory source (Win32 enumeration; an MFT source later).</summary>
    public IIndexSource Source { get; init; } = new Win32IndexSource();

    /// <summary>Volume serial of a drive root, or null when unknown (runs on the index thread).</summary>
    public Func<string, uint?> VolumeSerial { get; init; } = Mandrill.Core.Platform.Drives.TryGetVolumeSerial;

    /// <summary>Current index settings: the exclude list is read before every build, RescanHours once at start (WP-2.4 re-applies changes).</summary>
    public Func<IndexSettings> Settings { get; init; } = () => IndexSettings.Default;

    /// <summary>Clock and timers (tests use a fake).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Logger factory.</summary>
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;

    /// <summary>First verify pass after start (§8.3: 2 min).</summary>
    public TimeSpan VerifyDelay { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Save interval while something changed (§8.3: 15 min).</summary>
    public TimeSpan DirtySaveInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Partial save interval during a first build (§8.3: 5 min).</summary>
    public TimeSpan BuildSaveInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Partial save after this many new items during a first build (§8.3: 500k).</summary>
    public int BuildSaveItems { get; init; } = 500_000;

    /// <summary>Longest wait of <see cref="IIndexService.StopAsync"/> (§4 shutdown: 3 s, real time).</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Restart delay after the index thread failed (once).</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Builder throttle: entries between checkpoints.</summary>
    public int CheckpointEvery { get; init; } = 2_000;

    /// <summary>Builder throttle: sleep at each checkpoint.</summary>
    public TimeSpan ThrottleSleep { get; init; } = TimeSpan.FromMilliseconds(1);

    /// <summary>Drive-root watcher for change tracking (AC-2.7); null = no tracking (the index changes only by builds).</summary>
    public IChangeFeed? ChangeFeed { get; init; }

    /// <summary>Folders whose changes never reach the index (the app's data folder, §8.3).</summary>
    public IReadOnlyList<string> IgnoredPaths { get; init; } = [];

    /// <summary>Stat of one path without following links (change tracking; tests use a fake).</summary>
    public Func<string, PathStat> Stat { get; init; } = PathStat.Win32;

    /// <summary>How often dirty paths are applied (§8.3: 2 s).</summary>
    public TimeSpan ApplyInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Shortest gap between two overflow verifies of one drive (§8.3: 10 min).</summary>
    public TimeSpan OverflowVerifyInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Perf runs only: which fixed drives are indexed (<see cref="IndexDriveFilter"/>); null = all.</summary>
    public Func<string, bool>? IncludeDrive { get; init; }

    /// <summary>
    /// Builds run with <see cref="GCLatencyMode.SustainedLowLatency"/> (no blocking gen2 collections while
    /// the build allocates, risk R6), restored afterwards.
    /// </summary>
    public bool LowLatencyGcWhileBuilding { get; init; }
}

/// <summary>
/// The index service (architecture §8.5, ADR-0008). One dedicated background thread at BelowNormal priority in Windows
/// background mode (lower CPU, I/O and memory priority) is the only writer of every segment. It works through a command
/// queue: load the saved index (search works at once), build missing or incomplete fixed drives one at a time, verify every
/// drive 2 min after start and then every RescanHours (rebuild into a new segment, then swap), save while dirty, follow
/// drive arrival and removal, apply deletes reported by search, and (WP-2.4) apply watcher changes every 2 s, verify a
/// drive whose watcher overflowed (at most every 10 min), compact segments that drifted, and follow settings changes.
/// A write failure keeps the index in memory (E-2.14).
/// An unhandled failure restarts the thread once after a minute; a second one leaves the state at Error while the
/// segments stay searchable.
/// </summary>
public sealed class IndexService : IIndexService
{
    private const long StatusThrottleMs = 250;

    private readonly IndexServiceOptions _options;
    private readonly ILogger _logger;
    private readonly ILogger _perf; // Mandrill.Perf lines (WP-2.7): build, load, apply, overflow, compaction
    private readonly BlockingCollection<Command> _queue = [];
    private readonly ManualResetEvent _resume = new(initialState: true);
    private readonly Lock _gate = new();
    private readonly Lock _pauseGate = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ChangeTracker _changes = new();
    private readonly ChangeApplier _applier;
    private readonly string[] _ignored;
    private readonly CancellationTokenSource _stopCts = new();

    // Index-thread state (the restart timer touches it only while no index thread runs).
    private readonly HashSet<string> _pendingBuilds = new(PathUtil.Comparer);
    private readonly HashSet<string> _dirty = new(PathUtil.Comparer);
    private readonly Dictionary<string, IDisposable> _watches = new(PathUtil.Comparer);
    private readonly HashSet<string> _pendingCompactions = new(PathUtil.Comparer);
    private readonly HashSet<string> _overflowPending = new(PathUtil.Comparer);
    private readonly Dictionary<string, DateTime> _lastOverflowVerify = new(PathUtil.Comparer);
    private readonly Dictionary<string, string[]> _segmentExclude = new(PathUtil.Comparer); // what each segment was built with
    private Dictionary<string, DriveIndexInfo> _manifest = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;
    private bool _saveFailed;
    private int? _buildPercent;
    private string? _errorMessage;
    private DateTime? _lastUpdatedUtc;
    private long _lastEventTicks;
    private string[] _appliedExclude = [];
    private int _appliedRescanHours;
    private DateTime _lastApplyUtc;
    private string? _holdRoot; // a building drive whose changes wait for the build (its base is still being written)
    private HashSet<string>? _replay; // paths applied during a verify, replayed onto the new segment

    // Shared state.
    private DriveSegment[] _segments = [];
    private IndexStatus _status = new(IndexState.Loading, 0, null, null, false, null);
    private volatile CancellationTokenSource? _buildCts;
    private volatile string? _buildRoot;
    private volatile bool _failed;
    private int _failures;
    private int _applyQueued;

    // Guarded by _pauseGate.
    private int _pauseCount;
    private bool _handlesReleased;

    // Guarded by _gate.
    private bool _started;
    private bool _stopping;
    private bool _threadRunning;
    private ITimer? _verifyTimer;
    private ITimer? _saveTimer;
    private ITimer? _restartTimer;
    private ITimer? _applyTimer;

    /// <summary>Creates the service; nothing runs until <see cref="Start"/>.</summary>
    /// <param name="options">Options.</param>
    public IndexService(IndexServiceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrEmpty(options.IndexDirectory);
        ArgumentNullException.ThrowIfNull(options.Drives);
        _logger = options.LoggerFactory.CreateLogger<IndexService>();
        _perf = options.LoggerFactory.CreateLogger(MandrillLogging.PerfCategory);
        _applier = new ChangeApplier(options.Source, options.Stat, _resume);
        _ignored = [.. options.IgnoredPaths.Select(p => PathUtil.TryNormalize(p, out string n, out _) ? n : null).OfType<string>()];
    }

    /// <inheritdoc/>
    public event Action<IndexStatus>? StatusChanged;

    /// <inheritdoc/>
    public IReadOnlyList<DriveSegment> Segments => Volatile.Read(ref _segments);

    /// <inheritdoc/>
    public IndexStatus Status => Volatile.Read(ref _status);

    /// <inheritdoc/>
    public bool TryGetSegment(string root, out DriveSegment? segment)
    {
        segment = Find(root);
        return segment is not null;
    }

    /// <inheritdoc/>
    public IDisposable PauseForSearch()
    {
        // Count and gate change together: an unlocked "decrement to 0 → Set" could land after "increment to 1 → Reset".
        lock (_pauseGate)
        {
            if (++_pauseCount == 1 && !_handlesReleased)
            {
                _resume.Reset();
            }
        }

        return new PauseHandle(this);
    }

    /// <inheritdoc/>
    public void ReportMissing(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Enqueue(new DeleteCommand(path));
    }

    /// <inheritdoc/>
    public void ApplySettings() => Enqueue(new SettingsCommand());

    /// <inheritdoc/>
    public void Start()
    {
        int rescanHours = SafeSettings().RescanHours; // a user callback: not under the lock
        TimeSpan rescan = rescanHours > 0 ? TimeSpan.FromHours(rescanHours) : Timeout.InfiniteTimeSpan;
        lock (_gate)
        {
            if (_started || _stopping)
            {
                return;
            }

            _started = true;
            if (_options.IncludeDrive is not null)
            {
                _logger.LogWarning(
                    "Index restricted by {Variable}={Value} (debug perf run): only the listed drives are indexed",
                    IndexDriveFilter.Variable,
                    Environment.GetEnvironmentVariable(IndexDriveFilter.Variable));
            }

            if (_options.LowLatencyGcWhileBuilding)
            {
                _logger.LogWarning("Index builds run with GC latency mode SustainedLowLatency ({Variable}, debug perf run)", IndexDriveFilter.GcVariable);
            }

            _options.Drives.Changed += OnDrivesChanged;
            Enqueue(new LoadCommand());
            _verifyTimer = _options.Time.CreateTimer(_ => Enqueue(new VerifyCommand()), null, _options.VerifyDelay, rescan);
            _saveTimer = _options.Time.CreateTimer(_ => Enqueue(new SaveCommand()), null, _options.DirtySaveInterval, _options.DirtySaveInterval);
            if (_options.ChangeFeed is not null)
            {
                _applyTimer = _options.Time.CreateTimer(_ => RequestApply(), null, _options.ApplyInterval, _options.ApplyInterval);
            }

            StartThread();
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken ct)
    {
        if (!RequestStop())
        {
            return;
        }

        try
        {
            await _stopped.Task.WaitAsync(_options.StopTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Index thread did not stop within {Ms} ms; unsaved changes may be lost", (long)_options.StopTimeout.TotalMilliseconds);
        }
    }

    /// <summary>Requests a stop without waiting (the thread saves and exits on its own; it is a background thread).</summary>
    public void Dispose() => RequestStop();

    private static void EnterBackgroundMode(ILogger logger)
    {
        // Lowers this thread's CPU, I/O and memory priority (ADR-0008). Only allowed for the current thread.
        if (!PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(), THREAD_PRIORITY.THREAD_MODE_BACKGROUND_BEGIN))
        {
            logger.LogDebug("Index thread: background mode not available");
        }
    }

    private static string LetterOf(string root) => char.ToUpperInvariant(root[0]).ToString();

    private static DateTime? LastPass(DriveIndexInfo info)
        => info.VerifiedAtUtc > info.BuiltAtUtc || info.BuiltAtUtc is null ? info.VerifiedAtUtc : info.BuiltAtUtc;

    // The entries of an exclude list that lie on one drive: what a segment of that drive can reflect.
    private static string[] Mine(IEnumerable<string> exclude, string root)
        => [.. exclude.Where(p => PathUtil.IsSameOrUnder(p, root))];

    private static int? Progress(int count, int previousItems)
        => previousItems > 0 ? Math.Clamp((int)(count * 100L / previousItems), 0, 99) : null;

    // True when there is a thread to wait for.
    private bool RequestStop()
    {
        bool threadRunning;
        lock (_gate)
        {
            if (_stopping)
            {
                return _started;
            }

            _stopping = true;
            if (!_started)
            {
                return false;
            }

            threadRunning = _threadRunning;
            _options.Drives.Changed -= OnDrivesChanged;
            _verifyTimer?.Dispose();
            _saveTimer?.Dispose();
            _restartTimer?.Dispose();
            _applyTimer?.Dispose();
        }

        CancelBuild();
        _stopCts.Cancel(); // a long overlay scan of a new folder stops too
        lock (_pauseGate)
        {
            if (!_handlesReleased)
            {
                _resume.Set(); // a paused build must see the cancellation
            }
        }

        Enqueue(new StopCommand());
        if (!threadRunning)
        {
            CloseWatches(); // no index thread runs (failed, waiting for its restart), so this thread may touch them
            _stopped.TrySetResult(); // nothing runs that could save
        }

        return true;
    }

    private void CancelBuild()
    {
        try
        {
            _buildCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The build finished in the meantime.
        }
    }

    // Called under _gate.
    private void StartThread()
    {
        _threadRunning = true;
        var thread = new Thread(Run)
        {
            Name = "Mandrill index",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    private void Enqueue(Command command)
    {
        try
        {
            _queue.Add(command);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The queue was completed (or released) by the stop; nothing more to do.
        }
    }

    // Drive monitor callbacks run on a pool thread: cancel a build of a vanished drive at once, queue the rest.
    private void OnDrivesChanged(IReadOnlyList<DriveDescriptor> drives)
    {
        if (_buildRoot is { } building && !drives.Any(d => Indexed(d) && PathUtil.PathEquals(d.Root, building)))
        {
            CancelBuild();
        }

        Enqueue(new DrivesChangedCommand(drives));
    }

    private void Run()
    {
        EnterBackgroundMode(_logger);
        try
        {
            while (true)
            {
                Command command = _queue.Take();
                if (command is StopCommand)
                {
                    CloseWatches();
                    SaveDirty();
                    _queue.CompleteAdding();
                    ReleaseHandles();
                    break;
                }

                Handle(command);
            }

            _stopped.TrySetResult();
        }
        catch (Exception ex)
        {
            OnThreadFailed(ex);
        }
    }

    private void OnThreadFailed(Exception ex)
    {
        int failures = Interlocked.Increment(ref _failures);
        _logger.LogError(ex, "Index thread failed ({Count})", failures);
        _buildRoot = null;
        _buildCts = null;
        _failed = true;
        _errorMessage = ex.Message;
        bool restart;
        lock (_gate)
        {
            _threadRunning = false;
            restart = failures == 1 && !_stopping;
            if (restart)
            {
                // Created before the Error status is visible, so the delay counts from the failure.
                _restartTimer = _options.Time.CreateTimer(_ => Restart(), null, _options.RestartDelay, Timeout.InfiniteTimeSpan);
            }
        }

        PublishStatus(force: true);
        if (!restart)
        {
            _stopped.TrySetResult(); // stays in Error; the segments stay searchable
        }
    }

    // Timer thread. The failed index thread has exited; commands still queued stay valid and run on the new thread.
    private void Restart()
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _logger.LogInformation("Restarting the index thread");
            _failed = false;
            _errorMessage = null;
            Enqueue(new RestartCommand(_options.Drives.Current));
            StartThread();
        }
    }

    private void Handle(Command command)
    {
        switch (command)
        {
            case LoadCommand:
                LoadAll();
                break;
            case BuildCommand build:
                _pendingBuilds.Remove(build.Root);
                BuildDrive(build.Root, build.Verify);
                break;
            case VerifyCommand:
                VerifyAll();
                break;
            case RestartCommand restart:
                // A build that failed left its published segment incomplete: finish it (swapped in when done).
                ApplyDrives(restart.Drives);
                foreach (DriveSegment segment in Segments.Where(s => !s.IsComplete))
                {
                    QueueBuild(segment.Root, verify: false);
                }

                break;
            case DrivesChangedCommand changed:
                ApplyDrives(changed.Drives);
                break;
            case SaveCommand:
                SaveDirty();
                break;
            case DeleteCommand delete:
                ApplyDelete(delete.Path);
                break;
            case ApplyCommand:
                Volatile.Write(ref _applyQueued, 0);
                ApplyChanges();
                break;
            case CompactCommand compact:
                _pendingCompactions.Remove(compact.Root);
                Compact(compact.Root);
                break;
            case SettingsCommand:
                ApplySettingsNow();
                break;
        }

        bool quiet = command is BuildCommand or DeleteCommand or SaveCommand or ApplyCommand; // these publish their own changes
        PublishStatus(force: !quiet);
    }

    private void LoadAll()
    {
        _loading = true;
        try
        {
            PublishStatus(force: true);
            RemoveStaleTempFiles();
            _manifest = new Dictionary<string, DriveIndexInfo>(IndexManifest.Read(_options.IndexDirectory, _logger).Drives, StringComparer.OrdinalIgnoreCase);
            _lastUpdatedUtc = _manifest.Values.Select(LastPass).Max();
            _appliedExclude = ExcludeList();
            _appliedRescanHours = SafeSettings().RescanHours;
            foreach (DriveDescriptor drive in _options.Drives.Current.Where(Indexed))
            {
                LoadOrQueue(drive.Root);
            }
        }
        finally
        {
            _loading = false; // a failure here must not leave the restarted service showing Loading
        }
    }

    // A saved index loads at once (searchable before any rescan, AC-2.6); anything unusable is discarded and rebuilt.
    private void LoadOrQueue(string root)
    {
        if (Find(root) is not null)
        {
            return;
        }

        uint? serial = SafeSerial(root);
        if (serial is null)
        {
            _logger.LogInformation("Index: {Root} is not ready; not indexed now", root);
            return;
        }

        Watch(root); // before the load or build, so nothing that changes meanwhile is missed
        string letter = LetterOf(root);
        if (_manifest.TryGetValue(letter, out DriveIndexInfo? info))
        {
            string file = Path.Combine(_options.IndexDirectory, info.File);
            if (info.VolumeSerial == serial)
            {
                long loadStart = Stopwatch.GetTimestamp();
                IndexLoadResult loaded = IndexFile.Load(file, serial);
                if (loaded.Segment is { } segment && PathUtil.PathEquals(segment.Root, root))
                {
                    _perf.LogInformation(
                        "Index load {Root}: {Items} items in {Ms} ms, segment {SegmentMiB:F1} MiB, working set {WorkingSetMiB} MiB",
                        root,
                        segment.Count,
                        (long)Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds,
                        segment.ByteSize / 1048576.0,
                        Environment.WorkingSet >> 20);
                    Publish(segment);
                    _segmentExclude[root] = Mine(_appliedExclude, root); // saved with the list of that time; a settings change re-diffs
                    _logger.LogInformation("Index {Root} loaded: {Items} items, complete {Complete}", root, segment.Count, segment.IsComplete);
                    if (!segment.IsComplete)
                    {
                        QueueBuild(root, verify: false);
                    }

                    return;
                }

                _logger.LogWarning("Index file {File} discarded ({Reason}); rebuilding {Root}", info.File, loaded.Failure, root);
            }
            else
            {
                _logger.LogInformation("Index {Root}: volume serial changed; rebuilding", root);
            }

            TryDelete(file);
            _manifest.Remove(letter);
        }

        QueueBuild(root, verify: false);
    }

    // Every drive is rebuilt into a new segment and swapped in (an incomplete one is completed). A complete drive built or
    // verified less than VerifyDelay ago is skipped: a first build that just finished needs no second pass. Drives
    // without a segment are loaded or built.
    private void VerifyAll()
    {
        DateTime now = _options.Time.GetUtcNow().UtcDateTime;
        foreach (DriveSegment segment in Segments)
        {
            DateTime? last = _manifest.TryGetValue(LetterOf(segment.Root), out DriveIndexInfo? info) ? LastPass(info) : null;
            if (segment.IsComplete && last is { } at && now - at < _options.VerifyDelay)
            {
                continue;
            }

            QueueBuild(segment.Root, verify: segment.IsComplete);
        }

        // A fixed drive that was not ready (e.g. locked) when it arrived is tried again (published roots are skipped).
        foreach (DriveDescriptor drive in _options.Drives.Current.Where(Indexed))
        {
            LoadOrQueue(drive.Root);
        }
    }

    private void QueueBuild(string root, bool verify)
    {
        if (_pendingBuilds.Add(root))
        {
            Enqueue(new BuildCommand(root, verify));
        }
    }

    private void BuildDrive(string root, bool verify)
    {
        if (!IsFixedDrive(root) || SafeSerial(root) is not { } serial)
        {
            return; // removed (or not ready) since it was queued
        }

        // A first build is searchable while it fills. A rebuild of a loaded partial takes over once it holds more items,
        // so its checkpoints save and a second kill keeps this session's progress. A verify copy stays hidden until done.
        DriveSegment? existing = Find(root);
        var segment = new DriveSegment(root, serial);
        int takeOverAt = existing is null ? 0 : existing.IsComplete ? int.MaxValue : existing.Count + 1;
        bool published = false;
        void TakeOver()
        {
            if (!published)
            {
                if (existing is null)
                {
                    Publish(segment);
                }
                else
                {
                    Replace(existing, segment);
                }

                published = true;
            }
        }

        if (takeOverAt == 0)
        {
            TakeOver();
        }

        // Changes of a drive whose base is being written wait for the build; a verify keeps the old segment current and
        // replays the same paths onto the new one after the swap (§8.5 WP-2.4).
        bool hidden = takeOverAt == int.MaxValue;
        _holdRoot = hidden ? null : root;
        _replay = hidden ? new HashSet<string>(PathUtil.Comparer) : null;

        string letter = LetterOf(root);
        DriveIndexInfo? before = _manifest.GetValueOrDefault(letter);
        int previousItems = before?.Items ?? 0;
        var cts = new CancellationTokenSource();
        DateTime lastSave = _options.Time.GetUtcNow().UtcDateTime;
        int lastSavedCount = 0;
        _buildPercent = Progress(0, previousItems);
        _buildCts = cts;
        _buildRoot = root;
        lock (_gate)
        {
            if (_stopping)
            {
                cts.Cancel(); // the stop may have run before _buildCts was set
            }
        }

        if (!IsFixedDrive(root))
        {
            cts.Cancel(); // removed after the first check, before the monitor callback could see _buildRoot
        }

        // The current list, not the applied one: a SettingsCommand still queued behind this build then finds this
        // segment already built with it (it diffs per segment) and neither re-excludes nor rebuilds it.
        string[] buildExclude = ExcludeList();
        var builder = new SegmentBuilder(_options.Source, new IndexBuildOptions
        {
            Exclude = buildExclude,
            CheckpointEvery = _options.CheckpointEvery,
            ThrottleSleep = _options.ThrottleSleep,
            Resume = _resume,
            Checkpoint = s =>
            {
                _buildPercent = Progress(s.Count, previousItems);
                PublishStatus(force: false);
                DateTime now = _options.Time.GetUtcNow().UtcDateTime;
                if (!published && s.Count >= takeOverAt)
                {
                    TakeOver();
                }

                if (published && (now - lastSave >= _options.BuildSaveInterval || s.Count - lastSavedCount >= _options.BuildSaveItems))
                {
                    SaveSegment(s); // partial save: progress survives a kill (AC-2.5, E-2.3)
                    lastSave = now;
                    lastSavedCount = s.Count;
                }

                if (now - _lastApplyUtc >= _options.ApplyInterval)
                {
                    ApplyChanges(); // AC-2.7 holds during long builds of other drives and verifies
                }
            },
        }, _options.LoggerFactory.CreateLogger<SegmentBuilder>());

        PublishStatus(force: true);
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        int gen2Before = GC.CollectionCount(2);
        GCLatencyMode latency = GCSettings.LatencyMode;
        if (_options.LowLatencyGcWhileBuilding)
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        }

        try
        {
            IndexBuildResult result = builder.Build(segment, cts.Token);
            BuildPerf(root, verify, result, segment, GC.GetTotalPauseDuration() - pauseBefore, GC.CollectionCount(2) - gen2Before);
            TakeOver();
            if (_replay is { Count: > 0 } replay)
            {
                ApplyPaths(segment, replay);
            }

            _segmentExclude[root] = Mine(buildExclude, root);

            DateTime now = _options.Time.GetUtcNow().UtcDateTime;
            _manifest[letter] = new DriveIndexInfo(
                IndexFile.FileNameFor(root, serial),
                serial,
                result.Complete,
                verify ? before?.BuiltAtUtc ?? now : now,
                verify ? now : before?.VerifiedAtUtc,
                segment.Count);
            _lastUpdatedUtc = now;
            SaveSegment(segment);
            _logger.LogInformation("Index {Root} {Kind}: {Items} items in {Ms} ms", root, verify ? "verified" : "built", result.Items, (long)result.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Stopped or the drive vanished: a published partial segment is saved by the stop (or the removal); a
            // hidden copy is dropped.
            if (published && Find(root) == segment)
            {
                _dirty.Add(root);
                _segmentExclude[root] = Mine(buildExclude, root);
            }

            _logger.LogInformation("Index {Root}: build cancelled at {Items} items", root, segment.Count);
        }
        finally
        {
            if (_options.LowLatencyGcWhileBuilding)
            {
                GCSettings.LatencyMode = latency; // only what this build changed: the app may set a mode of its own
            }

            _buildRoot = null;
            _buildCts = null;
            _buildPercent = null;
            _holdRoot = null;
            _replay = null;
            cts.Dispose();
        }

        PublishStatus(force: true);
    }

    // R6/R15 inputs: build rate, GC pauses during the build, memory.
    private void BuildPerf(string root, bool verify, IndexBuildResult result, DriveSegment segment, TimeSpan gcPause, int gen2)
    {
        double seconds = Math.Max(result.Elapsed.TotalSeconds, 0.001);
        _perf.LogInformation(
            "Index build {Root} ({Kind}): {Items} items, {Directories} dirs, {Ms} ms, {Rate:F0} items/s, complete {Complete}, GC pause {PauseMs} ms, gen2 {Gen2}, latency mode {Mode}, segment {SegmentMiB:F1} MiB, managed {ManagedMiB} MiB, working set {WorkingSetMiB} MiB",
            root,
            verify ? "verify" : "build",
            result.Items,
            result.Directories,
            (long)result.Elapsed.TotalMilliseconds,
            result.Items / seconds,
            result.Complete,
            (long)gcPause.TotalMilliseconds,
            gen2,
            GCSettings.LatencyMode,
            segment.ByteSize / 1048576.0,
            GC.GetTotalMemory(forceFullCollection: false) >> 20,
            Environment.WorkingSet >> 20);
    }

    private void ApplyDrives(IReadOnlyList<DriveDescriptor> drives)
    {
        var fixedRoots = new HashSet<string>(drives.Where(Indexed).Select(d => d.Root), PathUtil.Comparer);
        foreach (DriveSegment segment in Segments.Where(s => !fixedRoots.Contains(s.Root)).ToList())
        {
            if (_dirty.Contains(segment.Root))
            {
                SaveSegment(segment); // keep a partial build or applied deletes for when the drive comes back
            }

            // AC-2.11: the entries become unsearchable; the file stays.
            Unpublish(segment);
            _dirty.Remove(segment.Root);
            _segmentExclude.Remove(segment.Root);
            Unwatch(segment.Root);
            _logger.LogInformation("Index {Root}: drive removed; unloaded (file kept)", segment.Root);
        }

        foreach (string root in fixedRoots)
        {
            LoadOrQueue(root);
        }
    }

    // Timer thread: one ApplyCommand in the queue at a time (a busy thread applies at its build checkpoints anyway).
    private void RequestApply()
    {
        if (Interlocked.Exchange(ref _applyQueued, 1) == 0)
        {
            Enqueue(new ApplyCommand());
        }
    }

    private void Watch(string root)
    {
        if (_options.ChangeFeed is not { } feed || _watches.ContainsKey(root))
        {
            return;
        }

        try
        {
            // Pool-thread callbacks: they only record (AC-2.7); the index thread applies.
            _watches[root] = feed.WatchRoot(root, changes => _changes.Add(root, changes), () => _changes.Overflow(root));
        }
        catch (Exception ex) when (ex is ArgumentException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Index {Root}: change tracking unavailable; only rescans update it", root);
        }
    }

    private void Unwatch(string root)
    {
        if (_watches.Remove(root, out IDisposable? watch))
        {
            DisposeQuietly(watch);
        }

        _changes.Forget(root);
        _overflowPending.Remove(root);
    }

    private void CloseWatches()
    {
        foreach (IDisposable watch in _watches.Values)
        {
            DisposeQuietly(watch);
        }

        _watches.Clear();
    }

    private void DisposeQuietly(IDisposable watch)
    {
        try
        {
            watch.Dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
        {
            _logger.LogDebug(ex, "Index: closing a watch failed");
        }
    }

    // Every ApplyInterval (timer or build checkpoint): dirty paths → stat → upsert/delete (§8.3), then overflow verifies.
    private void ApplyChanges()
    {
        _lastApplyUtc = _options.Time.GetUtcNow().UtcDateTime;
        foreach (string root in _changes.TakeOverflows())
        {
            _perf.LogInformation("Index overflow {Root}: changes dropped, a verify follows (at most every {Minutes} min)", root, (int)_options.OverflowVerifyInterval.TotalMinutes);
            _overflowPending.Add(root);
        }

        bool any = false;
        foreach (string root in _changes.DirtyRoots())
        {
            if (PathUtil.PathEquals(root, _holdRoot))
            {
                continue; // applied when its build ends (accepted against AC-2.7, §8.5 WP-2.4)
            }

            if (Find(root) is not { } segment)
            {
                if (!_pendingBuilds.Contains(root))
                {
                    _changes.Forget(root); // not indexed (not ready, or removed)
                }

                continue; // a queued first build picks them up when it ends
            }

            List<string> paths = _changes.Take(root, out long firstSeen);
            if (PathUtil.PathEquals(root, _buildRoot))
            {
                _replay?.UnionWith(paths);
            }

            long applyStart = Stopwatch.GetTimestamp();
            any |= ApplyPaths(segment, paths, firstSeen);
            if (paths.Count > 0)
            {
                // AC-2.7: the oldest change of the batch, from its first event to applied (searchable).
                _perf.LogInformation(
                    "Index apply {Root}: {Paths} paths in {Ms} ms, oldest change {AgeMs} ms",
                    root,
                    paths.Count,
                    (long)Stopwatch.GetElapsedTime(applyStart).TotalMilliseconds,
                    firstSeen == 0 ? -1 : (long)Stopwatch.GetElapsedTime(firstSeen).TotalMilliseconds);
            }
        }

        VerifyOverflows();
        if (any)
        {
            PublishStatus(force: false);
        }
    }

    // firstSeen: stamp of the batch's oldest change, kept for paths that go back for a retry (0 for a replay).
    private bool ApplyPaths(DriveSegment segment, IReadOnlyCollection<string> paths, long firstSeen = 0)
    {
        bool changed;
        var unknown = new List<string>();
        try
        {
            // Filtered with the applied list. Between a build that already used a newer list and its SettingsCommand, a
            // change under a just-excluded folder can land in the overlay below the Excluded folder; compaction drops it.
            changed = _applier.Apply(segment, paths, _ignored, _appliedExclude, _stopCts.Token, unknown);
        }
        catch (OperationCanceledException)
        {
            return false; // stopping; the next start verifies anyway
        }

        int dropped = _changes.Retry(segment.Root, unknown, unknown.Count == 0 ? paths : paths.Except(unknown, PathUtil.Comparer), firstSeen);
        if (dropped > 0)
        {
            _logger.LogDebug("Index {Root}: {Count} changed path(s) could not be read after {Tries} tries; the next verify catches them", segment.Root, dropped, ChangeTracker.MaxRetries);
        }

        if (changed)
        {
            MarkChanged(segment);
        }

        return changed;
    }

    private void MarkChanged(DriveSegment segment)
    {
        _dirty.Add(segment.Root);
        _lastUpdatedUtc = _options.Time.GetUtcNow().UtcDateTime;
        if (segment.IsComplete && segment.NeedsCompaction && !PathUtil.PathEquals(segment.Root, _buildRoot) && !_pendingBuilds.Contains(segment.Root)
            && _pendingCompactions.Add(segment.Root))
        {
            Enqueue(new CompactCommand(segment.Root));
        }
    }

    // The watcher lost events: verify that drive, at most once per OverflowVerifyInterval; a queued build covers it, a
    // running one does not (it may have listed the changed folders already).
    private void VerifyOverflows()
    {
        DateTime now = _options.Time.GetUtcNow().UtcDateTime;
        foreach (string root in _overflowPending.ToList())
        {
            if (PathUtil.PathEquals(root, _buildRoot))
            {
                continue;
            }

            if (Find(root) is not { } segment || _pendingBuilds.Contains(root))
            {
                _overflowPending.Remove(root);
                continue;
            }

            if (_lastOverflowVerify.TryGetValue(root, out DateTime last) && now - last < _options.OverflowVerifyInterval)
            {
                continue;
            }

            _overflowPending.Remove(root);
            _lastOverflowVerify[root] = now;
            _logger.LogInformation("Index {Root}: the change watcher overflowed; verifying the drive", root);
            QueueBuild(root, verify: segment.IsComplete);
        }
    }

    // Overlay > 50k or dead > 10 % (§8.3): rebuild in memory from base − dead + overlay, then swap.
    private void Compact(string root)
    {
        if (Find(root) is not { IsComplete: true, NeedsCompaction: true } segment || _pendingBuilds.Contains(root))
        {
            return; // gone, clean, or about to be rebuilt anyway
        }

        try
        {
            // The segment's own list: compaction must not apply a settings change that its SettingsCommand applies later.
            string[] exclude = _segmentExclude.GetValueOrDefault(root) ?? _appliedExclude;
            var options = new IndexBuildOptions { Exclude = exclude, ThrottleSleep = TimeSpan.Zero, Resume = _resume };
            var (compacted, result) = SegmentCompactor.Compact(segment, options, _options.LoggerFactory.CreateLogger<SegmentBuilder>(), _stopCts.Token);
            Replace(segment, compacted);
            _dirty.Add(root);
            _logger.LogInformation("Index {Root} compacted: {Before} → {After} items in {Ms} ms", root, segment.Count, compacted.Count, (long)result.Elapsed.TotalMilliseconds);
            _perf.LogInformation("Index compaction {Root}: {Before} → {After} items in {Ms} ms", root, segment.Count, compacted.Count, (long)result.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Stopping: the old segment stays.
        }
    }

    // AC-2.10 at runtime: an added exclude removes that subtree (the folder stays, marked); a removed one schedules a
    // scan of its drive. A RescanHours change re-arms the verify timer.
    private void ApplySettingsNow()
    {
        string[] exclude = ExcludeList();
        _appliedExclude = exclude;
        int added = 0, rescans = 0;
        foreach (DriveSegment segment in Segments)
        {
            // Diffed against what this segment was built with, not against the previous settings (a build may already
            // have used the new list).
            string[] built = _segmentExclude.GetValueOrDefault(segment.Root) ?? [];
            string[] mine = Mine(exclude, segment.Root);
            foreach (string path in mine.Except(built, PathUtil.Comparer))
            {
                ExcludeSubtree(segment, path);
                added++;
            }

            if (built.Any(p => !mine.Contains(p, PathUtil.Comparer)))
            {
                QueueBuild(segment.Root, verify: segment.IsComplete); // "removing schedules a scan" (AC-2.10)
                rescans++;
            }

            // Removed folders stay listed until the queued rebuild replaces this segment.
            _segmentExclude[segment.Root] = [.. built.Union(mine, PathUtil.Comparer)];
        }

        int hours = SafeSettings().RescanHours;
        if (hours != _appliedRescanHours)
        {
            _appliedRescanHours = hours;
            TimeSpan period = hours > 0 ? TimeSpan.FromHours(hours) : Timeout.InfiniteTimeSpan;
            lock (_gate)
            {
                if (!_stopping)
                {
                    _verifyTimer?.Change(period, period);
                }
            }
        }

        _logger.LogInformation("Index settings applied: {Added} folder(s) excluded, {Rescans} drive rescan(s) queued, rescan every {Hours} h", added, rescans, hours);
    }

    // The folder stays recorded with its mark (§8.3 build rules); only what lies below it goes. An unreadable folder keeps
    // its old metadata; a folder whose parent is not indexed (itself excluded, or not built yet) is not added as an orphan.
    private void ExcludeSubtree(DriveSegment segment, string path)
    {
        if (PathUtil.IsDriveRoot(path) || PathUtil.Parent(path) is not { } parent)
        {
            return;
        }

        int item = segment.Lookup(path);
        bool wasLive = item > 0 && (segment.FlagsOf(item) & IndexFlags.Dead) == 0;
        long size = wasLive ? segment.SizeOf(item) : 0;
        uint mtime = wasLive ? segment.MtimeOf(item) : 0;
        IndexFlags flags = wasLive ? segment.FlagsOf(item) : IndexFlags.Directory;
        bool changed = segment.Delete(path);
        PathStat found = _options.Stat(path);
        bool keep = found.State == PathState.Present || (found.State == PathState.Unknown && wasLive);
        if (found.State == PathState.Present)
        {
            (size, mtime, flags) = (found.Size, found.Mtime, found.Flags);
        }

        if (keep && (flags & IndexFlags.Directory) != 0 && ChangeApplier.IsKnown(segment, parent))
        {
            segment.Upsert(path, size, mtime, flags | IndexFlags.Excluded);
            changed = true;
        }

        if (changed)
        {
            MarkChanged(segment);
        }
    }

    private void ApplyDelete(string path)
    {
        if (PathUtil.TryNormalize(path, out string normalized, out _)
            && Segments.FirstOrDefault(s => PathUtil.IsSameOrUnder(normalized, s.Root)) is { } segment
            && segment.Delete(normalized))
        {
            _dirty.Add(segment.Root);
            _lastUpdatedUtc = _options.Time.GetUtcNow().UtcDateTime;
        }
    }

    private void SaveDirty()
    {
        foreach (string root in _dirty.ToList())
        {
            if (Find(root) is { } segment)
            {
                SaveSegment(segment);
            }
            else
            {
                _dirty.Remove(root);
            }
        }
    }

    // Drive file first, manifest last (§8.3). A failure keeps everything in memory and is retried at the next save.
    private void SaveSegment(DriveSegment segment)
    {
        string letter = LetterOf(segment.Root);
        string fileName = IndexFile.FileNameFor(segment.Root, segment.VolumeSerial);
        try
        {
            Directory.CreateDirectory(PathUtil.ToIo(_options.IndexDirectory));
            IndexFile.Save(segment, Path.Combine(_options.IndexDirectory, fileName));
            DriveIndexInfo? old = _manifest.GetValueOrDefault(letter);
            _manifest[letter] = new DriveIndexInfo(fileName, segment.VolumeSerial, segment.IsComplete, old?.BuiltAtUtc, old?.VerifiedAtUtc, segment.Count);
            IndexManifest.Write(_options.IndexDirectory, new IndexManifest(_manifest));
            _dirty.Remove(segment.Root);
            if (_saveFailed)
            {
                _saveFailed = false;
                _logger.LogInformation("Index files are written again");
                PublishStatus(force: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dirty.Add(segment.Root);
            if (!_saveFailed)
            {
                _saveFailed = true;
                _logger.LogWarning(ex, "Writing the index failed; it stays in memory and the write is retried");
                PublishStatus(force: true);
            }
        }
    }

    private void RemoveStaleTempFiles()
    {
        try
        {
            string directory = PathUtil.ToIo(_options.IndexDirectory);
            if (Directory.Exists(directory))
            {
                foreach (string temp in Directory.EnumerateFiles(directory, "*.tmp"))
                {
                    TryDelete(temp);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Index: stale temp files not removed");
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(PathUtil.ToIo(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Index: {File} not deleted", file);
        }
    }

    private uint? SafeSerial(string root)
    {
        try
        {
            return _options.VolumeSerial(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Index: volume serial of {Root} unavailable", root);
            return null;
        }
    }

    private IndexSettings SafeSettings()
    {
        try
        {
            return _options.Settings() ?? IndexSettings.Default;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Index settings unavailable; using the defaults");
            return IndexSettings.Default;
        }
    }

    private string[] ExcludeList()
    {
        var list = new List<string>();
        foreach (string path in SafeSettings().Exclude ?? [])
        {
            if (PathUtil.TryNormalize(path, out string normalized, out _))
            {
                list.Add(normalized);
            }
        }

        return [.. list];
    }

    private bool IsFixedDrive(string root)
        => _options.Drives.Current.Any(d => Indexed(d) && PathUtil.PathEquals(d.Root, root));

    // A fixed drive (AC-2.1), unless a perf run restricts the set (IndexDriveFilter).
    private bool Indexed(DriveDescriptor drive)
        => drive.Kind == DriveKind.Fixed && (_options.IncludeDrive?.Invoke(drive.Root) ?? true);

    private bool PauseHeld()
    {
        lock (_pauseGate)
        {
            return _pauseCount > 0;
        }
    }

    private DriveSegment? Find(string root)
    {
        foreach (DriveSegment segment in Segments)
        {
            if (PathUtil.PathEquals(segment.Root, root))
            {
                return segment;
            }
        }

        return null;
    }

    private void Publish(DriveSegment segment)
        => Volatile.Write(ref _segments, [.. Segments.Where(s => !PathUtil.PathEquals(s.Root, segment.Root)), segment]);

    private void Replace(DriveSegment old, DriveSegment fresh)
        => Volatile.Write(ref _segments, [.. Segments.Select(s => ReferenceEquals(s, old) ? fresh : s)]);

    private void Unpublish(DriveSegment segment)
        => Volatile.Write(ref _segments, [.. Segments.Where(s => !ReferenceEquals(s, segment))]);

    // Index thread. Status is always current; the event is throttled to 4 per second while the state stays the same.
    private void PublishStatus(bool force)
    {
        IndexState state = _failed ? IndexState.Error
            : _buildRoot is not null ? (PauseHeld() ? IndexState.Paused : IndexState.Scanning)
            : _loading ? IndexState.Loading
            : _pendingBuilds.Count > 0 ? IndexState.Scanning
            : IndexState.Ready;
        string? message = _failed ? _errorMessage : _buildRoot;
        var status = new IndexStatus(state, Segments.Sum(s => (long)s.Count), _buildRoot is null ? null : _buildPercent, _lastUpdatedUtc, _saveFailed, message);
        IndexStatus previous = Volatile.Read(ref _status);
        Volatile.Write(ref _status, status);
        long now = Environment.TickCount64;
        if (status == previous || (!force && status.State == previous.State && now - _lastEventTicks < StatusThrottleMs))
        {
            return;
        }

        _lastEventTicks = now;
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An index status handler failed");
        }
    }

    private void EndPause()
    {
        lock (_pauseGate)
        {
            if (--_pauseCount == 0 && !_handlesReleased)
            {
                _resume.Set();
            }
        }
    }

    // Index thread, after a clean stop: nothing waits on the gate or takes from the queue any more.
    private void ReleaseHandles()
    {
        lock (_pauseGate)
        {
            _handlesReleased = true;
            _resume.Dispose();
        }

        _queue.Dispose();
        _stopCts.Dispose();
    }

    private abstract record Command;

    private sealed record LoadCommand : Command;

    private sealed record BuildCommand(string Root, bool Verify) : Command;

    private sealed record VerifyCommand : Command;

    private sealed record RestartCommand(IReadOnlyList<DriveDescriptor> Drives) : Command;

    private sealed record DrivesChangedCommand(IReadOnlyList<DriveDescriptor> Drives) : Command;

    private sealed record SaveCommand : Command;

    private sealed record DeleteCommand(string Path) : Command;

    private sealed record StopCommand : Command;

    private sealed record ApplyCommand : Command;

    private sealed record CompactCommand(string Root) : Command;

    private sealed record SettingsCommand : Command;

    private sealed class PauseHandle(IndexService owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndPause();
            }
        }
    }
}
