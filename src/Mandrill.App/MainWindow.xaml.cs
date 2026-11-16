using System.ComponentModel;
using System.Globalization;
using Mandrill.App.Icons;
using Mandrill.App.Platform;
using Mandrill.App.Views;
using Mandrill.Core.Diagnostics;
using Mandrill.Core.Localization;
using Mandrill.Core.Platform;
using Mandrill.Presentation.Favorites;
using Mandrill.Presentation.Layout;
using Mandrill.Presentation.Panes;
using Mandrill.Presentation.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Mandrill.App;

/// <summary>Main window: menu bar, favorites bar (pane count and layout picker), pane layout, status bar.</summary>
public sealed partial class MainWindow : Window
{
    private const string LayoutGroup = "layout";

    // VK_OEM_PLUS / VK_OEM_MINUS: the "=" and "-" keys of the main block (Ctrl+Shift+= / Ctrl+Shift+-, AC-1.3a).
    private const VirtualKey OemPlus = (VirtualKey)0xBB;
    private const VirtualKey OemMinus = (VirtualKey)0xBD;
    private static readonly TimeSpan TransientStatus = TimeSpan.FromSeconds(2);
    private readonly IconProvider _icons;
    private readonly List<RadioMenuFlyoutItem> _layoutItems = [];
    private readonly DispatcherQueueTimer _transientTimer;
    private readonly ILogger _logger;
    private LayoutManager? _layout;
    private FavoritesBar? _favoritesBar;
    private FavoritesViewModel? _favorites;
    private PaneViewModel? _statusPane;
    private bool _countPending;

    /// <summary>Creates the window for <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">Window-level view model.</param>
    /// <param name="icons">Icon provider.</param>
    /// <param name="paths">Application paths (resources resolve through <see cref="AppPaths.Resource"/>).</param>
    /// <param name="logger">Logger for the window's queued UI work.</param>
    public MainWindow(MainViewModel viewModel, IconProvider icons, AppPaths paths, ILogger<MainWindow> logger)
    {
        ViewModel = viewModel;
        _icons = icons;
        _logger = logger;
        long cost = PerfLog.BeginCost();
        InitializeComponent();
        AppTheme.Track(TopBar.Resources); // SubtleButtons.xaml scope
        PerfLog.EndCost("MainWindow XAML", cost);
        cost = PerfLog.BeginCost();

        Title = Localizer.Current["app.title"];
        AppWindow.SetIcon(paths.AppIcon);
        SettingsItem.Icon = _icons.CreateElement(IconId.Settings);
        AddPaneItem.Icon = _icons.CreateElement(IconId.AddPane);
        RemovePaneItem.Icon = _icons.CreateElement(IconId.RemovePane);
        SetupTopBar();
        BuildLayoutMenu();
        BuildLayoutTiles();
        AddWindowShortcuts();
        WatchStatusCells();

        _transientTimer = DispatcherQueue.CreateTimer();
        _transientTimer.IsRepeating = false;
        _transientTimer.Tick += (_, _) => ShowPaneStatus();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                foreach (PaneView view in PaneArea.CreatedViews)
                {
                    view.FileList.CancelSlowClick(); // a click armed before switching away must not rename on return
                }
            }
        };
        // ExitHandler never throws (SessionCoordinator logs and closes), so this async void lambda cannot crash the process.
        ViewModel.ExitRequested += async (_, _) => await (ExitHandler?.Invoke() ?? CloseNow());
        ViewModel.StatusRequested += ShowStatus;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentLayout))
            {
                SyncLayoutChecks();
            }
        };
        PerfLog.EndCost("MainWindow setup", cost);
    }

    /// <summary>Window-level view model (x:Bind source).</summary>
    public MainViewModel ViewModel { get; }

    /// <summary>The pane area (tests).</summary>
    public LayoutHost Panes => PaneArea;

    /// <summary>
    /// Which status-bar separators show (WP-3): the one before a cell shows when that cell and some cell before it have text,
    /// so an empty or hidden cell leaves no double or dangling line.
    /// </summary>
    /// <param name="filled">Per cell, left to right: visible and with text.</param>
    /// <returns>Per separator (before cells 1..n-1): whether it shows.</returns>
    public static bool[] StatusSeparators(IReadOnlyList<bool> filled)
    {
        ArgumentNullException.ThrowIfNull(filled);
        var shown = new bool[Math.Max(0, filled.Count - 1)];
        bool before = filled.Count > 0 && filled[0];
        for (int i = 1; i < filled.Count; i++)
        {
            shown[i - 1] = before && filled[i];
            before |= filled[i];
        }

        return shown;
    }

    /// <summary>Shows a short message in the status bar (never a modal; ui-spec §7).</summary>
    /// <param name="text">Localized text.</param>
    public void ShowStatus(string text) => StatusText.Text = text;

    /// <summary>
    /// Shows the index indicator at the right of the status bar (AC-2.9), hidden for an empty text; text only, it never
    /// takes focus. UI thread.
    /// </summary>
    /// <param name="status">Localized text and tooltip.</param>
    public void ShowIndexStatus(IndexStatusText status)
    {
        ArgumentNullException.ThrowIfNull(status);
        IndexText.Text = status.Text;
        IndexText.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; // Standby: none
        ToolTipService.SetToolTip(IndexText, status.Detail);
    }

    /// <summary>File → Exit: set by the app to save the session first (WP-1.11); null closes at once.</summary>
    public Func<Task>? ExitHandler { get; set; }

    private Task CloseNow()
    {
        Close();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Shows the panes of <paramref name="layout"/> and drives the status bar from the active pane ("Pane N – path", item
    /// count or selection, pane messages; ui-spec §1–2).
    /// </summary>
    /// <param name="layout">The window's layout manager (already attached to <see cref="ViewModel"/>).</param>
    /// <param name="createView">Creates the attached view of a pane when the pane is first shown (WP-1.17).</param>
    public void ShowLayout(LayoutManager layout, Func<PaneViewModel, PaneView> createView)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        PaneArea.Attach(layout, createView);
        layout.ActiveChanged += BindActivePane;
        layout.LayoutChanged += BindActivePane;
        BindActivePane();
    }

    /// <summary>
    /// Fills the favorites part of the top bar (WP-1.12) and shows its messages in the status bar; a message with detail
    /// (the missing paths of a restored favorite, AC-1.31) carries it as the status tooltip.
    /// </summary>
    /// <param name="favorites">Favorites view model.</param>
    /// <param name="logger">Logger for the bar.</param>
    public void ShowFavorites(FavoritesViewModel favorites, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(favorites);
        DetachFavorites();
        var bar = new FavoritesBar();
        bar.Attach(favorites, _icons, logger);
        FavoritesArea.Children.Add(bar);
        favorites.StatusRequested += OnFavoriteStatus;
        _favoritesBar = bar;
        _favorites = favorites;
    }

    /// <summary>Removes the favorites bar and stops showing its messages (window closed, or a new view model).</summary>
    public void DetachFavorites()
    {
        if (_favorites is { } old)
        {
            old.StatusRequested -= OnFavoriteStatus;
        }

        if (_favoritesBar is { } bar)
        {
            bar.Detach();
            FavoritesArea.Children.Remove(bar);
        }

        _favorites = null;
        _favoritesBar = null;
    }

    // Shown until the active pane's status or folder changes (UpdatePaneText → ShowPaneStatus clears it).
    private void OnFavoriteStatus(FavoriteStatus status)
    {
        _transientTimer.Stop();
        StatusText.Text = status.Text;
        ToolTipService.SetToolTip(StatusText, status.Detail);
    }

    /// <summary>Focuses the active pane's file list (after Alt+N or startup).</summary>
    public void FocusActivePane() => PaneArea.ActiveView?.FocusList();

    /// <summary>The view of the active pane (null before <see cref="ShowLayout"/> or when no pane is shown).</summary>
    public PaneView? ActivePaneView => PaneArea.ActiveView;

    // The status bar follows the active pane only.
    private void BindActivePane()
    {
        PaneViewModel? active = _layout is { VisibleCount: > 0 } layout ? layout.ActivePane : null;
        if (!ReferenceEquals(active, _statusPane))
        {
            if (_statusPane is { } old)
            {
                old.Items.CollectionChanged -= OnItemsChanged;
                old.Items.Reset -= ScheduleCount;
                old.Items.Selection.Changed -= ScheduleCount;
                old.PropertyChanged -= OnPaneChanged;
            }

            _statusPane = active;
            if (active is not null)
            {
                active.Items.CollectionChanged += OnItemsChanged;
                active.Items.Reset += ScheduleCount;
                active.Items.Selection.Changed += ScheduleCount;
                active.PropertyChanged += OnPaneChanged;
            }
        }

        ScheduleCount();
        ShowPaneStatus();
        UpdatePaneText();
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ScheduleCount();

    private TextBlock[] StatusCells => [PaneText, CountText, StatusText, IndexText];

    // Before cells 1..3, in the order of StatusSeparators' result.
    private FrameworkElement[] StatusSeparatorLines => [CountSeparator, StatusSeparator, IndexSeparator];

    // Every write to a cell's Text (and the index cell's Visibility) re-syncs the separators, wherever it comes from.
    private void WatchStatusCells()
    {
        foreach (var line in StatusSeparatorLines)
        {
            HairLine.Snap(line);
        }

        foreach (var cell in StatusCells)
        {
            cell.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => SyncStatusSeparators());
        }

        IndexText.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => SyncStatusSeparators());
        SyncStatusSeparators();
    }

    private void SyncStatusSeparators()
    {
        bool[] shown = StatusSeparators([.. StatusCells.Select(c => c.Visibility == Visibility.Visible && c.Text.Length > 0)]);
        var lines = StatusSeparatorLines;
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i].Visibility = shown[i] ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdatePaneText()
    {
        if (_layout is not { VisibleCount: > 0 } layout || _statusPane is null)
        {
            PaneText.Text = string.Empty;
            return;
        }

        PaneText.Text = Localizer.Current.Format("status.pane", ("n", layout.ActiveIndex + 1), ("path", _statusPane.LocationText ?? string.Empty)); // a folder, or "This PC"
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PaneViewModel vm)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(PaneViewModel.Status):
                ShowPaneStatus();
                break;
            case nameof(PaneViewModel.LocationText):
                UpdatePaneText();
                ShowPaneStatus(); // a favorite's message (and its tooltip) ends with the next navigation
                break;
            case nameof(PaneViewModel.IconSize):
                StatusText.Text = Localizer.Current.Format("status.iconSize", ("size", vm.IconSize));
                _transientTimer.Interval = TransientStatus;
                _transientTimer.Start();
                break;
        }
    }

    private void ShowPaneStatus()
    {
        _transientTimer.Stop();
        StatusText.Text = _statusPane?.Status?.Text ?? string.Empty;
        ToolTipService.SetToolTip(StatusText, null);
    }

    // Selection changes arrive in bursts (Shift+click, rubber band); count once per burst, below input priority.
    private void ScheduleCount()
    {
        if (_countPending)
        {
            return;
        }

        _countPending = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, WinUiDispatcher.Guarded(
            () =>
            {
                _countPending = false;
                UpdateCount();
            },
            _logger));
    }

    private void UpdateCount()
    {
        if (_statusPane is not { } vm)
        {
            CountText.Text = string.Empty;
            return;
        }

        int selected = vm.Items.Selection.Count;
        if (selected > 0)
        {
            long bytes = vm.Items.Selection.SelectedBytes();
            CountText.Text = Localizer.Current.Plural("status.selected", selected, ("count", selected.ToString("N0", CultureInfo.CurrentCulture)), ("size", SizeFormat.Bytes(bytes)));
        }
        else
        {
            int count = vm.Items.Count;
            CountText.Text = vm.Items.Current is null ? string.Empty : Localizer.Current.Plural("status.items", count, ("count", count.ToString("N0", CultureInfo.CurrentCulture)));
        }
    }

    // ---- favorites bar: Add/Remove pane and the layout picker (ui-spec §1) ------------------------------------------

    private void SetupTopBar()
    {
        AddPaneButton.Content = TopBarIcon(IconId.AddPane);
        RemovePaneButton.Content = TopBarIcon(IconId.RemovePane);
        Describe(AddPaneButton, "window.addPane");
        Describe(RemovePaneButton, "window.removePane");
        Describe(LayoutPicker, "window.layout");
        LayoutFlyout.Opening += (_, _) => LayoutTiles.SelectedIndex = (int)ViewModel.CurrentLayout;
    }

    private IconElement TopBarIcon(IconId id) => _icons.CreateElement(id, size: 18);

    private static void Describe(FrameworkElement element, string key)
    {
        string text = Localizer.Current[key];
        ToolTipService.SetToolTip(element, text);
        AutomationProperties.SetName(element, text);
    }

    // 4×2 glyph tiles; the clicked item is the tile itself (its Tag is the layout).
    private void BuildLayoutTiles()
    {
        foreach (var info in LayoutCatalog.All)
        {
            var icon = _icons.CreateElement(IconProvider.ForLayout(info.Id), size: 32);
            var tile = new Border { Child = icon, Padding = new Thickness(4), Tag = info.Id };
            Describe(tile, info.TextKey);
            LayoutTiles.Items.Add(tile);
        }
    }

    private void OnLayoutTileClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FrameworkElement { Tag: LayoutId id })
        {
            ViewModel.SetLayoutCommand.Execute(id);
        }

        LayoutFlyout.Hide();
    }

    // The 8 radio items come from the catalog so names, glyphs and ids stay in one table.
    private void BuildLayoutMenu()
    {
        foreach (var info in LayoutCatalog.All)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = Localizer.Current[info.TextKey],
                GroupName = LayoutGroup,
                Icon = _icons.CreateElement(IconProvider.ForLayout(info.Id)),
                Command = ViewModel.SetLayoutCommand,
                CommandParameter = info.Id,
                Tag = info.Id,
            };
            _layoutItems.Add(item);
            LayoutMenu.Items.Add(item);
        }

        SyncLayoutChecks();
    }

    private void SyncLayoutChecks()
    {
        foreach (var item in _layoutItems)
        {
            item.IsChecked = (LayoutId)item.Tag == ViewModel.CurrentLayout;
        }

        var face = _icons.CreateElement(IconProvider.ForLayout(ViewModel.CurrentLayout), size: 20);
        LayoutPicker.Content = face;
    }

    // ---- window shortcuts (AC-1.4, AC-1.3a, ui-spec §5) ---------------------------------------------------------------

    // Alt+1-4 also work while a text field has focus (Explorer behaviour; they type nothing). Add/Remove pane stay silent
    // there (AC-1.19): Ctrl+Shift+- during an inline rename must not remove the pane being edited. No F6: the owner uses it
    // for his own tool (owner round 5 #11), so it passes through untouched.
    private void AddWindowShortcuts()
    {
        VirtualKey[] digits = [VirtualKey.Number1, VirtualKey.Number2, VirtualKey.Number3, VirtualKey.Number4];
        for (int i = 0; i < digits.Length; i++)
        {
            int number = i + 1;
            Shortcut(digits[i], VirtualKeyModifiers.Menu, () => Activate(ViewModel.ActivatePaneCommand, number));
        }

        foreach (VirtualKey key in new[] { OemPlus, VirtualKey.Add })
        {
            Shortcut(key, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => Run(ViewModel.AddPaneCommand), inTextFields: false);
        }

        foreach (VirtualKey key in new[] { OemMinus, VirtualKey.Subtract })
        {
            Shortcut(key, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => Run(ViewModel.RemovePaneCommand), inTextFields: false);
        }

        // The panes' Ctrl+F is scoped to the focused pane; with focus outside every pane (menu bar, favorites bar, back from
        // the Settings dialog) only this one is reached, so it opens the active pane's search (owner stage 3 #1).
        Shortcut(VirtualKey.F, VirtualKeyModifiers.Control, () => PaneArea.ActiveView?.RequestSearch());

        // File > New window (§18 D7); Ctrl+Shift+N stays the panes' New folder. Not while typing (a rename, the path box, a
        // dialog's text box): the text keeps the key and nothing in progress is disturbed (lead, QA MI).
        Shortcut(VirtualKey.N, VirtualKeyModifiers.Control, () => Run(ViewModel.NewWindowCommand), inTextFields: false);
    }

    private void Shortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action, bool inTextFields = true)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            if (!inTextFields && IsTextFieldFocused())
            {
                return; // the text field keeps the key
            }

            args.Handled = true;
            action();
        };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    private bool IsTextFieldFocused() => Content?.XamlRoot is { } root && IsTextField(FocusManager.GetFocusedElement(root));

    /// <summary>True for an element that takes typed text: shortcuts marked "not in text fields" leave it the key.</summary>
    /// <param name="focused">The focused element.</param>
    /// <returns>True for a TextBox, PasswordBox or RichEditBox.</returns>
    public static bool IsTextField(object? focused) => focused is TextBox or PasswordBox or RichEditBox;

    private static void Run(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    // Keyboard activation moves focus into the pane, so the next keys act on it.
    private void Activate(System.Windows.Input.ICommand command, int? number)
    {
        object? parameter = number;
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
            FocusActivePane();
        }
    }
}
