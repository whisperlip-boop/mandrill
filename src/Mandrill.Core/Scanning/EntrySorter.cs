using Mandrill.Core.IO;
using Mandrill.Core.State;

namespace Mandrill.Core.Scanning;

/// <summary>
/// Filters (Explorer hidden/system rule, AC-1.24) and sorts entries on worker threads (architecture §5): folders first,
/// then the column key, then natural name order; descending reverses the key within each group. Sorting allocates
/// nothing per entry: column keys are read from <see cref="FileEntry"/>, and the Type column uses one rank per extension id.
/// </summary>
public sealed class EntrySorter
{
    private readonly FileNameComparer _names;
    private readonly Func<int, string> _typeName;

    /// <summary>Creates a sorter. Names compare in Explorer's order (<see cref="FileNameComparer"/>).</summary>
    /// <param name="typeName">
    /// Type name per extension id for the Type column (WP-1.6 supplies shell type names); null uses the extension text.
    /// </param>
    /// <param name="extensions">Extension table used by the default type name.</param>
    public EntrySorter(Func<int, string>? typeName = null, ExtensionTable? extensions = null)
    {
        _names = FileNameComparer.Instance;
        var table = extensions ?? ExtensionTable.Shared;
        _typeName = typeName ?? table.ExtensionOf;
    }

    /// <summary>Name comparer used for the name key and tie-breaks.</summary>
    public FileNameComparer Names => _names;

    /// <summary>Returns the visible entries of <paramref name="entries"/> in display order (new array; input untouched).</summary>
    /// <param name="entries">Entries in any order.</param>
    /// <param name="view">Filter and order.</param>
    /// <returns>Sorted visible entries.</returns>
    public FileEntry[] FilterAndSort(IReadOnlyList<FileEntry> entries, ViewSpec view) => FilterAndSort(entries, view, out _);

    /// <summary>Returns the visible entries of <paramref name="entries"/> in display order (new array; input untouched).</summary>
    /// <param name="entries">Entries in any order.</param>
    /// <param name="view">Filter and order.</param>
    /// <param name="total">Entries the hidden/system rule shows, before <see cref="ViewSpec.NameFilter"/>.</param>
    /// <returns>Sorted visible entries.</returns>
    public FileEntry[] FilterAndSort(IReadOnlyList<FileEntry> entries, ViewSpec view, out int total)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(view);
        var visible = new FileEntry[entries.Count];
        int n = 0;
        total = 0;
        foreach (var e in entries)
        {
            if (e.IsVisible(view.ShowHidden, view.ShowSystem))
            {
                total++;
                if (view.NameFilter?.IsMatch(e.Name) != false)
                {
                    visible[n++] = e;
                }
            }
        }

        Array.Resize(ref visible, n);
        Array.Sort(visible, CreateComparer(view, visible));
        return visible;
    }

    /// <summary>Merges two lists already sorted with the same <paramref name="comparer"/> (O(n + m)).</summary>
    /// <param name="a">First sorted list.</param>
    /// <param name="b">Second sorted list.</param>
    /// <param name="comparer">Comparer both are sorted by.</param>
    /// <returns>Merged sorted array.</returns>
    public static FileEntry[] Merge(FileEntry[] a, FileEntry[] b, IComparer<FileEntry> comparer)
    {
        var result = new FileEntry[a.Length + b.Length];
        int i = 0, j = 0, k = 0;
        while (i < a.Length && j < b.Length)
        {
            result[k++] = comparer.Compare(a[i], b[j]) <= 0 ? a[i++] : b[j++];
        }

        Array.Copy(a, i, result, k, a.Length - i);
        Array.Copy(b, j, result, k + a.Length - i, b.Length - j);
        return result;
    }

    /// <summary>
    /// The comparer for <paramref name="view"/>. For the Type column, type names are resolved once per distinct extension
    /// id found in <paramref name="sample"/>; ids not in the sample all rank after those, ordered by name among themselves
    /// (the final snapshot of a cold scan re-sorts with every id known).
    /// </summary>
    /// <param name="view">Order.</param>
    /// <param name="sample">Entries that will be compared (for the Type rank table).</param>
    /// <returns>The comparer.</returns>
    public IComparer<FileEntry> CreateComparer(ViewSpec view, IReadOnlyList<FileEntry> sample)
    {
        ArgumentNullException.ThrowIfNull(view);
        int[]? typeRank = view.Column == SortColumn.Type ? BuildTypeRanks(sample) : null;
        return new EntryComparer(_names, view.Column, view.Descending, typeRank);
    }

    private int[] BuildTypeRanks(IReadOnlyList<FileEntry> sample)
    {
        int maxId = 0;
        foreach (var e in sample)
        {
            maxId = Math.Max(maxId, e.ExtId);
        }

        var ids = new HashSet<int>();
        foreach (var e in sample)
        {
            if (!e.IsDirectory)
            {
                ids.Add(e.ExtId);
            }
        }

        var ordered = ids.Select(id => (Id: id, Name: _typeName(id))).OrderBy(t => t.Name, _names).ThenBy(t => t.Id).ToArray();
        var rank = new int[maxId + 1];
        Array.Fill(rank, int.MaxValue);
        for (int r = 0; r < ordered.Length; r++)
        {
            rank[ordered[r].Id] = r;
        }

        return rank;
    }

    private sealed class EntryComparer : IComparer<FileEntry>
    {
        private readonly FileNameComparer _names;
        private readonly SortColumn _column;
        private readonly int _sign;
        private readonly int[]? _typeRank;

        public EntryComparer(FileNameComparer names, SortColumn column, bool descending, int[]? typeRank)
        {
            _names = names;
            _column = column;
            _sign = descending ? -1 : 1;
            _typeRank = typeRank;
        }

        public int Compare(FileEntry? x, FileEntry? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            // Folders first in both directions (AC-1.22).
            if (x.IsDirectory != y.IsDirectory)
            {
                return x.IsDirectory ? -1 : 1;
            }

            int c = _column switch
            {
                SortColumn.Date => x.LastWriteUtcTicks.CompareTo(y.LastWriteUtcTicks),
                SortColumn.Size => x.IsDirectory ? 0 : x.Size.CompareTo(y.Size),
                SortColumn.Type => x.IsDirectory ? 0 : Rank(x.ExtId).CompareTo(Rank(y.ExtId)),
                _ => 0,
            };
            if (c == 0)
            {
                c = _names.Compare(x.Name, y.Name);
            }

            return _sign * c;
        }

        private int Rank(int extId) => _typeRank is not null && (uint)extId < (uint)_typeRank.Length ? _typeRank[extId] : int.MaxValue;
    }
}
