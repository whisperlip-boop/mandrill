using Mandrill.Core.State;
using Windows.Foundation;
using Windows.System;

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
    /// row (horizontal flow) or per column (vertical flow), in index order; touching ranges are merged.
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
            if (first > last)
            {
                continue;
            }

            // Whole lines touch (Details has one row per line): one range, so a tall band costs one range, not one per row.
            if (result.Count > 0 && result[^1] is var (prevFirst, prevCount) && prevFirst + prevCount == first)
            {
                result[^1] = (prevFirst, prevCount + last - first + 1);
            }
            else
            {
                result.Add((first, last - first + 1));
            }
        }

        return result;
    }

    /// <summary>
    /// The selection changes when the band's hit ranges go from <paramref name="before"/> to <paramref name="after"/>, as in
    /// Explorer: no modifier selects exactly the hits, Shift adds them to <paramref name="initial"/>, Ctrl toggles them
    /// against it (Ctrl wins over Shift). Only indexes in exactly one of the two hit lists are touched, so the work follows
    /// the band's change, not the item count.
    /// </summary>
    /// <param name="before">Previous hit ranges (first, count), sorted and disjoint.</param>
    /// <param name="after">New hit ranges, sorted and disjoint.</param>
    /// <param name="initial">The selection when the band started.</param>
    /// <param name="modifiers">Modifiers held when the band started.</param>
    /// <returns>Ranges (first, count) to select or deselect, in index order.</returns>
    public static IReadOnlyList<(int First, int Count, bool Select)> BandChanges(
        IReadOnlyList<(int First, int Count)> before, IReadOnlyList<(int First, int Count)> after, IndexRangeSet initial, VirtualKeyModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(initial);
        bool toggle = modifiers.HasFlag(VirtualKeyModifiers.Control);
        bool add = !toggle && modifiers.HasFlag(VirtualKeyModifiers.Shift);
        var result = new List<(int First, int Count, bool Select)>();

        // Between two consecutive range ends, membership in before/after is constant.
        var ends = new List<int>((before.Count + after.Count) * 2);
        foreach (var (first, count) in before.Concat(after))
        {
            ends.Add(first);
            ends.Add(first + count);
        }

        ends.Sort();
        int b = 0, a = 0;
        for (int k = 0; k + 1 < ends.Count; k++)
        {
            int start = ends[k], end = ends[k + 1];
            bool inAfter = Covers(after, ref a, start);
            if (start == end || Covers(before, ref b, start) == inAfter)
            {
                continue;
            }

            // Wanted state of an index that was / was not selected when the band started.
            bool whenInitial = toggle ? !inAfter : add || inAfter;
            if (whenInitial == inAfter)
            {
                Emit(result, start, end, inAfter);
                continue;
            }

            var ranges = initial.Ranges;
            int lo = 0, hi = ranges.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                (lo, hi) = ranges[mid].Last < start ? (mid + 1, hi) : (lo, mid);
            }

            int at = start;
            for (int r = lo; r < ranges.Count && ranges[r].First < end; r++)
            {
                int from = Math.Max(at, ranges[r].First), to = Math.Min(end, ranges[r].Last + 1);
                Emit(result, at, from, inAfter);
                Emit(result, from, to, whenInitial);
                at = to;
            }

            Emit(result, at, end, inAfter);
        }

        return result;
    }

    // Whether a sorted, disjoint range list covers index; cursor only moves forward (callers ask in ascending order).
    private static bool Covers(IReadOnlyList<(int First, int Count)> ranges, ref int cursor, int index)
    {
        while (cursor < ranges.Count && ranges[cursor].First + ranges[cursor].Count <= index)
        {
            cursor++;
        }

        return cursor < ranges.Count && ranges[cursor].First <= index;
    }

    // Appends [start, end) with the state, merged into the previous piece when they touch and agree.
    private static void Emit(List<(int First, int Count, bool Select)> result, int start, int end, bool select)
    {
        if (end <= start)
        {
            return;
        }

        if (result.Count > 0 && result[^1] is var last && last.Select == select && last.First + last.Count == start)
        {
            result[^1] = (last.First, last.Count + end - start, select);
        }
        else
        {
            result.Add((start, end - start, select));
        }
    }
}
