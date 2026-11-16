using System.ComponentModel;
using Mandrill.Core.IO;
using Mandrill.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Mandrill.Core.Watching;

/// <summary>
/// One recursive watcher per fixed drive root in use, shared by all subscribers (ADR-0003). Events are queued per root and
/// fanned out by parent folder once per 250 ms window. Folders on non-fixed drives are polled instead (no handle, so eject
/// stays possible). Thread-safe; subscribing does no I/O on the caller's thread.
/// </summary>
public sealed class ChangeFeed : IChangeFeed, IDisposable
{
    /// <summary>Coalescing window (AC-1.15: refresh within 1 s).</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

    /// <summary>Above this many distinct names per folder and window, subscribers re-list instead (architecture §7).</summary>
    internal const int MaxNames = 200;

    /// <summary>Queued events per root and window before the window is treated as an overflow (bounds memory).</summary>
    internal const int MaxPendingEvents = 10_000;

    /// <summary>A root watcher outlives its last subscriber this long, so dispose-then-subscribe navigation does not churn handles.</summary>
    internal static readonly TimeSpan IdleLinger = TimeSpan.FromSeconds(10);

    /// <summary>Minimum polling interval for non-fixed drives.</summary>
    internal static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Polling interval = this × the last scan duration (at least <see cref="MinPollInterval"/>).</summary>
    internal const int PollFactor = 10;

    /// <summary>Restart delays after a watcher failure; the last one repeats.</summary>
    internal static readonly TimeSpan[] RestartDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)];

    private readonly IDriveMonitor _drives;
    private readonly IRootWatcherFactory _factory;
    private readonly TimeProvider _time;
    private readonly Action<Action> _runOnPool;
    private readonly ILogger _logger;
    private readonly Func<string, string?> _finalPath;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RootState> _roots = new(PathUtil.Comparer);
    private readonly HashSet<DirSubscription> _polled = [];
    private volatile bool _disposed;

    /// <summary>Creates the feed with FileSystemWatcher-based root watchers.</summary>
    /// <param name="drives">Drive list (decides fixed vs. polled; removal stops a root's watcher).</param>
    /// <param name="logger">Logger.</param>
    public ChangeFeed(IDriveMonitor drives, ILogger<ChangeFeed> logger)
        : this(drives, new FileSystemRootWatcherFactory(), TimeProvider.System, static a => ThreadPool.QueueUserWorkItem(_ => a()), logger)
    {
    }

    /// <summary>Creates the feed with explicit seams (tests).</summary>
    internal ChangeFeed(IDriveMonitor drives, IRootWatcherFactory factory, TimeProvider timeProvider, Action<Action> runOnPool, ILogger logger, Func<string, string?>? finalPath = null)
    {
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(runOnPool);
        ArgumentNullException.ThrowIfNull(logger);
        _drives = drives;
        _factory = factory;
        _time = timeProvider;
        _runOnPool = runOnPool;
        _logger = logger;
        _finalPath = finalPath ?? FinalPath;
        _drives.Changed += OnDrivesChanged;
    }

    /// <inheritdoc/>
    public IDisposable WatchDirectory(string dir, Action<DirChange> onChange, Func<TimeSpan> lastScanDuration)
    {
        string normalized = NormalizeDir(dir, nameof(dir));
        string root = RootOf(normalized);
        ArgumentNullException.ThrowIfNull(onChange);
        ArgumentNullException.ThrowIfNull(lastScanDuration);
        var sub = new DirSubscription(this, root, normalized, onChange, lastScanDuration);
        if (IsFixed(root))
        {
            AttachToRoot(root, r => r.Dirs.Add(sub));
            _runOnPool(() => ResolveRealDir(sub));
        }
        else
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SchedulePoll(sub);
        }

        return sub;
    }

    /// <inheritdoc/>
    public IDisposable WatchRoot(string root, Action<IReadOnlyList<RawChange>> onChanges, Action onOverflow)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!PathUtil.IsDriveRoot(root))
        {
            throw new ArgumentException($"'{root}' is not a drive root", nameof(root));
        }

        ArgumentNullException.ThrowIfNull(onChanges);
        ArgumentNullException.ThrowIfNull(onOverflow);
        string key = RootOf(root);
        var sub = new RootSubscription(this, key, onChanges, onOverflow);
        if (!IsFixed(key))
        {
            _logger.LogWarning("WatchRoot({Root}): not a fixed drive; no events will be delivered", key);
        }

        AttachToRoot(key, r => r.RootSubs.Add(sub));
        return sub;
    }

    /// <summary>Stops all watchers and timers. Later subscriptions throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        var toStop = new List<IDisposable>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (RootState r in _roots.Values)
            {
                StopWatcher(r, toStop);
                r.FlushTimer?.Dispose();
                r.RestartTimer?.Dispose();
                r.IdleTimer?.Dispose();
                foreach (DirSubscription d in r.Dirs)
                {
                    d.Disposed = true;
                }

                foreach (RootSubscription s in r.RootSubs)
                {
                    s.Disposed = true;
                }
            }

            _roots.Clear();
            foreach (DirSubscription d in _polled)
            {
                d.Disposed = true;
                d.PollTimer?.Dispose();
                d.PollTimer = null;
            }

            _polled.Clear();
        }

        _drives.Changed -= OnDrivesChanged;
        DisposeAll(toStop);
    }

    /// <summary>
    /// Events are matched by exact parent path (PathUtil.Parent form), so the folder is brought into that form:
    /// '/' → '\', no trailing '\' except on a drive root.
    /// </summary>
    internal static string NormalizeDir(string dir, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir, paramName);
        string normalized = dir.Replace('/', '\\');
        if (normalized.Length < 3 || !PathUtil.IsDriveRoot(normalized[..3]))
        {
            throw new ArgumentException($"'{dir}' is not an absolute drive-letter path", paramName);
        }

        string trimmed = normalized.TrimEnd('\\');
        return trimmed.Length < 3 ? normalized[..3] : trimmed;
    }

    // Upper-case drive root of a validated path ("c:\x" → "C:\").
    private static string RootOf(string path) => char.ToUpperInvariant(path[0]) + path[1..3];

    // Once per subscription, on the pool: a folder shown through a junction or symlink (itself or an ancestor) gets its
    // events under its real path, so the subscription matches that path too (and watches its drive). The subscriber keeps
    // its own path. No real path (gone, denied, a UNC or volume target): watched as shown, as before.
    private void ResolveRealDir(DirSubscription d)
    {
        string? real;
        try
        {
            real = _finalPath(d.Dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resolving the real path of {Dir} failed; watching it as shown", d.Dir);
            return;
        }

        if (real is null || PathUtil.PathEquals(real, d.Dir) || d.Disposed)
        {
            return;
        }

        string realRoot = RootOf(real);
        _logger.LogInformation("Watching {Dir} through its real path {Real}", d.Dir, real);
        if (realRoot == d.Root || !IsFixed(realRoot))
        {
            d.RealDir = real; // shortcut: a target on a non-fixed drive is not polled; F5 shows its changes
            return;
        }

        AttachToRoot(realRoot, r =>
        {
            if (!d.Disposed)
            {
                d.RealDir = real;
                d.RealRoot = realRoot;
                r.Dirs.Add(d);
            }
        });
    }

    // The folder's path with every junction and symlink on it resolved, or null (gone, denied, or a target without a drive
    // letter such as a UNC path or an unlettered volume, which no root watcher reports).
    private static string? FinalPath(string dir)
    {
        using SafeFileHandle handle = PInvoke.CreateFile(
            PathUtil.ToIo(dir),
            0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS,
            null);
        if (handle.IsInvalid)
        {
            return null;
        }

        char[] buffer = new char[1024];
        uint length = PInvoke.GetFinalPathNameByHandle(handle, buffer, GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
        if (length >= buffer.Length)
        {
            buffer = new char[length];
            length = PInvoke.GetFinalPathNameByHandle(handle, buffer, GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
        }

        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        string path = new(buffer, 0, (int)length);
        path = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        return path.Length >= 3 && PathUtil.IsDriveRoot(path[..3]) ? NormalizeDir(path, nameof(dir)) : null;
    }

    private bool IsFixed(string root)
        => _drives.Current.Any(d => d.Kind == DriveKind.Fixed && PathUtil.PathEquals(d.Root, root));

    private void AttachToRoot(string root, Action<RootState> add)
    {
        bool start;
        RootState r;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_roots.TryGetValue(root, out r!))
            {
                r = new RootState(root);
                _roots.Add(root, r);
            }

            add(r);
            r.IdleTimer?.Dispose();
            r.IdleTimer = null;
            start = r.Lease is null && !r.Starting && r.RestartTimer is null;
            if (start)
            {
                r.Starting = true;
            }
        }

        if (start)
        {
            _runOnPool(() => StartWatcher(r));
        }
    }

    // Runs on the pool: opening the root handle is file-system I/O.
    private void StartWatcher(RootState r)
    {
        lock (_gate)
        {
            if (_disposed || !r.HasSubscribers || !IsFixed(r.Root))
            {
                r.Starting = false;
                return;
            }
        }

        var lease = new Lease();
        IDisposable? watcher = null;
        Exception? error = null;
        try
        {
            watcher = _factory.Start(r.Root, c => OnRaw(r, lease, c), ex => OnError(r, lease, ex));
        }
        catch (Exception ex)
        {
            // Any failure here (missing root, access denied, Win32 errors) means "retry later"; this runs on a bare pool thread.
            error = ex;
        }

        bool keep = false;
        lock (_gate)
        {
            r.Starting = false;

            // !lease.Active: the watcher reported a failure before we got here (OnError already re-listed and scheduled a restart).
            bool diedDuringStart = watcher is not null && !lease.Active;
            if (watcher is not null && lease.Active && !_disposed && r.HasSubscribers)
            {
                keep = true;
                lease.Watcher = watcher;
                r.Lease = lease;
                if (r.RelistOnStart)
                {
                    // Events were missed while the watcher was down: everyone re-lists.
                    MarkOverflow(r);
                    r.RelistOnStart = false;
                }

                // Backoff is per failure episode.
                r.RestartAttempt = 0;
            }
            else
            {
                lease.Active = false;
                if ((error is not null || diedDuringStart) && !_disposed && r.HasSubscribers)
                {
                    ScheduleRestart(r);
                }
            }
        }

        if (error is not null)
        {
            _logger.LogError(error, "Cannot watch {Root}; retrying later", r.Root);
        }
        else if (keep)
        {
            _logger.LogInformation("Watching {Root}", r.Root);
        }
        else
        {
            watcher?.Dispose();
        }
    }

    private void OnRaw(RootState r, Lease lease, RawChange change)
    {
        if (!lease.Active)
        {
            return;
        }

        bool tooMany = false;
        lock (_gate)
        {
            if (!lease.Active)
            {
                return;
            }

            if (!r.Overflow)
            {
                if (r.Pending.Count >= MaxPendingEvents)
                {
                    tooMany = true;
                    MarkOverflow(r);
                }
                else
                {
                    r.Pending.Add(change);
                    EnsureFlushTimer(r);
                }
            }
        }

        if (tooMany)
        {
            _logger.LogWarning("More than {Max} changes on {Root} within one window; subscribers re-list", MaxPendingEvents, r.Root);
        }
    }

    private void OnError(RootState r, Lease lease, Exception error)
    {
        var toStop = new List<IDisposable>();
        lock (_gate)
        {
            if (!lease.Active || _disposed)
            {
                return;
            }

            MarkOverflow(r);
            if (error is not InternalBufferOverflowException)
            {
                // Deactivate this lease even if StartWatcher has not stored it yet, so a dead watcher is never kept.
                lease.Active = false;
                StopWatcher(r, toStop);
                ScheduleRestart(r);
            }
        }

        if (error is InternalBufferOverflowException)
        {
            _logger.LogWarning("Change buffer overflow on {Root}; subscribers re-list", r.Root);
        }
        else
        {
            _logger.LogError(error, "Watcher on {Root} failed; subscribers re-list, restarting later", r.Root);
        }

        DisposeAll(toStop);
    }

    // Caller holds _gate.
    private void MarkOverflow(RootState r)
    {
        r.Overflow = true;
        r.Pending.Clear();
        EnsureFlushTimer(r);
    }

    // Caller holds _gate. Throttle, not debounce: the first event of a window arms the timer, later ones do not postpone it.
    private void EnsureFlushTimer(RootState r)
        => r.FlushTimer ??= _time.CreateTimer(_ => Flush(r), null, Window, Timeout.InfiniteTimeSpan);

    // Caller holds _gate.
    private void ScheduleRestart(RootState r)
    {
        if (r.RestartTimer is not null)
        {
            return;
        }

        TimeSpan delay = RestartDelays[Math.Min(r.RestartAttempt, RestartDelays.Length - 1)];
        r.RestartAttempt++;
        r.RelistOnStart = true;
        r.RestartTimer = _time.CreateTimer(
            _ =>
            {
                lock (_gate)
                {
                    r.RestartTimer?.Dispose();
                    r.RestartTimer = null;
                    if (_disposed || r.Lease is not null || r.Starting)
                    {
                        return;
                    }

                    r.Starting = true;
                }

                StartWatcher(r);
            },
            null,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    // Caller holds _gate. The watcher itself is disposed by the caller outside the lock.
    private static void StopWatcher(RootState r, List<IDisposable> toStop)
    {
        if (r.Lease is { } lease)
        {
            lease.Active = false;
            if (lease.Watcher is not null)
            {
                toStop.Add(lease.Watcher);
            }

            r.Lease = null;
        }
    }

    private void Flush(RootState r)
    {
        // Taken before the swap, so windows are delivered in the order they were cut. Nobody holds _gate while waiting for it.
        lock (r.DeliveryGate)
        {
            IReadOnlyList<RawChange> events;
            bool overflow;
            DirSubscription[] dirs;
            RootSubscription[] rootSubs;
            lock (_gate)
            {
                r.FlushTimer?.Dispose();
                r.FlushTimer = null;
                events = r.Pending.AsReadOnly();
                r.Pending = [];
                overflow = r.Overflow;
                r.Overflow = false;
                dirs = [.. r.Dirs];
                rootSubs = [.. r.RootSubs];
            }

            foreach (RootSubscription s in rootSubs)
            {
                if (overflow)
                {
                    Invoke(s, () => s.OnOverflow());
                }
                else if (events.Count > 0)
                {
                    Invoke(s, () => s.OnChanges(events));
                }
            }

            foreach (var (sub, change) in Coalesce(dirs, events, overflow))
            {
                Invoke(sub, () => sub.OnChange(change));
            }
        }
    }

    /// <summary>Maps one window of raw events to at most one <see cref="DirChange"/> per subscription.</summary>
    internal static List<(DirSubscription Sub, DirChange Change)> Coalesce(IReadOnlyList<DirSubscription> dirs, IReadOnlyList<RawChange> events, bool overflow)
    {
        var result = new List<(DirSubscription, DirChange)>();
        if (dirs.Count == 0)
        {
            return result;
        }

        if (overflow)
        {
            result.AddRange(dirs.Select(d => (d, new DirChange(d.Dir, null, false))));
            return result;
        }

        // Parent folder → changed names; a null set means "too many, re-list".
        var byParent = new Dictionary<string, HashSet<string>?>(PathUtil.Comparer);
        var removed = new List<string>();
        foreach (RawChange e in events)
        {
            AddName(byParent, e.FullPath);
            if (e.Kind == ChangeKind.Deleted)
            {
                removed.Add(e.FullPath);
            }
            else if (e.Kind == ChangeKind.Renamed && e.OldFullPath is not null)
            {
                AddName(byParent, e.OldFullPath);
                removed.Add(e.OldFullPath);
            }
        }

        foreach (DirSubscription d in dirs)
        {
            string? real = d.RealDir; // events inside a junction or symlink arrive under the target's path
            if (removed.Any(p => PathUtil.IsSameOrUnder(d.Dir, p) || (real is not null && PathUtil.IsSameOrUnder(real, p))))
            {
                result.Add((d, new DirChange(d.Dir, null, true)));
            }
            else if (byParent.TryGetValue(d.Dir, out HashSet<string>? names) || (real is not null && byParent.TryGetValue(real, out names)))
            {
                result.Add((d, new DirChange(d.Dir, names is null ? null : [.. names], false)));
            }
        }

        return result;
    }

    private static void AddName(Dictionary<string, HashSet<string>?> byParent, string path)
    {
        string? parent = PathUtil.Parent(path);
        if (parent is null)
        {
            return;
        }

        if (!byParent.TryGetValue(parent, out HashSet<string>? names))
        {
            names = new HashSet<string>(PathUtil.Comparer);
            byParent.Add(parent, names);
        }

        if (names is not null && names.Add(PathUtil.LeafName(path)) && names.Count > MaxNames)
        {
            byParent[parent] = null;
        }
    }

    private void Invoke(Subscription sub, Action callback)
    {
        if (sub.Disposed)
        {
            return;
        }

        try
        {
            callback();
        }
        catch (Exception ex)
        {
            // A failing subscriber must not starve the others or kill the timer thread.
            _logger.LogError(ex, "Change subscriber failed");
        }
    }

    // Called without _gate: lastScanDuration is subscriber code.
    private void SchedulePoll(DirSubscription d)
    {
        TimeSpan last;
        try
        {
            last = d.LastScanDuration();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "lastScanDuration for {Dir} failed", d.Dir);
            last = TimeSpan.Zero;
        }

        TimeSpan interval = last * PollFactor > MinPollInterval ? last * PollFactor : MinPollInterval;
        lock (_gate)
        {
            if (!d.Disposed && !_disposed)
            {
                _polled.Add(d);
                d.PollTimer = _time.CreateTimer(_ => Poll(d), null, interval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Poll(DirSubscription d)
    {
        lock (d.PollGate)
        {
            Invoke(d, () => d.OnChange(new DirChange(d.Dir, null, false)));
        }

        lock (_gate)
        {
            d.PollTimer?.Dispose();
            d.PollTimer = null;
        }

        SchedulePoll(d);
    }

    private void Unsubscribe(DirSubscription d)
    {
        lock (_gate)
        {
            d.Disposed = true;
            d.PollTimer?.Dispose();
            d.PollTimer = null;
            _polled.Remove(d);
            if (_roots.TryGetValue(d.Root, out RootState? r) && r.Dirs.Remove(d))
            {
                ScheduleIdleStop(r);
            }

            if (d.RealRoot is { } realRoot && _roots.TryGetValue(realRoot, out RootState? real) && real.Dirs.Remove(d))
            {
                ScheduleIdleStop(real);
            }
        }

        // Wait for a delivery in progress so no callback runs after Dispose returns (skipped when called from a callback).
        WaitForDelivery(d);
        if (d.RealRoot is { } other)
        {
            WaitForDelivery(other, null);
        }
    }

    private void Unsubscribe(RootSubscription s)
    {
        lock (_gate)
        {
            s.Disposed = true;
            if (_roots.TryGetValue(s.Root, out RootState? r) && r.RootSubs.Remove(s))
            {
                ScheduleIdleStop(r);
            }
        }

        WaitForDelivery(s.Root, null);
    }

    private void WaitForDelivery(DirSubscription d) => WaitForDelivery(d.Root, d.PollGate);

    // Entering and leaving a gate waits for a delivery in progress; skipped on the delivering thread itself.
    private void WaitForDelivery(string root, object? pollGate)
    {
        RootState? r;
        lock (_gate)
        {
            _roots.TryGetValue(root, out r);
        }

        if (r is not null && !Monitor.IsEntered(r.DeliveryGate))
        {
            lock (r.DeliveryGate)
            {
            }
        }

        if (pollGate is not null && !Monitor.IsEntered(pollGate))
        {
            lock (pollGate)
            {
            }
        }
    }

    // Caller holds _gate.
    private void ScheduleIdleStop(RootState r)
    {
        if (r.HasSubscribers || r.IdleTimer is not null)
        {
            return;
        }

        r.IdleTimer = _time.CreateTimer(
            _ =>
            {
                var toStop = new List<IDisposable>();
                lock (_gate)
                {
                    r.IdleTimer?.Dispose();
                    r.IdleTimer = null;
                    if (r.HasSubscribers || !_roots.Remove(r.Root))
                    {
                        return;
                    }

                    StopWatcher(r, toStop);
                    r.FlushTimer?.Dispose();
                    r.RestartTimer?.Dispose();
                }

                DisposeAll(toStop);
                _logger.LogInformation("Stopped watching {Root} (no subscribers)", r.Root);
            },
            null,
            IdleLinger,
            Timeout.InfiniteTimeSpan);
    }

    private void OnDrivesChanged(IReadOnlyList<DriveDescriptor> drives)
    {
        var toStop = new List<IDisposable>();
        var toStart = new List<RootState>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (RootState r in _roots.Values)
            {
                bool fixedPresent = drives.Any(d => d.Kind == DriveKind.Fixed && PathUtil.PathEquals(d.Root, r.Root));
                if (!fixedPresent)
                {
                    StopWatcher(r, toStop);
                    r.RestartTimer?.Dispose();
                    r.RestartTimer = null;
                }
                else if (r.Lease is null && !r.Starting && r.HasSubscribers)
                {
                    // The drive came back: start fresh and let everyone re-list.
                    r.RestartTimer?.Dispose();
                    r.RestartTimer = null;
                    r.RestartAttempt = 0;
                    r.RelistOnStart = true;
                    r.Starting = true;
                    toStart.Add(r);
                }
            }
        }

        DisposeAll(toStop);
        foreach (RootState r in toStart)
        {
            _runOnPool(() => StartWatcher(r));
        }
    }

    private void DisposeAll(List<IDisposable> items)
    {
        foreach (IDisposable item in items)
        {
            try
            {
                item.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or Win32Exception)
            {
                _logger.LogWarning(ex, "Disposing a watcher failed");
            }
        }
    }

    /// <summary>Per-root watcher, queue and timers. Mutable fields are guarded by the feed's lock.</summary>
    private sealed class RootState(string root)
    {
        public string Root { get; } = root;

        public object DeliveryGate { get; } = new();

        public List<DirSubscription> Dirs { get; } = [];

        public List<RootSubscription> RootSubs { get; } = [];

        public List<RawChange> Pending { get; set; } = [];

        public bool Overflow { get; set; }

        public Lease? Lease { get; set; }

        public bool Starting { get; set; }

        public int RestartAttempt { get; set; }

        public bool RelistOnStart { get; set; }

        public ITimer? FlushTimer { get; set; }

        public ITimer? RestartTimer { get; set; }

        public ITimer? IdleTimer { get; set; }

        public bool HasSubscribers => Dirs.Count > 0 || RootSubs.Count > 0;
    }

    /// <summary>Identifies one running watcher; callbacks from a stopped watcher are ignored.</summary>
    private sealed class Lease
    {
        public volatile bool Active = true;

        public IDisposable? Watcher { get; set; }
    }

    /// <summary>Base for subscriptions: a disposed subscription never receives another callback.</summary>
    internal abstract class Subscription : IDisposable
    {
        private volatile bool _disposed;

        public bool Disposed
        {
            get => _disposed;
            set => _disposed = value;
        }

        public abstract void Dispose();
    }

    /// <summary>A <see cref="WatchDirectory"/> subscription.</summary>
    internal sealed class DirSubscription(ChangeFeed owner, string root, string dir, Action<DirChange> onChange, Func<TimeSpan> lastScanDuration) : Subscription
    {
        public string Root { get; } = root;

        public string Dir { get; } = dir;

        // Dir with its junctions and symlinks resolved, when it differs (set once, by ResolveRealDir).
        public string? RealDir { get; set; }

        // RealDir's drive root when it is another fixed drive, whose watcher then delivers to this subscription too.
        public string? RealRoot { get; set; }

        public Action<DirChange> OnChange { get; } = onChange;

        public Func<TimeSpan> LastScanDuration { get; } = lastScanDuration;

        public object PollGate { get; } = new();

        public ITimer? PollTimer { get; set; }

        public override void Dispose()
        {
            if (!Disposed)
            {
                owner.Unsubscribe(this);
            }
        }
    }

    /// <summary>A <see cref="WatchRoot"/> subscription.</summary>
    private sealed class RootSubscription(ChangeFeed owner, string root, Action<IReadOnlyList<RawChange>> onChanges, Action onOverflow) : Subscription
    {
        public string Root { get; } = root;

        public Action<IReadOnlyList<RawChange>> OnChanges { get; } = onChanges;

        public Action OnOverflow { get; } = onOverflow;

        public override void Dispose()
        {
            if (!Disposed)
            {
                owner.Unsubscribe(this);
            }
        }
    }
}
