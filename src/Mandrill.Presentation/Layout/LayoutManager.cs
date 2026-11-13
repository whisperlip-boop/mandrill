using Mandrill.Core.State;
using Mandrill.Presentation.Panes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Presentation.Layout;

/// <summary>Layout part of the session (AC-1.33): what <see cref="LayoutManager"/> restores and captures.</summary>
/// <param name="Layout">Layout key (<see cref="LayoutKeys"/>).</param>
/// <param name="ActivePane">Index of the active pane in <paramref name="Panes"/> order.</param>
/// <param name="Splitters">Splitter ratios per layout key.</param>
/// <param name="Panes">Visible panes in pane order; may be shorter than the layout's pane count (missing panes open as new).</param>
public sealed record LayoutSnapshot(string Layout, int ActivePane, IReadOnlyDictionary<string, SplitterState> Splitters, IReadOnlyList<PaneState> Panes);

/// <summary>
/// Owns the 4 panes and the window layout (architecture §6, ADR-0012, AC-1.1–1.4, 1.3a). Visible panes are
/// <c>Pool[0..VisibleCount)</c>; the others are hidden but keep their state for the session (AC-1.3). The App re-parents its
/// 4 PaneViews from <see cref="Pool"/> on <see cref="LayoutChanged"/>. UI thread only.
/// </summary>
public sealed class LayoutManager
{
    /// <summary>Most panes in a layout.</summary>
    public const int MaxPanes = 4;

    private readonly List<PaneViewModel> _pool;
    private readonly HashSet<PaneViewModel> _shown = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LayoutId, SplitterState> _splitters = [];
    private readonly Func<LayoutDefaults> _defaults;
    private readonly ILogger _logger;
    private int _activeIndex;

    /// <summary>Creates the manager. All panes start hidden until <see cref="RestoreAsync"/> or <see cref="Apply"/>.</summary>
    /// <param name="panes">Exactly <see cref="MaxPanes"/> panes in initial pane order. The caller owns and disposes them.</param>
    /// <param name="layoutDefaults">Current Settings → Layout defaults (read on every Add/Remove, AC-1.3b).</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    /// <exception cref="ArgumentException"><paramref name="panes"/> does not hold 4 distinct panes.</exception>
    public LayoutManager(IReadOnlyList<PaneViewModel> panes, Func<LayoutDefaults> layoutDefaults, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(panes);
        ArgumentNullException.ThrowIfNull(layoutDefaults);
        if (panes.Count != MaxPanes || panes.Distinct(ReferenceEqualityComparer.Instance).Count() != MaxPanes)
        {
            throw new ArgumentException($"Exactly {MaxPanes} distinct panes are required.", nameof(panes));
        }

        _pool = [.. panes];
        _defaults = layoutDefaults;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<LayoutManager>();
        foreach (PaneViewModel pane in _pool)
        {
            pane.SetVisible(false);
        }
    }

    /// <summary>Raised after the layout, the visible panes or their order changed.</summary>
    public event Action? LayoutChanged;

    /// <summary>Raised after the active pane changed.</summary>
    public event Action? ActiveChanged;

    /// <summary>
    /// Raised after <see cref="SetSplitters"/> stored ratios (a drag, or a session/favorite restore). A layout change
    /// resets the new layout's ratios without this event; <see cref="LayoutChanged"/> covers that case.
    /// </summary>
    public event Action? SplittersChanged;

    /// <summary>All 4 panes in pane order; the first <see cref="VisibleCount"/> are shown. Live view (reordered by Remove).</summary>
    public IReadOnlyList<PaneViewModel> Pool => _pool;

    /// <summary>Current layout.</summary>
    public LayoutId Current { get; private set; } = LayoutId.TwoLeftRight;

    /// <summary>Number of visible panes; 0 before the first <see cref="RestoreAsync"/> or <see cref="Apply"/>.</summary>
    public int VisibleCount { get; private set; }

    /// <summary>Index of the active pane in <see cref="Pool"/> (AC-1.4). Out-of-range values are ignored.</summary>
    public int ActiveIndex
    {
        get => _activeIndex;
        set => SetActive(value);
    }

    /// <summary>The active pane.</summary>
    public PaneViewModel ActivePane => _pool[_activeIndex];

    /// <summary>Whether <see cref="AddPane"/> can run (fewer than 4 panes).</summary>
    public bool CanAddPane => VisibleCount < MaxPanes;

    /// <summary>Whether <see cref="RemoveActivePane"/> can run (more than 1 pane).</summary>
    public bool CanRemovePane => VisibleCount > 1;

    /// <summary>
    /// Switches to <paramref name="id"/> (layout picker, View menu). Surviving panes keep their state in pane order; hidden
    /// panes keep theirs for the session; a pane shown for the first time opens at the active pane's folder. The new
    /// layout's splitters reset to equal (ui-spec §1). The active pane is clamped to the visible range.
    /// </summary>
    /// <param name="id">Layout.</param>
    public void Apply(LayoutId id)
    {
        if (VisibleCount > 0 && id == Current)
        {
            return;
        }

        string? activePath = VisibleCount > 0 ? ActivePane.ToState().Path : null; // a folder, or This PC (ADR-0015)
        Show(id, activePath);
        _splitters[id] = SplitterState.Equal;
        _logger.LogInformation("Layout {Layout} applied ({Count} panes)", LayoutCatalog.Get(id).Key, VisibleCount);
        LayoutChanged?.Invoke();
    }

    /// <summary>
    /// Restores a favorite (AC-1.29, AC-1.31): its layout, splitter ratios and per-pane tree state, and navigates every
    /// visible pane exactly once to the favorite's folder (nearest existing ancestor, else home). View mode, sort and
    /// history are kept (AC-1.29: not restored); the favorite navigations enter the history. Panes the favorite does not
    /// cover open at its first folder. The active pane keeps its index (clamped).
    /// </summary>
    /// <param name="layoutKey">Layout key of the favorite (<see cref="LayoutKeys"/>); an unknown key keeps the current layout.</param>
    /// <param name="splitters">Splitter ratios of the favorite.</param>
    /// <param name="panes">Favorite panes in pane order.</param>
    /// <param name="ct">Stops waiting for the results; the navigations continue (a newer navigation supersedes them).</param>
    /// <returns>The navigation results of the visible panes in pane order.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before all panes finished.</exception>
    public Task<NavigationResult[]> ApplyFavoriteAsync(string layoutKey, SplitterState splitters, IReadOnlyList<FavoritePane> panes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(splitters);
        ArgumentNullException.ThrowIfNull(panes);
        if (!LayoutCatalog.TryFromKey(layoutKey, out LayoutId id))
        {
            _logger.LogWarning("Unknown layout {Layout} in a favorite; keeping {Current}", layoutKey, LayoutCatalog.Get(Current).Key);
            id = Current;
        }

        int n = LayoutCatalog.Get(id).PaneCount;
        string extraPath = panes.Count > 0 ? panes[0].Path : string.Empty;

        // The navigation is each pane's only request (no reload on show, no "new pane at the active folder" first).
        Task<NavigationResult>?[] tasks = ShowPanes(n, reload: false, (i, pane, isNew) =>
        {
            FavoritePane? saved = i < panes.Count ? panes[i] : null;
            string path = saved?.Path ?? extraPath;

            // A pane beyond the favorite keeps its own tree state; a brand-new one gets the rule for this pane count.
            pane.TreeVisible = saved?.TreeVisible ?? (isNew ? DefaultTreeVisible(n) : pane.TreeVisible);

            // An empty path opens the home folder in the active tab and keeps the pane's view state, tabs and history.
            return path.Length > 0
                ? pane.NavigateAsync(path, NavOrigin.Favorite)
                : pane.RestoreAsync(pane.ToState() with { Path = string.Empty, Tabs = null }, fallbackToAncestor: true);
        });

        Current = id;
        VisibleCount = n;
        bool activeMoved = _activeIndex >= n;
        if (activeMoved)
        {
            _activeIndex = n - 1;
        }

        SetSplitters(id, splitters);
        _logger.LogInformation("Favorite layout {Layout} applied ({Count} panes)", LayoutCatalog.Get(id).Key, n);
        LayoutChanged?.Invoke();
        if (activeMoved)
        {
            ActiveChanged?.Invoke();
        }

        return Task.WhenAll(tasks.Select(t => t!)).WaitAsync(ct);
    }

    /// <summary>One more pane in the default arrangement for the new count (AC-1.3a). The new pane becomes active.</summary>
    public void AddPane()
    {
        if (!CanAddPane || VisibleCount == 0)
        {
            return;
        }

        int newIndex = VisibleCount;
        Apply(DefaultFor(VisibleCount + 1));
        SetActive(newIndex);
    }

    /// <summary>
    /// Removes the active pane (AC-1.3a): it moves just past the visible range (state kept for a later Add), survivors keep
    /// their order and re-flow into the default arrangement for the new count. The pane now at the removed index becomes
    /// active, else the previous one. Example: [A,B,C,D], 3 visible, active B → [A,C,B,D], 2 visible, active C.
    /// </summary>
    public void RemoveActivePane()
    {
        if (!CanRemovePane)
        {
            return;
        }

        int k = _activeIndex;
        int n = VisibleCount;
        PaneViewModel removed = _pool[k];
        _pool.RemoveAt(k);
        _pool.Insert(n - 1, removed);
        _activeIndex = Math.Min(k, n - 2);
        Apply(DefaultFor(n - 1));
        ActiveChanged?.Invoke();
    }

    /// <summary>Activates a pane by index; a pane that is not visible is ignored (Alt+N with N greater than the pane count).</summary>
    /// <param name="index">Pane index in <see cref="Pool"/> (0-based).</param>
    /// <returns>True when that pane is now active.</returns>
    public bool Activate(int index)
    {
        if (index < 0 || index >= VisibleCount)
        {
            return false;
        }

        SetActive(index);
        return true;
    }

    /// <summary>Splitter ratios of <paramref name="id"/> (equal when never set).</summary>
    /// <param name="id">Layout.</param>
    /// <returns>The ratios.</returns>
    public SplitterState GetSplitters(LayoutId id) => _splitters.TryGetValue(id, out SplitterState? s) ? s : SplitterState.Equal;

    /// <summary>Stores splitter ratios of <paramref name="id"/> (after a drag, or from a session or favorite after <see cref="Apply"/>).</summary>
    /// <param name="id">Layout.</param>
    /// <param name="state">Star ratios; values outside (0, 1) fall back to 0.5.</param>
    public void SetSplitters(LayoutId id, SplitterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var clean = new SplitterState(ValidRatio(state.Col), ValidRatio(state.Row));
        if (clean != state)
        {
            _logger.LogWarning("Invalid splitter ratios {State} for layout {Layout}; using {Clean}", state, LayoutCatalog.Get(id).Key, clean);
        }

        _splitters[id] = clean;
        SplittersChanged?.Invoke();
    }

    /// <summary>
    /// Restores the layout part of the session (AC-1.33): layout, splitters, pane states and the active pane. Panes missing
    /// from <paramref name="snapshot"/> open at the active pane's folder. On first run the tree is expanded in 1–2 pane layouts
    /// and collapsed in 3–4 pane layouts (ui-spec §2).
    /// </summary>
    /// <param name="snapshot">Saved layout (normalized by the session store).</param>
    /// <param name="firstRun">True when no session existed.</param>
    /// <param name="ct">Stops waiting for the results; the panes' navigations continue (a newer navigation supersedes them).</param>
    /// <returns>The navigation results of the visible panes in pane order (redirected paths for one status message).</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before all panes finished.</exception>
    public Task<NavigationResult[]> RestoreAsync(LayoutSnapshot snapshot, bool firstRun, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!LayoutCatalog.TryFromKey(snapshot.Layout, out LayoutId id))
        {
            _logger.LogWarning("Unknown layout {Layout} in session; using {Default}", snapshot.Layout, LayoutKeys.TwoLeftRight);
            id = LayoutId.TwoLeftRight;
        }

        _splitters.Clear();
        foreach (var (key, value) in snapshot.Splitters ?? new Dictionary<string, SplitterState>())
        {
            if (LayoutCatalog.TryFromKey(key, out LayoutId splitterLayout) && value is not null)
            {
                SetSplitters(splitterLayout, value);
            }
        }

        int n = LayoutCatalog.Get(id).PaneCount;
        IReadOnlyList<PaneState> saved = snapshot.Panes ?? [];
        int active = Math.Clamp(snapshot.ActivePane, 0, n - 1);
        string newPanePath = active < saved.Count ? saved[active].Path : saved.Count > 0 ? saved[0].Path : string.Empty;

        Current = id;
        VisibleCount = n;
        _activeIndex = active;
        Task<NavigationResult>?[] tasks = ShowPanes(n, reload: false, (i, pane, _) =>
        {
            PaneState state = i < saved.Count && saved[i] is { } s ? s : NewPaneState(newPanePath, n);
            if (firstRun)
            {
                state = state with { TreeVisible = DefaultTreeVisible(n) };
            }

            return pane.RestoreAsync(state, fallbackToAncestor: true);
        });

        _logger.LogInformation("Layout {Layout} restored ({Count} panes, active {Active}, first run {FirstRun})", LayoutCatalog.Get(id).Key, n, active, firstRun);
        LayoutChanged?.Invoke();
        ActiveChanged?.Invoke();
        return Task.WhenAll(tasks.Select(t => t!)).WaitAsync(ct);
    }

    /// <summary>The layout part of the session to save (visible panes only; hidden panes are session-only, AC-1.3).</summary>
    /// <returns>The snapshot.</returns>
    public LayoutSnapshot Capture()
    {
        var splitters = new Dictionary<string, SplitterState>(StringComparer.Ordinal);
        foreach (var (id, state) in _splitters)
        {
            splitters[LayoutCatalog.Get(id).Key] = state;
        }

        var panes = new PaneState[VisibleCount];
        for (int i = 0; i < panes.Length; i++)
        {
            panes[i] = _pool[i].ToState();
        }

        return new LayoutSnapshot(LayoutCatalog.Get(Current).Key, _activeIndex, splitters, panes);
    }

    // First-run rule (ui-spec §2), also used for a pane shown for the first time (lead decision Q8).
    private static bool DefaultTreeVisible(int paneCount) => paneCount <= 2;

    private static PaneState NewPaneState(string path, int paneCount)
        => PaneState.Default with { Path = path, TreeVisible = DefaultTreeVisible(paneCount) };

    private static double ValidRatio(double ratio) => double.IsFinite(ratio) && ratio > 0 && ratio < 1 ? ratio : 0.5;

    private LayoutId DefaultFor(int paneCount)
    {
        string key;
        try
        {
            key = _defaults().ForPaneCount(paneCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the layout defaults failed; using the built-in default");
            key = LayoutDefaults.Default.ForPaneCount(paneCount);
        }

        if (LayoutCatalog.TryFromKey(key, out LayoutId id) && LayoutCatalog.Get(id).PaneCount == paneCount)
        {
            return id;
        }

        _logger.LogWarning("Layout default {Key} does not fit {Count} panes; using the built-in default", key, paneCount);
        LayoutCatalog.TryFromKey(LayoutDefaults.Default.ForPaneCount(paneCount), out id);
        return id;
    }

    private void Show(LayoutId id, string? activePath)
    {
        int n = LayoutCatalog.Get(id).PaneCount;
        ShowPanes(n, reload: true, (_, pane, isNew) =>
        {
            if (isNew)
            {
                // Brand-new pane: the active pane's folder (AC-1.3), tree by the first-run rule for this count (Q8).
                return pane.RestoreAsync(NewPaneState(activePath ?? string.Empty, n), fallbackToAncestor: true);
            }

            // Shown before, but hiding cancelled its first navigation: keep its view state, open the active pane's folder.
            return pane.CurrentPath is null && !pane.IsThisPc
                ? pane.RestoreAsync(pane.ToState() with { Path = activePath ?? string.Empty, Tabs = null }, fallbackToAncestor: true)
                : null;
        });

        Current = id;
        VisibleCount = n;
        if (_activeIndex >= n)
        {
            _activeIndex = n - 1;
            ActiveChanged?.Invoke();
        }
    }

    // The one show/hide loop: hides Pool[n..), shows Pool[0..n) and lets the caller open each visible pane. isNew = shown
    // for the first time this session. reload: false when open() navigates the pane right away (one request per pane).
    private Task<NavigationResult>?[] ShowPanes(int n, bool reload, Func<int, PaneViewModel, bool, Task<NavigationResult>?> open)
    {
        var tasks = new Task<NavigationResult>?[n];
        for (int i = 0; i < MaxPanes; i++)
        {
            PaneViewModel pane = _pool[i];
            if (i >= n)
            {
                pane.SetVisible(false);
                pane.DiscardBackgroundTabs(); // owner decision: a removed pane keeps only its active tab
                continue;
            }

            bool isNew = !_shown.Contains(pane);
            pane.SetVisible(true, reload);
            _shown.Add(pane);
            tasks[i] = open(i, pane, isNew);
        }

        return tasks;
    }

    private void SetActive(int index)
    {
        if (index >= 0 && index < VisibleCount && index != _activeIndex)
        {
            _activeIndex = index;
            ActiveChanged?.Invoke();
        }
    }
}
