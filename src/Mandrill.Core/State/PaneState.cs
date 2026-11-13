using System.Text.Json.Serialization;
using Mandrill.Core.IO;

namespace Mandrill.Core.State;

/// <summary>Pane view mode (AC-1.21). Persisted as "icons" / "details" / "list".</summary>
public enum ViewMode
{
    /// <summary>Icons (GridView, Ctrl+wheel size).</summary>
    [JsonStringEnumMemberName("icons")]
    Icons,

    /// <summary>Details (columns).</summary>
    [JsonStringEnumMemberName("details")]
    Details,

    /// <summary>List (16 px icons in columns).</summary>
    [JsonStringEnumMemberName("list")]
    List,
}

/// <summary>Sort column (AC-1.22). Persisted as "name" / "date" / "type" / "size"; reused by Core.Scanning.</summary>
public enum SortColumn
{
    /// <summary>Name (natural order).</summary>
    [JsonStringEnumMemberName("name")]
    Name,

    /// <summary>Last write time.</summary>
    [JsonStringEnumMemberName("date")]
    Date,

    /// <summary>Shell type name.</summary>
    [JsonStringEnumMemberName("type")]
    Type,

    /// <summary>File size.</summary>
    [JsonStringEnumMemberName("size")]
    Size,
}

/// <summary>Sort order of a pane.</summary>
/// <param name="Column">Sort column.</param>
/// <param name="Descending">True for descending.</param>
public sealed record SortState(SortColumn Column, bool Descending)
{
    /// <summary>Name, ascending.</summary>
    public static SortState Default { get; } = new(SortColumn.Name, false);

    internal static SortState Normalize(SortState? sort, ICollection<string> fixes, string owner)
    {
        SortState valid = Fixes.Check(fixes, owner + ".sort", sort, sort is not null, Default)!;
        return valid with { Column = Fixes.Check(fixes, owner + ".sort.column", valid.Column, Enum.IsDefined(valid.Column), Default.Column) };
    }
}

/// <summary>Details column widths in DIPs.</summary>
/// <param name="Name">Name column.</param>
/// <param name="Date">Date modified column.</param>
/// <param name="Type">Type column.</param>
/// <param name="Size">Size column.</param>
public sealed record ColumnWidths(double Name, double Date, double Type, double Size)
{
    /// <summary>Initial widths (WP-1.8 may tune them).</summary>
    public static ColumnWidths Default { get; } = new(280, 150, 140, 100);

    internal ColumnWidths Normalize(ICollection<string> fixes, string owner) => new(
        Fixes.Check(fixes, owner + ".columns.name", Name, Fixes.IsPositive(Name), Default.Name),
        Fixes.Check(fixes, owner + ".columns.date", Date, Fixes.IsPositive(Date), Default.Date),
        Fixes.Check(fixes, owner + ".columns.type", Type, Fixes.IsPositive(Type), Default.Type),
        Fixes.Check(fixes, owner + ".columns.size", Size, Fixes.IsPositive(Size), Default.Size));
}

/// <summary>Persisted state of one pane tab (architecture §17.3). Back/forward history is not persisted.</summary>
/// <param name="Path">Folder shown; "" = home folder, <see cref="PathUtil.ThisPc"/> = This PC.</param>
/// <param name="ViewMode">View mode.</param>
/// <param name="Sort">Sort order.</param>
/// <param name="Color">Palette index 0..9, fixed for the tab's life.</param>
public sealed record TabState(string Path, ViewMode ViewMode, SortState Sort, int Color);

/// <summary>Persisted state of one pane (architecture §10, §17.3, AC-1.33). Back/forward history is not persisted.</summary>
/// <param name="Path">Folder shown; "" = none (the pane opens the home folder).</param>
/// <param name="ViewMode">View mode.</param>
/// <param name="Sort">Sort order.</param>
/// <param name="TreeVisible">Whether the tree panel is expanded.</param>
/// <param name="TreeWidth">Tree panel width in DIPs.</param>
/// <param name="IconSize">Icons-view icon size in px (16–256).</param>
/// <param name="Columns">Details column widths.</param>
/// <param name="PathHistory">Path-box history, most recent first, ≤20 (AC-1.18).</param>
/// <param name="Tabs">Tabs in strip order, 1..<see cref="MaxTabs"/>; null = one tab from the top-level values (pre-tabs file).</param>
/// <param name="ActiveTab">Index of the shown tab; <paramref name="Path"/>, <paramref name="ViewMode"/> and <paramref name="Sort"/> are its values.</param>
public sealed record PaneState(
    string Path,
    ViewMode ViewMode,
    SortState Sort,
    bool TreeVisible,
    double TreeWidth,
    int IconSize,
    ColumnWidths Columns,
    IReadOnlyList<string> PathHistory,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TabState>? Tabs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int ActiveTab = 0)
{
    /// <summary>Most tabs per pane.</summary>
    public const int MaxTabs = 10;

    /// <summary>Smallest icons-view icon size.</summary>
    public const int MinIconSize = 16;

    /// <summary>Largest icons-view icon size.</summary>
    public const int MaxIconSize = 256;

    /// <summary>A pane with no path, Details view, name ascending, tree shown.</summary>
    public static PaneState Default { get; } = new(string.Empty, ViewMode.Details, SortState.Default, true, 240, 48, ColumnWidths.Default, []);

    internal PaneState Normalize(ICollection<string> fixes, string owner)
    {
        IReadOnlyList<string> history = PathHistoryList.Normalize(PathHistory);
        if (PathHistory is null || history.Count != PathHistory.Count)
        {
            fixes.Add(owner + ".pathHistory: removed empty, duplicate or excess entries");
        }

        string path = Fixes.Check(fixes, owner + ".path", Path, Path is not null, string.Empty);
        ViewMode viewMode = Fixes.Check(fixes, owner + ".viewMode", ViewMode, Enum.IsDefined(ViewMode), Default.ViewMode);
        SortState sort = SortState.Normalize(Sort, fixes, owner);
        var (tabs, activeTab) = NormalizeTabs(fixes, owner, new TabState(path, viewMode, sort, 0));
        return new PaneState(
            path,
            viewMode,
            sort,
            TreeVisible,
            Fixes.Check(fixes, owner + ".treeWidth", TreeWidth, Fixes.IsPositive(TreeWidth), Default.TreeWidth),
            Fixes.Check(fixes, owner + ".iconSize", IconSize, IconSize is >= MinIconSize and <= MaxIconSize, Default.IconSize),
            Fixes.Check(fixes, owner + ".columns", Columns, Columns is not null, ColumnWidths.Default).Normalize(fixes, owner),
            history,
            tabs,
            activeTab);
    }

    // The top-level values win for the active tab; background tabs are not checked on disk (§17.3).
    private (IReadOnlyList<TabState>? Tabs, int ActiveTab) NormalizeTabs(ICollection<string> fixes, string owner, TabState shown)
    {
        if (Tabs is null)
        {
            return (null, Fixes.Check(fixes, owner + ".activeTab", ActiveTab, ActiveTab == 0, 0));
        }

        var tabs = new List<TabState>();
        int activeTab = ActiveTab;
        foreach (TabState? tab in Tabs)
        {
            if (tab is null)
            {
                fixes.Add(owner + ".tabs: removed a null entry");
                if (tabs.Count < activeTab)
                {
                    activeTab--; // the index keeps pointing at the same tab
                }
            }
            else if (tabs.Count == MaxTabs)
            {
                fixes.Add($"{owner}.tabs: removed entries beyond {MaxTabs}");
                break;
            }
            else
            {
                tabs.Add(tab);
            }
        }

        if (tabs.Count == 0)
        {
            fixes.Add(owner + ".tabs: empty list → one tab");
            return ([shown], 0);
        }

        int active = Fixes.Check(fixes, owner + ".activeTab", activeTab, activeTab >= 0 && activeTab < tabs.Count, 0);
        var kept = new bool[tabs.Count];
        var used = new HashSet<int>();
        for (int i = 0; i < tabs.Count; i++)
        {
            kept[i] = tabs[i].Color is >= 0 and < MaxTabs && used.Add(tabs[i].Color);
        }

        for (int i = 0; i < tabs.Count; i++)
        {
            string tabOwner = $"{owner}.tabs[{i}]";
            TabState tab = tabs[i];
            int color = tab.Color;
            if (!kept[i])
            {
                color = Enumerable.Range(0, MaxTabs).First(c => !used.Contains(c));
                used.Add(color);
                fixes.Add($"{tabOwner}.color: '{tab.Color}' (out of range or duplicate) → '{color}'");
            }

            if (i == active)
            {
                tabs[i] = shown with { Color = color };
                continue;
            }

            // A background "" (a tab left before its first folder loaded) opens This PC; only null is a repair worth a warning.
            string path = Fixes.Check(fixes, tabOwner + ".path", tab.Path, tab.Path is not null, PathUtil.ThisPc);
            tabs[i] = new TabState(
                path.Length > 0 ? path : PathUtil.ThisPc,
                Fixes.Check(fixes, tabOwner + ".viewMode", tab.ViewMode, Enum.IsDefined(tab.ViewMode), Default.ViewMode),
                SortState.Normalize(tab.Sort, fixes, tabOwner),
                color);
        }

        return (tabs, active);
    }
}
