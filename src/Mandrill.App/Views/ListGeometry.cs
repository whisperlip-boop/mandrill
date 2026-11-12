using Mandrill.Core.State;
using Windows.Foundation;

namespace Mandrill.App.Views;

/// <summary>Pure layout math for the file list: icon-size steps (ui-spec §2) and rubber-band hit-testing on uniform grids.</summary>
public static class ListGeometry
{
    /// <summary>
    /// Icons-view sizes for Ctrl+wheel, in pixels. Explorer's View menu sizes are steps too: small 16, medium 48, large 96,
    /// extra large 256 (owner F7).
    /// </summary>
    public static IReadOnlyList<int> IconSteps { get; } = [16, 24, 32, 48, 64, 96, 128, 256];

    /// <summary>"Small icons" size (View menu).</summary>
    public const int SmallIcons = 16;

    /// <summary>"Medium icons" size (View menu; the default).</summary>
    public const int MediumIcons = 48;

    /// <summary>"Large icons" size (View menu).</summary>
    public const int LargeIcons = 96;

    /// <summary>"Extra large icons" size (View menu).</summary>
    public const int ExtraLargeIcons = 256;

    /// <summary>The next icon size in <paramref name="direction"/> (+1 larger, -1 smaller), clamped to the steps.</summary>
    /// <param name="current">Current size (snapped to the nearest step first).</param>
    /// <param name="direction">+1 or -1.</param>
    /// <returns>The new size.</returns>
    public static int StepIconSize(int current, int direction)
    {
        int index = 0;
        for (int i = 0; i < IconSteps.Count; i++)
        {
            if (Math.Abs(IconSteps[i] - current) < Math.Abs(IconSteps[index] - current))
            {
                index = i;
            }
        }

        return IconSteps[Math.Clamp(index + Math.Sign(direction), 0, IconSteps.Count - 1)];
    }

    /// <summary>
    /// Ctrl+wheel over the list (owner #9), Explorer's order: Details, List, then the icon sizes up to extra large
    /// (+1 = larger). Clamped at both ends; the icon size is kept when leaving Icons.
    /// </summary>
    /// <param name="mode">Current view mode.</param>
    /// <param name="iconSize">Current icon size.</param>
    /// <param name="direction">+1 larger, -1 smaller.</param>
    /// <returns>The new mode and icon size.</returns>
    public static (ViewMode Mode, int IconSize) StepView(ViewMode mode, int iconSize, int direction) => (mode, Math.Sign(direction)) switch
    {
        (ViewMode.Details, > 0) => (ViewMode.List, iconSize),
        (ViewMode.List, > 0) => (ViewMode.Icons, SmallIcons),
        (ViewMode.List, < 0) => (ViewMode.Details, iconSize),
        (ViewMode.Icons, < 0) when StepIconSize(iconSize, 0) == IconSteps[0] => (ViewMode.List, iconSize),
        (ViewMode.Icons, _) => (ViewMode.Icons, StepIconSize(iconSize, direction)),
        _ => (mode, iconSize),
    };

    /// <summary>
    /// Ctrl+wheel accumulation: adds <paramref name="delta"/> to the pending delta (precision touchpads send fractions of
    /// WHEEL_DELTA = 120) and takes the whole notches off it; a reversal of direction starts over from <paramref name="delta"/>.
    /// </summary>
    /// <param name="pending">Delta not yet turned into a step.</param>
    /// <param name="delta">The wheel event's delta.</param>
    /// <returns>Steps to take (the sign is the direction) and the new pending delta.</returns>
    public static (int Steps, int Pending) AccumulateWheel(int pending, int delta)
    {
        int total = Math.Sign(pending) == -Math.Sign(delta) ? delta : pending + delta;
        int steps = total / 120; // toward zero, so the remainder keeps the sign
        return (steps, total - (steps * 120));
    }

    /// <summary>Icons-view cell size for an icon size: room for the icon and a two-line label.</summary>
    /// <param name="iconSize">Icon size in pixels.</param>
    /// <returns>Cell width and height.</returns>
    public static Size IconCell(int iconSize) => new(Math.Max(iconSize + 32, 88), iconSize + 48);

    /// <summary>
    /// Index ranges (first, count) of the cells of a uniform wrap grid that intersect <paramref name="band"/>, one range per
    /// row (horizontal flow) or per column (vertical flow), in index order.
    /// </summary>
    /// <param name="band">Selection rectangle in panel coordinates.</param>
    /// <param name="cell">Cell size.</param>
    /// <param name="panel">Panel size (the wrap extent: width for horizontal flow, height for vertical flow).</param>
    /// <param name="itemCount">Number of items.</param>
    /// <param name="horizontalFlow">True when items fill rows left to right (Icons); false for columns top to bottom (List).</param>
    /// <returns>The ranges.</returns>
    public static IReadOnlyList<(int First, int Count)> HitRanges(Rect band, Size cell, Size panel, int itemCount, bool horizontalFlow)
    {
        var result = new List<(int, int)>();
        if (itemCount <= 0 || cell.Width <= 0 || cell.Height <= 0 || band.Width <= 0 || band.Height <= 0)
        {
            return result;
        }

        // Lines wrap along the flow; "across" counts cells per line.
        double wrapExtent = horizontalFlow ? panel.Width : panel.Height;
        double cellAcross = horizontalFlow ? cell.Width : cell.Height;
        double cellAlong = horizontalFlow ? cell.Height : cell.Width;
        int perLine = Math.Max(1, (int)Math.Floor(wrapExtent / cellAcross));
        int lines = (itemCount + perLine - 1) / perLine;

        double acrossStart = horizontalFlow ? band.Left : band.Top;
        double acrossEnd = horizontalFlow ? band.Right : band.Bottom;
        double alongStart = horizontalFlow ? band.Top : band.Left;
        double alongEnd = horizontalFlow ? band.Bottom : band.Right;

        int firstLine = Math.Max(0, (int)Math.Floor(alongStart / cellAlong));
        int lastLine = Math.Min(lines - 1, (int)Math.Floor((alongEnd - 0.001) / cellAlong));
        int firstCell = Math.Max(0, (int)Math.Floor(acrossStart / cellAcross));
        int lastCell = Math.Min(perLine - 1, (int)Math.Floor((acrossEnd - 0.001) / cellAcross));
        if (firstLine > lastLine || firstCell > lastCell)
        {
            return result;
        }

        for (int line = firstLine; line <= lastLine; line++)
        {
            int first = (line * perLine) + firstCell;
            int last = Math.Min(itemCount - 1, (line * perLine) + lastCell);
            if (first <= last)
            {
                result.Add((first, last - first + 1));
            }
        }

        return result;
    }
}
