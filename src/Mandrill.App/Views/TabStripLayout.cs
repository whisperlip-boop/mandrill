namespace Mandrill.App.Views;

/// <summary>
/// Pane tab widths (ui-spec §2a "Width sharing"): tabs shrink evenly from 160 to 96 px; when even 96 px tabs do not fit,
/// the strip overflows: 96 px tabs in a clipped viewport after the chevrons and "…", "+" pinned after the viewport.
/// </summary>
/// <param name="TabWidth">Bounding width of every tab.</param>
/// <param name="Overflow">Whether the chevrons, "…" and scrolling are needed.</param>
/// <param name="Viewport">Width of the tab viewport (all tabs when not overflowing).</param>
public sealed record TabStripLayout(double TabWidth, bool Overflow, double Viewport)
{
    /// <summary>Default and largest tab width.</summary>
    public const double MaxTabWidth = 160;

    /// <summary>Smallest tab width.</summary>
    public const double MinTabWidth = 96;

    /// <summary>Overlap of neighbouring tabs (2 × slant).</summary>
    public const double Overlap = 16;

    /// <summary>"+" and its 4 px gap.</summary>
    public const double PlusSlot = 28;

    /// <summary>"&lt;" and "&gt;" (20 each), the 16 px "…" slot and the 4 px gap before the first tab.</summary>
    public const double OverflowSlot = 60;

    /// <summary>Distance between the left edges of neighbouring tabs.</summary>
    public double Pitch => TabWidth - Overlap;

    /// <summary>Width of all tabs side by side.</summary>
    /// <param name="count">Number of tabs.</param>
    /// <returns>The content width.</returns>
    public double ContentWidth(int count) => count <= 0 ? 0 : (count * TabWidth) - (Overlap * (count - 1));

    /// <summary>Lays out <paramref name="count"/> tabs in <paramref name="available"/> px (the row minus its margin, the view buttons and "+").</summary>
    /// <param name="available">ui-spec's A, measured without the chevrons so they never flicker on and off.</param>
    /// <param name="count">Number of tabs (1..10).</param>
    /// <returns>The layout.</returns>
    public static TabStripLayout Compute(double available, int count)
    {
        if (count <= 0)
        {
            return new TabStripLayout(MaxTabWidth, false, 0);
        }

        if ((count * MinTabWidth) - (Overlap * (count - 1)) > available)
        {
            return new TabStripLayout(MinTabWidth, true, Math.Max(0, available - OverflowSlot));
        }

        double width = Math.Clamp((available + (Overlap * (count - 1))) / count, MinTabWidth, MaxTabWidth);
        var fit = new TabStripLayout(width, false, 0);
        return fit with { Viewport = fit.ContentWidth(count) };
    }
}
