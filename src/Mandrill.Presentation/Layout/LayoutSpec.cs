namespace Mandrill.Presentation.Layout;

/// <summary>Cell range of one pane in the 3×3 layout grid [pane, splitter, pane] (architecture §6, ADR-0012).</summary>
/// <param name="Row">First grid row (0 or 2).</param>
/// <param name="Column">First grid column (0 or 2).</param>
/// <param name="RowSpan">Rows spanned (1 or 3).</param>
/// <param name="ColumnSpan">Columns spanned (1 or 3).</param>
public sealed record PanePlacement(int Row, int Column, int RowSpan = 1, int ColumnSpan = 1);

/// <summary>Range of grid tracks a splitter covers along its length.</summary>
/// <param name="Start">First track.</param>
/// <param name="Span">Number of tracks.</param>
public sealed record TrackSpan(int Start, int Span);

/// <summary>
/// Grid placement of one layout. Panes are in pane order (reading order of their top-left cells: Pane 1…n, Alt+1…4).
/// The column splitter sits in grid column 1 and spans <see cref="ColumnSplitter"/> rows; the row splitter sits in grid
/// row 1 and spans <see cref="RowSplitter"/> columns.
/// </summary>
/// <param name="Id">Layout.</param>
/// <param name="PaneCount">Number of panes.</param>
/// <param name="Panes">Pane placements in pane order.</param>
/// <param name="ColumnSplitter">Rows covered by the column splitter; null when the layout has none.</param>
/// <param name="RowSplitter">Columns covered by the row splitter; null when the layout has none.</param>
public sealed record LayoutSpec(LayoutId Id, int PaneCount, IReadOnlyList<PanePlacement> Panes, TrackSpan? ColumnSplitter, TrackSpan? RowSplitter)
{
    /// <summary>Grid tracks per axis: pane, splitter, pane.</summary>
    public const int GridTracks = 3;

    private static readonly TrackSpan Full = new(0, 3);

    /// <summary>The 8 layouts in <see cref="LayoutId"/> order (architecture §6 table).</summary>
    public static IReadOnlyList<LayoutSpec> All { get; } =
    [
        new(LayoutId.One, 1, [new(0, 0, 3, 3)], null, null),
        new(LayoutId.TwoLeftRight, 2, [new(0, 0, 3, 1), new(0, 2, 3, 1)], Full, null),
        new(LayoutId.TwoTopBottom, 2, [new(0, 0, 1, 3), new(2, 0, 1, 3)], null, Full),
        new(LayoutId.ThreeTop1Bottom2, 3, [new(0, 0, 1, 3), new(2, 0), new(2, 2)], new(2, 1), Full),
        new(LayoutId.ThreeTop2Bottom1, 3, [new(0, 0), new(0, 2), new(2, 0, 1, 3)], new(0, 1), Full),
        new(LayoutId.ThreeLeft1Right2, 3, [new(0, 0, 3, 1), new(0, 2), new(2, 2)], Full, new(2, 1)),
        new(LayoutId.ThreeLeft2Right1, 3, [new(0, 0), new(0, 2, 3, 1), new(2, 0)], Full, new(0, 1)),
        new(LayoutId.FourGrid, 4, [new(0, 0), new(0, 2), new(2, 0), new(2, 2)], Full, Full),
    ];

    /// <summary>Placement of <paramref name="id"/>.</summary>
    /// <param name="id">Layout.</param>
    /// <returns>The spec.</returns>
    public static LayoutSpec Get(LayoutId id) => All[(int)id];
}
