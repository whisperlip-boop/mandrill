namespace Mandrill.Core.Index;

/// <summary>How listing one directory for the index ended.</summary>
public enum ListOutcome
{
    /// <summary>Listed; the buffer holds its direct children.</summary>
    Ok,

    /// <summary>Access denied: recorded as an empty, complete directory and counted (AC-2.12).</summary>
    AccessDenied,

    /// <summary>The directory vanished or is not a directory: recorded as empty and complete.</summary>
    NotFound,

    /// <summary>
    /// Not known to the source (e.g. a merge source over an incomplete segment), or the listing failed with an I/O error
    /// other than not-found (not ready, sharing, device, a file at the path): the subtree stays incomplete.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Enumerates one directory at a time for the index builder (architecture §8.4 "MFT seam"). The Win32 implementation lists
/// with FindFirstFileEx; a later MFT/USN source can replace it. Called on the index thread only.
/// </summary>
public interface IIndexSource
{
    /// <summary>Lists the direct children of <paramref name="directory"/> into <paramref name="buffer"/> (cleared first).</summary>
    /// <param name="directory">Normalized directory path.</param>
    /// <param name="buffer">Reusable entry buffer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How the listing ended.</returns>
    ListOutcome List(string directory, IndexEntryBuffer buffer, CancellationToken ct);
}

/// <summary>
/// Reusable buffer for the children of one directory: names in one char array, no string per entry, so building the
/// index creates almost no garbage (ADR-0008). Writer thread only.
/// </summary>
public sealed class IndexEntryBuffer
{
    private char[] _chars = new char[64 * 1024];
    private Entry[] _entries = new Entry[1024];
    private int[] _order = new int[1024];
    private int _charCount;
    private Comparison<int>? _compare;

    /// <summary>Number of entries.</summary>
    public int Count { get; private set; }

    /// <summary>Removes all entries.</summary>
    public void Clear()
    {
        Count = 0;
        _charCount = 0;
    }

    /// <summary>Adds an entry.</summary>
    /// <param name="name">File or directory name.</param>
    /// <param name="size">Size in bytes (0 for directories).</param>
    /// <param name="mtime">Last write time in Unix seconds.</param>
    /// <param name="flags">Item flags (Directory, Reparse, Hidden, System).</param>
    public void Add(ReadOnlySpan<char> name, long size, uint mtime, IndexFlags flags)
    {
        if (_charCount + name.Length > _chars.Length)
        {
            Array.Resize(ref _chars, Math.Max(_chars.Length * 2, _charCount + name.Length));
        }

        if (Count == _entries.Length)
        {
            Array.Resize(ref _entries, _entries.Length * 2);
            Array.Resize(ref _order, _order.Length * 2);
        }

        name.CopyTo(_chars.AsSpan(_charCount));
        _entries[Count] = new Entry(_charCount, name.Length, size, mtime, flags);
        _order[Count] = Count;
        _charCount += name.Length;
        Count++;
    }

    /// <summary>Name of entry <paramref name="index"/> in sorted order (after <see cref="Sort"/>).</summary>
    /// <param name="index">Position.</param>
    /// <returns>The name.</returns>
    public ReadOnlySpan<char> NameAt(int index)
    {
        ref readonly Entry e = ref _entries[_order[index]];
        return _chars.AsSpan(e.Start, e.Length);
    }

    /// <summary>Size of entry <paramref name="index"/> in sorted order.</summary>
    /// <param name="index">Position.</param>
    /// <returns>The size.</returns>
    public long SizeAt(int index) => _entries[_order[index]].Size;

    /// <summary>Modified time of entry <paramref name="index"/> in sorted order.</summary>
    /// <param name="index">Position.</param>
    /// <returns>Unix seconds.</returns>
    public uint MtimeAt(int index) => _entries[_order[index]].Mtime;

    /// <summary>Flags of entry <paramref name="index"/> in sorted order.</summary>
    /// <param name="index">Position.</param>
    /// <returns>The flags.</returns>
    public IndexFlags FlagsAt(int index) => _entries[_order[index]].Flags;

    /// <summary>Sorts the entries by name, <see cref="StringComparison.OrdinalIgnoreCase"/> (the child-block order, §8.1).</summary>
    public void Sort()
    {
        if (Count > 1)
        {
            _order.AsSpan(0, Count).Sort(_compare ??= Compare); // one delegate per buffer, not per directory
        }
    }

    /// <summary>Converts a UTC time to the index's Unix seconds, clamped to the uint range.</summary>
    /// <param name="utc">UTC time.</param>
    /// <returns>Seconds since 1970-01-01.</returns>
    public static uint ToUnixSeconds(DateTimeOffset utc) => (uint)Math.Clamp(utc.ToUnixTimeSeconds(), 0, uint.MaxValue);

    private int Compare(int a, int b)
    {
        ref readonly Entry x = ref _entries[a];
        ref readonly Entry y = ref _entries[b];
        return _chars.AsSpan(x.Start, x.Length).CompareTo(_chars.AsSpan(y.Start, y.Length), StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Entry(int Start, int Length, long Size, uint Mtime, IndexFlags Flags);
}
