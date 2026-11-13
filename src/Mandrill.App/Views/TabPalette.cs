using Windows.UI;

namespace Mandrill.App.Views;

/// <summary>Pointer state of a pane tab (ui-spec §2a colour table).</summary>
public enum TabVisualState
{
    /// <summary>No pointer.</summary>
    Rest,

    /// <summary>Pointer over the tab.</summary>
    Hover,

    /// <summary>Pointer pressed on the tab.</summary>
    Pressed,
}

/// <summary>
/// Pane tab colours outside high contrast (ui-spec §2a): Material Blue 50–900 per tab colour index, the same in light and
/// dark, black overlays for hover/pressed and the inactive-pane scrim (the row background at 35 %). The text is the
/// palette's unless it falls below 4.5:1 on the final fill; then black or white, whichever reads better (designer rule).
/// </summary>
public static class TabPalette
{
    /// <summary>WCAG AA contrast for normal text.</summary>
    public const double MinContrast = 4.5;

    private const byte HoverAlpha = 0x0F;   // black 6 %
    private const byte PressedAlpha = 0x1A; // black 10 %
    private const double ScrimOpacity = 0.35;

    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);

    /// <summary>Tab fills by colour index 0..9.</summary>
    public static IReadOnlyList<Color> Fills { get; } =
    [
        Rgb(0xE3F2FD), Rgb(0xBBDEFB), Rgb(0x90CAF9), Rgb(0x64B5F6), Rgb(0x42A5F5),
        Rgb(0x2196F3), Rgb(0x1E88E5), Rgb(0x1976D2), Rgb(0x1565C0), Rgb(0x0D47A1),
    ];

    /// <summary>Palette text colour by colour index (owner: white from #1976D2 on).</summary>
    public static IReadOnlyList<Color> Texts { get; } = [Black, Black, Black, Black, Black, Black, Black, White, White, White];

    /// <summary>The fill a tab shows.</summary>
    /// <param name="color">Colour index 0..9.</param>
    /// <param name="state">Pointer state.</param>
    /// <param name="paneActive">False adds the inactive-pane scrim.</param>
    /// <param name="rowBackground">The tab row's background (the pane toolbar's), for the scrim.</param>
    /// <returns>The opaque fill.</returns>
    public static Color Fill(int color, TabVisualState state, bool paneActive, Color rowBackground)
    {
        Color fill = state switch
        {
            TabVisualState.Hover => Over(Fills[color], Color.FromArgb(HoverAlpha, 0, 0, 0)),
            TabVisualState.Pressed => Over(Fills[color], Color.FromArgb(PressedAlpha, 0, 0, 0)),
            _ => Fills[color],
        };
        return paneActive ? fill : Over(fill, Color.FromArgb((byte)Math.Round(255 * ScrimOpacity), rowBackground.R, rowBackground.G, rowBackground.B));
    }

    /// <summary>The text colour on <paramref name="fill"/>.</summary>
    /// <param name="color">Colour index 0..9.</param>
    /// <param name="fill">The tab's fill (<see cref="Fill"/>).</param>
    /// <param name="paneActive">An inactive pane always picks the better of black and white (ui-spec §2a).</param>
    /// <returns>Black, white or the palette text colour.</returns>
    public static Color Text(int color, Color fill, bool paneActive)
        => paneActive && Contrast(Texts[color], fill) >= MinContrast
            ? Texts[color]
            : Contrast(Black, fill) >= Contrast(White, fill) ? Black : White;

    /// <summary>The strip colour under the row: the active tab's rest fill (scrimmed in an inactive pane).</summary>
    /// <param name="color">The active tab's colour index.</param>
    /// <param name="paneActive">Whether the pane is active.</param>
    /// <param name="rowBackground">The row background.</param>
    /// <returns>The strip colour.</returns>
    public static Color Strip(int color, bool paneActive, Color rowBackground) => Fill(color, TabVisualState.Rest, paneActive, rowBackground);

    /// <summary>WCAG 2 contrast ratio of two opaque colours.</summary>
    /// <param name="a">First colour.</param>
    /// <param name="b">Second colour.</param>
    /// <returns>1..21.</returns>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // Source-over of a translucent colour on an opaque one.
    private static Color Over(Color under, Color over)
    {
        double a = over.A / 255.0;
        byte Mix(byte u, byte o) => (byte)Math.Round((o * a) + (u * (1 - a)));
        return Color.FromArgb(255, Mix(under.R, over.R), Mix(under.G, over.G), Mix(under.B, over.B));
    }

    private static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Lin(c.R)) + (0.7152 * Lin(c.G)) + (0.0722 * Lin(c.B));
    }

    private static Color Rgb(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
