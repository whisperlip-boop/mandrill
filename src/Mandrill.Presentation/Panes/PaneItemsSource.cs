using System.Collections;
using System.Collections.Specialized;
using Mandrill.Core.IO;
using Mandrill.Core.Scanning;

namespace Mandrill.Presentation.Panes;

/// <summary>
/// Read-only list over the shown <see cref="EntrySnapshot"/> for ListView/GridView (architecture §5, ADR-0002). The items are
/// the <see cref="FileEntry"/> objects. A diff against the shown version raises per-item notifications; anything else
/// raises one Reset. UI thread only.
/// </summary>
public sealed class PaneItemsSource : IList, INotifyCollectionChanged
{
    // Non-null only while Apply raises per-item notifications: the list must match each notification as it is raised.
    private List<FileEntry>? _working;

    /// <summary>Creates an empty source.</summary>
    public PaneItemsSource() => Selection = new SelectionModel(this);

    /// <summary>Raised for every list change (Reset or per-item).</summary>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>Raised just before a Reset (the view captures its fallback scroll anchor).</summary>
    public event Action? Resetting;

    /// <summary>Raised just after a Reset.</summary>
    public event Action? Reset;

    /// <summary>The shown snapshot; null when the pane shows nothing (error state, before the first navigation).</summary>
    public EntrySnapshot? Current { get; private set; }

    /// <summary>Selection, pruned to the present names on every change.</summary>
    public SelectionModel Selection { get; }

    /// <inheritdoc/>
    public int Count => _working?.Count ?? Current?.Visible.Count ?? 0;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool IsFixedSize => false;

    /// <inheritdoc/>
    public bool IsSynchronized => false;

    /// <inheritdoc/>
    public object SyncRoot => this;

    /// <inheritdoc/>
    public object? this[int index]
    {
        get => _working is { } w ? w[index] : (Current?.Visible ?? throw new ArgumentOutOfRangeException(nameof(index)))[index];
        set => throw new NotSupportedException();
    }

    /// <summary>Shows <paramref name="snapshot"/> with one Reset. A different folder clears the selection.</summary>
    /// <param name="snapshot">Snapshot to show.</param>
    public void Replace(EntrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool sameFolder = Current is not null && PathUtil.PathEquals(Current.Path, snapshot.Path);
        Resetting?.Invoke();
        Current = snapshot;
        Selection.Bind(snapshot, sameFolder);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        Reset?.Invoke();
    }

    /// <summary>
    /// Shows <paramref name="snapshot"/>: per-item Remove/Add/Replace when its diff applies to the shown version
    /// (scroll and selection stay native), otherwise <see cref="Replace"/>.
    /// </summary>
    /// <param name="snapshot">Snapshot to show.</param>
    public void Apply(EntrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Current is not { } current || snapshot.Diff is not { } diff || diff.BaseVersion != current.Version
            || !PathUtil.PathEquals(current.Path, snapshot.Path) || !IsConsistent(diff, current, snapshot))
        {
            Replace(snapshot);
            return;
        }

        // Selection first: realized containers may ask IsSelected while the notifications below are raised.
        Selection.Bind(snapshot, sameFolder: true);
        var working = new List<FileEntry>(current.Visible);
        _working = working;
        try
        {
            foreach (int i in diff.RemovedOld)
            {
                FileEntry removed = working[i];
                working.RemoveAt(i);
                Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, i));
            }

            foreach (var (i, entry) in diff.Inserted)
            {
                working.Insert(i, entry);
                Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, entry, i));
            }

            foreach (int i in diff.ChangedNew)
            {
                FileEntry old = working[i];
                FileEntry updated = snapshot.Visible[i];
                working[i] = updated;
                Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, updated, old, i));
            }
        }
        finally
        {
            _working = null;
            Current = snapshot;
        }
    }

    /// <summary>Shows nothing (error state). Clears the selection.</summary>
    public void Clear()
    {
        if (Current is null)
        {
            return;
        }

        Resetting?.Invoke();
        Current = null;
        Selection.Bind(null, sameFolder: false);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        Reset?.Invoke();
    }

    /// <summary>
    /// O(1) by name through the snapshot's name map: an older object of the same entry (re-listed, or modified in size,
    /// time or attributes) keeps its index. The controls anchor their scroll position on a Reset through this call; WP-1.8
    /// measured value matching against name matching at 100k rows (row 50,000, entries around the anchor modified):
    /// value matching jumped to the top (shift -49,999, 22 of 23 calls missed), name matching kept the row (shift 0).
    /// A same-named entry of another folder also matches; the view scrolls to the top on a folder change instead.
    /// Linear (by reference/value) only while <see cref="Apply"/> is raising notifications.
    /// </summary>
    /// <param name="value">A <see cref="FileEntry"/>.</param>
    /// <returns>The index, or -1.</returns>
    public int IndexOf(object? value)
    {
        if (value is not FileEntry entry)
        {
            return -1;
        }

        if (_working is { } w)
        {
            return w.IndexOf(entry);
        }

        if (Current is not { } s)
        {
            return -1;
        }

        return s.IndexOfName(entry.Name);
    }

    /// <inheritdoc/>
    public bool Contains(object? value) => IndexOf(value) >= 0;

    /// <inheritdoc/>
    public void CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (int i = 0; i < Count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    /// <inheritdoc/>
    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    /// <inheritdoc/>
    public int Add(object? value) => throw new NotSupportedException();

    /// <inheritdoc/>
    void IList.Clear() => throw new NotSupportedException();

    /// <inheritdoc/>
    public void Insert(int index, object? value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public void Remove(object? value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public void RemoveAt(int index) => throw new NotSupportedException();

    // A diff that does not fit the shown list would desynchronize the control; fall back to Reset instead.
    private static bool IsConsistent(EntryDiff diff, EntrySnapshot current, EntrySnapshot next)
    {
        int count = current.Visible.Count;
        int previous = int.MaxValue;
        foreach (int i in diff.RemovedOld)
        {
            if (i < 0 || i >= count || i >= previous)
            {
                return false;
            }

            previous = i;
        }

        count -= diff.RemovedOld.Length;
        previous = -1;
        foreach (var (i, _) in diff.Inserted)
        {
            if (i <= previous || i > count)
            {
                return false;
            }

            previous = i;
            count++;
        }

        return count == next.Visible.Count && diff.ChangedNew.All(i => i >= 0 && i < count);
    }

    private void Raise(NotifyCollectionChangedEventArgs e) => CollectionChanged?.Invoke(this, e);
}
