using CommunityToolkit.Mvvm.ComponentModel;
using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.State;
using Mandrill.Presentation.Navigation;

namespace Mandrill.Presentation.Panes;

/// <summary>
/// One tab of a pane (architecture §17.2): saved state only. The pane shows the active tab; a background tab keeps its
/// location, view mode, sort and back/forward history in memory and has no scan, listing or watcher. UI thread only.
/// </summary>
public sealed partial class PaneTab : ObservableObject
{
    internal PaneTab(int color, string location, ViewMode viewMode, SortState sort)
    {
        Color = color;
        Location = location;
        ViewMode = viewMode;
        Sort = sort;
    }

    /// <summary>Palette index 0..9, fixed for the tab's life.</summary>
    public int Color { get; }

    /// <summary>Committed folder, <see cref="PathUtil.ThisPc"/>, or "" (the home folder, opened on activation).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ToolTip))]
    public partial string Location { get; internal set; } = string.Empty;

    /// <summary>Whether this is the pane's shown tab.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; internal set; }

    /// <summary>Tab text: the folder's last segment, a drive root as "C:\", This PC by its localized name.</summary>
    public string Title => PathUtil.IsThisPc(Location) ? Localizer.Current["thispc.name"] : PathUtil.LeafName(Location);

    /// <summary>Full path; This PC by its localized name.</summary>
    public string ToolTip => PathUtil.IsThisPc(Location) ? Localizer.Current["thispc.name"] : Location;

    // Valid while the tab is in the background; the active tab's live values are the pane's.
    internal ViewMode ViewMode { get; set; }

    internal SortState Sort { get; set; }

    internal NavHistory History { get; } = new();

    internal TabState ToState() => new(Location, ViewMode, Sort, Color);
}
