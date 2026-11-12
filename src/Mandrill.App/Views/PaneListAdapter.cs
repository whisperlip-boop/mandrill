using System.Collections;
using System.Collections.Specialized;
using Mandrill.Core.IO;
using Mandrill.Presentation.Panes;
using Microsoft.UI.Xaml.Data;

namespace Mandrill.App.Views;

/// <summary>
/// The list controls' ItemsSource (architecture §5, spike S2): forwards <see cref="IList"/>/<see cref="INotifyCollectionChanged"/>
/// to <see cref="PaneItemsSource"/> and implements <see cref="ISelectionInfo"/>/<see cref="IItemsRangeInfo"/> over its
/// name-keyed <see cref="SelectionModel"/>, so Ctrl+A at 100k is O(1) and selection survives Reset. UI thread only.
/// </summary>
public sealed partial class PaneListAdapter : IList, INotifyCollectionChanged, ISelectionInfo, IItemsRangeInfo
{
    private readonly PaneItemsSource _items;

    /// <summary>Wraps <paramref name="items"/>.</summary>
    /// <param name="items">The pane's list.</param>
    public PaneListAdapter(PaneItemsSource items)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _items.CollectionChanged += (_, e) => CollectionChanged?.Invoke(this, e);
    }

    /// <inheritdoc/>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>The control reported a new visible range (first, last index); feeds icon priority.</summary>
    public event Action<int, int>? VisibleRangeChanged;

    /// <summary>The wrapped list.</summary>
    public PaneItemsSource Items => _items;

    /// <inheritdoc/>
    public int Count => _items.Count;

    /// <inheritdoc/>
    public bool IsFixedSize => false;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool IsSynchronized => false;

    /// <inheritdoc/>
    public object SyncRoot => this;

    /// <summary>IndexOf calls since the last <see cref="ResetCounters"/> (diagnostics; architecture §15 measurement).</summary>
    public int IndexOfCalls { get; private set; }

    /// <summary>IndexOf calls that missed (returned -1) since the last <see cref="ResetCounters"/>.</summary>
    public int IndexOfMisses { get; private set; }

    /// <summary>GetSelectedRanges calls since the last <see cref="ResetCounters"/> (diagnostics; O(n) for partial selections).</summary>
    public int GetSelectedRangesCalls { get; private set; }

    /// <summary>Last visible range from <see cref="RangesChanged"/> (diagnostics, tests).</summary>
    public (int First, int Last) LastVisibleRange { get; private set; } = (-1, -1);

    /// <inheritdoc/>
    public object? this[int index]
    {
        get => _items[index];
        set => throw new NotSupportedException();
    }

    /// <summary>Clears the measurement counters.</summary>
    public void ResetCounters()
    {
        IndexOfCalls = 0;
        IndexOfMisses = 0;
        GetSelectedRangesCalls = 0;
    }

    /// <inheritdoc/>
    public int IndexOf(object? value)
    {
        IndexOfCalls++;
        int i = _items.IndexOf(value);
        if (i < 0)
        {
            IndexOfMisses++;
        }

        return i;
    }

    /// <inheritdoc/>
    public bool Contains(object? value) => _items.Contains(value);

    /// <inheritdoc/>
    public void CopyTo(Array array, int index) => _items.CopyTo(array, index);

    /// <inheritdoc/>
    public IEnumerator GetEnumerator() => _items.GetEnumerator();

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

    /// <inheritdoc/>
    public void SelectRange(ItemIndexRange itemIndexRange)
    {
        ArgumentNullException.ThrowIfNull(itemIndexRange);
        if (CoversAll(itemIndexRange))
        {
            _items.Selection.SelectAll(); // Ctrl+A: O(1), no per-item work (S2)
        }
        else
        {
            _items.Selection.Select(itemIndexRange.FirstIndex, (int)itemIndexRange.Length);
        }
    }

    /// <inheritdoc/>
    public void DeselectRange(ItemIndexRange itemIndexRange)
    {
        ArgumentNullException.ThrowIfNull(itemIndexRange);
        if (CoversAll(itemIndexRange))
        {
            _items.Selection.Clear();
        }
        else
        {
            _items.Selection.Deselect(itemIndexRange.FirstIndex, (int)itemIndexRange.Length);
        }
    }

    /// <inheritdoc/>
    public bool IsSelected(int index) => _items.Selection.IsSelected(index);

    /// <inheritdoc/>
    public IReadOnlyList<ItemIndexRange> GetSelectedRanges()
    {
        GetSelectedRangesCalls++;
        int count = _items.Count;
        int selectedCount = _items.Selection.Count;
        if (selectedCount == 0 || count == 0)
        {
            return [];
        }

        if (selectedCount >= count)
        {
            return [new ItemIndexRange(0, (uint)count)]; // Ctrl+A: no per-item scan
        }

        if (_items.Selection.SelectedRanges() is { } fast)
        {
            // O(k log k) from the selection's names (a 100k pass cost 9–11 ms per selection change, WP-1.8 review).
            return fast.Select(r => new ItemIndexRange(r.First, (uint)r.Count)).ToArray();
        }

        // Mid-update (per-item Apply): the exact per-index check over the list the control sees.
        var ranges = new List<ItemIndexRange>();
        int start = -1;
        for (int i = 0; i <= count; i++)
        {
            bool selected = i < count && _items.Selection.IsSelected(i);
            if (selected && start < 0)
            {
                start = i;
            }
            else if (!selected && start >= 0)
            {
                ranges.Add(new ItemIndexRange(start, (uint)(i - start)));
                start = -1;
            }
        }

        return ranges;
    }

    /// <inheritdoc/>
    public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        ArgumentNullException.ThrowIfNull(visibleRange);
        LastVisibleRange = (visibleRange.FirstIndex, visibleRange.LastIndex);
        VisibleRangeChanged?.Invoke(visibleRange.FirstIndex, visibleRange.LastIndex);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // IItemsRangeInfo is IDisposable; the adapter holds no native resources.
    }

    /// <summary>The entry at <paramref name="index"/>, or null when out of range.</summary>
    /// <param name="index">Index.</param>
    /// <returns>The entry.</returns>
    public FileEntry? EntryAt(int index) => (uint)index < (uint)_items.Count ? _items[index] as FileEntry : null;

    private bool CoversAll(ItemIndexRange range) => range.FirstIndex <= 0 && range.LastIndex >= _items.Count - 1 && _items.Count > 0;
}
