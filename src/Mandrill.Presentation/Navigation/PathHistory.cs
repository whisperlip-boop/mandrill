using Mandrill.Core.State;

namespace Mandrill.Presentation.Navigation;

/// <summary>
/// Path-box dropdown history of one pane (AC-1.18): most recent first, de-duplicated case-insensitively, at most
/// <see cref="PathHistoryList.MaxEntries"/>. Persisted in <see cref="PaneState.PathHistory"/>. UI thread only.
/// </summary>
public sealed class PathHistory
{
    private readonly List<string> _items;

    /// <summary>Creates an empty history.</summary>
    public PathHistory()
        : this(null)
    {
    }

    /// <summary>Creates a history from persisted entries (normalized: empty and duplicate entries dropped, capped).</summary>
    /// <param name="persisted">Entries, most recent first.</param>
    public PathHistory(IEnumerable<string?>? persisted)
    {
        _items = [.. PathHistoryList.Normalize(persisted)];
        Items = _items.AsReadOnly();
    }

    /// <summary>Live read-only view, most recent first.</summary>
    public IReadOnlyList<string> Items { get; }

    /// <summary>Puts <paramref name="path"/> first, removing an earlier entry for the same path and the oldest beyond the cap.</summary>
    /// <param name="path">Successfully navigated path.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or white space.</exception>
    public void Add(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        int existing = _items.FindIndex(p => PathHistoryList.Comparer.Equals(p, path));
        if (existing >= 0)
        {
            _items.RemoveAt(existing);
        }

        _items.Insert(0, path);
        if (_items.Count > PathHistoryList.MaxEntries)
        {
            _items.RemoveRange(PathHistoryList.MaxEntries, _items.Count - PathHistoryList.MaxEntries);
        }
    }

    /// <summary>Copy for persistence (unaffected by later changes).</summary>
    /// <returns>The entries, most recent first.</returns>
    public IReadOnlyList<string> Snapshot() => [.. _items];
}
