using Mandrill.App.Platform;
using Mandrill.Presentation.Layout;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mandrill.App.Icons;

/// <summary>All icons in the app (ui-spec §8.3, ADR-0014).</summary>
public enum IconId
{
    /// <summary>Back.</summary>
    Back,

    /// <summary>Forward.</summary>
    Forward,

    /// <summary>Up one level.</summary>
    Up,

    /// <summary>Down / dropdown.</summary>
    Down,

    /// <summary>Refresh.</summary>
    Refresh,

    /// <summary>Search (coloured).</summary>
    Search,

    /// <summary>Favorites (coloured).</summary>
    Favorite,

    /// <summary>New folder.</summary>
    NewFolder,

    /// <summary>Generic add.</summary>
    Add,

    /// <summary>Add pane.</summary>
    AddPane,

    /// <summary>Remove pane.</summary>
    RemovePane,

    /// <summary>View mode.</summary>
    ViewMode,

    /// <summary>Settings.</summary>
    Settings,

    /// <summary>Tree toggle while the tree is shown (owner F20: the icon reflects the state).</summary>
    TreeShown,

    /// <summary>Tree toggle while the tree is hidden.</summary>
    TreeHidden,

    /// <summary>Layout glyph 1 pane.</summary>
    LayoutOne,

    /// <summary>Layout glyph 2 panes left/right.</summary>
    LayoutTwoLeftRight,

    /// <summary>Layout glyph 2 panes top/bottom.</summary>
    LayoutTwoTopBottom,

    /// <summary>Layout glyph 1 top + 2 bottom.</summary>
    LayoutThreeTop1Bottom2,

    /// <summary>Layout glyph 2 top + 1 bottom.</summary>
    LayoutThreeTop2Bottom1,

    /// <summary>Layout glyph 1 left + 2 right.</summary>
    LayoutThreeLeft1Right2,

    /// <summary>Layout glyph 2 left + 1 right.</summary>
    LayoutThreeLeft2Right1,

    /// <summary>Layout glyph 2 × 2.</summary>
    LayoutFourGrid,

    /// <summary>View menu: extra large icons (Explorer's own icon, owner SVG).</summary>
    ViewExtraLargeIcons,

    /// <summary>View menu: large icons.</summary>
    ViewLargeIcons,

    /// <summary>View menu: medium icons.</summary>
    ViewMediumIcons,

    /// <summary>View menu: small icons.</summary>
    ViewSmallIcons,

    /// <summary>View menu: list.</summary>
    ViewList,

    /// <summary>View menu: details.</summary>
    ViewDetails,

    /// <summary>Close (a pane tab's x).</summary>
    Close,
}

/// <summary>
/// Maps <see cref="IconId"/> to an <see cref="IconElement"/>. XAML never names icon files. The owner's icons are SVGs
/// (WP-1.16): the monochrome ones are drawn as <see cref="PathIcon"/> geometry from <see cref="IconPaths"/>
/// (generated from the SVGs), scaled and centred into the requested size, so they tint with the theme foreground and stay
/// crisp; the coloured search/favorite are an <see cref="ImageIcon"/> of an <see cref="SvgImageSource"/>. Monochrome
/// icons inherit Foreground from their control by default, so they follow the theme and dim when disabled.
/// Caveat (spike S1): inside a subtree with a different RequestedTheme than its parent, inherited Foreground is not
/// re-resolved; pass an explicit theme brush via <see cref="CreateElement"/> in that case.
/// </summary>
/// <param name="logger">Logs colour SVGs that fail to load (a blank icon otherwise leaves no trace); null logs nothing.</param>
public sealed class IconProvider(ILogger? logger = null)
{
    private const string BaseUri = "ms-appx:///Assets/Icons/";

    /// <summary>File name (without extension) under Assets\Icons for each icon.</summary>
    /// <param name="id">Icon.</param>
    /// <returns>The file stem.</returns>
    public static string FileStem(IconId id) => id switch
    {
        IconId.Back => "left-arrow",
        IconId.Forward => "right-arrow",
        IconId.Up => "up-arrow",
        IconId.Down => "down-arrow",
        IconId.Refresh => "refresh",
        IconId.Search => "search",
        IconId.Favorite => "favorite",
        IconId.NewFolder => "add-folder",
        IconId.Add => "add",
        IconId.AddPane => "add_window",
        IconId.RemovePane => "minus_window",
        IconId.ViewMode => "viewmode",
        IconId.Settings => "settings",
        IconId.TreeShown => "tree-view",
        IconId.TreeHidden => "no_tree_view",
        // Owner glyph files are not in picker order (architecture §10); w2.svg draws left | right, w3.svg top / bottom (owner round 6 #2).
        IconId.LayoutOne => "w1",
        IconId.LayoutTwoLeftRight => "w2",
        IconId.LayoutTwoTopBottom => "w3",
        IconId.LayoutThreeTop2Bottom1 => "w4",
        IconId.LayoutThreeTop1Bottom2 => "w5",
        IconId.LayoutThreeLeft2Right1 => "w6",
        IconId.LayoutThreeLeft1Right2 => "w7",
        IconId.LayoutFourGrid => "w8",
        IconId.ViewExtraLargeIcons => "windows.iconsize.extralarge",
        IconId.ViewLargeIcons => "windows.iconsize.large",
        IconId.ViewMediumIcons => "windows.iconsize.medium",
        IconId.ViewSmallIcons => "windows.iconsize.smallicon",
        IconId.ViewList => "windows.iconsize.list",
        IconId.ViewDetails => "windows.iconsize.details",
        IconId.Close => "windows.close",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    /// <summary>True for icons that keep their own colours (owner decision: search, favorite).</summary>
    /// <param name="id">Icon.</param>
    /// <returns>True when rendered as a colour image.</returns>
    public static bool IsColour(IconId id) => id is IconId.Search or IconId.Favorite;

    /// <summary>Layout glyph for a layout.</summary>
    /// <param name="layout">Layout.</param>
    /// <returns>The glyph icon.</returns>
    public static IconId ForLayout(LayoutId layout) => layout switch
    {
        LayoutId.One => IconId.LayoutOne,
        LayoutId.TwoLeftRight => IconId.LayoutTwoLeftRight,
        LayoutId.TwoTopBottom => IconId.LayoutTwoTopBottom,
        LayoutId.ThreeTop1Bottom2 => IconId.LayoutThreeTop1Bottom2,
        LayoutId.ThreeTop2Bottom1 => IconId.LayoutThreeTop2Bottom1,
        LayoutId.ThreeLeft1Right2 => IconId.LayoutThreeLeft1Right2,
        LayoutId.ThreeLeft2Right1 => IconId.LayoutThreeLeft2Right1,
        LayoutId.FourGrid => IconId.LayoutFourGrid,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, null),
    };

    /// <summary>The geometry of a monochrome icon, scaled and centred into a <paramref name="size"/> square.</summary>
    /// <param name="stem">File stem (<see cref="FileStem"/>).</param>
    /// <param name="size">Edge in pixels.</param>
    /// <returns>A new geometry.</returns>
    public static Geometry CreateGeometry(string stem, double size)
    {
        IconPath path = IconPaths.All.TryGetValue(stem, out IconPath? found)
            ? found
            : throw new ArgumentOutOfRangeException(nameof(stem), stem, "No path data: run tools/icons/Export-IconPaths.ps1");
        Geometry geometry;
        if (path.Paths.Count == 1)
        {
            geometry = Parse(path.Paths[0]);
        }
        else
        {
            // The generator guarantees one fill rule per icon; the group applies it to all children. SVG fills each path
            // on its own, so overlapping paths of opposite winding would render differently here: check new multi-path
            // icons visually.
            var group = new GeometryGroup { FillRule = path.Paths[0].StartsWith("F0", StringComparison.Ordinal) ? FillRule.EvenOdd : FillRule.Nonzero };
            foreach (string data in path.Paths)
            {
                group.Children.Add(Parse(data));
            }

            geometry = group;
        }

        double scale = size / Math.Max(path.Width, path.Height);
        double dx = (size - (path.Width * scale)) / 2;
        double dy = (size - (path.Height * scale)) / 2;

        // An identity MatrixTransform makes WinUI draw the geometry empty (F17: 16-unit icons at 16 px, IconRenderTests).
        if (scale != 1 || dx != 0 || dy != 0)
        {
            geometry.Transform = new MatrixTransform { Matrix = new Matrix(scale, 0, 0, scale, dx, dy) };
        }

        return geometry;
    }

    // System icon-font glyphs (Segoe Fluent Icons, Segoe MDL2 Assets fallback; owner WP-1.15). Always written as escapes:
    // the characters are in the Private Use Area and show as blanks in editors and diffs.

    /// <summary>"More" (U+E712, three dots): the overflow menus (owner: a Windows glyph there).</summary>
    public const string MoreGlyph = "\uE712";

    /// <summary>
    /// A system icon-font glyph (Segoe Fluent Icons, Segoe MDL2 Assets fallback); it inherits the theme foreground like
    /// the monochrome owner icons.
    /// </summary>
    /// <param name="glyph">The glyph.</param>
    /// <param name="size">Font size in pixels.</param>
    /// <returns>A new element.</returns>
    public static IconElement CreateGlyph(string glyph, double size = 16) => new FontIcon { Glyph = glyph, FontSize = size };

    /// <summary>Creates an element for slots typed as <see cref="IconElement"/> (menu items, buttons).</summary>
    /// <param name="id">Icon.</param>
    /// <param name="foreground">Explicit tint for monochrome icons; null inherits.</param>
    /// <param name="size">Edge of the icon in pixels (menu items use 16).</param>
    /// <returns>A new element of that size.</returns>
    public IconElement CreateElement(IconId id, Brush? foreground = null, double size = 16)
    {
        string stem = FileStem(id);
        if (IsColour(id))
        {
            var uri = new Uri(BaseUri + stem + ".svg");
            var svg = new SvgImageSource(uri);
            if (logger is not null)
            {
                svg.OpenFailed += (_, e) => logger.LogWarning("Icon {Uri} failed to load: {Status}", uri, e.Status);
            }

            // Not an ImageIconSource in an IconSourceElement: WinUI draws that empty (F18, IconRenderTests).
            return new ImageIcon { Source = svg, Width = size, Height = size };
        }

        long cost = PerfLog.BeginCost();
        var icon = new PathIcon { Data = CreateGeometry(stem, size), Width = size, Height = size };
        PerfLog.EndCost("icon geometry", cost);
        if (foreground is not null)
        {
            icon.Foreground = foreground;
        }

        return icon;
    }

    private static Geometry Parse(string data) => (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);
}
