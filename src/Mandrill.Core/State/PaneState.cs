using System.Text.Json.Serialization;

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

/// <summary>Persisted state of one pane (architecture §10, AC-1.33). Back/forward history is not persisted.</summary>
/// <param name="Path">Folder shown; "" = none (the pane opens the home folder).</param>
/// <param name="ViewMode">View mode.</param>
/// <param name="Sort">Sort order.</param>
/// <param name="TreeVisible">Whether the tree panel is expanded.</param>
/// <param name="TreeWidth">Tree panel width in DIPs.</param>
/// <param name="IconSize">Icons-view icon size in px (16–256).</param>
/// <param name="Columns">Details column widths.</param>
/// <param name="PathHistory">Path-box history, most recent first, ≤20 (AC-1.18).</param>
public sealed record PaneState(
    string Path,
    ViewMode ViewMode,
    SortState Sort,
    bool TreeVisible,
    double TreeWidth,
    int IconSize,
    ColumnWidths Columns,
    IReadOnlyList<string> PathHistory)
{
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

        SortState sort = Fixes.Check(fixes, owner + ".sort", Sort, Sort is not null, SortState.Default);
        return new PaneState(
            Fixes.Check(fixes, owner + ".path", Path, Path is not null, string.Empty),
            Fixes.Check(fixes, owner + ".viewMode", ViewMode, Enum.IsDefined(ViewMode), Default.ViewMode),
            sort with { Column = Fixes.Check(fixes, owner + ".sort.column", sort.Column, Enum.IsDefined(sort.Column), SortState.Default.Column) },
            TreeVisible,
            Fixes.Check(fixes, owner + ".treeWidth", TreeWidth, Fixes.IsPositive(TreeWidth), Default.TreeWidth),
            Fixes.Check(fixes, owner + ".iconSize", IconSize, IconSize is >= MinIconSize and <= MaxIconSize, Default.IconSize),
            Fixes.Check(fixes, owner + ".columns", Columns, Columns is not null, ColumnWidths.Default).Normalize(fixes, owner),
            history);
    }
}
