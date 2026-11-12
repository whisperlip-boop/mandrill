using System.Text;
using Mandrill.Core.IO;

namespace Mandrill.Core.Index;

/// <summary>An item added to a segment after it was built (architecture §8.3 overlay).</summary>
/// <param name="Path">Full path.</param>
/// <param name="Size">Size in bytes (0 for directories).</param>
/// <param name="Mtime">Last write time, Unix seconds.</param>
/// <param name="Flags">Item flags (never <see cref="IndexFlags.Dead"/>).</param>
public sealed record OverlayItem(string Path, long Size, uint Mtime, IndexFlags Flags);

/// <summary>
/// The index of one fixed drive (architecture §8.1, ADR-0005): columns in "children-block DFS" order. Item 0 is the drive
/// root. The direct children of a directory D are <c>[ChildStart(D), ChildStart(D) + ChildCount(D))</c>, sorted
/// OrdinalIgnoreCase, and all its descendants are <c>[ChildStart(D), SubtreeEnd(D))</c>. <c>SubtreeEnd == -1</c> means
/// the subtree is not complete yet (build in progress, partial load, or a subtree the source could not provide).
/// <para>
/// Threading: one writer (the index thread). Appends land past the published <see cref="Count"/> and chunks never move,
/// so readers below <see cref="Count"/> need no lock. Overlay changes, dead marks and in-place updates take a short write
/// lock; overlay readers take the read lock, while flags (including Dead) are read lock-free with volatile reads (§8.2).
/// Upsert/Delete are meant for a segment whose build finished (WP-2.4 applies changes after the build or replays them).
/// The lock is not disposed: it has no finalizer and lives as long as the segment.
/// </para>
/// </summary>
public sealed class DriveSegment
{
    /// <summary>Overlay size above which <see cref="NeedsCompaction"/> is true.</summary>
    public const int CompactOverlayItems = 50_000;

    private readonly ChunkedColumn<int> _nameOffset = new();
    private readonly ChunkedColumn<int> _parent = new();
    private readonly ChunkedColumn<int> _childStart = new();
    private readonly ChunkedColumn<int> _childCount = new();
    private readonly ChunkedColumn<int> _subtreeEnd = new();
    private readonly ChunkedColumn<long> _size = new();
    private readonly ChunkedColumn<uint> _mtime = new();
    private readonly ChunkedColumn<byte> _flags = new();
    private readonly NameBlob _names = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    // Overlay: parent directory path → name → item (PathUtil.Comparer; names OrdinalIgnoreCase like the file system).
    private readonly Dictionary<string, Dictionary<string, OverlayItem>> _overlay = new(PathUtil.Comparer);
    private int _overlayCount;
    private int _deadCount;
    private int _count;
    private int _pending;

    /// <summary>Creates an empty segment: only the root item, its subtree not built yet.</summary>
    /// <param name="root">Drive root, e.g. "C:\".</param>
    /// <param name="volumeSerial">Volume serial number (checked when a saved index is loaded).</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is not a drive root.</exception>
    public DriveSegment(string root, uint volumeSerial)
        : this(root, volumeSerial, addRoot: true)
    {
    }

    private DriveSegment(string root, uint volumeSerial, bool addRoot)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!PathUtil.TryNormalize(root, out string normalized, out _) || !PathUtil.IsDriveRoot(normalized))
        {
            throw new ArgumentException("A drive root such as C:\\ is required.", nameof(root));
        }

        Root = normalized;
        VolumeSerial = volumeSerial;
        if (addRoot)
        {
            AddItem(Root.AsSpan(0, 2), -1, 0, 0, IndexFlags.Directory); // item 0, named "C:"
            Publish();
        }
    }

    /// <summary>Drive root, e.g. "C:\".</summary>
    public string Root { get; }

    /// <summary>Volume serial number of the drive this segment was built from.</summary>
    public uint VolumeSerial { get; }

    /// <summary>Published item count (readers see items below it).</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Whether the whole drive is indexed (the root's subtree is complete).</summary>
    public bool IsComplete => SubtreeEnd(0) >= 0;

    /// <summary>Items added since the build (overlay).</summary>
    public int OverlayCount => Volatile.Read(ref _overlayCount);

    /// <summary>Base items marked dead since the build.</summary>
    public int DeadCount => Volatile.Read(ref _deadCount);

    /// <summary>True when the overlay or the dead marks grew enough to rebuild the segment (§8.3).</summary>
    public bool NeedsCompaction => OverlayCount > CompactOverlayItems || DeadCount > Count / 10;

    /// <summary>Allocated bytes of the columns and names (memory accounting, budget ≤150 MB per 1M items).</summary>
    public long ByteSize
        => _nameOffset.ByteSize + _parent.ByteSize + _childStart.ByteSize + _childCount.ByteSize + _subtreeEnd.ByteSize
           + _size.ByteSize + _mtime.ByteSize + _flags.ByteSize + _names.ByteSize;

    internal NameBlob Names => _names;

    internal ChunkedColumn<int> NameOffsets => _nameOffset;

    internal ChunkedColumn<int> Parents => _parent;

    internal ChunkedColumn<int> ChildStarts => _childStart;

    internal ChunkedColumn<int> ChildCounts => _childCount;

    internal ChunkedColumn<int> SubtreeEnds => _subtreeEnd;

    internal ChunkedColumn<long> Sizes => _size;

    internal ChunkedColumn<uint> Mtimes => _mtime;

    internal ChunkedColumn<byte> FlagColumn => _flags;

    /// <summary>Name of <paramref name="item"/> (the root's name is "C:").</summary>
    /// <param name="item">Item below <see cref="Count"/>.</param>
    /// <returns>The name.</returns>
    public ReadOnlySpan<char> NameOf(int item) => _names.Get(_nameOffset[item]);

    /// <summary>Parent item, or -1 for the root.</summary>
    /// <param name="item">Item.</param>
    /// <returns>The parent.</returns>
    public int ParentOf(int item) => _parent[item];

    /// <summary>First item of the child block, or -1 when the directory has not been listed.</summary>
    /// <param name="item">Item.</param>
    /// <returns>The index.</returns>
    public int ChildStart(int item) => Volatile.Read(ref _childStart[item]);

    /// <summary>Number of direct children (0 for files and unlisted directories).</summary>
    /// <param name="item">Item.</param>
    /// <returns>The count.</returns>
    public int ChildCount(int item) => _childCount[item];

    /// <summary>End (exclusive) of all descendants, or -1 while the subtree is incomplete.</summary>
    /// <param name="item">Item.</param>
    /// <returns>The end.</returns>
    public int SubtreeEnd(int item) => Volatile.Read(ref _subtreeEnd[item]); // pairs with SetSubtreeEnd's Volatile.Write

    /// <summary>Size in bytes.</summary>
    /// <param name="item">Item.</param>
    /// <returns>The size.</returns>
    public long SizeOf(int item) => _size[item];

    /// <summary>Last write time in Unix seconds.</summary>
    /// <param name="item">Item.</param>
    /// <returns>The time.</returns>
    public uint MtimeOf(int item) => _mtime[item];

    /// <summary>Item flags.</summary>
    /// <param name="item">Item.</param>
    /// <returns>The flags.</returns>
    public IndexFlags FlagsOf(int item) => (IndexFlags)Volatile.Read(ref _flags[item]);

    /// <summary>Full path of <paramref name="item"/>.</summary>
    /// <param name="item">Item.</param>
    /// <returns>The path.</returns>
    public string PathOf(int item)
    {
        if (item == 0)
        {
            return Root;
        }

        var parts = new List<int>();
        for (int i = item; i > 0; i = _parent[i])
        {
            parts.Add(i);
        }

        var sb = new StringBuilder(Root, 64);
        for (int k = parts.Count - 1; k >= 0; k--)
        {
            sb.Append(NameOf(parts[k]));
            if (k > 0)
            {
                sb.Append('\\');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The item at <paramref name="path"/> (also when dead), or -1 when the path is not on this drive, not indexed yet, or
    /// only in the overlay. One binary search per path component inside the sorted child blocks.
    /// </summary>
    /// <param name="path">Normalized path on this drive.</param>
    /// <returns>The item or -1.</returns>
    public int Lookup(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!PathUtil.IsSameOrUnder(path, Root))
        {
            return -1;
        }

        int item = 0;
        int count = Count;
        foreach (Range part in path.AsSpan(Root.Length).Split('\\'))
        {
            ReadOnlySpan<char> name = path.AsSpan(Root.Length)[part];
            if (name.IsEmpty)
            {
                continue;
            }

            int start = Volatile.Read(ref _childStart[item]);
            int n = _childCount[item];
            if (start < 0 || n == 0 || start + n > count)
            {
                return -1;
            }

            int lo = start, hi = start + n - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                int c = NameOf(mid).CompareTo(name, StringComparison.OrdinalIgnoreCase);
                if (c == 0)
                {
                    found = mid;
                    break;
                }

                if (c < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (found < 0)
            {
                return -1;
            }

            item = found;
        }

        return item;
    }

    /// <summary>
    /// Records a file or directory that exists now (change tracking, §8.3): an indexed item is updated in place (and no
    /// longer dead); anything else goes to the overlay under its parent. Writer only.
    /// </summary>
    /// <param name="path">Full path on this drive.</param>
    /// <param name="size">Size in bytes.</param>
    /// <param name="mtime">Last write time, Unix seconds.</param>
    /// <param name="flags">Flags (Dead is ignored).</param>
    public void Upsert(string path, long size, uint mtime, IndexFlags flags)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!PathUtil.IsSameOrUnder(path, Root))
        {
            return; // another drive's path
        }

        flags &= ~IndexFlags.Dead;
        _lock.EnterWriteLock();
        try
        {
            int item = Lookup(path);
            if (item > 0)
            {
                IndexFlags old = FlagsOf(item);
                if ((old & IndexFlags.Dead) != 0)
                {
                    _deadCount--;
                }

                // A stat knows nothing of the exclude list: keep the Excluded mark.
                _size[item] = size;
                _mtime[item] = mtime;
                Volatile.Write(ref _flags[item], (byte)(flags | (old & IndexFlags.Excluded)));
                return;
            }

            if (item == 0 || PathUtil.Parent(path) is not { } parent)
            {
                return; // the root itself has no metadata to update
            }

            if (!_overlay.TryGetValue(parent, out var children))
            {
                children = new Dictionary<string, OverlayItem>(StringComparer.OrdinalIgnoreCase);
                _overlay[parent] = children;
            }

            string leaf = PathUtil.LeafName(path);
            if (!children.ContainsKey(leaf))
            {
                _overlayCount++;
            }

            children[leaf] = new OverlayItem(path, size, mtime, flags);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Records that <paramref name="path"/> no longer exists (§8.3): an indexed item is marked dead, a directory with its
    /// whole subtree; overlay items at or under the path are removed. Writer only.
    /// </summary>
    /// <param name="path">Full path on this drive.</param>
    /// <returns>True when something was removed.</returns>
    public bool Delete(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!PathUtil.IsSameOrUnder(path, Root) || PathUtil.PathEquals(path, Root))
        {
            return false;
        }

        _lock.EnterWriteLock();
        try
        {
            bool removed = RemoveOverlayAtOrUnder(path);
            int item = Lookup(path);
            if (item > 0)
            {
                removed |= MarkDead(item);
            }

            return removed;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Overlay items whose parent is <paramref name="directory"/> (a copy; read lock).</summary>
    /// <param name="directory">Directory path.</param>
    /// <returns>The items, unordered.</returns>
    public IReadOnlyList<OverlayItem> OverlayChildren(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _lock.EnterReadLock();
        try
        {
            return _overlay.TryGetValue(directory, out var children) ? [.. children.Values] : [];
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>True when <paramref name="path"/> is an overlay item (read lock; no copy, unlike <see cref="OverlayChildren"/>).</summary>
    /// <param name="path">Full path.</param>
    /// <returns>True when the overlay holds it.</returns>
    public bool OverlayContains(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (PathUtil.Parent(path) is not { } parent)
        {
            return false;
        }

        _lock.EnterReadLock();
        try
        {
            // Change application asks this per path: copying a folder's children each time was quadratic (WP-2.7).
            return _overlay.TryGetValue(parent, out var children) && children.ContainsKey(PathUtil.LeafName(path));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>All overlay items (a copy; persistence and tests).</summary>
    /// <returns>The items.</returns>
    public IReadOnlyList<OverlayItem> OverlayItems()
    {
        _lock.EnterReadLock();
        try
        {
            return [.. _overlay.Values.SelectMany(c => c.Values)];
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Number of live items in the subtree of <paramref name="item"/> (descendants only) whose name contains
    /// <paramref name="needle"/>, case-insensitively (Ordinal when the needle has no cased letter, e.g. Korean). One
    /// vectorized IndexOf pass over the names of a complete subtree; the timing baseline for search (§8.4, WP-2.2 builds
    /// the scoped walker and the overlay merge on it).
    /// </summary>
    /// <param name="item">A directory with a complete subtree (e.g. the root of a complete drive).</param>
    /// <param name="needle">Substring to find (no '\0').</param>
    /// <returns>The number of matching items, or -1 when the subtree is not complete.</returns>
    /// <exception cref="ArgumentException"><paramref name="needle"/> is empty or contains '\0'.</exception>
    public int CountMatches(int item, string needle)
    {
        ArgumentException.ThrowIfNullOrEmpty(needle);
        if (needle.Contains('\0'))
        {
            throw new ArgumentException("A name never contains '\\0'.", nameof(needle)); // the only way to match across names
        }
        int first = _childStart[item];
        int end = _subtreeEnd[item];
        if (end < 0)
        {
            return -1;
        }

        if (first < 0 || end <= first)
        {
            return 0;
        }

        StringComparison comparison = HasCasedLetter(needle) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int matches = 0;
        ScanMatches(first, end, needle, comparison, _ =>
        {
            matches++;
            return true;
        });
        return matches;
    }

    /// <summary>
    /// Calls <paramref name="onCandidate"/> once for every live (not Dead) item in <c>[first, end)</c> whose name contains
    /// <paramref name="literal"/>, in item order, until it returns false; every live item when the literal is empty. One
    /// vectorized IndexOf pass over the names (§8.4): the search walker applies the full match and its filters. Items
    /// past the published <see cref="Count"/> are never visited.
    /// </summary>
    /// <param name="first">First item (e.g. a directory's child start).</param>
    /// <param name="end">End item, exclusive (its subtree end, or child start + child count).</param>
    /// <param name="literal">Text the names must contain (no '\0'); "" for all names.</param>
    /// <param name="comparison">Comparison for <paramref name="literal"/>.</param>
    /// <param name="onCandidate">Receives the item; returns false to stop.</param>
    /// <returns>False when <paramref name="onCandidate"/> stopped the scan.</returns>
    internal bool ScanMatches(int first, int end, string literal, StringComparison comparison, Func<int, bool> onCandidate)
    {
        end = Math.Min(end, Count);
        if (first < 0 || end <= first)
        {
            return true;
        }

        if (literal.Length == 0)
        {
            for (int item = first; item < end; item++)
            {
                if ((FlagsOf(item) & IndexFlags.Dead) == 0 && !onCandidate(item))
                {
                    return false;
                }
            }

            return true;
        }

        int startOffset = _nameOffset[first];

        // The end comes from published items only: names the builder appended past Count are not part of the range.
        int endOffset = end < Count ? _nameOffset[end] : _nameOffset[end - 1] + NameOf(end - 1).Length + 1;
        int startChunk = startOffset >> NameBlob.ChunkShift;
        int lastChunk = (endOffset - 1) >> NameBlob.ChunkShift;
        for (int chunk = startChunk; chunk <= lastChunk; chunk++)
        {
            ReadOnlySpan<char> used = _names.UsedChunk(chunk);
            int from = chunk == startChunk ? startOffset & (NameBlob.ChunkChars - 1) : 0;
            int to = chunk == lastChunk ? Math.Min(used.Length, ((endOffset - 1) & (NameBlob.ChunkChars - 1)) + 1) : used.Length;
            int position = from;
            while (position < to)
            {
                int hit = used[position..to].IndexOf(literal, comparison);
                if (hit < 0)
                {
                    break;
                }

                int global = (chunk << NameBlob.ChunkShift) | (position + hit);
                int matchItem = ItemAtOffset(global, first, end);
                if ((FlagsOf(matchItem) & IndexFlags.Dead) == 0 && !onCandidate(matchItem))
                {
                    return false;
                }

                // Next name: one candidate per item.
                int nameEnd = used[(position + hit)..to].IndexOf('\0');
                position = nameEnd < 0 ? to : position + hit + nameEnd + 1;
            }
        }

        return true;
    }

    // Writer API used by the builder: fill items past Count, then Publish().
    internal int AddItem(ReadOnlySpan<char> name, int parent, long size, uint mtime, IndexFlags flags)
    {
        int item = _pending;
        if (item == int.MaxValue)
        {
            throw new InvalidOperationException("The segment is full.");
        }

        EnsureCapacity(item + 1);
        _nameOffset[item] = _names.Append(name);
        _parent[item] = parent;
        _childStart[item] = -1;
        _childCount[item] = 0;
        _subtreeEnd[item] = (flags & IndexFlags.Directory) != 0 ? -1 : item + 1;
        _size[item] = size;
        _mtime[item] = mtime;
        _flags[item] = (byte)flags;
        _pending = item + 1;
        return item;
    }

    internal int Pending => _pending;

    internal void SetChildBlock(int item, int start, int count)
    {
        // Count first, start last with release semantics: a lock-free reader that sees the start (ChildStart, Lookup:
        // acquire) also sees its count, never a new start with the old count 0 (search would skip the subtree).
        _childCount[item] = count;
        Volatile.Write(ref _childStart[item], start);
    }

    internal void SetSubtreeEnd(int item, int end) => Volatile.Write(ref _subtreeEnd[item], end);

    internal void Publish() => Volatile.Write(ref _count, _pending);

    // Loader: an empty segment whose columns the reader fills chunk by chunk.
    internal static DriveSegment CreateForLoad(string root, uint volumeSerial) => new(root, volumeSerial, addRoot: false);

    internal void CompleteLoad(int count, IReadOnlyList<OverlayItem> overlay, int deadCount)
    {
        _pending = count;
        Publish();
        _deadCount = deadCount;
        foreach (OverlayItem item in overlay)
        {
            Upsert(item.Path, item.Size, item.Mtime, item.Flags);
        }
    }

    private static bool HasCasedLetter(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (char.ToUpperInvariant(c) != char.ToLowerInvariant(c))
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureCapacity(int count)
    {
        _nameOffset.EnsureCapacity(count);
        _parent.EnsureCapacity(count);
        _childStart.EnsureCapacity(count);
        _childCount.EnsureCapacity(count);
        _subtreeEnd.EnsureCapacity(count);
        _size.EnsureCapacity(count);
        _mtime.EnsureCapacity(count);
        _flags.EnsureCapacity(count);
    }

    // Largest item in [first, end) whose name offset is ≤ offset (name offsets grow with the item index).
    private int ItemAtOffset(int offset, int first, int end)
    {
        int lo = first, hi = end - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_nameOffset[mid] <= offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    // Marks an item and, for a directory, every listed descendant dead. Under the write lock.
    private bool MarkDead(int item)
    {
        bool any = false;
        var stack = new Stack<int>();
        stack.Push(item);
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if ((FlagsOf(i) & IndexFlags.Dead) == 0)
            {
                Volatile.Write(ref _flags[i], (byte)(FlagsOf(i) | IndexFlags.Dead));
                _deadCount++;
                any = true;
            }

            int start = _childStart[i];
            for (int c = 0; start >= 0 && c < _childCount[i]; c++)
            {
                stack.Push(start + c);
            }
        }

        return any;
    }

    private bool RemoveOverlayAtOrUnder(string path)
    {
        bool removed = false;
        if (PathUtil.Parent(path) is { } parent && _overlay.TryGetValue(parent, out var siblings) && siblings.Remove(PathUtil.LeafName(path)))
        {
            _overlayCount--;
            removed = true;
            if (siblings.Count == 0)
            {
                _overlay.Remove(parent);
            }
        }

        foreach (string key in _overlay.Keys.Where(k => PathUtil.IsSameOrUnder(k, path)).ToList())
        {
            _overlayCount -= _overlay[key].Count;
            _overlay.Remove(key);
            removed = true;
        }

        return removed;
    }
}
