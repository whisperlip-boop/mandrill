using Mandrill.Core.State;

namespace Mandrill.Presentation.Layout;

/// <summary>The 8 pane layouts (ui-spec §1, architecture §6), in layout-picker order.</summary>
public enum LayoutId
{
    /// <summary>1 pane.</summary>
    One,

    /// <summary>2 panes, left and right.</summary>
    TwoLeftRight,

    /// <summary>2 panes, top and bottom.</summary>
    TwoTopBottom,

    /// <summary>3 panes: 1 top, 2 bottom.</summary>
    ThreeTop1Bottom2,

    /// <summary>3 panes: 2 top, 1 bottom.</summary>
    ThreeTop2Bottom1,

    /// <summary>3 panes: 1 left, 2 right.</summary>
    ThreeLeft1Right2,

    /// <summary>3 panes: 2 left, 1 right.</summary>
    ThreeLeft2Right1,

    /// <summary>4 panes, 2 × 2.</summary>
    FourGrid,
}

/// <summary>Static facts about one layout.</summary>
/// <param name="Id">Layout.</param>
/// <param name="Key">Persisted id ("1", "2_lr", …; architecture §6).</param>
/// <param name="PaneCount">Number of visible panes.</param>
/// <param name="TextKey">Locale key of the display name.</param>
public sealed record LayoutInfo(LayoutId Id, string Key, int PaneCount, string TextKey);

/// <summary>Catalog of all layouts in picker order.</summary>
public static class LayoutCatalog
{
    /// <summary>All layouts, in ui-spec §1 picker order. Glyph files (w1…w8) are numbered differently; see IconProvider.</summary>
    public static IReadOnlyList<LayoutInfo> All { get; } =
    [
        new(LayoutId.One, LayoutKeys.One, 1, "layout.1"),
        new(LayoutId.TwoLeftRight, LayoutKeys.TwoLeftRight, 2, "layout.2_lr"),
        new(LayoutId.TwoTopBottom, LayoutKeys.TwoTopBottom, 2, "layout.2_tb"),
        new(LayoutId.ThreeTop1Bottom2, LayoutKeys.ThreeTop1Bottom2, 3, "layout.3_t1b2"),
        new(LayoutId.ThreeTop2Bottom1, LayoutKeys.ThreeTop2Bottom1, 3, "layout.3_t2b1"),
        new(LayoutId.ThreeLeft1Right2, LayoutKeys.ThreeLeft1Right2, 3, "layout.3_l1r2"),
        new(LayoutId.ThreeLeft2Right1, LayoutKeys.ThreeLeft2Right1, 3, "layout.3_l2r1"),
        new(LayoutId.FourGrid, LayoutKeys.FourGrid, 4, "layout.4_grid"),
    ];

    /// <summary>Facts for <paramref name="id"/>.</summary>
    /// <param name="id">Layout.</param>
    /// <returns>The catalog entry.</returns>
    public static LayoutInfo Get(LayoutId id) => All[(int)id];

    /// <summary>Layout for a persisted key (<see cref="LayoutKeys"/>).</summary>
    /// <param name="key">Layout key, e.g. "2_lr".</param>
    /// <param name="id">The layout when found.</param>
    /// <returns>False for an unknown key.</returns>
    public static bool TryFromKey(string? key, out LayoutId id)
    {
        foreach (LayoutInfo info in All)
        {
            if (string.Equals(info.Key, key, StringComparison.Ordinal))
            {
                id = info.Id;
                return true;
            }
        }

        id = default;
        return false;
    }
}
