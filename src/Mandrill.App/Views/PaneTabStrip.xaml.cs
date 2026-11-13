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
using Windows.System;
using Windows.UI;

namespace Mandrill.App.Views;

/// <summary>
/// A pane's tab row (ui-spec §2a, architecture §17.5, ADR-0017): one <see cref="PaneTabItem"/> per <see cref="PaneViewModel.Tabs"/>
/// entry, overlapping by 16 px (left in front, active on top), "+" after the last tab, the 2 px strip, a shared context
/// menu, roving keyboard focus and UIA Tab/TabItem peers. Tab state lives in the view model; this only mirrors it.
/// </summary>
public sealed partial class PaneTabStrip : UserControl
{
    /// <summary>Tab width (bounding box) before T4's shrink and overflow.</summary>
    public const double TabWidth = 160;

    /// <summary>Overlap of neighbouring tabs (2 × slant).</summary>
    public const double Overlap = 2 * PaneTabItem.Slant;

    private readonly Dictionary<PaneTab, PaneTabItem> _byTab = [];
    private readonly List<PaneTabItem> _items = [];
    private readonly MenuFlyout _menu = new();
    private readonly Button _plus = new() { Width = 24, Height = 24, Padding = new Thickness(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
    private PaneViewModel? _vm;
    private IconProvider? _icons;
    private PaneTabItem? _roving;
    private PaneTabItem? _active;
    private bool _menuFilled;
    private bool _isPaneActive = true;
    private double _reservedRight;

    /// <summary>Creates the strip; call <see cref="Attach"/> before use.</summary>
    public PaneTabStrip()
    {
        InitializeComponent();
        AppTheme.Track(Resources); // SubtleButtons.xaml scope ("+")
        TabHost.Children.Add(_plus);
        _plus.Click += OnPlusClick;
        _menu.Opening += OnMenuOpening;
        SizeChanged += (_, _) => Arrange();
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
            Arrange();
        }
    }

    /// <summary>The tab items in strip order (tests).</summary>
    public IReadOnlyList<PaneTabItem> Items => _items;

    /// <summary>The "+" button (tests).</summary>
    public Button NewTabButton => _plus;

    /// <summary>The shared tab context menu (tests).</summary>
    public MenuFlyout TabMenu => _menu;

    /// <summary>The strip's current colour (tests).</summary>
    public Brush? StripBrush => Strip.Fill;

    /// <summary>The row background the inactive-pane scrim blends with: the theme's page background (tests use it too).</summary>
    public Color RowBackground => (RowBackgroundSwatch.Background as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.White;

    internal PaneTabItem? ActiveItem => _vm is null ? null : _byTab.GetValueOrDefault(_vm.ActiveTab);

    internal bool CanClose => _vm?.CanCloseTab == true;

    internal int Count => _items.Count;

    /// <summary>Binds the strip to its pane.</summary>
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
        vm.Tabs.CollectionChanged += OnTabsChanged;
        vm.PropertyChanged += OnPaneChanged;
        Sync();
    }

    internal int IndexOf(PaneTabItem item) => _items.IndexOf(item);

    /// <summary>Activates a tab (UIA Select, Enter/Space): keeps the focus where it is.</summary>
    internal void Activate(PaneTabItem item) => _ = _vm?.ActivateTabAsync(item.Tab);

    internal void Close(PaneTabItem item) => _vm?.CloseTab(item.Tab);

    internal void OnTabPressed(PaneTabItem item)
    {
        Activate(item);
        ListFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void OnTabFocused(PaneTabItem item)
    {
        SetRoving(item);
        item.Refresh();
    }

    /// <summary>The colours of a tab in its current state (ui-spec §2a colour and high-contrast tables).</summary>
    internal TabLook LookOf(PaneTabItem item)
    {
        bool active = item.Tab.IsActive;
        Brush focusOuter = FocusSwatch.Background;
        Brush focusInner = FocusSwatch.BorderBrush;
        if (AppTheme.IsHighContrast())
        {
            bool highlighted = active && _isPaneActive;
            Brush fill = highlighted ? HcActiveSwatch.Background : HcSwatch.Background;
            Brush text = highlighted ? HcActiveSwatch.BorderBrush : HcSwatch.BorderBrush;
            return item.VisualState != TabVisualState.Rest
                ? new TabLook(fill, text, HcHoverSwatch.Background, 2, focusOuter, focusInner) // HC pressed = HC hover
                : new TabLook(fill, text, HcSwatch.BorderBrush, active && !_isPaneActive ? 2 : 1, focusOuter, focusInner);
        }

        Color fillColor = TabPalette.Fill(item.Tab.Color, item.VisualState, _isPaneActive, RowBackground);
        return new TabLook(
            new SolidColorBrush(fillColor),
            new SolidColorBrush(TabPalette.Text(item.Tab.Color, fillColor, _isPaneActive)),
            EdgeSwatch.Background,
            1,
            focusOuter,
            focusInner);
    }

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer() => new PaneTabStripAutomationPeer(this);

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaneViewModel.ActiveTab):
                SetRoving(ActiveItem);
                Arrange();
                RefreshAll();
                RaiseSelected(ActiveItem, _active);
                _active = ActiveItem;
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

        bool focusInRow = HasFocusWithin();
        var keep = new HashSet<PaneTab>(_vm.Tabs);
        foreach (PaneTabItem gone in _items.Where(i => !keep.Contains(i.Tab)).ToList())
        {
            gone.Detach();
            _byTab.Remove(gone.Tab);
            TabHost.Children.Remove(gone);
        }

        _items.Clear();
        foreach (PaneTab tab in _vm.Tabs)
        {
            if (!_byTab.TryGetValue(tab, out PaneTabItem? item))
            {
                item = new PaneTabItem(tab, this, _icons) { Width = TabWidth, ContextFlyout = _menu };
                item.KeyDown += OnTabKeyDown;
                _byTab[tab] = item;
                TabHost.Children.Insert(TabHost.Children.Count - 1, item); // before "+": tabs come first in Tab order
            }

            _items.Add(item);
        }

        SetRoving(_roving is not null && _byTab.ContainsKey(_roving.Tab) ? _roving : ActiveItem);
        _active = ActiveItem;
        ApplyCanAdd();
        Arrange();
        RefreshAll();
        if (focusInRow && !HasFocusWithin())
        {
            // The focused tab was closed: once the close has activated its neighbour, focus that (ui-spec §2a).
            DispatcherQueue.TryEnqueue(() => ActiveItem?.Focus(FocusState.Keyboard));
        }
    }

    // T3: fixed 160 px tabs at pitch 144, clipped before the view buttons (T4 adds shrink and overflow).
    private void Arrange()
    {
        double pitch = TabWidth - Overlap;
        int n = _items.Count;
        for (int i = 0; i < n; i++)
        {
            PaneTabItem item = _items[i];
            Canvas.SetLeft(item, i * pitch);
            Canvas.SetZIndex(item, item.Tab.IsActive ? n + 1 : n - i);
        }

        Canvas.SetLeft(_plus, n == 0 ? 0 : ((n - 1) * pitch) + TabWidth + 4);
        double width = Math.Max(0, ActualWidth - TabHost.Margin.Left - _reservedRight);
        TabHost.Width = width;
        TabHost.Height = Height;
        TabHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, Height) };
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
        string tip = Localizer.Current[canAdd ? "pane.tab.new" : "pane.tab.limitReached"];
        ToolTipService.SetToolTip(_plus, tip);
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

    private void OnTabKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var item = (PaneTabItem)sender;
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
                SetRoving(_items[target]);
                _items[target].Focus(FocusState.Keyboard); // moves focus only; no wrap
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
            ActiveItem?.Focus(FocusState.Keyboard); // the 10th tab disables "+": focus moves to the new tab, not away
        }
        else
        {
            ListFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // Built when first opened (the pane View menu pattern); acts on the right-clicked tab, which it does not activate.
    private void OnMenuOpening(object? sender, object e)
    {
        if (_vm is null || _menu.Target is not PaneTabItem target)
        {
            return;
        }

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
                    item.IsEnabled = canAdd; // disabled, not hidden, at the limit
                    ToolTipService.SetToolTip(item, canAdd ? null : Localizer.Current["pane.tab.limitReached"]);
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
                break;
            case "closeOthers":
                _vm.CloseOtherTabs(target.Tab);
                break;
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
}
