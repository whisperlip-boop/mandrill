using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Mandrill.App.Icons;
using Mandrill.App.Platform;
using Mandrill.Core.Localization;
using Mandrill.Core.Shell;
using Mandrill.Core.State;
using Mandrill.Presentation.Common;
using Mandrill.Presentation.Panes;
using Mandrill.Presentation.Tree;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Mandrill.App.Views;

/// <summary>
/// One pane (ui-spec §2, AC-1.5): toolbar with overflow below ~360 px, path box, tree column (content in WP-1.9) and the
/// file list. Pane shortcuts are scoped to the pane and stay silent while a text field has focus (AC-1.19).
/// </summary>
public sealed partial class PaneView : UserControl
{
    /// <summary>Below this pane width New folder, View mode and Refresh move into the » menu (ui-spec §2).</summary>
    public const double OverflowWidth = 360;

    private const double TreeMinWidth = 120;

    private readonly List<RadioMenuFlyoutItem> _viewItems = [];
    private Action? _fillViewFlyout;
    private bool _listDeferred;
    private Action? _attachList;
    private bool _focusHeld; // DeferList → CompleteDeferredFocus: a just-revealed list is not loaded yet either
    private bool _focusOnReveal;
    private object? _focusedWhenAsked;
    private PaneViewModel? _vm;
    private IconProvider? _icons;
    private static bool s_accessPromptOpen; // UI thread only
    private ILogger _logger = NullLogger.Instance;
    private FolderTreeView? _tree;
    private IIconService? _iconService;
    private IconBitmapCache? _bitmaps;
    private IUiDispatcher? _dispatcher;
    private bool _isActive = true;
    private bool _filterAlways;

    /// <summary>Creates the view; call <see cref="Attach"/> before use.</summary>
    public PaneView()
    {
        long cost = PerfLog.BeginCost();
        InitializeComponent();
        AppTheme.Track(Toolbar.Resources); // SubtleButtons.xaml scope
        AppTheme.Track(ViewModeButtons.Resources);
        PerfLog.EndCost("PaneView XAML", cost);
        Files.ContextMenuRequested += (_, request) => ContextMenuRequested?.Invoke(this, request);
        DriveRows.ContextMenuRequested += (_, request) => ContextMenuRequested?.Invoke(this, request);
        Files.DropRequested += (_, request) => DropRequested?.Invoke(this, request);
        SizeChanged += (_, e) => ApplyOverflow(e.NewSize.Width);
        Body.SizeChanged += (_, e) => TreeColumn.MaxWidth = Math.Max(TreeMinWidth, e.NewSize.Width * 0.5);
        TreeHost.SizeChanged += OnTreeSized;
        ViewModeButtons.SizeChanged += (_, e) => TabRow.ReservedRight = e.NewSize.Width + ViewModeButtons.Margin.Right + 8; // ≥ 8 px before the view buttons
        TabRow.ListFocusRequested += (_, _) => FocusList();

        // Any click or focus inside the pane activates it (AC-1.4); handled events too (the list handles its clicks).
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPanePointerPressed), handledEventsToo: true);
        GotFocus += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        ActualThemeChanged += (_, _) =>
        {
            ApplyActive();
            Files.RefreshSelectionBrushes();
        };
        ApplyActive(); // the starting active pane never sees an IsActive change: HC frame width from the first frame
    }

    /// <summary>Raised when a click or focus lands inside the pane (the layout makes it the active pane).</summary>
    public event EventHandler? Activated;

    /// <summary>The Search button or Ctrl+F: open (or raise) this pane's search popup (AC-2.13, WP-2.2b).</summary>
    public event EventHandler? SearchRequested;

    /// <summary>Items were dropped on the file list (WP-3.4) or the folder tree (3.4b); the composition runs the copy or move.</summary>
    public event EventHandler<PaneDropRequest>? DropRequested;

    /// <summary>Raises <see cref="DropRequested"/> as a drop on the list does (tests).</summary>
    /// <param name="request">The drop.</param>
    public void RequestDrop(PaneDropRequest request) => DropRequested?.Invoke(this, request);

    /// <summary>A context menu was requested in the file list (WP-3.1); the selection is already adjusted.</summary>
    public event EventHandler<PaneContextMenuRequest>? ContextMenuRequested;

    /// <summary>Ctrl+C / Ctrl+X with items selected, or Ctrl+V, from the focused file list (ui-spec §5, AC-3.13–3.15, 3.23–3.24).</summary>
    public event EventHandler<PaneClipboardAction>? ClipboardRequested;

    /// <summary>Del (false) or Shift+Del (true) with items selected or a tree folder focused (ui-spec §5, AC-3.8; silent with nothing selected, AC-3.24).</summary>
    public event EventHandler<bool>? DeleteRequested;

    /// <summary>
    /// Alt+Enter from the focused file list (WP-3.6, ui-spec §5, Explorer): Properties of the selection, or of the pane's
    /// folder when nothing is selected.
    /// </summary>
    public event EventHandler? PropertiesRequested;

    /// <summary>The folder tree in the tree column (null before <see cref="AttachTree"/>).</summary>
    public FolderTreeView? FolderTree => _tree;

    /// <summary>
    /// Whether this is the active pane (AC-1.4, ui-spec §2): accent frame, full-strength toolbar and accent selection;
    /// inactive panes get a neutral frame, a dimmed toolbar and a grey selection.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                ApplyActive();
            }
        }
    }

    /// <summary>The pane shown (null before <see cref="Attach"/>).</summary>
    public PaneViewModel? ViewModel => _vm;

    /// <summary>True when the pane is in overflow mode (tests).</summary>
    public bool IsOverflowing => OverflowButton.Visibility == Visibility.Visible;

    /// <summary>The file list (tests, focus).</summary>
    public FileListView FileList => Files;

    /// <summary>The tab row (tests).</summary>
    public PaneTabStrip TabStrip => TabRow;

    /// <summary>The quick filter's row, box and toggle button (tests).</summary>
    public (FrameworkElement Row, TextBox Box, ToggleButton Toggle) Filter => (FilterRow, FilterBox, FilterToggle);

    /// <summary>
    /// Settings → Filter bar (ui-spec §2b): true keeps the filter row in the pane (Esc and its button only clear the text);
    /// false shows it on demand (Esc and its button clear and hide it).
    /// </summary>
    public bool FilterBarAlways
    {
        get => _filterAlways;
        set
        {
            _filterAlways = value;
            ApplyFilter();
        }
    }

    /// <summary>
    /// The filter toggle, View → Filter or Ctrl+Shift+F: shows the filter row and puts the caret in its box; nothing at
    /// This PC.
    /// </summary>
    public void OpenFilter()
    {
        if (_vm is null || _vm.IsThisPc)
        {
            return;
        }

        _vm.IsFilterOpen = true;
        ApplyFilter();
        FilterBox.Select(FilterBox.Text.Length, 0);
        if (!FilterBox.Focus(FocusState.Programmatic))
        {
            DispatcherQueue.TryEnqueue(() => FilterBox.Focus(FocusState.Programmatic)); // the row was collapsed until now
        }
    }

    /// <summary>
    /// Ctrl+F or the Search button: commits an open inline rename first (as Explorer does when another window takes over;
    /// Ctrl+F does not move the focus out of the editor), then raises <see cref="SearchRequested"/>.
    /// </summary>
    public void RequestSearch()
    {
        if (Files.IsRenaming)
        {
            _ = Files.CommitRenameOnLeaveAsync();
        }

        // The pointer is still over the button, so its tooltip would stay on top of the popup that opens over it.
        if (ToolTipService.GetToolTip(SearchButton) is ToolTip tip)
        {
            tip.IsOpen = false;
        }

        SearchRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The toolbar's Search button (tests).</summary>
    public Button SearchToolButton => SearchButton;

    /// <summary>The toolbar's New folder and View buttons (tests).</summary>
    public (Button NewFolder, Control View) ItemToolButtons => (NewFolderButton, ViewButton);

    /// <summary>The path box (tests).</summary>
    public PathBox PathBox => PathInput;

    /// <summary>The View dropdown's menu items, in Explorer's order (tests).</summary>
    public IReadOnlyList<RadioMenuFlyoutItem> ViewMenuItems
    {
        get
        {
            _fillViewFlyout?.Invoke();
            return ((MenuFlyout)ViewButton.Flyout).Items.OfType<RadioMenuFlyoutItem>().ToArray();
        }
    }

    /// <summary>The tab row's Details and Large icons buttons (tests).</summary>
    public (ToggleButton Details, ToggleButton LargeIcons) ViewModeToggles => (DetailsViewButton, LargeIconsViewButton);

    /// <summary>The toolbar's "»" overflow button (tests).</summary>
    public DropDownButton OverflowMenuButton => OverflowButton;

    /// <summary>The toolbar's tree show/hide button (tests).</summary>
    public Button TreeToggleButton => TreeButton;

    // View menu entries (owner F7, Explorer's View menu): icon sizes are the Icons mode at a fixed size. The icons are
    // Explorer's own View-menu icons, supplied by the owner as SVGs (F15', WP-1.16).
    private static ViewChoice[] ViewChoices { get; } =
    [
        new(ViewMode.Icons, ListGeometry.ExtraLargeIcons, "pane.view.extraLargeIcons", IconId.ViewExtraLargeIcons),
        new(ViewMode.Icons, ListGeometry.LargeIcons, "pane.view.largeIcons", IconId.ViewLargeIcons),
        new(ViewMode.Icons, ListGeometry.MediumIcons, "pane.view.mediumIcons", IconId.ViewMediumIcons),
        new(ViewMode.Icons, ListGeometry.SmallIcons, "pane.view.smallIcons", IconId.ViewSmallIcons),
        new(ViewMode.List, null, "pane.view.list", IconId.ViewList),
        new(ViewMode.Details, null, "pane.view.details", IconId.ViewDetails),
    ];

    /// <summary>Binds the view to a pane.</summary>
    /// <param name="vm">Pane view model.</param>
    /// <param name="iconService">Shell icons and type names.</param>
    /// <param name="bitmaps">UI-thread icon bitmaps.</param>
    /// <param name="dispatcher">UI dispatcher.</param>
    /// <param name="icons">Toolbar icon provider.</param>
    /// <param name="logger">Logger.</param>
    public void Attach(PaneViewModel vm, IIconService iconService, IconBitmapCache bitmaps, IUiDispatcher dispatcher, IconProvider icons, ILogger logger)
    {
        if (_vm is not null)
        {
            throw new InvalidOperationException("The pane view is already attached to a pane");
        }

        long cost = PerfLog.BeginCost();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _iconService = iconService ?? throw new ArgumentNullException(nameof(iconService));
        _bitmaps = bitmaps ?? throw new ArgumentNullException(nameof(bitmaps));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? NullLogger.Instance;

        Setup(TreeButton, IconId.TreeShown, "pane.toolbar.tree");
        TreeButton.Click += (_, _) => ToggleTree();
        Setup(BackButton, IconId.Back, "pane.toolbar.back", vm.BackCommand);
        Setup(ForwardButton, IconId.Forward, "pane.toolbar.forward", vm.ForwardCommand);
        Setup(UpButton, IconId.Up, "pane.toolbar.up", vm.UpCommand);
        Setup(NewFolderButton, IconId.NewFolder, "pane.toolbar.newFolder");
        NewFolderButton.Click += (_, _) => CreateFolder();
        Setup(RefreshButton, IconId.Refresh, "pane.toolbar.refresh", vm.RefreshCommand);
        Setup(SearchButton, IconId.Search, "pane.toolbar.search");
        SearchButton.Click += (_, _) => RequestSearch();
        ViewButton.Content = Icon(IconId.ViewMode);
        Describe(ViewButton, "pane.toolbar.view");
        ViewButton.Flyout = LazyFlyout(FillViewFlyout, out _fillViewFlyout);
        OverflowButton.Content = IconProvider.CreateGlyph(IconProvider.MoreGlyph);
        OverflowButton.Flyout = LazyFlyout(FillOverflowFlyout, out _);
        Describe(OverflowButton, "pane.toolbar.more");
        SetupViewToggle(DetailsViewButton, ViewChoices.Single(c => c.Key == "pane.view.details"));
        SetupViewToggle(LargeIconsViewButton, ViewChoices.Single(c => c.Key == "pane.view.largeIcons"));
        SetupFilter(vm);

        TabRow.Attach(vm, icons);
        PathInput.Attach(vm, _logger);
        PathInput.ListFocusRequested += (_, _) => FocusList(); // QA S3-5: a typed This PC focuses the drive list
        void AttachList() => Files.Attach(vm, iconService, bitmaps, dispatcher, _logger);
        if (_listDeferred)
        {
            _attachList = AttachList; // RevealList
        }
        else
        {
            AttachList();
        }
        AddShortcuts();
        PreviewKeyDown += OnPanePreviewKeyDown;
        vm.PropertyChanged += OnViewModelChanged;
        vm.AccessPromptRequested += OnAccessPromptRequested;
        ApplyTree();
        SyncViewChecks();
        PerfLog.EndCost("PaneView.Attach", cost);
    }

    /// <summary>Puts the pane's folder tree into the tree column (WP-1.9).</summary>
    /// <param name="tree">The pane's tree view model.</param>
    /// <exception cref="InvalidOperationException">A tree is already attached, or the pane is not attached yet.</exception>
    public void AttachTree(TreeViewModel tree)
    {
        if (_tree is not null)
        {
            throw new InvalidOperationException("The pane view already has a tree");
        }

        if (_iconService is null || _bitmaps is null || _dispatcher is null)
        {
            throw new InvalidOperationException("Attach the pane before its tree");
        }

        long cost = PerfLog.BeginCost();
        _tree = new FolderTreeView();
        _tree.Attach(tree, _iconService, _bitmaps, _dispatcher, _logger);
        _tree.DropRequested += (_, request) => DropRequested?.Invoke(this, request); // WP-3.4b: the pane runs it like a list drop
        _tree.ContextMenuRequested += (_, request) => ContextMenuRequested?.Invoke(this, request); // WP6: the shell menu of a node
        TreeHost.Child = _tree;
        PerfLog.EndCost("PaneView.AttachTree", cost);
    }

    /// <summary>Binds the This PC drive list (ADR-0015), shown instead of the file list while the pane is there.</summary>
    /// <param name="drives">The pane's drive list.</param>
    /// <exception cref="InvalidOperationException">The pane is not attached yet.</exception>
    public void AttachDriveList(DriveListViewModel drives)
    {
        if (_vm is null || _iconService is null || _bitmaps is null || _dispatcher is null)
        {
            throw new InvalidOperationException("Attach the pane before its drive list");
        }

        DriveRows.Attach(drives, _iconService, _bitmaps, _dispatcher, _logger);
        ApplyThisPc();
    }

    /// <summary>The This PC drive list (tests).</summary>
    public DriveListView DriveList => DriveRows;

    /// <summary>Shows or hides the tree column (toolbar button, Ctrl+B).</summary>
    public void ToggleTree()
    {
        if (_vm is not null)
        {
            _vm.TreeVisible = !_vm.TreeVisible;
        }
    }

    /// <summary>Focuses the file list.</summary>
    public void FocusList()
    {
        if (_focusHeld)
        {
            // WP-1.17: remembered for CompleteDeferredFocus, with what had the focus when it was asked for.
            _focusOnReveal = true;
            _focusedWhenAsked = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
            return;
        }

        if (_vm is { IsThisPc: true })
        {
            DriveRows.FocusList();
            return;
        }

        Files.FocusList();
    }

    /// <summary>
    /// After <see cref="RevealList"/> has been laid out: gives the list the focus that <see cref="FocusList"/> asked for since
    /// <see cref="DeferList"/>, unless the focus has moved since (the user clicked or typed somewhere). The request is used
    /// up either way, and later <see cref="FocusList"/> calls focus the list directly.
    /// </summary>
    /// <param name="focus">False drops the request (the pane is no longer the active one: focusing it would re-activate it).</param>
    public void CompleteDeferredFocus(bool focus = true)
    {
        _focusHeld = false;
        if (!_focusOnReveal)
        {
            return;
        }

        _focusOnReveal = false;
        object? now = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        if (focus && ReferenceEquals(now, _focusedWhenAsked))
        {
            Files.FocusList();
        }

        _focusedWhenAsked = null;
    }

    /// <summary>True while the list is held back by <see cref="DeferList"/>.</summary>
    public bool IsListDeferred => _listDeferred;

    /// <summary>
    /// Staged startup (WP-1.17), called before <see cref="Attach"/>: the file list is attached to the pane (its list control
    /// created and bound) only by <see cref="RevealList"/>. Until then it is an empty area in its column, so the window's
    /// first frame skips the list's first layout and nothing moves when it appears. The list control is created inside the
    /// shown pane, so it loads with its template applied (a list collapsed before its first layout would not).
    /// </summary>
    /// <exception cref="InvalidOperationException">The view is already attached.</exception>
    public void DeferList()
    {
        if (_vm is not null)
        {
            throw new InvalidOperationException("Defer the list before the pane view is attached");
        }

        _listDeferred = true;
        _focusHeld = true;
    }

    /// <summary>Attaches the list held back by <see cref="DeferList"/>; nothing when it is not held back.</summary>
    public void RevealList()
    {
        if (!_listDeferred)
        {
            return;
        }

        _listDeferred = false;
        Action? attach = _attachList;
        _attachList = null;
        attach?.Invoke();
    }

    /// <summary>
    /// Mouse back/forward buttons (owner F12): XButton1 = Back, XButton2 = Forward, anywhere in the pane (list, tree,
    /// toolbar), as in Explorer.
    /// </summary>
    /// <param name="back">True for XButton1 (Back), false for XButton2 (Forward).</param>
    /// <returns>True when the command ran (there was somewhere to go).</returns>
    public bool NavigateByMouseButton(bool back)
    {
        if (_vm is null)
        {
            return false;
        }

        ICommand command = back ? _vm.BackCommand : _vm.ForwardCommand;
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private void OnPanePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        // The button that caused this press (not the held buttons): a left click while X1 is held stays a left click.
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind is Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed or Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed)
        {
            NavigateByMouseButton(back: kind == Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed);
            e.Handled = true; // an X button does nothing else in the pane
        }
    }

    private static void Run(ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private IconElement Icon(IconId id) => _icons!.CreateElement(id, size: 16);

    private void Setup(Button button, IconId icon, string tooltipKey, ICommand? command = null)
    {
        button.Content = Icon(icon);
        if (command is not null)
        {
            button.Command = command;
        }

        Describe(button, tooltipKey);
    }

    private static void Describe(FrameworkElement element, string key)
    {
        string text = Localizer.Current[key];
        ToolTipService.SetToolTip(element, new ToolTip { Content = text }); // an object, so RequestSearch can close it
        AutomationProperties.SetName(element, text);
    }

    // WP-1.17: the menus are filled when first opened. Built eagerly, the four panes' menus cost ~60 icons and ~50 items
    // before the first frame (40-80 ms of the startup stall).
    private MenuFlyout LazyFlyout(Action<MenuFlyout> fill, out Action ensureFilled)
    {
        var flyout = new MenuFlyout();
        bool filled = false;
        void Ensure()
        {
            if (filled)
            {
                return;
            }

            filled = true;
            fill(flyout);
            SyncViewChecks();
        }

        flyout.Opening += (_, _) => Ensure();
        ensureFilled = Ensure;
        return flyout;
    }

    private void FillViewFlyout(MenuFlyout flyout)
    {
        foreach (var item in ViewItems())
        {
            flyout.Items.Add(item);
        }
    }

    private void FillOverflowFlyout(MenuFlyout flyout)
    {
        var newFolder = new MenuFlyoutItem { Text = Localizer.Current["pane.toolbar.newFolder"], Icon = _icons!.CreateElement(IconId.NewFolder) };
        newFolder.Click += (_, _) => CreateFolder();
        var view = new MenuFlyoutSubItem { Text = Localizer.Current["pane.toolbar.view"], Icon = _icons.CreateElement(IconId.ViewMode) };
        foreach (var item in ViewItems())
        {
            view.Items.Add(item);
        }

        var refresh = new MenuFlyoutItem { Text = Localizer.Current["pane.toolbar.refresh"], Icon = _icons.CreateElement(IconId.Refresh), Command = _vm!.RefreshCommand };

        // ADR-0015: no new folder and no view change at This PC (the pane keeps its view for when it leaves).
        void Gate() => newFolder.IsEnabled = view.IsEnabled = _vm is not { IsThisPc: true };
        Gate();
        flyout.Opening += (_, _) => Gate();
        flyout.Items.Add(newFolder);
        flyout.Items.Add(view);
        flyout.Items.Add(refresh);
    }

    // One radio set per menu (view dropdown and overflow); both reflect the pane's view mode. Explorer's order and icons
    // (owner F7): the four icon sizes are the Icons mode at fixed sizes; Ctrl+wheel sizes in between check none of them.
    private IEnumerable<RadioMenuFlyoutItem> ViewItems()
    {
        string group = "view-" + Guid.NewGuid().ToString("N");
        foreach (var choice in ViewChoices)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = Localizer.Current[choice.Key],
                GroupName = group,
                Tag = choice,
                Icon = _icons!.CreateElement(choice.Icon),
            };
            item.Click += (_, _) => ApplyViewChoice(choice);
            _viewItems.Add(item);
            yield return item;
        }
    }

    private void ApplyViewChoice(ViewChoice choice)
    {
        if (_vm is null)
        {
            return;
        }

        if (choice.IconSize is { } size)
        {
            _vm.IconSize = size;
        }

        _vm.SetView(choice.Mode); // ViewMode and IconSize changes re-sync the checks (OnViewModelChanged)
    }

    // Explorer's status-bar pair in the tab row: the View menu's path. The click already flipped the check, so it re-syncs
    // even when the mode did not change (clicking the checked one keeps it checked).
    private void SetupViewToggle(ToggleButton button, ViewChoice choice)
    {
        button.Tag = choice;
        button.Content = Icon(choice.Icon);
        Describe(button, choice.Key);
        button.Click += (_, _) =>
        {
            ApplyViewChoice(choice);
            SyncViewChecks();
        };
    }

    // ---- quick filter (ui-spec §2b) -------------------------------------------------------------------------------------

    private void SetupFilter(PaneViewModel vm)
    {
        FilterToggle.Content = IconProvider.CreateGlyph(IconProvider.FilterGlyph, size: 14);
        Describe(FilterToggle, "pane.toolbar.filter");
        FilterToggle.Click += (_, _) =>
        {
            if (!_filterAlways && vm.IsFilterOpen)
            {
                DismissFilter();
            }
            else
            {
                OpenFilter();
            }

            ApplyFilter(); // a ToggleButton flips itself; the row decides
        };
        FilterCloseButton.Content = _icons!.CreateElement(IconId.Close, size: 12); // fills the 24 px button's Padding-6 content box, centred
        FilterCloseButton.Click += (_, _) => DismissFilter();
        FilterBox.Loaded += (_, _) =>
        {
            // FilterCloseButton is the row's only X: cap the box's own clear button as BeginRename does for the rename box.
            FilterBox.ApplyTemplate();
            if (FileListView.Descendant<Button>(FilterBox, b => b.Name == "DeleteButton") is { } clear)
            {
                clear.MinWidth = 0;
                clear.MaxWidth = 0;
            }
        };
        FilterBox.TextChanged += (_, _) =>
        {
            if (vm.FilterText != FilterBox.Text)
            {
                vm.FilterText = FilterBox.Text; // live; the pane re-filters what it has loaded (no I/O)
            }
        };
        FilterBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                DismissFilter();
            }
        };
        vm.Items.CurrentChanged += UpdateFilterCount; // every applied snapshot, also one that changes only the total (QA M2)
        ApplyFilter();
    }

    // Esc or the row's button: "Always show" only clears the text; "Show when needed" also hides the row.
    private void DismissFilter()
    {
        if (_vm is null)
        {
            return;
        }

        if (_filterAlways)
        {
            _vm.FilterText = string.Empty;
            return;
        }

        bool refocus = IsFocusIn(FilterRow);
        _vm.CloseFilter();
        if (refocus)
        {
            FocusList();
        }
    }

    private void ApplyFilter()
    {
        if (_vm is null)
        {
            return;
        }

        bool available = !_vm.IsThisPc;
        bool shown = _filterAlways || (available && _vm.IsFilterOpen);
        // Collapsing or disabling (This PC under "Always show") the focused box would move the focus to the next pane (QA F1).
        // Park it on this pane's tab first: a just-shown drive list refuses the focus until laid out (it retries then).
        if ((!shown || !available) && IsFocusIn(FilterRow))
        {
            TabRow.HoldFocus();
            FocusList();
        }

        FilterRow.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        FilterBox.IsEnabled = FilterCloseButton.IsEnabled = available; // "Always show" keeps the row at This PC, disabled like the View dropdown
        FilterToggle.IsEnabled = available;
        FilterToggle.IsChecked = shown && available;
        Describe(FilterCloseButton, _filterAlways ? "pane.filter.clear" : "pane.filter.close");
        UpdateFilterCount();
    }

    private void UpdateFilterCount()
    {
        FilterCount.Text = FilterRow.Visibility == Visibility.Visible && _vm?.Items.Current is { } shown
            ? Localizer.Current.Format(
                "pane.filter.count",
                ("shown", shown.Visible.Count.ToString("N0", CultureInfo.CurrentCulture)),
                ("total", shown.Total.ToString("N0", CultureInfo.CurrentCulture)))
            : string.Empty;
    }

    private void SyncViewChecks()
    {
        foreach (var item in _viewItems)
        {
            item.IsChecked = IsCurrent((ViewChoice)item.Tag);
        }

        DetailsViewButton.IsChecked = DetailsViewButton.Tag is ViewChoice details && IsCurrent(details);
        LargeIconsViewButton.IsChecked = LargeIconsViewButton.Tag is ViewChoice large && IsCurrent(large);
    }

    private bool IsCurrent(ViewChoice choice) =>
        _vm is not null && choice.Mode == _vm.ViewMode && (choice.IconSize is not { } size || size == _vm.IconSize);

    // Inactive: the accent frame hides and the toolbar dims to 80 % (primary glyphs stay well above 4.5:1 in light and
    // dark); the list shows a grey selection. High contrast: no dimming at all (ui-spec §2: inactive text stays ≥ 4.5:1).
    private void ApplyActive()
    {
        ActiveFrame.Visibility = _isActive ? Visibility.Visible : Visibility.Collapsed;
        ActiveFrame.BorderThickness = new Thickness(AppTheme.IsHighContrast() ? 2 : 1); // owner #12: 1 px; HC keeps 2 px
        Toolbar.Opacity = ViewModeButtons.Opacity = _isActive || AppTheme.IsHighContrast() ? 1.0 : 0.8;
        Files.IsActive = _isActive;
        TabRow.IsPaneActive = _isActive; // the tabs use the designer's scrim, not the toolbar's 80 % opacity
    }

    // ADR-0015: the drive list replaces the file list; New folder, the view menu and the view buttons are off (FileListView keeps its state).
    private void ApplyThisPc()
    {
        bool thisPc = _vm is { IsThisPc: true };

        // Collapsing the focused list would move the focus to the next Tab stop, the next pane's tabs, which activates that
        // pane (QA F1). Park it on this pane's tab, then give it to the list now shown once that has been laid out.
        bool refocus = IsFocusIn(thisPc ? Files : DriveRows) && TabRow.HoldFocus();
        Files.Visibility = thisPc ? Visibility.Collapsed : Visibility.Visible;
        DriveRows.Visibility = thisPc ? Visibility.Visible : Visibility.Collapsed;
        NewFolderButton.IsEnabled = !thisPc;
        ViewButton.IsEnabled = !thisPc;
        DetailsViewButton.IsEnabled = !thisPc;
        LargeIconsViewButton.IsEnabled = !thisPc;
        if (refocus)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusList());
        }
    }

    private bool IsFocusIn(UIElement root)
    {
        for (var d = XamlRoot is { } r ? FocusManager.GetFocusedElement(r) as DependencyObject : null; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, root))
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyOverflow(double width)
    {
        bool narrow = width > 0 && width < OverflowWidth;
        var full = narrow ? Visibility.Collapsed : Visibility.Visible;
        NewFolderButton.Visibility = full;
        ViewButton.Visibility = full;
        RefreshButton.Visibility = full;
        OverflowButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyTree()
    {
        if (_vm is null)
        {
            return;
        }

        if (_vm.TreeVisible)
        {
            TreeColumn.MinWidth = TreeMinWidth;
            TreeColumn.Width = new GridLength(Math.Max(TreeMinWidth, _vm.TreeWidth));
            TreeSplitter.Visibility = Visibility.Visible;
            TreeHost.Visibility = Visibility.Visible;
            TreeButton.Content = Icon(IconId.TreeShown);
        }
        else
        {
            TreeColumn.MinWidth = 0;
            TreeColumn.Width = new GridLength(0);
            TreeSplitter.Visibility = Visibility.Collapsed;
            TreeHost.Visibility = Visibility.Collapsed;
            TreeButton.Content = Icon(IconId.TreeHidden);
        }
    }

    // The splitter changes the column width; remember it per pane (session, WP-1.11).
    private void OnTreeSized(object sender, SizeChangedEventArgs e)
    {
        if (_vm is { TreeVisible: true } && e.NewSize.Width >= TreeMinWidth && Math.Abs(e.NewSize.Width - _vm.TreeWidth) > 0.5)
        {
            _vm.TreeWidth = e.NewSize.Width;
        }
    }

    // Owner F14: Explorer's permission prompt; Continue grants access (UAC) and re-opens the folder. One prompt at a time
    // (one ContentDialog per window): a second pane asking meanwhile (e.g. two panes restoring the same folder) keeps the
    // plain access-denied state. async void: nothing may escape.
    private async void OnAccessPromptRequested(string folder)
    {
        if (_vm is not { } vm || _iconService is null || _bitmaps is null || _dispatcher is null || XamlRoot is not { } root || s_accessPromptOpen)
        {
            return;
        }

        s_accessPromptOpen = true;
        try
        {
            if (await FolderAccessDialog.ShowAsync(root, _iconService, _bitmaps, _dispatcher, _logger, folder))
            {
                await vm.GrantAccessAndRetryAsync(folder, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The folder access prompt could not be shown for {Folder}", folder);
        }
        finally
        {
            s_accessPromptOpen = false;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaneViewModel.TreeVisible):
                ApplyTree();
                break;
            case nameof(PaneViewModel.IsThisPc):
                ApplyThisPc();
                ApplyFilter();
                break;
            case nameof(PaneViewModel.IsFilterOpen):
                ApplyFilter();
                break;
            case nameof(PaneViewModel.FilterText) when FilterBox.Text != _vm!.FilterText:
                FilterBox.Text = _vm.FilterText; // cleared by a navigation, or another tab's
                break;
            case nameof(PaneViewModel.ViewMode):
            case nameof(PaneViewModel.IconSize):
                SyncViewChecks();
                break;
        }
    }

    /// <summary>
    /// Ctrl+C / Ctrl+X / Ctrl+V / Ctrl+Shift+C: only from the focused file list (like Del; WP-3.3 keeps the tree out), never
    /// while renaming; all but Paste need a selection (AC-3.24).
    /// </summary>
    /// <param name="action">The clipboard action.</param>
    public void RequestClipboard(PaneClipboardAction action)
    {
        if (_vm is null || Files.IsRenaming || !Files.HasFocusWithin)
        {
            return;
        }

        if (action != PaneClipboardAction.Paste && _vm.Items.Selection.Count == 0)
        {
            return;
        }

        ClipboardRequested?.Invoke(this, action);
    }

    /// <summary>New folder (toolbar, Ctrl+Shift+N, the fallback context menu).</summary>
    public void RequestNewFolder() => CreateFolder();

    /// <summary>New text document (the fallback context menu; the native menu's "New" submenu is the primary path).</summary>
    public void RequestNewFile()
    {
        if (_vm is { } vm)
        {
            _ = CreateFileSafeAsync(vm);
        }
    }

    /// <summary>Alt+Enter: raises <see cref="PropertiesRequested"/> from the focused file list, never while renaming.</summary>
    public void RequestProperties()
    {
        if (_vm is { CurrentPath: not null } && !Files.IsRenaming && Files.HasFocusWithin)
        {
            PropertiesRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Del / Shift+Del: raises <see cref="DeleteRequested"/> when items are selected and no rename is open, or when the
    /// focused tree has a deletable folder selected (<see cref="FolderTreeView.DeleteTarget"/>).
    /// </summary>
    /// <param name="permanent">Shift+Del: delete without the Recycle Bin (after the shell's confirmation).</param>
    public void RequestDelete(bool permanent)
    {
        // Reviewer B1: only the focused file list's selection or the focused tree's folder; with focus in a toolbar, Del is not ours.
        if ((_vm is { Items.Selection.Count: > 0 } && !Files.IsRenaming && Files.HasFocusWithin) || _tree?.DeleteTarget is not null)
        {
            DeleteRequested?.Invoke(this, permanent);
        }
    }

    private void CreateFolder()
    {
        if (_vm is not null)
        {
            _ = CreateFolderSafeAsync(_vm);
        }
    }

    private async Task CreateFileSafeAsync(PaneViewModel vm)
    {
        try
        {
            await vm.CreateFileAsync(); // failures are reported by the view model (status + log)
        }
        catch (ObjectDisposedException)
        {
            // Pane closed meanwhile.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating a file failed");
        }
    }

    private async Task CreateFolderSafeAsync(PaneViewModel vm)
    {
        try
        {
            await vm.CreateFolderAsync(); // failures are reported by the view model (status + log)
        }
        catch (ObjectDisposedException)
        {
            // Pane closed meanwhile.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating a folder failed");
        }
    }

    // ---- shortcuts (AC-1.19, ui-spec §5) -------------------------------------------------------------------------

    private void AddShortcuts()
    {
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, () => Run(_vm!.BackCommand), inTextFields: false);
        Add(VirtualKey.Right, VirtualKeyModifiers.Menu, () => Run(_vm!.ForwardCommand), inTextFields: false);
        Add(VirtualKey.Up, VirtualKeyModifiers.Menu, () => Run(_vm!.UpCommand), inTextFields: false);
        Add(VirtualKey.F5, VirtualKeyModifiers.None, () => Run(_vm!.RefreshCommand), inTextFields: false);
        Add(VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, CreateFolder, inTextFields: false);
        Add(VirtualKey.L, VirtualKeyModifiers.Control, PathInput.FocusAndSelect, inTextFields: true);
        Add(VirtualKey.D, VirtualKeyModifiers.Menu, PathInput.FocusAndSelect, inTextFields: true);
        Add(VirtualKey.F, VirtualKeyModifiers.Control, RequestSearch, inTextFields: true);
        Add(VirtualKey.F, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, OpenFilter, inTextFields: true);
        Add(VirtualKey.Delete, VirtualKeyModifiers.None, () => RequestDelete(permanent: false), inTextFields: false);
        AddListKey(VirtualKey.C, VirtualKeyModifiers.Control, () => RequestClipboard(PaneClipboardAction.Copy));
        AddListKey(VirtualKey.X, VirtualKeyModifiers.Control, () => RequestClipboard(PaneClipboardAction.Cut));
        AddListKey(VirtualKey.V, VirtualKeyModifiers.Control, () => RequestClipboard(PaneClipboardAction.Paste));
        AddListKey(VirtualKey.C, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => RequestClipboard(PaneClipboardAction.CopyAsPath));
        AddListKey(VirtualKey.Enter, VirtualKeyModifiers.Menu, RequestProperties);
        Add(VirtualKey.Delete, VirtualKeyModifiers.Shift, () => RequestDelete(permanent: true), inTextFields: false);

        // Tabs (ui-spec §2a): opening and closing stay silent while typing (a rename or the path box keeps its text).
        // Ctrl+Tab / Ctrl+Shift+Tab: OnPreviewKeyDown.
        Add(VirtualKey.T, VirtualKeyModifiers.Control, () => _ = _vm!.NewTabAsync(), inTextFields: false);
        Add(VirtualKey.W, VirtualKeyModifiers.Control, () => _vm!.CloseTab(_vm.ActiveTab), inTextFields: false);
        Add(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = _vm!.ReopenClosedTabAsync(), inTextFields: false);
    }

    // Ctrl+Tab / Ctrl+Shift+Tab (ui-spec §2a): tunnelling, because a focused text box would insert a TAB before any
    // accelerator ran (QA M1: the rename box, path box and filter box all did). The key is always taken; it switches tabs
    // except during a rename, whose text a folder change would drop (committing first is unsafe while the extension
    // question is open).
    private void OnPanePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || _vm is null || !FileListView.IsControlDown())
        {
            return;
        }

        e.Handled = true;
        if (!Files.IsRenaming)
        {
            _ = _vm.ActivateNextTabAsync(forward: !FileListView.IsShiftDown());
        }
    }

    // QA m5: Ctrl+C/X/V (and Ctrl+Shift+C, Alt+Enter) belong to the focused list; with focus elsewhere in the pane the key
    // stays unhandled for that control.
    private void AddListKey(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers, ScopeOwner = this };
        accelerator.Invoked += (_, args) =>
        {
            if (Files.HasFocusWithin && !Files.IsRenaming)
            {
                args.Handled = true;
                action();
            }
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action, bool inTextFields)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers, ScopeOwner = this };
        accelerator.Invoked += (_, args) =>
        {
            if (!inTextFields && IsTextFieldFocused())
            {
                return; // let the text field have the key (AC-1.19: shortcuts are silent while typing)
            }

            args.Handled = true;
            action();
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private bool IsTextFieldFocused()
        => XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or RichEditBox;

    private sealed record ViewChoice(ViewMode Mode, int? IconSize, string Key, IconId Icon);
}
