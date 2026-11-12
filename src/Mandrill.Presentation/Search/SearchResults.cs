using System.Collections;
using System.Collections.Specialized;
using Mandrill.Core.IO;
using Mandrill.Core.Search;

namespace Mandrill.Presentation.Search;

/// <summary>Columns of the search result list (ui-spec §3).</summary>
public enum SearchColumn
{
    /// <summary>Filename.</summary>
    Name,

    /// <summary>Extension.</summary>
    Extension,

    /// <summary>Containing folder.</summary>
    Path,

    /// <summary>Size.</summary>
    Size,

    /// <summary>Date modified.</summary>
    Modified,
}

/// <summary>What a <see cref="SearchResults"/> Reset was (a selection survives an append, not a new list or a sort).</summary>
public enum SearchResultsReset
{
    /// <summary>All rows removed (a new search).</summary>
    Cleared,

    /// <summary>A batch appended at the end; earlier rows kept their positions.</summary>
    Appended,

    /// <summary>The rows were reordered (a sort).</summary>
    Replaced,

    /// <summary>Many rows removed at once (a bulk delete, qa-wp35 F1); the selection is cleared.</summary>
    Removed,
}

/// <summary>
/// The result rows of one search popup (architecture §3): a list the virtualized view projects lazily. Batches are
/// appended with one Reset each (a ListView cannot take multi-item Add notifications), rows are removed one by one (a bulk
/// delete with one Reset), and a sort replaces the whole list. UI thread only.
/// </summary>
public sealed class SearchResults : IList, IReadOnlyList<SearchHit>, INotifyCollectionChanged
{
    private List<SearchHit> _items = [];

    /// <inheritdoc/>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>Number of rows.</summary>
    public int Count => _items.Count;

    /// <summary>What the latest Reset notification was (read it in the CollectionChanged handler).</summary>
    public SearchResultsReset LastReset { get; private set; }

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool IsFixedSize => false;

    /// <inheritdoc/>
    public bool IsSynchronized => false;

    /// <inheritdoc/>
    public object SyncRoot => this;

    /// <summary>The row at <paramref name="index"/>.</summary>
    /// <param name="index">Row index.</param>
    /// <returns>The hit.</returns>
    public SearchHit this[int index] => _items[index];

    /// <inheritdoc/>
    object? IList.this[int index]
    {
        get => _items[index];
        set => throw new NotSupportedException();
    }

    /// <summary>The comparer of a column (names and paths in Explorer's order), ascending.</summary>
    /// <param name="column">Column.</param>
    /// <returns>The comparer.</returns>
    public static IComparer<SearchHit> ComparerFor(SearchColumn column) => column switch
    {
        SearchColumn.Name => Comparer<SearchHit>.Create((a, b) => FileNameComparer.Instance.Compare(a.Name, b.Name)),
        SearchColumn.Extension => Comparer<SearchHit>.Create((a, b) =>
        {
            int c = ExtensionOf(a).CompareTo(ExtensionOf(b), StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : FileNameComparer.Instance.Compare(a.Name, b.Name);
        }),
        SearchColumn.Path => Comparer<SearchHit>.Create((a, b) =>
        {
            int c = FileNameComparer.Instance.Compare(a.Directory, b.Directory);
            return c != 0 ? c : FileNameComparer.Instance.Compare(a.Name, b.Name);
        }),
        SearchColumn.Size => Comparer<SearchHit>.Create((a, b) =>
        {
            // Folders have no size: they sort before files, as in Explorer.
            int c = a.IsDirectory == b.IsDirectory ? a.Size.CompareTo(b.Size) : a.IsDirectory ? -1 : 1;
            return c != 0 ? c : FileNameComparer.Instance.Compare(a.Name, b.Name);
        }),
        _ => Comparer<SearchHit>.Create((a, b) =>
        {
            int c = a.Modified.CompareTo(b.Modified);
            return c != 0 ? c : FileNameComparer.Instance.Compare(a.Name, b.Name);
        }),
    };

    /// <summary>Removes all rows (also through <see cref="IList.Clear"/>).</summary>
    public void Clear()
    {
        if (_items.Count > 0)
        {
            _items = [];
            LastReset = SearchResultsReset.Cleared;
            RaiseReset();
        }
    }

    /// <summary>Appends a batch (one Reset).</summary>
    /// <param name="hits">Hits.</param>
    public void Append(IReadOnlyList<SearchHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count > 0)
        {
            _items.AddRange(hits);
            LastReset = SearchResultsReset.Appended;
            RaiseReset();
        }
    }

    /// <summary>Removes one row (a result that no longer exists).</summary>
    /// <param name="hit">The row.</param>
    /// <returns>True when it was there.</returns>
    public bool Remove(SearchHit hit)
    {
        int index = _items.IndexOf(hit);
        if (index < 0)
        {
            return false;
        }

        _items.RemoveAt(index);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, hit, index));
        return true;
    }

    /// <summary>
    /// Removes every row in <paramref name="hits"/> (results a command deleted). Up to <see cref="BulkRemoveThreshold"/> rows:
    /// one pass, last row first, one Remove notification each (the selection shifts as with <see cref="Remove"/>). More: the
    /// list is rebuilt in one pass with one Reset (<see cref="SearchResultsReset.Removed"/>, the selection is cleared), since
    /// a Remove per row costs O(rows) each on the UI thread (qa-wp35 F1: 450 ms for 50k of 100k).
    /// </summary>
    /// <param name="hits">Rows to remove; rows not shown are ignored.</param>
    /// <returns>The rows that were removed (last first for a small removal, in list order for a bulk one).</returns>
    public IReadOnlyList<SearchHit> RemoveAll(IReadOnlySet<SearchHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var removed = new List<SearchHit>();
        if (hits.Count > BulkRemoveThreshold)
        {
            var kept = new List<SearchHit>(_items.Count);
            foreach (var hit in _items)
            {
                (hits.Contains(hit) ? removed : kept).Add(hit);
            }

            if (removed.Count > 0)
            {
                _items = kept;
                LastReset = SearchResultsReset.Removed;
                RaiseReset();
            }

            return removed;
        }

        for (int index = _items.Count - 1; index >= 0; index--)
        {
            var hit = _items[index];
            if (hits.Contains(hit))
            {
                _items.RemoveAt(index);
                removed.Add(hit);
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, hit, index));
            }
        }

        return removed;
    }

    /// <summary>Above this many rows to remove, <see cref="RemoveAll"/> rebuilds the list with one Reset.</summary>
    public const int BulkRemoveThreshold = 1_000;

    /// <summary>A copy of the rows (to sort off the UI thread).</summary>
    /// <returns>The rows.</returns>
    public List<SearchHit> Snapshot() => [.. _items];

    /// <summary>Replaces the rows with a sorted copy made by <see cref="Snapshot"/> (one Reset).</summary>
    /// <param name="sorted">The same rows in a new order.</param>
    public void ReplaceWith(List<SearchHit> sorted)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        _items = sorted;
        LastReset = SearchResultsReset.Replaced;
        RaiseReset();
    }

    /// <inheritdoc/>
    public IEnumerator<SearchHit> GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc/>
    public bool Contains(object? value) => value is SearchHit hit && _items.Contains(hit);

    /// <inheritdoc/>
    public int IndexOf(object? value) => value is SearchHit hit ? _items.IndexOf(hit) : -1;

    /// <inheritdoc/>
    public void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    /// <inheritdoc/>
    int IList.Add(object? value) => throw new NotSupportedException();

    /// <inheritdoc/>
    void IList.Clear() => Clear();

    /// <inheritdoc/>
    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    /// <inheritdoc/>
    void IList.Remove(object? value) => throw new NotSupportedException();

    /// <inheritdoc/>
    void IList.RemoveAt(int index) => throw new NotSupportedException();

    /// <summary>
    /// The Extension column's value, shared by the cell and the sort (QA B2): none for a folder, otherwise
    /// <see cref="Path.GetExtension(ReadOnlySpan{char})"/> without its dot ("gitignore" for ".gitignore"); a lone
    /// trailing dot is none. No allocation: a 100k sort compares millions of times.
    /// </summary>
    /// <param name="hit">The result.</param>
    /// <returns>The extension, empty for none.</returns>
    public static ReadOnlySpan<char> ExtensionOf(SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (hit.IsDirectory)
        {
            return [];
        }

        var extension = Path.GetExtension(hit.Name.AsSpan());
        return extension.Length <= 1 ? [] : extension[1..];
    }

    private void RaiseReset() => CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
}
