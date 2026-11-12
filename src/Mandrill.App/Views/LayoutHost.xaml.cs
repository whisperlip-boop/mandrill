using CommunityToolkit.WinUI.Controls;
using Mandrill.Core.State;
using Mandrill.Presentation.Layout;
using Mandrill.Presentation.Panes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mandrill.App.Views;

/// <summary>
/// The pane area (architecture §6, ADR-0012, AC-1.1–1.4): places the visible <see cref="PaneView"/>s in the 3×3 grid per
/// <see cref="LayoutSpec"/>, shows the layout's splitters with the ratios from <see cref="LayoutManager"/>, stores the
/// ratios after a drag or keyboard resize, and shows which pane is active. Hidden panes leave the visual tree but keep
/// their view (never rebuilt). With a view factory, a pane's view is created the first time the pane is shown (WP-1.17:
/// hidden panes cost no startup time). UI thread only.
/// </summary>
public sealed partial class LayoutHost : UserControl
{
    private readonly Dictionary<PaneViewModel, PaneView> _views = new(ReferenceEqualityComparer.Instance);
    private LayoutManager? _layout;
    private Func<PaneViewModel, PaneView>? _create;

    /// <summary>Creates the host; call <c>Attach</c> before use.</summary>
    public LayoutHost()
    {
        InitializeComponent();
        foreach (GridSplitter splitter in new[] { ColumnSplitter, RowSplitter })
        {
            splitter.ManipulationCompleted += (_, _) => CaptureSplitters();
            splitter.KeyUp += (_, _) => CaptureSplitters();
        }
    }

    /// <summary>The 3×3 grid (tests).</summary>
    public Grid Grid => LayoutGrid;

    /// <summary>The splitter between the left and right panes (tests).</summary>
    public GridSplitter ColumnSplitterControl => ColumnSplitter;

    /// <summary>The splitter between the top and bottom panes (tests).</summary>
    public GridSplitter RowSplitterControl => RowSplitter;

    /// <summary>The view of the active pane (null before <c>Attach</c>).</summary>
    public PaneView? ActiveView => _layout is { VisibleCount: > 0 } layout && _views.TryGetValue(layout.ActivePane, out PaneView? view) ? view : null; // a shown pane has its view (Apply)

    /// <summary>Connects the host to the window's layout and the 4 pane views (one per pane in the pool).</summary>
    /// <param name="layout">Layout manager.</param>
    /// <param name="views">Attached pane views, one for each pane in <see cref="LayoutManager.Pool"/>.</param>
    /// <exception cref="ArgumentException">A pane of the pool has no view.</exception>
    /// <exception cref="InvalidOperationException">Already attached.</exception>
    public void Attach(LayoutManager layout, IEnumerable<PaneView> views)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(views);
        if (_layout is not null)
        {
            throw new InvalidOperationException("The layout host is already attached");
        }

        foreach (PaneView view in views)
        {
            PaneViewModel vm = view.ViewModel ?? throw new ArgumentException("A pane view is not attached", nameof(views));
            _views[vm] = view;
            view.Activated += OnPaneActivated;
        }

        if (layout.Pool.Any(p => !_views.ContainsKey(p)))
        {
            throw new ArgumentException("Every pane of the layout needs a view", nameof(views));
        }

        Connect(layout);
    }

    /// <summary>
    /// Connects the host to the window's layout; the view of a pane is created by <paramref name="create"/> when the pane is
    /// first shown (or first asked for through <see cref="ViewOf"/>), then kept.
    /// </summary>
    /// <param name="layout">Layout manager.</param>
    /// <param name="create">Creates the attached view of a pane of <see cref="LayoutManager.Pool"/>.</param>
    /// <exception cref="InvalidOperationException">Already attached.</exception>
    public void Attach(LayoutManager layout, Func<PaneViewModel, PaneView> create)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(create);
        if (_layout is not null)
        {
            throw new InvalidOperationException("The layout host is already attached");
        }

        _create = create;
        Connect(layout);
    }

    /// <summary>The views created so far (with a factory, only of panes that have been shown).</summary>
    public IReadOnlyCollection<PaneView> CreatedViews => _views.Values;

    private void Connect(LayoutManager layout)
    {
        _layout = layout;
        layout.LayoutChanged += Apply;
        layout.ActiveChanged += ApplyActive;
        layout.SplittersChanged += OnSplittersChanged; // ratios set from outside (favorite restore, WP-1.12) reach the grid
        Apply();
    }

    /// <summary>The view of <paramref name="pane"/>, created now if the host has a factory and the pane has no view yet.</summary>
    /// <param name="pane">A pane of the pool.</param>
    /// <returns>Its view.</returns>
    /// <exception cref="KeyNotFoundException">The pane is not in the pool, or has no view and the host has no factory.</exception>
    public PaneView ViewOf(PaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        if (_views.TryGetValue(pane, out PaneView? view))
        {
            return view;
        }

        if (_create is null || _layout is null || !_layout.Pool.Contains(pane))
        {
            throw new KeyNotFoundException("The pane has no view");
        }

        view = _create(pane);
        if (!ReferenceEquals(view.ViewModel, pane))
        {
            throw new InvalidOperationException("The view factory returned a view of another pane");
        }

        _views[pane] = view;
        view.Activated += OnPaneActivated;
        return view;
    }

    private void OnPaneActivated(object? sender, EventArgs e)
    {
        if (_layout is { } layout && sender is PaneView { ViewModel: { } vm })
        {
            for (int i = 0; i < layout.VisibleCount; i++)
            {
                if (ReferenceEquals(layout.Pool[i], vm))
                {
                    layout.Activate(i);
                    return;
                }
            }
        }
    }

    // Re-parents the visible panes and positions the splitters for the current layout.
    private void Apply()
    {
        if (_layout is not { } layout)
        {
            return;
        }

        LayoutSpec spec = LayoutSpec.Get(layout.Current);
        var visible = new HashSet<PaneView>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < layout.VisibleCount; i++)
        {
            PaneView view = ViewOf(layout.Pool[i]);
            visible.Add(view);
            PanePlacement place = spec.Panes[i];
            Grid.SetRow(view, place.Row);
            Grid.SetColumn(view, place.Column);
            Grid.SetRowSpan(view, place.RowSpan);
            Grid.SetColumnSpan(view, place.ColumnSpan);
            if (!LayoutGrid.Children.Contains(view))
            {
                LayoutGrid.Children.Add(view);
            }
        }

        for (int i = LayoutGrid.Children.Count - 1; i >= 0; i--)
        {
            if (LayoutGrid.Children[i] is PaneView view && !visible.Contains(view))
            {
                LayoutGrid.Children.RemoveAt(i);
            }
        }

        Place(ColumnSplitter, spec.ColumnSplitter, column: true);
        Place(RowSplitter, spec.RowSplitter, column: false);

        // A pane that spans the splitter track must not leave a 5 px gap on that axis.
        LayoutGrid.ColumnDefinitions[1].Width = new GridLength(spec.ColumnSplitter is null ? 0 : 5);
        LayoutGrid.RowDefinitions[1].Height = new GridLength(spec.RowSplitter is null ? 0 : 5);
        ApplyRatios(layout.GetSplitters(layout.Current), spec);
        ApplyActive();
    }

    private static void Place(GridSplitter splitter, TrackSpan? span, bool column)
    {
        if (span is null)
        {
            splitter.Visibility = Visibility.Collapsed;
            return;
        }

        splitter.Visibility = Visibility.Visible;
        if (column)
        {
            Grid.SetRow(splitter, span.Start);
            Grid.SetRowSpan(splitter, span.Span);
        }
        else
        {
            Grid.SetColumn(splitter, span.Start);
            Grid.SetColumnSpan(splitter, span.Span);
        }
    }

    // Star ratios keep the split proportional when the window is resized (SplitterState holds ratios, not pixels).
    private void ApplyRatios(SplitterState state, LayoutSpec spec)
    {
        double col = spec.ColumnSplitter is null ? 0.5 : state.Col;
        double row = spec.RowSplitter is null ? 0.5 : state.Row;
        LayoutGrid.ColumnDefinitions[0].Width = new GridLength(col, GridUnitType.Star);
        LayoutGrid.ColumnDefinitions[2].Width = new GridLength(1 - col, GridUnitType.Star);
        LayoutGrid.RowDefinitions[0].Height = new GridLength(row, GridUnitType.Star);
        LayoutGrid.RowDefinitions[2].Height = new GridLength(1 - row, GridUnitType.Star);
    }

    /// <summary>
    /// Stores the current split as ratios in the layout (called after a splitter drag or keyboard resize; tests call it
    /// directly) and re-applies them as star sizes, so the split stays proportional when the window is resized.
    /// </summary>
    public void CaptureSplitters()
    {
        if (_layout is not { VisibleCount: > 0 } layout)
        {
            return;
        }

        SplitterState old = layout.GetSplitters(layout.Current);
        LayoutSpec spec = LayoutSpec.Get(layout.Current);
        double col = spec.ColumnSplitter is null ? old.Col : Ratio(LayoutGrid.ColumnDefinitions[0].ActualWidth, LayoutGrid.ColumnDefinitions[2].ActualWidth, old.Col);
        double row = spec.RowSplitter is null ? old.Row : Ratio(LayoutGrid.RowDefinitions[0].ActualHeight, LayoutGrid.RowDefinitions[2].ActualHeight, old.Row);
        var state = new SplitterState(col, row);
        layout.SetSplitters(layout.Current, state); // SplittersChanged re-applies them as stars
    }

    private void OnSplittersChanged()
    {
        if (_layout is { } layout)
        {
            ApplyRatios(layout.GetSplitters(layout.Current), LayoutSpec.Get(layout.Current));
        }
    }

    private static double Ratio(double first, double second, double fallback)
        => first + second > 0 ? first / (first + second) : fallback;

    private void ApplyActive()
    {
        if (_layout is not { } layout)
        {
            return;
        }

        foreach (var (vm, view) in _views)
        {
            view.IsActive = layout.VisibleCount > 0 && ReferenceEquals(vm, layout.ActivePane);
        }
    }
}
