using System.Collections.ObjectModel;
using System.Text.Json.Serialization.Metadata;

namespace Mandrill.Core.State;

/// <summary>
/// session.json (architecture §10, AC-1.33): window, layout, splitters and visible panes, saved 2 s after a change and on exit.
/// Removed-pane state is memory-only (AC-1.3).
/// </summary>
/// <param name="Window">Window geometry; null = none saved (first run).</param>
/// <param name="Layout">Layout key (<see cref="LayoutKeys"/>).</param>
/// <param name="ActivePane">Index of the active pane in <paramref name="Panes"/> order.</param>
/// <param name="Splitters">Splitter ratios per layout key.</param>
/// <param name="Panes">Visible panes in pane order; may be shorter than the layout's pane count (missing panes open as new).</param>
public sealed record SessionState(
    WindowPlacement? Window,
    string Layout,
    int ActivePane,
    IReadOnlyDictionary<string, SplitterState> Splitters,
    IReadOnlyList<PaneState> Panes)
    : IStateDocument<SessionState>
{
    /// <summary>Schema version written on save.</summary>
    public static int CurrentVersion => 1;

    /// <summary>Source-generated serialization metadata.</summary>
    public static JsonTypeInfo<SessionState> TypeInfo => MandrillJsonContext.Default.SessionState;

    /// <summary>First-run session: no geometry, 2_lr, pane 1 active, no panes (they open at the home folder).</summary>
    /// <returns>A new default document.</returns>
    public static SessionState CreateDefault()
        => new(null, LayoutKeys.TwoLeftRight, 0, ReadOnlyDictionary<string, SplitterState>.Empty, []);

    /// <inheritdoc/>
    public SessionState Normalize(ICollection<string> fixes)
    {
        ArgumentNullException.ThrowIfNull(fixes);
        string layout = Fixes.Check(fixes, "layout", Layout, LayoutKeys.IsKnown(Layout), LayoutKeys.TwoLeftRight);
        int paneCount = LayoutKeys.PaneCount(layout);

        var panes = new List<PaneState>();
        foreach (PaneState? pane in Panes ?? [])
        {
            if (pane is null)
            {
                fixes.Add("panes: removed a null entry");
            }
            else if (panes.Count == paneCount)
            {
                fixes.Add($"panes: removed entries beyond the {paneCount} panes of layout {layout}");
                break;
            }
            else
            {
                panes.Add(pane.Normalize(fixes, $"panes[{panes.Count}]"));
            }
        }

        var splitters = new Dictionary<string, SplitterState>(StringComparer.Ordinal);
        foreach (var (key, value) in Splitters ?? ReadOnlyDictionary<string, SplitterState>.Empty)
        {
            if (LayoutKeys.IsKnown(key) && value is not null)
            {
                splitters[key] = value.Normalize(fixes, $"splitters.{key}");
            }
            else
            {
                fixes.Add($"splitters: removed entry '{key}'");
            }
        }

        WindowPlacement? window = Window;
        if (window is not null && (window.Width <= 0 || window.Height <= 0))
        {
            fixes.Add($"window: size {window.Width}x{window.Height} is not positive; geometry dropped");
            window = null;
        }

        return new SessionState(
            window,
            layout,
            Fixes.Check(fixes, "activePane", ActivePane, ActivePane >= 0 && ActivePane < paneCount, 0),
            splitters,
            panes);
    }
}

/// <summary>Main window geometry in physical pixels of the virtual screen. The App clamps it to a visible display.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Restored (non-maximized) width.</param>
/// <param name="Height">Restored (non-maximized) height.</param>
/// <param name="Maximized">Whether the window was maximized.</param>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);
