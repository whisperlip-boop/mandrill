namespace Mandrill.App.Views;

/// <summary>
/// A set of row indexes kept as sorted, disjoint, non-adjacent ranges (the search results' selection, WP-2.2b): select
/// all of 100k rows is one range, and membership is a binary search. Pure logic.
/// </summary>
public sealed class IndexRangeSet
{
    private readonly List<(int First, int Last)> _ranges = [];

    /// <summary>The ranges, sorted, inclusive bounds.</summary>
    public IReadOnlyList<(int First, int Last)> Ranges => _ranges;

    /// <summary>Number of indexes in the set.</summary>
    public int Count => _ranges.Sum(r => r.Last - r.First + 1);

    /// <summary>Adds <c>[first, last]</c>.</summary>
    /// <param name="first">First index.</param>
    /// <param name="last">Last index (inclusive).</param>
    public void Add(int first, int last)
    {
        if (first < 0 || last < first)
        {
            return;
        }

        // Absorb every range that overlaps or touches [first, last].
        int i = 0;
        while (i < _ranges.Count && _ranges[i].Last < first - 1)
        {
            i++;
        }

        while (i < _ranges.Count && _ranges[i].First <= last + 1)
        {
            first = Math.Min(first, _ranges[i].First);
            last = Math.Max(last, _ranges[i].Last);
            _ranges.RemoveAt(i);
        }

        _ranges.Insert(i, (first, last));
    }

    /// <summary>Removes <c>[first, last]</c>.</summary>
    /// <param name="first">First index.</param>
    /// <param name="last">Last index (inclusive).</param>
    public void Remove(int first, int last)
    {
        if (last < first)
        {
            return;
        }

        for (int i = _ranges.Count - 1; i >= 0; i--)
        {
            var (a, b) = _ranges[i];
            if (b < first || a > last)
            {
                continue;
            }

            _ranges.RemoveAt(i);
            if (b > last)
            {
                _ranges.Insert(i, (last + 1, b));
            }

            if (a < first)
            {
                _ranges.Insert(i, (a, first - 1));
            }
        }
    }

    /// <summary>Whether <paramref name="index"/> is in the set.</summary>
    /// <param name="index">Index.</param>
    /// <returns>True when selected.</returns>
    public bool Contains(int index)
    {
        int lo = 0, hi = _ranges.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (index < _ranges[mid].First)
            {
                hi = mid - 1;
            }
            else if (index > _ranges[mid].Last)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A row at <paramref name="index"/> was removed: drop it and shift the indexes after it down by one.</summary>
    /// <param name="index">Removed index.</param>
    public void RemoveAt(int index)
    {
        Remove(index, index);
        for (int i = 0; i < _ranges.Count; i++)
        {
            if (_ranges[i].First > index)
            {
                _ranges[i] = (_ranges[i].First - 1, _ranges[i].Last - 1);
            }
        }

        // Shifting may make two ranges touch (the removed row was the gap between them).
        for (int i = _ranges.Count - 1; i > 0; i--)
        {
            if (_ranges[i - 1].Last + 1 >= _ranges[i].First)
            {
                _ranges[i - 1] = (_ranges[i - 1].First, Math.Max(_ranges[i - 1].Last, _ranges[i].Last));
                _ranges.RemoveAt(i);
            }
        }
    }

    /// <summary>Removes everything.</summary>
    public void Clear() => _ranges.Clear();
}
