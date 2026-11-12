using System.Collections;
using System.Collections.Specialized;
using Mandrill.Core.Search;
using Mandrill.Presentation.Search;
using Microsoft.UI.Xaml.Data;

namespace Mandrill.App.Views;

/// <summary>
/// What the search result ListView binds to (WP-2.2b, like <see cref="PaneListAdapter"/> for panes): forwards
/// <see cref="SearchResults"/> and keeps the selection itself (<see cref="ISelectionInfo"/>) as index ranges, so selecting
/// or reading the selection never walks 100k rows (ListView.SelectedItems/SelectedRanges are banned, spike S2). The
/// selection survives an appended batch, shifts when a row is removed, and is cleared by a new search or a sort. UI thread.
/// No IItemsRangeInfo yet: visible rows' icon keys are not prioritized (the icon queue is LIFO and mostly per-extension).
/// </summary>
public sealed partial class SearchListAdapter : IList, INotifyCollectionChanged, ISelectionInfo, IDisposable
{
    private readonly SearchResults _results;
    private readonly IndexRangeSet _selection = new();

    /// <summary>Creates the adapter.</summary>
    /// <param name="results">The rows.</param>
    public SearchListAdapter(SearchResults results)
    {
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _results.CollectionChanged += OnResultsChanged;
    }

    /// <inheritdoc/>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <inheritdoc/>
    public int Count => _results.Count;

    /// <inheritdoc/>
    public bool IsFixedSize => false;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool IsSynchronized => false;

    /// <inheritdoc/>
    public object SyncRoot => this;

    /// <summary>Stops following the results (the view detached).</summary>
    public void Dispose() => _results.CollectionChanged -= OnResultsChanged;

    /// <summary>Number of selected rows.</summary>
    public int SelectedCount => _selection.Count;

    /// <inheritdoc/>
    public object? this[int index]
    {
        get => _results[index];
        set => throw new NotSupportedException();
    }

    /// <summary>The selected rows in list order.</summary>
    /// <returns>The hits.</returns>
    public IReadOnlyList<SearchHit> SelectedHits()
    {
        var hits = new List<SearchHit>();
        foreach (var (first, last) in _selection.Ranges)
        {
            for (int i = first; i <= last && i < _results.Count; i++)
            {
                hits.Add(_results[i]);
            }
        }

        return hits;
    }

    /// <inheritdoc/>
    public void SelectRange(ItemIndexRange itemIndexRange)
    {
        ArgumentNullException.ThrowIfNull(itemIndexRange);
        _selection.Add(itemIndexRange.FirstIndex, itemIndexRange.LastIndex);
    }

    /// <inheritdoc/>
    public void DeselectRange(ItemIndexRange itemIndexRange)
    {
        ArgumentNullException.ThrowIfNull(itemIndexRange);
        _selection.Remove(itemIndexRange.FirstIndex, itemIndexRange.LastIndex);
    }

    /// <inheritdoc/>
    public bool IsSelected(int index) => _selection.Contains(index);

    /// <inheritdoc/>
    public IReadOnlyList<ItemIndexRange> GetSelectedRanges()
        => [.. _selection.Ranges.Select(r => new ItemIndexRange(r.First, (uint)(r.Last - r.First + 1)))];

    /// <inheritdoc/>
    public int IndexOf(object? value) => _results.IndexOf(value);

    /// <inheritdoc/>
    public bool Contains(object? value) => _results.Contains(value);

    /// <inheritdoc/>
    public void CopyTo(Array array, int index) => _results.CopyTo(array, index);

    /// <inheritdoc/>
    public IEnumerator GetEnumerator() => ((IEnumerable)_results).GetEnumerator();

    /// <inheritdoc/>
    public int Add(object? value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public void Clear() => throw new NotSupportedException();

    /// <inheritdoc/>
    public void Insert(int index, object? value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public void Remove(object? value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public void RemoveAt(int index) => throw new NotSupportedException();

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Remove:
                _selection.RemoveAt(e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Reset when _results.LastReset != SearchResultsReset.Appended:
                _selection.Clear();
                break;
        }

        CollectionChanged?.Invoke(this, e);
    }
}
