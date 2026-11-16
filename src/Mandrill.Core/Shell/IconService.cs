using System.Collections.Concurrent;
using Mandrill.Core.IO;
using Mandrill.Core.Platform;
using Mandrill.Core.Watching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.UI.Shell;

namespace Mandrill.Core.Shell;

/// <summary>
/// Shell icons for list rows (AC-1.14, architecture §3/§9): per type by default, per item (<see cref="IconKey.UniquePath"/>)
/// for .exe/.lnk/.ico/.url/.cur/.ani/.scr/.cpl/.msc, folders with ReadOnly/System attributes (desktop.ini icons) and
/// reparse points, which also get the shell's link overlay, and at Large and Jumbo files of a type with a thumbnail handler
/// (their shell thumbnail, else the type icon; owner #7). Per-item lookups read the item, so they run only on fixed
/// drives and for paths shorter than 260 characters; elsewhere the by-type icon is used. Misses are resolved one at a
/// time on the "icons" STA, newest request first, into a byte-bounded LRU. A lookup that throws or times out keeps the
/// placeholder for <see cref="FailureCooldown"/> and is then retried (at most <see cref="MaxAttempts"/> times, so a handler
/// that always hangs costs a bounded number of abandoned STA threads); failures are retried at once after the STA was
/// replaced. A shell that simply has no icon is cached as final. Tree nodes use <see cref="KeyForFolder"/>: fixed and
/// removable drive roots get their Shell icon by path (autorun.inf icons, as in Explorer; re-read after any drive change),
/// other drive kinds the stock icon of their kind (no media access). Lookups that end without the item's own icon
/// (failure, no icon, per-item lookup fell back to the type, type icon is the generic file icon) are logged at Debug
/// with path and extension (owner F3 diagnostics).
/// </summary>
public sealed class IconService : IIconService, IDisposable
{
    /// <summary>Default cache bound (rendered pixels).</summary>
    public const long DefaultMaxCacheBytes = 64L << 20;

    /// <summary>Lookups per key that may fail before the failure is final for the session.</summary>
    public const int MaxAttempts = 3;

    private const int EntryOverheadBytes = 96;
    private static readonly TimeSpan DefaultIconTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadyDelay = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan DriveRefresh = TimeSpan.FromSeconds(5);
    private static readonly string[] PerItemExtensions = [".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".scr", ".cpl", ".msc"];

    // Image types Windows thumbnails out of the box: thumbnailed even if the handler lookup says otherwise.
    private static readonly string[] ThumbnailExtensions = [".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp"];

    private readonly ShellSta _sta;
    private readonly TypeNameCache _types;
    private readonly ExtensionTable _extensions;
    private readonly Func<IReadOnlyList<DriveDescriptor>> _drives;
    private readonly TimeProvider _time;
    private readonly Func<IconKey, IconImage?> _resolve;
    private readonly Func<string, bool> _hasThumbnailHandler;
    private readonly TimeSpan _iconTimeout;
    private readonly long _maxBytes;
    private readonly ILogger _logger;
    private readonly HashSet<int> _perItemExtIds;

    // Extension id -> its files get thumbnails at Large and up. Seeded with the built-in image types (true) and the per-item
    // types (false: they keep their own icon, though .ico and .lnk register handlers); any other type is decided once, on
    // the STA, when its type icon is first resolved, and shows that type icon until then.
    private readonly ConcurrentDictionary<int, bool> _thumbnailTypes;
    private readonly Lock _gate = new();
    private readonly Dictionary<IconKey, LinkedListNode<CacheEntry>> _cache = [];
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<IconKey, LinkedListNode<IconKey>> _queued = [];
    private readonly LinkedList<IconKey> _queue = new();
    private readonly List<IconKey> _ready = [];
    private readonly Dictionary<IconKey, int> _failedAttempts = [];
    private readonly HashSet<IconKey> _inFlight = [];
    // Cancelled, never disposed: a pump may still read the token, and this source has no timer to release.
    private readonly CancellationTokenSource _disposed = new();
    private long _bytes;
    private bool _pumping;
    private ITimer? _flushTimer;
    private readonly IDriveMonitor? _monitor;
    private int _driveEpoch; // bumped on every drive change (under _gate): a drive-root lookup from before is not cached
    private volatile DriveKind?[]? _driveKinds;
    private long _driveKindsAt;

    // System image index of the "unknown file type" icon (STA side, diagnostics only); -1 until first needed.
    private int _genericFileIndex = -1;

    /// <summary>Creates the service.</summary>
    /// <param name="sta">The "icons" STA (shared with <paramref name="typeNames"/>).</param>
    /// <param name="typeNames">Type-name cache.</param>
    /// <param name="drives">
    /// Drive monitor whose cached list classifies drives, so lookups and keys make no system call on the UI thread; null
    /// queries GetLogicalDrives/GetDriveTypeW directly (no media access) at most every 5 s.
    /// </param>
    /// <param name="extensions">Extension table the entries' ids come from (default <see cref="ExtensionTable.Shared"/>).</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    /// <param name="maxCacheBytes">Cache bound in bytes of rendered pixels.</param>
    public IconService(
        ShellSta sta,
        TypeNameCache typeNames,
        IDriveMonitor? drives = null,
        ExtensionTable? extensions = null,
        ILoggerFactory? loggerFactory = null,
        long maxCacheBytes = DefaultMaxCacheBytes)
        : this(sta, typeNames, extensions, drives is null ? Drives.GetDrives : () => drives.Current, TimeProvider.System, loggerFactory, maxCacheBytes, monitor: drives)
    {
    }

    /// <summary>Test seam: drive list, clock, the STA-side lookup with its timeout, the drive changes to follow, and the thumbnail-handler lookup.</summary>
    internal IconService(
        ShellSta sta,
        TypeNameCache typeNames,
        ExtensionTable? extensions,
        Func<IReadOnlyList<DriveDescriptor>> drives,
        TimeProvider timeProvider,
        ILoggerFactory? loggerFactory,
        long maxCacheBytes,
        Func<IconKey, IconImage?>? resolve = null,
        TimeSpan? iconTimeout = null,
        IDriveMonitor? monitor = null,
        Func<string, bool>? hasThumbnailHandler = null)
    {
        _sta = sta ?? throw new ArgumentNullException(nameof(sta));
        _types = typeNames ?? throw new ArgumentNullException(nameof(typeNames));
        _extensions = extensions ?? ExtensionTable.Shared;
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCacheBytes);
        _maxBytes = maxCacheBytes;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<IconService>();
        _perItemExtIds = PerItemExtensions.Select(e => _extensions.IdOf("x" + e)).ToHashSet();
        _thumbnailTypes = new(_perItemExtIds.Select(id => KeyValuePair.Create(id, false))
            .Concat(ThumbnailExtensions.Select(e => KeyValuePair.Create(_extensions.IdOf("x" + e), true))));
        _resolve = resolve ?? Resolve;
        _hasThumbnailHandler = hasThumbnailHandler ?? ShellIconReader.HasThumbnailHandler;
        _iconTimeout = iconTimeout ?? DefaultIconTimeout;
        _types.TypeNamesReady += OnTypeNamesReady;
        _sta.Replaced += OnStaReplaced;
        _monitor = monitor;
        if (monitor is not null)
        {
            monitor.Changed += OnDrivesChanged;
        }
    }

    /// <summary>How long a failed lookup keeps the placeholder before the next request retries it.</summary>
    public static TimeSpan FailureCooldown { get; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public event Action<IReadOnlyList<IconKey>>? IconsReady;

    /// <inheritdoc/>
    public event Action? TypeNamesReady;

    /// <summary>Rendered bytes currently cached (diagnostics, tests).</summary>
    public long CachedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>The key <see cref="TryGet(FileEntry, string, IconSize, out IconKey, out IconImage?)"/> uses for an entry (no I/O).</summary>
    /// <param name="entry">Listed entry.</param>
    /// <param name="dir">Folder that contains it.</param>
    /// <param name="size">Image list.</param>
    /// <returns>The key.</returns>
    public IconKey KeyFor(FileEntry entry, string dir, IconSize size)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(dir);
        int extId = entry.IsDirectory ? IconKey.FolderExtId : entry.ExtId;
        // A linked image keeps the link icon with its overlay; its target may be remote or a cloud placeholder.
        bool thumbnail = !entry.IsDirectory && !entry.IsReparsePoint && IsThumbnail(extId, size);
        bool perItem = entry.IsReparsePoint
            || (entry.IsDirectory
                ? (entry.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0
                : _perItemExtIds.Contains(extId) || thumbnail);
        if (perItem)
        {
            string path = PathUtil.Combine(dir, entry.Name);
            // Shell APIs take plain paths; long ones and non-fixed drives use the by-type icon (architecture §9).
            if (path.Length < 260 && DriveKindOf(path) == DriveKind.Fixed)
            {
                return new IconKey(extId, path, size, thumbnail ? (entry.LastWriteUtcTicks, entry.Size) : null);
            }
        }

        return new IconKey(extId, null, size);
    }

    /// <inheritdoc/>
    public IconKey KeyForFolder(string path, bool perItem, IconSize size)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (PathUtil.IsThisPc(path))
        {
            return new IconKey(IconKey.ThisPcExtId, null, size);
        }

        DriveKind? kind = DriveKindOf(path);
        if (PathUtil.IsDriveRoot(path))
        {
            int stock = kind switch
            {
                DriveKind.Fixed or DriveKind.Removable => IconKey.FolderExtId,
                DriveKind.CdRom => IconKey.CdRomDriveExtId,
                DriveKind.Network => IconKey.NetworkDriveExtId,
                DriveKind.Ram => IconKey.RamDriveExtId,
                _ => IconKey.OtherDriveExtId,
            };
            return stock == IconKey.FolderExtId ? new IconKey(IconKey.FolderExtId, path, size) : new IconKey(stock, null, size);
        }

        return perItem && path.Length < 260 && kind == DriveKind.Fixed
            ? new IconKey(IconKey.FolderExtId, path, size)
            : new IconKey(IconKey.FolderExtId, null, size);
    }

    /// <inheritdoc/>
    public bool TryGet(FileEntry entry, string dir, IconSize size, out IconKey key, out IconImage? image)
    {
        key = KeyFor(entry, dir, size);
        return TryGet(key, out image);
    }

    /// <inheritdoc/>
    public bool TryGet(IconKey key, out IconImage? image)
    {
        bool startPump = false;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                if (node.Value.RetryAt is not { } retryAt || _time.GetUtcNow() < retryAt)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    image = node.Value.Image;
                    return true;
                }

                RemoveLocked(node); // cooldown over: queue a retry below
            }

            if (_inFlight.Contains(key))
            {
                image = null;
                return false; // being resolved right now; IconsReady follows
            }

            if (_queued.TryGetValue(key, out var queued))
            {
                _queue.Remove(queued);
                _queue.AddFirst(queued);
            }
            else
            {
                _queued[key] = _queue.AddFirst(key);
            }

            if (!_pumping && !_disposed.IsCancellationRequested)
            {
                _pumping = startPump = true;
            }
        }

        if (startPump)
        {
            _ = Task.Run(PumpAsync);
        }

        image = null;
        return false;
    }

    /// <inheritdoc/>
    public void SetPriority(IReadOnlyCollection<IconKey> visible)
    {
        ArgumentNullException.ThrowIfNull(visible);
        lock (_gate)
        {
            foreach (var key in visible.Reverse())
            {
                if (_queued.TryGetValue(key, out var node))
                {
                    _queue.Remove(node);
                    _queue.AddFirst(node);
                }
            }
        }
    }

    /// <inheritdoc/>
    public void Cancel(IconKey key)
    {
        lock (_gate)
        {
            if (_queued.Remove(key, out var node))
            {
                _queue.Remove(node);
                // Other waiters (the other pane, search, the tree) see the key in IconsReady and request it again.
                ReadyLocked(key);
            }
        }
    }

    /// <inheritdoc/>
    public string TypeName(FileEntry entry) => _types.Get(entry);

    /// <summary>Stops the queue (the STA and the type-name cache are owned by the caller).</summary>
    public void Dispose()
    {
        _disposed.Cancel();
        _types.TypeNamesReady -= OnTypeNamesReady;
        _sta.Replaced -= OnStaReplaced;
        if (_monitor is not null)
        {
            _monitor.Changed -= OnDrivesChanged;
        }

        lock (_gate)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
    }

    private void OnTypeNamesReady() => TypeNamesReady?.Invoke();

    // Failures recorded before the replacement may have been caused by the stuck worker: let the next request retry them.
    private void OnStaReplaced()
    {
        lock (_gate)
        {
            for (var node = _lru.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.RetryAt is not null)
                {
                    RemoveLocked(node);
                }

                node = next;
            }
        }
    }

    // A drive letter may now hold another stick: its root icon (and a failure of the old one) must be read again.
    private void OnDrivesChanged(IReadOnlyList<DriveDescriptor> drives)
    {
        _driveKinds = null;
        lock (_gate)
        {
            _driveEpoch++;
            for (var node = _lru.First; node is not null;)
            {
                var next = node.Next;
                if (IsDriveRootKey(node.Value.Key))
                {
                    RemoveLocked(node);
                }

                node = next;
            }

            foreach (IconKey key in _failedAttempts.Keys.Where(IsDriveRootKey).ToList())
            {
                _failedAttempts.Remove(key);
            }
        }
    }

    private static bool IsDriveRootKey(IconKey key) => key.UniquePath is { } path && PathUtil.IsDriveRoot(path);

    private void RemoveLocked(LinkedListNode<CacheEntry> node)
    {
        _lru.Remove(node);
        _cache.Remove(node.Value.Key);
        _bytes -= node.Value.Bytes;
    }

    // Kind of the drive a plain "X:\..." path is on; null for other paths (UNC, \\?\) and unknown letters.
    private DriveKind? DriveKindOf(string path)
    {
        if (path.Length < 3 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
        {
            return null;
        }

        long now = _time.GetTimestamp();
        var kinds = _driveKinds;
        if (kinds is null || _time.GetElapsedTime(Volatile.Read(ref _driveKindsAt), now) > DriveRefresh)
        {
            // Cheap (GetLogicalDrives/GetDriveTypeW, no media access); refreshed so a re-used letter is re-classified.
            kinds = new DriveKind?[26];
            foreach (var d in _drives())
            {
                kinds[char.ToUpperInvariant(d.Root[0]) - 'A'] = d.Kind;
            }

            _driveKinds = kinds;
            Volatile.Write(ref _driveKindsAt, now);
        }

        return kinds[char.ToUpperInvariant(path[0]) - 'A'];
    }

    private async Task PumpAsync()
    {
        try
        {
            await PumpLoopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fire-and-forget: an escaping exception would be unobserved and leave the queue stalled with _pumping set.
            _logger.LogError(ex, "Icon queue stopped unexpectedly");
            lock (_gate)
            {
                _pumping = false;
                _inFlight.Clear();
            }
        }
    }

    private async Task PumpLoopAsync()
    {
        var ct = _disposed.Token;
        while (true)
        {
            IconKey key;
            int epoch;
            lock (_gate)
            {
                epoch = _driveEpoch;
                if (_queue.First is not { } first || ct.IsCancellationRequested)
                {
                    _pumping = false;
                    return;
                }

                key = first.Value;
                _queue.RemoveFirst();
                _queued.Remove(key);
                _inFlight.Add(key);
            }

            IconImage? image = null;
            bool failed = false;
            try
            {
                image = await _sta.InvokeAsync(
                    () =>
                    {
                        DecideThumbnails(key);
                        return _resolve(key);
                    },
                    _iconTimeout,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _pumping = false;
                    _inFlight.Remove(key);
                }

                return;
            }
            catch (Exception ex)
            {
                // Timeout (STA replaced), COM failure or a disposed STA: placeholder for now, retried after the cooldown.
                _logger.LogWarning("Icon {Extension} {Size} {Path} failed: {Error}", Describe(key), key.Size, key.UniquePath ?? "(by type)", ex.Message);
                failed = true;
            }

            Store(key, image, failed, epoch);
        }
    }

    // STA thread: before a type's icon is first reported, so the rows that asked for it (IconsReady) ask again and get
    // thumbnail keys if the type has a handler.
    private void DecideThumbnails(IconKey key)
    {
        if (key.UniquePath is not null || key.ExtId <= 0 || _thumbnailTypes.ContainsKey(key.ExtId))
        {
            return;
        }

        string extension = _extensions.ExtensionOf(key.ExtId);
        bool thumbnails = _hasThumbnailHandler(extension);
        _thumbnailTypes[key.ExtId] = thumbnails;
        _logger.LogDebug("Thumbnails for {Extension}: {Decision}", extension, thumbnails ? "handler registered" : "no handler, type icon");
    }

    // STA thread.
    private IconImage? Resolve(IconKey key)
    {
        if (StockIdOf(key.ExtId) is { } stock)
        {
            return Rendered(key, ShellIconReader.LookupStock(stock));
        }

        bool folder = key.ExtId == IconKey.FolderExtId;
        string extension = folder ? string.Empty : _extensions.ExtensionOf(key.ExtId);
        IconLookup? lookup = null;
        if (key.UniquePath is not null && key.Thumbnail is not null)
        {
            // Twice the nominal size leaves room for 200 % scaling (Large: 96 px); Jumbo is 256 already.
            if (ShellIconReader.Thumbnail(key.UniquePath, Math.Min((int)key.Size * 2, (int)IconSize.Jumbo), _logger) is { } thumbnail)
            {
                return thumbnail;
            }

            // The row shows the type icon (Generic) it already holds, instead of a copy rendered per file.
            _logger.LogDebug("Icon {Path} ({Extension}) {Size}: no thumbnail, showing the type icon", key.UniquePath, Describe(key), key.Size);
            return null;
        }

        if (key.UniquePath is not null)
        {
            lookup = ShellIconReader.LookupByPath(key.UniquePath);
            if (lookup is null)
            {
                _logger.LogDebug("Icon {Path} ({Extension}): per-item lookup failed, using the type icon", key.UniquePath, Describe(key));
                if (folder && PathUtil.IsDriveRoot(key.UniquePath))
                {
                    // A drive, not a folder icon.
                    lookup = ShellIconReader.LookupStock(DriveKindOf(key.UniquePath) == DriveKind.Removable ? SHSTOCKICONID.SIID_DRIVEREMOVE : SHSTOCKICONID.SIID_DRIVEFIXED);
                }
            }
        }

        if (lookup is null)
        {
            lookup = ShellIconReader.LookupByType(folder, extension);
            if (!folder && lookup is { } byType && GenericFileIndex() is { } generic && byType.Index == generic)
            {
                // The shell has no type icon for this extension (or only a per-file icon handler, which by-type lookups
                // do not run): Explorer may show a per-file icon here.
                _logger.LogDebug("Icon {Path} ({Extension}): the type has only the generic file icon", key.UniquePath ?? "(by type)", Describe(key));
            }
        }

        return Rendered(key, lookup);
    }

    // STA thread.
    private IconImage? Rendered(IconKey key, IconLookup? lookup)
    {
        IconImage? image = lookup is { } l ? ShellIconReader.Render(l, key.Size) : null;
        if (image is null)
        {
            _logger.LogDebug("Icon {Path} ({Extension}) {Size}: the shell returned no icon", key.UniquePath ?? "(by type)", Describe(key), key.Size);
        }

        return image;
    }

    // STA thread: the unknown-type icon (a file without extension); null while it cannot be looked up (asked again next
    // time, so one transient failure does not switch the diagnostic off for the session).
    private int? GenericFileIndex()
    {
        if (_genericFileIndex < 0)
        {
            _genericFileIndex = ShellIconReader.LookupByType(false, string.Empty)?.Index ?? -1;
        }

        return _genericFileIndex < 0 ? null : _genericFileIndex;
    }

    // Explorer shows thumbnails from "medium icons" (48) up; smaller sizes keep the type icon (owner #7).
    private bool IsThumbnail(int extId, IconSize size) => size >= IconSize.Large && _thumbnailTypes.GetValueOrDefault(extId);

    private static SHSTOCKICONID? StockIdOf(int extId) => extId switch
    {
        IconKey.RemovableDriveExtId => SHSTOCKICONID.SIID_DRIVEREMOVE,
        IconKey.CdRomDriveExtId => SHSTOCKICONID.SIID_DRIVECD,
        IconKey.NetworkDriveExtId => SHSTOCKICONID.SIID_DRIVENET,
        IconKey.RamDriveExtId => SHSTOCKICONID.SIID_DRIVERAM,
        IconKey.OtherDriveExtId => SHSTOCKICONID.SIID_DRIVEFIXED,
        IconKey.ThisPcExtId => SHSTOCKICONID.SIID_DESKTOPPC,
        IconKey.WarningExtId => SHSTOCKICONID.SIID_WARNING,
        IconKey.ShieldExtId => SHSTOCKICONID.SIID_SHIELD,
        _ => null,
    };

    // Extension text for logs (".txt", "folder", "drive", "warning", "shield").
    private string Describe(IconKey key) => key.ExtId switch
    {
        IconKey.FolderExtId => "folder",
        IconKey.WarningExtId => "warning",
        IconKey.ShieldExtId => "shield",
        < IconKey.FolderExtId => "drive",
        0 => "(no extension)",
        _ => _extensions.ExtensionOf(key.ExtId),
    };

    private void Store(IconKey key, IconImage? image, bool failed, int epoch)
    {
        lock (_gate)
        {
            _inFlight.Remove(key);
            if (epoch != _driveEpoch && IsDriveRootKey(key))
            {
                ReadyLocked(key); // read from the media before the change: the waiter asks again and gets a fresh lookup
                return;
            }

            if (_cache.TryGetValue(key, out var old))
            {
                RemoveLocked(old);
            }

            DateTimeOffset? retryAt = null;
            if (failed)
            {
                int attempts = _failedAttempts.GetValueOrDefault(key) + 1;
                _failedAttempts[key] = attempts;
                if (attempts < MaxAttempts)
                {
                    retryAt = _time.GetUtcNow() + FailureCooldown;
                }
                else
                {
                    _logger.LogWarning("Icon {Extension} {Size} {Path}: {Attempts} failed lookups, placeholder kept for this session", Describe(key), key.Size, key.UniquePath ?? "(by type)", attempts);
                }
            }
            else
            {
                _failedAttempts.Remove(key);
            }

            var entry = new CacheEntry(key, image, (image?.Bgra.Length ?? 0) + EntryOverheadBytes, retryAt);
            _cache[key] = _lru.AddFirst(entry);
            _bytes += entry.Bytes;
            while (_bytes > _maxBytes && _lru.Last is { } last && last != _lru.First)
            {
                _lru.RemoveLast();
                _cache.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes;
            }

            ReadyLocked(key);
        }
    }

    private void ReadyLocked(IconKey key)
    {
        _ready.Add(key);
        if (_flushTimer is null && !_disposed.IsCancellationRequested)
        {
            _flushTimer = _time.CreateTimer(_ => Flush(), null, ReadyDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        IconKey[] keys;
        lock (_gate)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            keys = [.. _ready];
            _ready.Clear();
        }

        if (keys.Length == 0)
        {
            return;
        }

        try
        {
            IconsReady?.Invoke(keys);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IconsReady handler failed");
        }
    }

    // RetryAt: null = final (an image, or a lookup the shell answered with nothing); otherwise a failure in cooldown.
    private sealed record CacheEntry(IconKey Key, IconImage? Image, long Bytes, DateTimeOffset? RetryAt);
}
