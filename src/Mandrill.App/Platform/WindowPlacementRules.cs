using Mandrill.Core.State;
using Windows.Graphics;

namespace Mandrill.App.Platform;

/// <summary>
/// Where a saved window goes on the current displays (architecture §7: clamped to a visible display area). A window that is
/// at least half on one work area keeps its position (size clamped to that area); otherwise it is centred on the primary
/// work area. Pure, so the rule is testable without displays.
/// </summary>
public static class WindowPlacementRules
{
    /// <summary>Smallest restored size, so a corrupt-but-valid tiny size cannot hide the window.</summary>
    public static readonly SizeInt32 MinimumSize = new(480, 320);

    /// <summary>The rectangle to restore.</summary>
    /// <param name="saved">Saved normal bounds (physical pixels).</param>
    /// <param name="workAreas">Work areas of the current displays; the first one is the primary display.</param>
    /// <returns>The rectangle, or null when there is no display to place it on.</returns>
    public static RectInt32? Fit(WindowPlacement saved, IReadOnlyList<RectInt32> workAreas)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(workAreas);
        if (workAreas.Count == 0)
        {
            return null;
        }

        var rect = new RectInt32(saved.X, saved.Y, Math.Max(MinimumSize.Width, saved.Width), Math.Max(MinimumSize.Height, saved.Height));
        long area = (long)rect.Width * rect.Height;
        foreach (var work in workAreas)
        {
            if (Overlap(rect, work) * 2 >= area)
            {
                return ClampInto(rect, work);
            }
        }

        // Mostly off-screen (display removed or resolution changed): centre on the primary display.
        var primary = workAreas[0];
        int width = Math.Min(rect.Width, primary.Width);
        int height = Math.Min(rect.Height, primary.Height);
        return new RectInt32(primary.X + ((primary.Width - width) / 2), primary.Y + ((primary.Height - height) / 2), width, height);
    }

    /// <summary>
    /// The saved placement for instance <paramref name="instanceSlot"/> (architecture §18 D5): instance n &gt; 1 opens
    /// (n-1) × 32 px (at 100 %) right of and below it, so windows started together do not cover each other exactly; a
    /// maximized placement stays as it is. <see cref="Fit"/> then keeps the result on screen.
    /// </summary>
    /// <param name="saved">Saved normal bounds (physical pixels).</param>
    /// <param name="instanceSlot">This process's instance number (0 = none, 1 = the first).</param>
    /// <param name="scale">The window's DPI scale (1 at 100 %).</param>
    /// <returns>The placement to fit.</returns>
    public static WindowPlacement Cascade(WindowPlacement saved, int instanceSlot, double scale)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (instanceSlot <= 1 || saved.Maximized)
        {
            return saved;
        }

        int offset = (int)Math.Round((instanceSlot - 1) * 32 * scale);
        return saved with { X = saved.X + offset, Y = saved.Y + offset };
    }

    /// <summary>
    /// The normal bounds to save. A cascaded window (§18 D5) that still sits exactly where the cascade put it saves the
    /// placement it started from: otherwise every start of a second window would add another offset.
    /// </summary>
    /// <param name="current">The window's normal bounds now.</param>
    /// <param name="cascaded">Where the cascade put it; null when it was not cascaded.</param>
    /// <param name="saved">The placement it started from.</param>
    /// <returns>The bounds to save.</returns>
    public static RectInt32 ToSave(RectInt32 current, RectInt32? cascaded, WindowPlacement? saved)
        => cascaded == current && saved is { } s ? new RectInt32(s.X, s.Y, s.Width, s.Height) : current;

    /// <summary>A child window of <paramref name="size"/> centred over <paramref name="owner"/>, kept inside the owner's work area.</summary>
    /// <param name="owner">The owner window's bounds (physical pixels).</param>
    /// <param name="size">The child's size (physical pixels).</param>
    /// <param name="workArea">Work area of the owner's display; null when unknown (no clamping).</param>
    /// <returns>The child's bounds; shrunk to the work area when it does not fit.</returns>
    public static RectInt32 CentreOver(RectInt32 owner, SizeInt32 size, RectInt32? workArea)
    {
        var rect = new RectInt32(owner.X + ((owner.Width - size.Width) / 2), owner.Y + ((owner.Height - size.Height) / 2), size.Width, size.Height);
        return workArea is { Width: > 0, Height: > 0 } work ? ClampInto(rect, work) : rect;
    }

    private static long Overlap(RectInt32 a, RectInt32 b)
    {
        long w = Math.Min((long)a.X + a.Width, (long)b.X + b.Width) - Math.Max(a.X, b.X);
        long h = Math.Min((long)a.Y + a.Height, (long)b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    private static RectInt32 ClampInto(RectInt32 rect, RectInt32 work)
    {
        int width = Math.Min(rect.Width, work.Width);
        int height = Math.Min(rect.Height, work.Height);
        int x = Math.Clamp(rect.X, work.X, work.X + work.Width - width);
        int y = Math.Clamp(rect.Y, work.Y, work.Y + work.Height - height);
        return new RectInt32(x, y, width, height);
    }
}
