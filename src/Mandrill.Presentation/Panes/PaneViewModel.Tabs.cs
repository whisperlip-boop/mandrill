using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mandrill.Core.IO;
using Mandrill.Core.State;

namespace Mandrill.Presentation.Panes;

// Per-pane tabs (architecture §17.2, ADR-0017): a tab is saved state; the pane shows the active tab. Activating a tab swaps
// its history and view state in and runs one navigation (NavOrigin.Tab); the request-id supersession drops the old one.
public sealed partial class PaneViewModel
{
    /// <summary>Tabs in strip order; never empty, at most <see cref="PaneState.MaxTabs"/>.</summary>
    public ObservableCollection<PaneTab> Tabs { get; } = [];

    /// <summary>The shown tab; its history is the pane's back/forward history.</summary>
    [ObservableProperty]
    public partial PaneTab ActiveTab { get; private set; }

    /// <summary>Whether a tab can be added (New, Duplicate).</summary>
    public bool CanAddTab => Tabs.Count < PaneState.MaxTabs;

    /// <summary>Whether a tab can be closed (the last one cannot).</summary>
    public bool CanCloseTab => Tabs.Count > 1;

    /// <summary>Shows <paramref name="tab"/>: its view mode, sort and history, then its folder.</summary>
    /// <param name="tab">A tab of this pane.</param>
    /// <returns>The navigation outcome; completed Committed at once when the tab is already active.</returns>
    /// <exception cref="ArgumentException"><paramref name="tab"/> is not in <see cref="Tabs"/>.</exception>
    public Task<NavigationResult> ActivateTabAsync(PaneTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        // A blank pane (a failed path superseded this tab's load) re-opens the tab's own location (review T2 m4).
        if (tab == ActiveTab && (CurrentPath is not null || IsThisPc || IsLoading))
        {
            return Task.FromResult(new NavigationResult(NavigationOutcome.Committed, tab.Location));
        }

        if (!Tabs.Contains(tab))
        {
            throw new ArgumentException("The tab does not belong to this pane.", nameof(tab));
        }

        ActiveTab.ViewMode = ViewMode;
        ActiveTab.Sort = new SortState(SortColumn, SortDescending);

        // Until the tab's folder commits the pane shows nothing (as before a first navigation), so nothing can read the
        // outgoing tab's folder as this tab's: session capture, Up, Refresh/Retry, Gone, a hide and re-show (review C1).
        StopShowingFolder();
        IsThisPc = false;
        CurrentPath = null;
        FolderError = null;
        PathError = false;
        SetActiveTab(tab);
        ViewMode = tab.ViewMode;
        SortColumn = tab.Sort.Column;
        SortDescending = tab.Sort.Descending;

        // An interrupted Back/Forward already moved the history, so its target wins over the last committed location.
        string? target = tab.History.Current ?? (tab.Location.Length > 0 ? tab.Location : _context.HomeFolder);
        return StartNavigation(target ?? PathUtil.ThisPc, NavOrigin.Tab, fallback: true);
    }

    /// <summary>Appends a tab at This PC (Details, name ascending) and shows it.</summary>
    /// <returns>The navigation outcome; Failed without a change when <see cref="CanAddTab"/> is false.</returns>
    public Task<NavigationResult> NewTabAsync()
        => CanAddTab ? InsertTab(Tabs.Count, PathUtil.ThisPc, PaneState.Default.ViewMode, SortState.Default) : Failed();

    /// <summary>Inserts a copy of <paramref name="source"/> (location, view mode, sort; not history) to its right and shows it.</summary>
    /// <param name="source">A tab of this pane.</param>
    /// <returns>The navigation outcome; Failed without a change when <see cref="CanAddTab"/> is false or the tab is unknown.</returns>
    public Task<NavigationResult> DuplicateTabAsync(PaneTab source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int index = Tabs.IndexOf(source);
        if (!CanAddTab || index < 0)
        {
            return Failed();
        }

        return source == ActiveTab
            ? InsertTab(index + 1, ShownLocation, ViewMode, new SortState(SortColumn, SortDescending))
            : InsertTab(index + 1, source.Location, source.ViewMode, source.Sort);
    }

    /// <summary>Closes <paramref name="tab"/>; closing the active tab shows its right neighbour, else its left one.</summary>
    /// <param name="tab">A tab of this pane; no-op for the last tab or an unknown one.</param>
    public void CloseTab(PaneTab tab)
    {
        int index = Tabs.IndexOf(tab);
        if (!CanCloseTab || index < 0)
        {
            return;
        }

        // Activate first: observers of the list change must never see an active tab outside Tabs (review m1).
        if (tab == ActiveTab)
        {
            _ = ActivateTabAsync(Tabs[index + 1 < Tabs.Count ? index + 1 : index - 1]);
        }

        Tabs.RemoveAt(index);
        NotifyTabs();
    }

    /// <summary>Closes every tab but <paramref name="keep"/>, which becomes the active tab.</summary>
    /// <param name="keep">A tab of this pane; no-op for an unknown one.</param>
    public void CloseOtherTabs(PaneTab keep)
    {
        if (!Tabs.Contains(keep) || Tabs.Count == 1)
        {
            return;
        }

        if (keep != ActiveTab)
        {
            _ = ActivateTabAsync(keep);
        }

        for (int i = Tabs.Count - 1; i >= 0; i--)
        {
            if (Tabs[i] != keep)
            {
                Tabs.RemoveAt(i);
            }
        }

        NotifyTabs();
    }

    /// <summary>Moves a tab within the strip (drag reorder); no navigation.</summary>
    /// <param name="from">Current index.</param>
    /// <param name="to">New index.</param>
    /// <exception cref="ArgumentOutOfRangeException">An index is outside <see cref="Tabs"/>.</exception>
    public void MoveTab(int from, int to)
    {
        if (from != to)
        {
            Tabs.Move(from, to);
            NotifyTabs();
        }
    }

    /// <summary>A layout change removed the pane: it keeps only its active tab (owner decision; no navigation).</summary>
    public void DiscardBackgroundTabs() => CloseOtherTabs(ActiveTab);

    // The active tab's location as saved and duplicated: its own target while its folder is still loading.
    private string ShownLocation => IsThisPc ? PathUtil.ThisPc : CurrentPath ?? ActiveTab.Location;

    private static Task<NavigationResult> Failed() => Task.FromResult(new NavigationResult(NavigationOutcome.Failed));

    private Task<NavigationResult> InsertTab(int index, string location, ViewMode viewMode, SortState sort)
    {
        int color = Enumerable.Range(0, PaneState.MaxTabs).First(c => Tabs.All(t => t.Color != c));
        var tab = new PaneTab(color, location, viewMode, sort);
        Tabs.Insert(index, tab);
        NotifyTabs();
        return ActivateTabAsync(tab);
    }

    // Restore: the saved tabs with empty histories; the caller then applies the top-level values and navigates.
    private void ResetTabs(PaneState state)
    {
        Tabs.Clear();
        foreach (TabState tab in state.Tabs!)
        {
            Tabs.Add(new PaneTab(tab.Color, tab.Path, tab.ViewMode, tab.Sort));
        }

        SetActiveTab(Tabs[state.ActiveTab >= 0 && state.ActiveTab < Tabs.Count ? state.ActiveTab : 0]);
        NotifyTabs();
    }

    private void SetActiveTab(PaneTab tab)
    {
        if (tab != ActiveTab)
        {
            ActiveTab.IsActive = false;
            tab.IsActive = true;
            ActiveTab = tab;
        }

        NotifyHistory();
    }

    // The Tabs notification is the session-save trigger; the collection events drive the strip.
    private void NotifyTabs()
    {
        OnPropertyChanged(nameof(Tabs));
        OnPropertyChanged(nameof(CanAddTab));
        OnPropertyChanged(nameof(CanCloseTab));
    }
}
