namespace Mandrill.Core.State;

/// <summary>
/// Persisted layout ids (architecture §6/§10), the single source of the key strings. Presentation's LayoutCatalog
/// references these constants; a Presentation test checks order and pane counts against LayoutId.
/// </summary>
public static class LayoutKeys
{
    /// <summary>1 pane.</summary>
    public const string One = "1";

    /// <summary>2 panes, left and right.</summary>
    public const string TwoLeftRight = "2_lr";

    /// <summary>2 panes, top and bottom.</summary>
    public const string TwoTopBottom = "2_tb";

    /// <summary>3 panes: 1 top, 2 bottom.</summary>
    public const string ThreeTop1Bottom2 = "3_t1b2";

    /// <summary>3 panes: 2 top, 1 bottom.</summary>
    public const string ThreeTop2Bottom1 = "3_t2b1";

    /// <summary>3 panes: 1 left, 2 right.</summary>
    public const string ThreeLeft1Right2 = "3_l1r2";

    /// <summary>3 panes: 2 left, 1 right.</summary>
    public const string ThreeLeft2Right1 = "3_l2r1";

    /// <summary>4 panes, 2 × 2.</summary>
    public const string FourGrid = "4_grid";

    /// <summary>All keys in layout-picker order (same order as LayoutCatalog).</summary>
    public static IReadOnlyList<string> All { get; } =
        [One, TwoLeftRight, TwoTopBottom, ThreeTop1Bottom2, ThreeTop2Bottom1, ThreeLeft1Right2, ThreeLeft2Right1, FourGrid];

    /// <summary>Whether <paramref name="key"/> is one of the 8 layout ids (case-sensitive).</summary>
    /// <param name="key">Persisted value.</param>
    /// <returns>True for a known key.</returns>
    public static bool IsKnown(string? key) => key is not null && All.Contains(key, StringComparer.Ordinal);

    /// <summary>Visible pane count of a layout.</summary>
    /// <param name="key">A known layout key.</param>
    /// <returns>1 to 4.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not a known layout key.</exception>
    public static int PaneCount(string key)
    {
        if (!IsKnown(key))
        {
            throw new ArgumentException($"Unknown layout key '{key}'", nameof(key));
        }

        // Every key starts with its pane count.
        return key[0] - '0';
    }

    /// <summary>Keys of all layouts with <paramref name="paneCount"/> panes, in picker order.</summary>
    /// <param name="paneCount">Pane count.</param>
    /// <returns>Matching keys; empty for a count outside 1–4.</returns>
    public static IReadOnlyList<string> ForPaneCount(int paneCount) => [.. All.Where(k => PaneCount(k) == paneCount)];
}
