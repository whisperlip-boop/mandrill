using Mandrill.Core.IO;
using Mandrill.Core.Scanning;

namespace Mandrill.Presentation.Panes;

/// <summary>
/// Selection of one pane, keyed by entry name (Ordinal) so it survives Reset and diffs without a restore step (ADR-0002).
/// Either "all selected" plus exclusions, or a set of inclusions; Ctrl+A at 100k is O(1). Indexes refer to the owning
/// <see cref="PaneItemsSource"/> as the list control currently sees it. UI thread only.
/// </summary>
public sealed class SelectionModel
{
    // Inclusions, or exclusions while _all is set.
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly PaneItemsSource _owner;
    private EntrySnapshot? _snapshot;
    private bool _all;

    internal SelectionModel(PaneItemsSource owner) => _owner = owner;

    /// <summary>Raised after the selection changed.</summary>
    public event Action? Changed;

    /// <summary>Number of selected entries (status bar; never ListView.SelectedItems).</summary>
    public int Count => _all ? (_snapshot?.Visible.Count ?? 0) - _names.Count : _names.Count;

    /// <summary>Whether the entry at <paramref name="index"/> of the shown list is selected.</summary>
    /// <param name="index">Index in the shown list.</param>
    /// <returns>False for an index outside the list.</returns>
    public bool IsSelected(int index)
        => (uint)index < (uint)_owner.Count && _owner[index] is FileEntry e && IsSelected(e.Name);

    /// <summary>Whether the entry named <paramref name="name"/> (a name in the shown list) is selected.</summary>
    /// <param name="name">Entry name (exact, Ordinal).</param>
    /// <returns>True when selected.</returns>
    public bool IsSelected(string name) => _snapshot is { } s && s.IndexOfName(name) >= 0 && (_all ^ _names.Contains(name));

    /// <summary>Selects <paramref name="count"/> entries starting at <paramref name="start"/>; out-of-range parts are ignored.</summary>
    /// <param name="start">First index.</param>
    /// <param name="count">Number of entries.</param>
    public void Select(int start, int count) => SetRange(start, count, true);

    /// <summary>Deselects <paramref name="count"/> entries starting at <paramref name="start"/>; out-of-range parts are ignored.</summary>
    /// <param name="start">First index.</param>
    /// <param name="count">Number of entries.</param>
    public void Deselect(int start, int count) => SetRange(start, count, false);

    /// <summary>Selects everything shown (O(1)).</summary>
    public void SelectAll()
    {
        if (_snapshot is null || (_all && _names.Count == 0))
        {
            return;
        }

        _all = true;
        _names.Clear();
        Changed?.Invoke();
    }

    /// <summary>Clears the selection.</summary>
    public void Clear()
    {
        if (!_all && _names.Count == 0)
        {
            return;
        }

        _all = false;
        _names.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// Selected index ranges (first, count) of the shown list in ascending order, built from the names the model holds:
    /// O(k log k) for k selected (or excluded) names instead of a pass over the whole list. Null while the list is being
    /// updated item by item (<see cref="PaneItemsSource.Apply"/>), when indexes of the bound snapshot do not match the
    /// list the control sees; callers then fall back to <see cref="IsSelected(int)"/>.
    /// </summary>
    /// <returns>The ranges, or null.</returns>
    public IReadOnlyList<(int First, int Count)>? SelectedRanges()
    {
        if (_snapshot is not { } s)
        {
            return [];
        }

        if (!ReferenceEquals(_owner.Current, s))
        {
            return null;
        }

        var marked = new List<int>(_names.Count);
        foreach (string name in _names)
        {
            int i = s.IndexOfName(name);
            if (i >= 0)
            {
                marked.Add(i);
            }
        }

        marked.Sort();
        var ranges = new List<(int First, int Count)>();
        if (!_all)
        {
            // Inclusions: runs of consecutive indexes.
            for (int k = 0; k < marked.Count;)
            {
                int start = marked[k];
                int end = start;
                while (++k < marked.Count && marked[k] == end + 1)
                {
                    end++;
                }

                ranges.Add((start, end - start + 1));
            }

            return ranges;
        }

        // Exclusions under "all selected": the gaps between them.
        int next = 0;
        foreach (int excluded in marked)
        {
            if (excluded > next)
            {
                ranges.Add((next, excluded - next));
            }

            next = excluded + 1;
        }

        if (next < s.Visible.Count)
        {
            ranges.Add((next, s.Visible.Count - next));
        }

        return ranges;
    }

    /// <summary>Total size in bytes of the selected files (folders count 0), without allocating. O(k), or O(n) when everything is selected.</summary>
    /// <returns>The sum.</returns>
    public long SelectedBytes()
    {
        if (_snapshot is not { } s)
        {
            return 0;
        }

        long total = 0;
        if (_all)
        {
            foreach (FileEntry e in s.Visible)
            {
                if (!e.IsDirectory && !_names.Contains(e.Name))
                {
                    total += e.Size;
                }
            }

            return total;
        }

        foreach (string name in _names)
        {
            int i = s.IndexOfName(name);
            if (i >= 0 && s.Visible[i] is { IsDirectory: false } e)
            {
                total += e.Size;
            }
        }

        return total;
    }

    /// <summary>Selected entries in view order. O(k log k), or O(n) when everything is selected.</summary>
    /// <returns>The entries.</returns>
    public IReadOnlyList<FileEntry> SelectedEntries()
    {
        if (_snapshot is not { } s)
        {
            return [];
        }

        if (_all)
        {
            var all = new List<FileEntry>(Math.Max(0, Count));
            foreach (FileEntry e in s.Visible)
            {
                if (!_names.Contains(e.Name))
                {
                    all.Add(e);
                }
            }

            return all;
        }

        var indices = new List<int>(_names.Count);
        foreach (string name in _names)
        {
            int i = s.IndexOfName(name);
            if (i >= 0)
            {
                indices.Add(i);
            }
        }

        indices.Sort();
        var result = new FileEntry[indices.Count];
        for (int k = 0; k < result.Length; k++)
        {
            result[k] = s.Visible[indices[k]];
        }

        return result;
    }

    /// <summary>
    /// Binds to the snapshot about to be shown. Same folder: names that disappeared are pruned, and with "all selected"
    /// entries that were not in the previous snapshot are excluded, so new files do not appear selected (Explorer
    /// behaviour). Another folder or nothing: the selection is cleared.
    /// </summary>
    internal void Bind(EntrySnapshot? snapshot, bool sameFolder)
    {
        EntrySnapshot? previous = _snapshot;
        int before = Count;
        _snapshot = snapshot;
        if (snapshot is null || previous is null || !sameFolder)
        {
            bool had = _all || _names.Count > 0;
            _all = false;
            _names.Clear();
            if (had)
            {
                Changed?.Invoke();
            }

            return;
        }

        bool pruned = _names.Count > 0 && _names.RemoveWhere(n => snapshot.IndexOfName(n) < 0) > 0;
        if (_all)
        {
            foreach (FileEntry e in snapshot.Visible)
            {
                if (previous.IndexOfName(e.Name) < 0)
                {
                    _names.Add(e.Name);
                }
            }
        }

        // New names never change the selected set (they are excluded under "all", absent from inclusions). A removed
        // selected entry lowers Count; a removed exclusion or inclusion is pruned. So these two cover every change.
        if (pruned || Count != before)
        {
            Changed?.Invoke();
        }
    }

    private void SetRange(int start, int count, bool select)
    {
        if (_snapshot is null || count <= 0)
        {
            return;
        }

        int end = (int)Math.Min(_owner.Count, (long)start + count);
        bool changed = false;
        for (int i = Math.Max(0, start); i < end; i++)
        {
            if (_owner[i] is not FileEntry e)
            {
                continue;
            }

            // In "all" mode the set holds exclusions, so selecting removes and deselecting adds.
            changed |= select ^ _all ? _names.Add(e.Name) : _names.Remove(e.Name);
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }
}
