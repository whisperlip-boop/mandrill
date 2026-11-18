using Mandrill.Core.IO;

namespace Mandrill.Core.Scanning;

/// <summary>Per-item changes from one snapshot version to the next (architecture §3, §5).</summary>
/// <param name="BaseVersion">Version the diff applies to.</param>
/// <param name="RemovedOld">Indices in the base list to remove, descending (apply first).</param>
/// <param name="Inserted">New entries with their index in the new list, ascending (apply after removals).</param>
/// <param name="ChangedNew">Indices in the new list whose entry changed in place (size, time, attributes).</param>
public sealed record EntryDiff(long BaseVersion, int[] RemovedOld, (int NewIndex, FileEntry Entry)[] Inserted, int[] ChangedNew)
{
    /// <summary>Number of per-item operations.</summary>
    public int ChangeCount => RemovedOld.Length + Inserted.Length + ChangedNew.Length;
}

/// <summary>
/// Immutable, cumulative listing of one folder as shown: filtered and sorted. A later snapshot for the same request
/// replaces earlier ones. <see cref="IndexOfName"/> is O(1); its map is built on the worker before publishing, also for
/// intermediate snapshots. That is deliberate: every published snapshot is shown (Replace → Reset), and the list control
/// calls IndexOf during a Reset (WP-0 S2: 10–64 calls), so a lazily built map would be built on the UI thread. The cost
/// is one dictionary per snapshot (≈ 10 ms per 100k entries on the worker, at most every 250 ms).
/// </summary>
public sealed class EntrySnapshot
{
    private readonly Dictionary<string, int> _indexByName;

    internal EntrySnapshot(string path, string? redirectedFrom, long version, FileEntry[] visible, bool isFinal, EntryDiff? diff, int skipped, TimeSpan elapsed, bool fromCache, int total)
    {
        Path = path;
        Total = total;
        RedirectedFrom = redirectedFrom;
        Version = version;
        Visible = visible;
        IsFinal = isFinal;
        Diff = diff;
        Skipped = skipped;
        Elapsed = elapsed;
        FromCache = fromCache;
        _indexByName = new Dictionary<string, int>(visible.Length, StringComparer.Ordinal);
        for (int i = 0; i < visible.Length; i++)
        {
            _indexByName[visible[i].Name] = i;
        }
    }

    /// <summary>Folder shown (normalized).</summary>
    public string Path { get; }

    /// <summary>The requested path when <see cref="ScanRequest.FallbackToAncestor"/> redirected to an ancestor; else null.</summary>
    public string? RedirectedFrom { get; }

    /// <summary>Monotonic version (process-wide).</summary>
    public long Version { get; }

    /// <summary>Visible entries in display order.</summary>
    public IReadOnlyList<FileEntry> Visible { get; }

    /// <summary>Entries the hidden/system rule shows, before the name filter (= <see cref="Visible"/>.Count without one).</summary>
    public int Total { get; }

    /// <summary>True for the last snapshot of a request.</summary>
    public bool IsFinal { get; }

    /// <summary>Per-item changes against <see cref="EntryDiff.BaseVersion"/>, or null when the consumer should replace (Reset).</summary>
    public EntryDiff? Diff { get; }

    /// <summary>Entries that could not be read.</summary>
    public int Skipped { get; }

    /// <summary>Time since the request started.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>True when the content came from <see cref="DirectoryCache"/> (a reconcile follows).</summary>
    public bool FromCache { get; }

    /// <summary>Index of the entry named exactly <paramref name="name"/> in <see cref="Visible"/>, or -1.</summary>
    /// <param name="name">Entry name.</param>
    /// <returns>The index or -1.</returns>
    public int IndexOfName(string name) => _indexByName.TryGetValue(name, out int i) ? i : -1;
}

/// <summary>Computes <see cref="EntryDiff"/> between two visible lists.</summary>
public static class EntryDiffer
{
    /// <summary>
    /// Diff by name (Ordinal) from <paramref name="oldList"/> to <paramref name="newList"/>. The longest run of common
    /// entries in unchanged relative order is kept; other common entries moved and are expressed as remove + insert, so
    /// applying RemovedOld (descending) and then Inserted (ascending) to the old list yields the new list. ChangedNew marks
    /// in-place updates. O(n + k log k).
    /// </summary>
    /// <param name="baseVersion">Version of <paramref name="oldList"/>.</param>
    /// <param name="oldList">Shown list.</param>
    /// <param name="newList">New list.</param>
    /// <param name="maxChanges">Upper bound; above it the result is null (the consumer resets).</param>
    /// <returns>The diff, or null when it would exceed <paramref name="maxChanges"/>.</returns>
    public static EntryDiff? Compute(long baseVersion, IReadOnlyList<FileEntry> oldList, IReadOnlyList<FileEntry> newList, int maxChanges)
    {
        var oldIndex = new Dictionary<string, int>(oldList.Count, StringComparer.Ordinal);
        for (int i = 0; i < oldList.Count; i++)
        {
            oldIndex[oldList[i].Name] = i;
        }

        // Common entries in new order, with their old index.
        var commonNew = new List<int>();
        var commonOld = new List<int>();
        for (int n = 0; n < newList.Count; n++)
        {
            if (oldIndex.TryGetValue(newList[n].Name, out int o))
            {
                commonNew.Add(n);
                commonOld.Add(o);
            }
        }

        // Keep the largest set of common entries whose relative order is unchanged (longest increasing subsequence of
        // old indices); every other common entry moved and becomes remove + insert. One entry jumping to the front
        // (e.g. a modified file under a date-descending sort) is then 1 remove + 1 insert, not a reset.
        var keepCommon = LongestIncreasing(commonOld);
        var keptOld = new bool[oldList.Count];
        var keptNew = new bool[newList.Count];
        var changed = new List<int>();
        for (int k = 0; k < commonNew.Count; k++)
        {
            if (keepCommon[k])
            {
                keptOld[commonOld[k]] = true;
                keptNew[commonNew[k]] = true;
                if (!SameContent(oldList[commonOld[k]], newList[commonNew[k]]))
                {
                    changed.Add(commonNew[k]);
                }
            }
        }

        var inserted = new List<(int, FileEntry)>();
        for (int n = 0; n < newList.Count; n++)
        {
            if (!keptNew[n])
            {
                inserted.Add((n, newList[n]));
                if (inserted.Count + changed.Count > maxChanges)
                {
                    return null;
                }
            }
        }

        var removed = new List<int>();
        for (int o = oldList.Count - 1; o >= 0; o--)
        {
            if (!keptOld[o])
            {
                removed.Add(o);
                if (removed.Count + inserted.Count + changed.Count > maxChanges)
                {
                    return null;
                }
            }
        }

        return new EntryDiff(baseVersion, removed.ToArray(), inserted.ToArray(), changed.ToArray());
    }

    // Marks one longest strictly increasing subsequence of values (patience sorting, O(k log k)).
    private static bool[] LongestIncreasing(List<int> values)
    {
        int count = values.Count;
        var keep = new bool[count];
        if (count == 0)
        {
            return keep;
        }

        var tailIndex = new int[count];
        var previous = new int[count];
        int length = 0;
        for (int i = 0; i < count; i++)
        {
            int lo = 0, hi = length;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (values[tailIndex[mid]] < values[i])
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            previous[i] = lo > 0 ? tailIndex[lo - 1] : -1;
            tailIndex[lo] = i;
            if (lo == length)
            {
                length++;
            }
        }

        for (int i = tailIndex[length - 1]; i >= 0; i = previous[i])
        {
            keep[i] = true;
        }

        return keep;
    }

    private static bool SameContent(FileEntry a, FileEntry b)
        => a.Size == b.Size && a.LastWriteUtcTicks == b.LastWriteUtcTicks && a.Attributes == b.Attributes && a.ExtId == b.ExtId;
}
