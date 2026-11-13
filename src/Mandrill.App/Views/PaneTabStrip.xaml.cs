using System.Collections.Specialized;
using System.ComponentModel;
using Mandrill.App.Icons;
using Mandrill.App.Platform;
using Mandrill.Core.Localization;
using Mandrill.Presentation.Panes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Mandrill.App.Views;

/// <summary>
/// A pane's tab row (ui-spec §2a, architecture §17.5, ADR-0017): one <see cref="PaneTabItem"/> per <see cref="PaneViewModel.Tabs"/>
/// entry, overlapping by 16 px (left in front, active on top), shrinking 160 → 96 px and then overflowing with "&lt;" "&gt;"
/// "…" and a pinned "+", the 2 px strip, a shared context menu, roving keyboard focus, drag reorder within the pane and UIA
/// Tab/TabItem peers. Tab state lives in the view model; this only mirrors it.
/// </summary>
public sealed partial class PaneTabStrip : UserControl
{
    /// <summary>Movement before a press becomes a drag (fixed: Win32 metrics are not used in the App, ui-spec §2a).</summary>
    public const double DragThreshold = 4;

    private const double ChevronWidth = 20;
    private const double MoreWidth = 16;
    private const double EdgeScrollZone = 24;
    private const double EdgeScrollStep = 8;
    private static readonly UISettings Settings = new();

    private readonly Dictionary<PaneTab, PaneTabItem> _byTab = [];
    private readonly Dictionary<TranslateTransform, (Storyboard Board, double To)> _animations = [];
    private readonly List<PaneTabItem> _items = [];
    private readonly MenuFlyout _menu = new();
    private readonly Canvas _viewport = new() { Height = 24 };
    private readonly Canvas _track = new() { Height = 24 };
    private readonly TranslateTransform _scroll = new();
    private readonly Button _left;
    private readonly Button _right;
    private readonly Border _more = new() { Width = MoreWidth, Height = 24, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Border _plusHost = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Button _plus = new() { Width = 24, Height = 24, Padding = new Thickness(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _edgeScroll;
    private PaneViewModel? _vm;
    private PaneTabItem? _roving;
    private PaneTabItem? _active;
    private IconProvider? _icons;
    private TabStripLayout _layout = TabStripLayout.Compute(0, 1);
    private Press? _press;
    private double _offset;
    private bool _menuFilled;
    private bool _menuFromRow;
    private bool _isPaneActive = true;
    private double _reservedRight;

    /// <summary>Creates the strip; call <see cref="Attach"/> before use.</summary>
    public PaneTabStrip()
    {
        InitializeComponent();
        AppTheme.Track(Resources); // SubtleButtons.xaml scope ("+", chevrons)
        _left = Chevron("pane.tab.scrollLeft", -1);
        _right = Chevron("pane.tab.scrollRight", 1);
        ToolTipService.SetToolTip(_more, Localizer.Current["pane.tab.hidden"]);
        AutomationProperties.SetAccessibilityView(_more, AccessibilityView.Raw);

        // A disabled control raises no pointer events, so its own tooltip never opens: the limit tooltip sits on a host.
        _plusHost.Child = _plus;
        _track.RenderTransform = _scroll;
        _viewport.Children.Add(_track);
        RowCanvas.Children.Add(_left);
        RowCanvas.Children.Add(_right);
        RowCanvas.Children.Add(_more);
        RowCanvas.Children.Add(_viewport);
        RowCanvas.Children.Add(_plusHost);
        _plus.Click += OnPlusClick;
        _menu.Opening += OnMenuOpening;
        _menu.Closed += (_, _) => RefocusAfterMenu();
        _edgeScroll = DispatcherQueue.CreateTimer();
        _edgeScroll.Interval = TimeSpan.FromMilliseconds(16);
        _edgeScroll.Tick += (_, _) => EdgeScrollTick();
        SizeChanged += (_, _) => ArrangeTabs();
        ActualThemeChanged += (_, _) => RefreshAll();
    }

    /// <summary>A tab was activated with the mouse: the pane moves the focus to its list (ui-spec §2a).</summary>
    public event EventHandler? ListFocusRequested;

    /// <summary>Whether the pane is the active one; inactive panes scrim the tabs and the strip.</summary>
    public bool IsPaneActive
    {
        get => _isPaneActive;
        set
        {
            if (_isPaneActive != value)
            {
                _isPaneActive = value;
                RefreshAll();
            }
        }
    }

    /// <summary>Width kept free at the right end for the view buttons and their 8 px gap.</summary>
    public double ReservedRight
    {
        get => _reservedRight;
        set
        {
            _reservedRight = value;
            ArrangeTabs();
        }
    }

    /// <summary>The tab items in strip order (tests).</summary>
    public IReadOnlyList<PaneTabItem> Items => _items;

    /// <summary>The "+" button (tests).</summary>
    public Button NewTabButton => _plus;

    /// <summary>The tooltip shown over "+": New tab, or the limit message when disabled (tests).</summary>
    public object NewTabToolTip => ToolTipService.GetToolTip(_plusHost);

    /// <summary>The scroll buttons (tests).</summary>
    public (Button Left, Button Right) ScrollButtons => (_left, _right);

    /// <summary>The current width sharing (tests).</summary>
    public TabStripLayout Layout => _layout;

    /// <summary>Horizontal scroll offset of the tabs in overflow (tests).</summary>
    public double ScrollOffset => _offset;

    /// <summary>The shared tab context menu (tests).</summary>
    public MenuFlyout TabMenu => _menu;

    /// <summary>The strip's current colour (tests).</summary>
    public Brush? StripBrush => Strip.Fill;

    /// <summary>The row background the inactive-pane scrim blends with: the theme's page background (tests use it too).</summary>
    public Color RowBackground => (RowBackgroundSwatch.Background as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.White;

    internal PaneTabItem? ActiveItem => _vm is null ? null : _byTab.GetValueOrDefault(_vm.ActiveTab);

    internal bool CanClose => _vm?.CanCloseTab == true;

    internal int Count => _items.Count;

    /// <summary>
    /// Binds the strip to its pane for the pane's lifetime: it never unsubscribes, which holds while each pane has exactly one
    /// view (App builds one per pane and keeps it). A view rebuilt for a live pane would need a Detach.
    /// </summary>
    /// <param name="vm">The pane.</param>
    /// <param name="icons">Icon provider for "+" and the x.</param>
    /// <exception cref="InvalidOperationException">Already attached.</exception>
    public void Attach(PaneViewModel vm, IconProvider icons)
    {
        if (_vm is not null)
        {
            throw new InvalidOperationException("The tab strip is already attached to a pane");
        }

        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _plus.Content = icons.CreateElement(IconId.Add, size: 12);
        _left.Content = icons.CreateElement(IconId.TabLeft, size: 10);
        _right.Content = icons.CreateElement(IconId.TabRight, size: 10);
        _more.Child = icons.CreateElement(IconId.Ellipsis, size: 12);
        vm.Tabs.CollectionChanged += OnTabsChanged;
        vm.PropertyChanged += OnPaneChanged;
        Sync();
    }

    /// <summary>Left button pressed on a tab at <paramref name="x"/> (row coordinates): activates it; may become a drag.</summary>
    /// <param name="item">The tab.</param>
    /// <param name="x">Pointer x in the row.</param>
    /// <param name="pointerId">The pressing pointer; events of other pointers are ignored (0 in tests).</param>
    public void PressTab(PaneTabItem item, double x, uint pointerId = 0)
    {
        ArgumentNullException.ThrowIfNull(item);
        CancelDrag();
        Activate(item);
        _press = new Press(item, x, _items.IndexOf(item), _offset, pointerId);
    }

    /// <summary>The pointer moved while pressed: past <see cref="DragThreshold"/> the tab follows it and the others slide aside.</summary>
    /// <param name="x">Pointer x in the row.</param>
    public void DragTo(double x)
    {
        if (_press is not { } press)
        {
            return;
        }

        press.LastX = x;
        double dx = x - press.StartX;
        if (!press.Dragging)
        {
            if (Math.Abs(dx) < DragThreshold)
            {
                return;
            }

            press.Dragging = true;
            press.Item.IsDragging = true;
            Canvas.SetZIndex(press.Item, _items.Count + 2); // raised to the top
            SetRoving(press.Item);
            press.Item.Focus(FocusState.Pointer); // Esc reaches the tab
            press.Item.Refresh();
        }

        double slot = press.From * _layout.Pitch;
        double left = Math.Clamp(slot + dx + (_offset - press.StartOffset), _offset, Math.Max(_offset, _offset + _layout.Viewport - _layout.TabWidth));
        SetShift(press.Item.Shift, left - slot);
        press.Target = Math.Clamp((int)Math.Round(left / _layout.Pitch), 0, _items.Count - 1);
        for (int i = 0; i < _items.Count; i++)
        {
            if (i != press.From)
            {
                double shift = press.From < press.Target && i > press.From && i <= press.Target ? -_layout.Pitch
                    : press.Target < press.From && i >= press.Target && i < press.From ? _layout.Pitch
                    : 0;
                Animate(_items[i].Shift, shift, 120);
            }
        }

        double inView = x - _viewport.ActualOffset.X;
        bool nearEdge = _layout.Overflow && (inView < EdgeScrollZone || inView > _layout.Viewport - EdgeScrollZone);
        if (nearEdge && !_edgeScroll.IsRunning)
        {
            _edgeScroll.Start();
        }
        else if (!nearEdge)
        {
            _edgeScroll.Stop();
        }
    }

    /// <summary>The press ended: a drag moves the tab once (<see cref="PaneViewModel.MoveTab"/>); a click only activated it.</summary>
    public void ReleaseTab()
    {
        if (_press is not { } press)
        {
            return;
        }

        EndPress(press);
        if (press.Dragging && _vm is not null)
        {
            double visual = (press.From * _layout.Pitch) + press.Item.Shift.X;
            foreach (PaneTabItem item in _items)
            {
                SetShift(item.Shift, 0); // their slots after the move are where they were shown
            }

            if (press.Target != press.From)
            {
                _vm.MoveTab(press.From, press.Target);
            }

            SetShift(press.Item.Shift, visual - (press.Target * _layout.Pitch));
            Animate(press.Item.Shift, 0, 100); // snaps into the gap
        }

        ListFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Esc or a lost capture during a drag: the old order comes back.</summary>
    public void CancelDrag()
    {
        if (_press is not { } press)
        {
            return;
        }

        EndPress(press);
        foreach (PaneTabItem item in _items)
        {
            Animate(item.Shift, 0, 100);
        }
    }

    /// <summary>Left/Right/Home/End: moves the focus (the roving Tab stop) to a tab without activating it.</summary>
    /// <param name="item">The tab.</param>
    /// <param name="state">Keyboard (focus ring) or Programmatic.</param>
    /// <returns>Whether the tab got the focus.</returns>
    public bool FocusTab(PaneTabItem item, FocusState state = FocusState.Keyboard)
    {
        ArgumentNullException.ThrowIfNull(item);
        SetRoving(item);
        BringIntoView(item);
        return item.Focus(state);
    }

    /// <summary>Parks the focus on the active tab without a focus ring (the pane swaps its list under the focus, QA F1).</summary>
    /// <returns>Whether the tab got the focus.</returns>
    public bool HoldFocus() => ActiveItem is { } item && FocusTab(item, FocusState.Programmatic);

    internal int IndexOf(PaneTabItem item) => _items.IndexOf(item);

    /// <summary>Activates a tab (UIA Select, Enter/Space): keeps the focus where it is.</summary>
    internal void Activate(PaneTabItem item) => _ = _vm?.ActivateTabAsync(item.Tab);

    internal void Close(PaneTabItem item) => _vm?.CloseTab(item.Tab);

    internal void OnTabFocused(PaneTabItem item)
    {
        SetRoving(item);
        if (item.FocusState == FocusState.Keyboard)
        {
            BringIntoView(item);
        }

        item.Refresh();
    }

    /// <summary>The colours of a tab in its current state (ui-spec §2a colour and high-contrast tables).</summary>
    internal TabLook LookOf(PaneTabItem item)
    {
        bool active = item.Tab.IsActive;
        bool highlighted = active && _isPaneActive;
        Brush focusOuter = FocusSwatch.Background;
        Brush focusInner = FocusSwatch.BorderBrush;
        if (AppTheme.IsHighContrast())
        {
            Brush fill = highlighted ? HcActiveSwatch.Background : HcSwatch.Background;
            Brush text = highlighted ? HcActiveSwatch.BorderBrush : HcSwatch.BorderBrush;
            // The x on hover/press: the tab's system pair inverted (visible on every HC tab fill, including Highlight).
            Color hcFill = ((SolidColorBrush)fill).Color;
            Color hcText = ((SolidColorBrush)text).Color;
            return item.VisualState != TabVisualState.Rest || item.IsDragging
                ? new TabLook(fill, text, HcHoverSwatch.Background, 2, focusOuter, focusInner, hcText, hcText, hcFill) // HC pressed = HC hover
                : new TabLook(fill, text, highlighted ? ActivePaneSwatch.Background : HcSwatch.BorderBrush, active && !_isPaneActive ? 2 : 1, focusOuter, focusInner, hcText, hcText, hcFill);
        }

        Color fillColor = TabPalette.Fill(item.Tab.Color, item.VisualState, _isPaneActive, RowBackground);
        Color textColor = TabPalette.Text(item.Tab.Color, fillColor, _isPaneActive);
        return new TabLook(
            new SolidColorBrush(fillColor),
            new SolidColorBrush(textColor),
            highlighted ? ActivePaneSwatch.Background : EdgeSwatch.Background, // owner: the active pane's selected tab takes its frame colour
            item.IsDragging ? 2 : 1,
            focusOuter,
            focusInner,
            TabPalette.CloseFill(fillColor, pressed: false),
            TabPalette.CloseFill(fillColor, pressed: true),
            textColor);
    }

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer() => new PaneTabStripAutomationPeer(this);

    // One animation per transform. A new target starts from the in-flight value (no From) and then retires the old
    // storyboard; a direct set stops it (else it would win on completion).
    private void Animate(TranslateTransform target, double to, int milliseconds)
    {
        bool hasRunning = _animations.TryGetValue(target, out var running);
        if (hasRunning ? running.To == to : target.X == to)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(Settings.AnimationsEnabled ? milliseconds : 0),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, nameof(TranslateTransform.X));
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (_animations.TryGetValue(target, out var current) && current.Board == storyboard)
            {
                SetShift(target, to);
            }
        };
        _animations[target] = (storyboard, to);
        storyboard.Begin();
        if (hasRunning)
        {
            running.Board.Stop();
        }
    }

    // Stops a running animation of the transform, then sets its value.
    private void SetShift(TranslateTransform target, double x)
    {
        if (_animations.Remove(target, out var running))
        {
            running.Board.Stop();
        }

        target.X = x;
    }

    private Button Chevron(string key, int direction)
    {
        var button = new Button
        {
            Width = ChevronWidth,
            Height = 24,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed,
        };
        ToolTipService.SetToolTip(button, Localizer.Current[key]);
        AutomationProperties.SetName(button, Localizer.Current[key]);
        button.Click += (_, _) => ScrollTo(_offset + (direction * _layout.Pitch)); // one pitch: the next hidden tab
        return button;
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaneViewModel.ActiveTab):
                SetRoving(ActiveItem);
                ArrangeTabs();
                RefreshAll();
                RaiseSelected(ActiveItem, _active);
                _active = ActiveItem;
                if (_active is not null)
                {
                    BringIntoView(_active); // activation, creation and duplication
                }

                break;
            case nameof(PaneViewModel.CanAddTab):
                ApplyCanAdd();
                break;
            case nameof(PaneViewModel.CanCloseTab):
                RefreshAll();
                break;
        }
    }

    // Mirrors Tabs: existing items are kept (focus and peers survive), new ones built, removed ones dropped.
    private void Sync()
    {
        if (_vm is null || _icons is null)
        {
            return;
        }

        CancelDrag();
        int countBefore = _items.Count;
        bool focusInRow = HasFocusWithin();
        var keep = new HashSet<PaneTab>(_vm.Tabs);
        foreach (PaneTabItem gone in _items.Where(i => !keep.Contains(i.Tab)).ToList())
        {
            gone.Detach();
            _byTab.Remove(gone.Tab);
            _track.Children.Remove(gone);
        }

        _items.Clear();
        foreach (PaneTab tab in _vm.Tabs)
        {
            if (!_byTab.TryGetValue(tab, out PaneTabItem? item))
            {
                item = new PaneTabItem(tab, this, _icons) { ContextFlyout = _menu };
                item.KeyDown += OnTabKeyDown;
                item.PointerPressed += OnTabPointerPressed;
                item.PointerMoved += OnTabPointerMoved;
                item.PointerReleased += OnTabPointerReleased;
                item.PointerCaptureLost += OnTabCaptureLost;
                item.PointerCanceled += OnTabPointerCanceled;
                _byTab[tab] = item;
                _track.Children.Add(item);
            }

            _items.Add(item);
        }

        // UIA and Tab order walk the children: keep them in strip order after Duplicate and moves (review T3 m1).
        for (int i = 0; i < _items.Count; i++)
        {
            int at = _track.Children.IndexOf(_items[i]);
            if (at != i)
            {
                _track.Children.Move((uint)at, (uint)i);
            }
        }

        SetRoving(_roving is not null && _byTab.ContainsKey(_roving.Tab) ? _roving : ActiveItem);
        _active = ActiveItem;
        ApplyCanAdd();
        ArrangeTabs();
        RefreshAll();
        if (_items.Count != countBefore && ActiveItem is { } shown)
        {
            BringIntoView(shown); // after a close the active tab's slot moved
        }

        if (focusInRow && !HasFocusWithin())
        {
            // The focused tab was closed: focus the tab the close activated (ui-spec §2a).
            DispatcherQueue.TryEnqueue(() => ActiveItem?.Focus(FocusState.Keyboard));
        }
    }

    // ui-spec §2a width sharing. A is measured without the chevrons, so entering and leaving overflow never flickers.
    private void ArrangeTabs()
    {
        int n = _items.Count;
        double available = ActualWidth - RowCanvas.Margin.Left - _reservedRight - TabStripLayout.PlusSlot;
        TabStripLayout previous = _layout;
        _layout = TabStripLayout.Compute(available, n);
        bool overflow = _layout.Overflow;
        _left.Visibility = _right.Visibility = _more.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_right, ChevronWidth);
        Canvas.SetLeft(_more, 2 * ChevronWidth);
        double viewportLeft = overflow ? TabStripLayout.OverflowSlot : 0;
        Canvas.SetLeft(_viewport, viewportLeft);
        _viewport.Width = _layout.Viewport;
        _viewport.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, _layout.Viewport, 24) };
        Canvas.SetLeft(_plusHost, viewportLeft + _layout.Viewport + 4); // pinned after the viewport: it never scrolls
        RowCanvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, ActualWidth - RowCanvas.Margin.Left - _reservedRight), 24) }; // never under the view buttons
        for (int i = 0; i < n; i++)
        {
            PaneTabItem item = _items[i];
            item.Width = _layout.TabWidth;
            Canvas.SetLeft(item, i * _layout.Pitch);
            Canvas.SetZIndex(item, item.Tab.IsActive ? n + 1 : n - i);
        }

        ScrollTo(_offset, animate: false);
        if (_layout.Viewport != previous.Viewport && ActiveItem is { } active)
        {
            BringIntoView(active, animate: false); // restore and resize: the active tab stays visible (QA F2)
        }
    }

    private void ScrollTo(double offset, bool animate = true)
    {
        double max = Math.Max(0, _layout.ContentWidth(_items.Count) - _layout.Viewport);
        _offset = _layout.Overflow ? Math.Clamp(offset, 0, max) : 0;
        if (animate)
        {
            Animate(_scroll, -_offset, 120);
        }
        else
        {
            SetShift(_scroll, -_offset);
        }

        bool leftOn = _offset > 0.5;
        bool rightOn = _offset < max - 0.5;
        _left.IsEnabled |= leftOn;
        _right.IsEnabled |= rightOn;
        if (!leftOn && rightOn && _left.FocusState != FocusState.Unfocused)
        {
            _right.Focus(_left.FocusState); // a focused chevron that disables itself must not drop the focus
        }
        else if (!rightOn && leftOn && _right.FocusState != FocusState.Unfocused)
        {
            _left.Focus(_right.FocusState);
        }

        _left.IsEnabled = leftOn;
        _right.IsEnabled = rightOn;
    }

    private void BringIntoView(PaneTabItem item, bool animate = true)
    {
        int index = _items.IndexOf(item);
        if (!_layout.Overflow || index < 0)
        {
            return;
        }

        double left = index * _layout.Pitch;
        double right = left + _layout.TabWidth;
        if (left < _offset)
        {
            ScrollTo(left, animate);
        }
        else if (right > _offset + _layout.Viewport)
        {
            ScrollTo(right - _layout.Viewport, animate);
        }
    }

    // Overflow drag near the viewport edge: 8 px per 16 ms (ui-spec §2a).
    private void EdgeScrollTick()
    {
        if (_press is not { Dragging: true } press)
        {
            _edgeScroll.Stop();
            return;
        }

        double inView = press.LastX - _viewport.ActualOffset.X;
        double before = _offset;
        ScrollTo(_offset + (inView < EdgeScrollZone ? -EdgeScrollStep : EdgeScrollStep), animate: false);
        if (_offset == before)
        {
            _edgeScroll.Stop(); // at a scroll limit: nothing more to do until the pointer moves
            return;
        }

        DragTo(press.LastX);
    }

    private void EndPress(Press press)
    {
        _press = null;
        _edgeScroll.Stop();
        press.Item.IsDragging = false;
        press.Item.ReleasePointerCaptures();
        ArrangeTabs(); // z-order back
        press.Item.Refresh();
    }

    private void RefreshAll()
    {
        foreach (PaneTabItem item in _items)
        {
            item.Refresh();
        }

        if (ActiveItem is not { } active)
        {
            return;
        }

        Strip.Fill = AppTheme.IsHighContrast()
            ? (_isPaneActive ? HcActiveSwatch.Background : HcSwatch.BorderBrush)
            : new SolidColorBrush(TabPalette.Strip(active.Tab.Color, _isPaneActive, RowBackground));
    }

    private void ApplyCanAdd()
    {
        bool canAdd = _vm?.CanAddTab == true;
        _plus.IsEnabled = canAdd;
        ToolTipService.SetToolTip(_plusHost, Localizer.Current[canAdd ? "pane.tab.new" : "pane.tab.limitReached"]);
        AutomationProperties.SetName(_plus, Localizer.Current["pane.tab.new"]);
        AutomationProperties.SetHelpText(_plus, canAdd ? string.Empty : Localizer.Current["pane.tab.limitReached"]);
    }

    // Roving focus: exactly one tab is a Tab stop (the active tab, or the last focused one).
    private void SetRoving(PaneTabItem? item)
    {
        _roving = item;
        foreach (PaneTabItem each in _items)
        {
            each.IsTabStop = each == item;
        }
    }

    private void OnTabPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var item = (PaneTabItem)sender;
            PressTab(item, e.GetCurrentPoint(RowCanvas).Position.X, e.Pointer.PointerId);
            item.CapturePointer(e.Pointer);
        }
    }

    // Only the pointer that pressed, on the tab it pressed, drives the press (a second finger or a stray hover does not).
    private bool IsPressPointer(object sender, PointerRoutedEventArgs e)
        => _press is { } press && ReferenceEquals(sender, press.Item) && e.Pointer.PointerId == press.PointerId;

    private void OnTabPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPressPointer(sender, e))
        {
            return;
        }

        if (e.GetCurrentPoint(this).IsInContact)
        {
            DragTo(e.GetCurrentPoint(RowCanvas).Position.X);
        }
        else
        {
            CancelDrag(); // the release was never seen
        }
    }

    private void OnTabPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (IsPressPointer(sender, e))
        {
            ReleaseTab();
            e.Handled = true;
        }
    }

    // A capture lost with the contact up is the release itself (the order of the two events is not relied on).
    private void OnTabCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPressPointer(sender, e))
        {
            return;
        }

        if (e.GetCurrentPoint(this).IsInContact)
        {
            CancelDrag();
        }
        else
        {
            ReleaseTab();
        }
    }

    private void OnTabPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (IsPressPointer(sender, e))
        {
            CancelDrag();
        }
    }

    private void OnTabKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var item = (PaneTabItem)sender;
        if (e.Key == VirtualKey.Escape && _press is { Dragging: true })
        {
            CancelDrag();
            e.Handled = true;
            return;
        }

        int index = _items.IndexOf(item);
        int target = e.Key switch
        {
            VirtualKey.Left => index - 1,
            VirtualKey.Right => index + 1,
            VirtualKey.Home => 0,
            VirtualKey.End => _items.Count - 1,
            _ => -2,
        };
        if (target != -2)
        {
            if (target >= 0 && target < _items.Count && target != index)
            {
                FocusTab(_items[target]); // moves focus only; no wrap
            }

            e.Handled = true;
        }
        else if (e.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            Activate(item); // keyboard activation keeps the focus on the tab
            e.Handled = true;
        }
    }

    private void OnPlusClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        bool keyboard = _plus.FocusState == FocusState.Keyboard;
        _ = _vm.NewTabAsync();
        if (keyboard)
        {
            // The 10th tab disables "+": focus moves to the new tab, once it is in the tree and loaded (review T3 m3).
            DispatcherQueue.TryEnqueue(() => _ = ActiveItem is { } item && FocusTab(item));
        }
        else
        {
            ListFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // Built when first opened (the pane View menu pattern); acts on the right-clicked tab, which it does not activate.
    private void OnMenuOpening(object? sender, object e)
    {
        if (_vm is null || _menu.Target is not PaneTabItem)
        {
            return;
        }

        _menuFromRow = HasFocusWithin(); // before the menu takes the focus: a right-click from the list leaves it there
        if (!_menuFilled)
        {
            _menuFilled = true;
            _menu.Items.Add(new MenuFlyoutItem { Text = Localizer.Current["pane.tab.new"], Tag = "new" });
            _menu.Items.Add(new MenuFlyoutItem { Text = Localizer.Current["pane.tab.duplicate"], Tag = "duplicate" });
            _menu.Items.Add(new MenuFlyoutSeparator());
            _menu.Items.Add(new MenuFlyoutItem { Text = Localizer.Current["pane.tab.close"], Tag = "close" });
            _menu.Items.Add(new MenuFlyoutItem { Text = Localizer.Current["pane.tab.closeOthers"], Tag = "closeOthers" });
            foreach (MenuFlyoutItem item in _menu.Items.OfType<MenuFlyoutItem>())
            {
                item.Click += OnMenuClick;
            }
        }

        bool canAdd = _vm.CanAddTab;
        bool canClose = _vm.CanCloseTab;
        foreach (MenuFlyoutItemBase item in _menu.Items)
        {
            switch (item.Tag as string)
            {
                case "new" or "duplicate":
                    item.IsEnabled = canAdd; // disabled, not hidden, at the limit; HelpText only (lead: no tooltip, as in Windows)
                    AutomationProperties.SetHelpText(item, canAdd ? string.Empty : Localizer.Current["pane.tab.limitReached"]);
                    break;
                default:
                    item.Visibility = canClose ? Visibility.Visible : Visibility.Collapsed; // hidden with one tab left
                    break;
            }
        }
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _menu.Target is not PaneTabItem target)
        {
            return;
        }

        switch (((MenuFlyoutItem)sender).Tag as string)
        {
            case "new":
                _ = _vm.NewTabAsync();
                break;
            case "duplicate":
                _ = _vm.DuplicateTabAsync(target.Tab);
                break;
            case "close":
                _vm.CloseTab(target.Tab);
                RefocusAfterMenu();
                break;
            case "closeOthers":
                _vm.CloseOtherTabs(target.Tab);
                RefocusAfterMenu();
                break;
        }
    }

    // After Close tab / Close other tabs from a menu opened in the row, the focus stays in the row on the newly active tab
    // (ui-spec §2a). Low priority: after the flyout has handed the focus back to its (possibly closed) target. Called on the
    // click and on Closed, whichever WinUI raises last.
    private void RefocusAfterMenu()
    {
        if (_menuFromRow)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // WinUI hands the focus of a closed target to another button of the row (measured), not to a tab.
                bool onTab = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is PaneTabItem focused && _items.Contains(focused);
                if (!_menu.IsOpen && ActiveItem is { } item && !onTab)
                {
                    FocusTab(item);
                }
            });
        }
    }

    private bool HasFocusWithin()
    {
        for (var d = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d == this)
            {
                return true;
            }
        }

        return false;
    }

    private static void RaiseSelected(PaneTabItem? selected, PaneTabItem? previous)
    {
        if (selected is not null && selected != previous && FrameworkElementAutomationPeer.FromElement(selected) is { } peer)
        {
            peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, false, true);
            peer.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
        }
    }

    // One left-button press on a tab; becomes a drag past the threshold.
    private sealed class Press(PaneTabItem item, double startX, int from, double startOffset, uint pointerId)
    {
        public PaneTabItem Item { get; } = item;

        public double StartX { get; } = startX;

        public int From { get; } = from;

        public double StartOffset { get; } = startOffset;

        public uint PointerId { get; } = pointerId;

        public double LastX { get; set; } = startX;

        public int Target { get; set; } = from;

        public bool Dragging { get; set; }
    }
}
