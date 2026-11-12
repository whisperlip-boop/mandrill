using Mandrill.App.Views;
using Mandrill.Core.Shell;
using Mandrill.Presentation.Panes;

namespace Mandrill.App.Composition;

/// <summary>
/// Builds and wires a pane's view when the pane is first shown (WP-1.17 lazy views): search, the context menus, Del, the
/// clipboard keys and drops (<see cref="PaneContextMenus"/>), and the clipboard's current cut set (AC-3.14). A view built
/// after startup thus gets everything a view built at startup gets. UI thread only.
/// </summary>
public sealed class PaneViewWiring
{
    private readonly Func<PaneViewModel, bool, PaneView> _build;
    private readonly PaneContextMenus _menus;
    private readonly IClipboardService _clipboard;
    private readonly Action<PaneViewModel> _openSearch;
    private readonly List<PaneView> _views = [];

    /// <summary>Creates the wiring.</summary>
    /// <param name="build">Creates the attached view (with its tree) of a pane; the flag asks for <see cref="PaneView.DeferList"/>
    /// before the view is attached.</param>
    /// <param name="menus">Context menus, Del, clipboard keys and drops of every view.</param>
    /// <param name="clipboard">The file clipboard (cut set).</param>
    /// <param name="openSearch">Opens the search of a pane (Ctrl+F, the Search button).</param>
    public PaneViewWiring(Func<PaneViewModel, bool, PaneView> build, PaneContextMenus menus, IClipboardService clipboard, Action<PaneViewModel> openSearch)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _menus = menus ?? throw new ArgumentNullException(nameof(menus));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _openSearch = openSearch ?? throw new ArgumentNullException(nameof(openSearch));
    }

    /// <summary>The views built so far, in order.</summary>
    public IReadOnlyList<PaneView> Views => _views;

    /// <summary>While true, new views start with their list held back (<see cref="PaneView.DeferList"/>, staged startup).</summary>
    public bool DeferLists { get; set; }

    /// <summary>Builds and wires the view of <paramref name="pane"/> (the factory of <see cref="LayoutHost"/>).</summary>
    /// <param name="pane">A pane of the pool.</param>
    /// <returns>The new view.</returns>
    public PaneView Create(PaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        PaneView view = _build(pane, DeferLists);
        view.SearchRequested += (_, _) => _openSearch(pane);
        _menus.Attach(view);
        view.FileList.SetCutPaths(CutPaths()); // kept by a deferred list and applied to its rows once they exist
        _views.Add(view);
        return view;
    }

    /// <summary>Shows the clipboard's current cut set in every view (after <see cref="IClipboardService.Changed"/>).</summary>
    public void ApplyCut()
    {
        IReadOnlySet<string>? cut = CutPaths();
        foreach (PaneView view in _views)
        {
            view.FileList.SetCutPaths(cut);
        }
    }

    private IReadOnlySet<string>? CutPaths() => _clipboard.Current is { Cut: true } files
        ? new HashSet<string>(files.Paths, StringComparer.OrdinalIgnoreCase)
        : null;
}
