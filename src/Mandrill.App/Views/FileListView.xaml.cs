using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Mandrill.App.Platform;
using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.Platform;
using Mandrill.Core.Scanning;
using Mandrill.Core.Shell;
using Mandrill.Core.State;
using Mandrill.Presentation.Common;
using Mandrill.Presentation.Panes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using IconSize = Mandrill.Core.Shell.IconSize;

namespace Mandrill.App.Views;

/// <summary>
/// The file list of one pane (architecture §5, ui-spec §2): Details/Icons/List over one <see cref="PaneListAdapter"/>,
/// rows filled in ContainerContentChanging, icons and type names refreshed only on realized containers, type-ahead,
/// rubber band (Icons/List), Ctrl+wheel view mode and icon size, slow-click rename, Details header with sort and resizable columns.
/// </summary>
public sealed partial class FileListView : UserControl
{
    private const string FolderGlyph = "\uE8B7";
    private const string FileGlyph = "\uE8A5";
    private const string SortUpGlyph = "\uE70E";
    private const string SortDownGlyph = "\uE70D";
    private const double NameMinWidth = 120;
    private const double ColumnMinWidth = 40;
    private const double ListRowHeight = 22;
    private const double ListMaxWidth = 240;
    private const double AutoScrollEdge = 24;
    private const double AutoScrollStep = 24;

    private static readonly SortColumn[] HeaderColumns = [SortColumn.Name, SortColumn.Date, SortColumn.Type, SortColumn.Size];


    // Realized rows: their entries and the icons they wait for (shared with the search results, RowIconBinder).
    private RowIconBinder<FileEntry>? _rows;
    private bool _isActive = true;
    private bool _highContrast = AppTheme.IsHighContrast(); // refreshed with the selection brushes (a theme change)
    private bool _scrollToStartOnLoad;
    private readonly TypeAhead _typeAhead = new();
    private readonly TextBlock _measure = new();
    private readonly TextBlock[] _headerArrows = new TextBlock[4];
    private readonly ColumnGripper[] _grippers = new ColumnGripper[4];

    // Width of the column being dragged when the drag started: the divider follows the pointer from the press position, so
    // dragging past the minimum and back does not drift (QA F-3).
    private double _dragStartWidth;
    private PaneViewModel? _vm;
    private PaneListAdapter? _adapter;
    private IIconService? _icons;
    private IconBitmapCache? _bitmaps;
    private IUiDispatcher? _dispatcher;
    private ILogger _logger = NullLogger.Instance;
    private ListViewBase? _list;
    private ScrollViewer? _scroller;
    private ViewMode _mode;
    private double _listItemWidth = ListMaxWidth;
    private Action<IReadOnlyList<IconKey>>? _iconsReadyHandler;
    private Action? _typeNamesHandler;

    // Inline rename (AC-1.20, AC-3.11): the entry being edited, and a guard against re-entrant commits.
    private FileEntry? _renaming;
    private bool _committing;

    // Explorer's slow second click (owner #3): the armed entry, the last press, and whether this press brought the focus
    // into the list (GettingFocus is synchronous, so it is set before the list's own press handler runs).
    private string? _slowClickName;
    private long _lastPressTick = long.MinValue / 2;
    private bool _focusEnteredByPointer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _slowClickTimer;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

    // Ctrl+wheel delta not yet turned into a view step (ListGeometry.AccumulateWheel).
    private int _wheelDelta;

    // Perf (requirements §6 "folder open, first screen"): when the pane started loading; 0 when not measuring.
    private long _loadStart;

    // Folder shown before a Reset: a different folder scrolls to the top (same-folder refreshes keep the native anchor).
    private string? _resetFrom;

    // Rubber band (Icons/List): start in panel coordinates, so scrolling while dragging keeps the anchor.
    private bool _banding;
    private bool _bandAdditive;
    private Point _bandStart;
    private Point _bandLastInList;
    private IReadOnlyList<(int First, int Count)> _bandRanges = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _bandTimer;

    // Details column widths in effect (stored widths, clamped to the minimums); refreshed on Columns changes.
    private double[] _widths = [ColumnWidths.Default.Name, ColumnWidths.Default.Date, ColumnWidths.Default.Type, ColumnWidths.Default.Size];
    private readonly TranslateTransform _headerShift = new();

    // Explorer's item font (an App resource, App.xaml) for the text built in code.
    private static FontFamily ListFontFamily => (FontFamily)Application.Current.Resources["MandrillListFontFamily"];

    private static double ListFontSize => (double)Application.Current.Resources["MandrillListFontSize"];

    /// <summary>Creates the view; call <see cref="Attach"/> before use.</summary>
    public FileListView()
    {
        InitializeComponent();
        AppTheme.Track(Resources); // ExplorerItems.xaml scope
        AppTheme.Track(RenameBox.Resources); // its opaque Dark focus fill must not beat the High contrast palette
        _measure.FontFamily = ListFontFamily; // auto-fit measures in the rows' font
        _measure.FontSize = ListFontSize;
        RetryLink.Click += (_, _) => _vm?.RetryCommand.Execute(null);
        Header.RenderTransform = _headerShift;
        EditCanvas.SizeChanged += (_, e) => EditCanvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        HeaderClip.SizeChanged += (_, e) => HeaderClipRect.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        RenameBox.KeyDown += OnRenameKeyDown;
        RenameBox.LostFocus += (_, _) =>
        {
            if (_renaming is not null && !_committing)
            {
                _ = CommitRenameAsync(fromFocusLoss: true);
            }
        };
    }

    /// <summary>A right-click, Shift+F10 or the menu key asked for a context menu (WP-3.1); the selection is already adjusted.</summary>
    public event EventHandler<PaneContextMenuRequest>? ContextMenuRequested;

    /// <summary>True when the keyboard focus is in this list (Del acts on its selection only then).</summary>
    public bool HasFocusWithin
    {
        get => XamlRoot is not null && IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, this);
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject root)
    {
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Asks before a rename changes a file's extension (AC-3.11, Explorer's wording); true = rename. Null shows the built-in
    /// dialog; tests set it.
    /// </summary>
    public Func<Task<bool>>? ConfirmExtensionChange { get; set; }

    /// <summary>True while the inline rename editor is open (tests).</summary>
    public bool IsRenaming => _renaming is not null;

    /// <summary>The inline rename editor (tests).</summary>
    public TextBox RenameEditor => RenameBox;

    /// <summary>The active list control (tests, focus handling); null before <see cref="Attach"/>.</summary>
    public ListViewBase? List => _list;

    /// <summary>The adapter set as ItemsSource (tests).</summary>
    public PaneListAdapter? Adapter => _adapter;

    /// <summary>Binds the view to a pane.</summary>
    /// <param name="vm">Pane view model.</param>
    /// <param name="icons">Icon and type-name service.</param>
    /// <param name="bitmaps">UI-thread bitmap cache.</param>
    /// <param name="dispatcher">UI dispatcher (icon events arrive on pool threads).</param>
    /// <param name="logger">Logger.</param>
    public void Attach(PaneViewModel vm, IIconService icons, IconBitmapCache bitmaps, IUiDispatcher dispatcher, ILogger logger)
    {
        if (_vm is not null)
        {
            throw new InvalidOperationException("The file list is already attached to a pane");
        }

        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _bitmaps = bitmaps ?? throw new ArgumentNullException(nameof(bitmaps));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? NullLogger.Instance;
        _rows = new RowIconBinder<FileEntry>(icons, bitmaps);
        _adapter = new PaneListAdapter(vm.Items);
        _adapter.VisibleRangeChanged += OnVisibleRangeChanged;
        vm.PropertyChanged += OnViewModelChanged;
        vm.Items.Resetting += () => _resetFrom = vm.Items.Current?.Path;
        vm.Items.Reset += OnItemsReset;
        vm.Items.CollectionChanged += (_, _) => UpdateMessage();
        vm.SelectionRequested += OnSelectionRequested;
        _iconsReadyHandler = keys => _dispatcher.Post(() => OnIconsReady(keys), UiPriority.Low);
        _typeNamesHandler = () => _dispatcher.Post(RefreshTypeCells, UiPriority.Low);
        icons.IconsReady += _iconsReadyHandler;
        icons.TypeNamesReady += _typeNamesHandler;
        BuildHeader();
        ShowMode(vm.ViewMode);
        UpdateMessage();
    }

    /// <summary>Unsubscribes from the long-lived icon service.</summary>
    public void Detach()
    {
        CancelSlowClick();
        _wheelDelta = 0;
        if (_icons is not null)
        {
            _icons.IconsReady -= _iconsReadyHandler;
            _icons.TypeNamesReady -= _typeNamesHandler;
        }
    }

    /// <summary>Moves keyboard focus to the list (focused item if any).</summary>
    public void FocusList() => _list?.Focus(FocusState.Programmatic);

    /// <summary>
    /// Whether the pane is active (WP-1.10): the active pane shows Explorer's selection, inactive panes a grey one
    /// (ui-spec §2 active pane indication, item 3). Applied to realized rows now and to rows realized later.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            RefreshSelectionBrushes();
        }
    }

    /// <summary>The Details header dividers, one per column (tests).</summary>
    public IReadOnlyList<ColumnGripper> ColumnGrippers => _grippers;

    /// <summary>Re-applies the selection brush to the realized rows (after a theme change the grey must follow the theme).</summary>
    public void RefreshSelectionBrushes()
    {
        _highContrast = AppTheme.IsHighContrast();
        foreach (SelectorItem container in _rows?.Rows ?? [])
        {
            ApplySelectionBrush(container);
        }
    }

    private static T? Ancestor<T>(DependencyObject? start, DependencyObject? stopAt = null)
        where T : DependencyObject
    {
        for (var d = start; d is not null && d != stopAt; d = VisualTreeHelper.GetParent(d))
        {
            if (d is T t)
            {
                return t;
            }
        }

        return null;
    }

    internal static T? Descendant<T>(DependencyObject root, Func<T, bool>? match = null)
        where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && (match is null || match(t)))
            {
                return t;
            }

            if (Descendant(child, match) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string FormatDate(long utcTicks)
    {
        if (utcTicks <= 0 || utcTicks > DateTime.MaxValue.Ticks)
        {
            return string.Empty;
        }

        return new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    private static bool IsControlDown()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // Explorer shows cut items semi-transparent until they are pasted or the clipboard changes.
    private const double CutOpacity = 0.5;

    // Explorer opens a keyboard (Shift+F10, menu key) context menu slightly inside the focused row's lower left corner (the
    // search results too).
    internal const double KeyboardMenuInset = 16;

    private IReadOnlySet<string>? _cutPaths;

    /// <summary>Shift is held (a context menu request: extended verbs).</summary>
    /// <returns>True when Shift is down.</returns>
    internal static bool IsShiftDown()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ---- view modes ---------------------------------------------------------------------------------------------

    private void ShowMode(ViewMode mode)
    {
        CloseRename(focusList: false);
        CancelSlowClick();
        if (_list is not null)
        {
            _list.ContainerContentChanging -= OnContainerContentChanging;
            _list.ItemsSource = null;
            ListHost.Children.Clear();
            _rows?.Clear();
            _scroller = null;
        }

        _mode = mode;
        ListViewBase list = mode switch
        {
            ViewMode.Details => new ListView
            {
                ItemTemplate = (DataTemplate)Resources["DetailsRowTemplate"],
                ItemContainerStyle = (Style)Resources["DetailsItemStyle"],
            },
            ViewMode.Icons => new GridView
            {
                ItemTemplate = (DataTemplate)Resources["IconsCellTemplate"],
                ItemContainerStyle = (Style)Resources["GridItemStyle"],
                ItemsPanel = (ItemsPanelTemplate)Resources["IconsPanelTemplate"],
            },
            _ => new GridView
            {
                ItemTemplate = (DataTemplate)Resources["ListCellTemplate"],
                ItemContainerStyle = (Style)Resources["GridItemStyle"],
                ItemsPanel = (ItemsPanelTemplate)Resources["ListPanelTemplate"],
            },
        };

        list.SelectionMode = ListViewSelectionMode.Extended;
        list.IsItemClickEnabled = false;
        AutomationProperties.SetName(list, Localizer.Current["pane.fileList"]); // QA s2: the list had no UIA name
        if (mode == ViewMode.List)
        {
            ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        }
        else if (mode == ViewMode.Details)
        {
            ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        }

        list.ContainerContentChanging += OnContainerContentChanging;
        list.DoubleTapped += OnDoubleTapped;
        AttachDragDrop(list);
        list.ContextRequested += OnListContextRequested;
        list.KeyDown += OnListKeyDown;
        // Any key (arrows and Space are handled by the control itself) or focus leaving the list ends a pending slow click.
        list.AddHandler(KeyDownEvent, new KeyEventHandler((_, _) => CancelSlowClick()), handledEventsToo: true);
        list.LostFocus += (_, _) =>
        {
            if (!HasFocusWithin)
            {
                CancelSlowClick();
            }
        };
        list.CharacterReceived += OnCharacterReceived;
        list.Loaded += OnListLoaded;
        list.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheel), handledEventsToo: true);
        list.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSlowClickPressed), handledEventsToo: true);
        list.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSlowClickReleased), handledEventsToo: true);
        list.GettingFocus += (_, e) => _focusEnteredByPointer |= e.InputDevice is FocusInputDeviceKind.Mouse or FocusInputDeviceKind.Pen or FocusInputDeviceKind.Touch
            && !IsWithin(e.OldFocusedElement, list);
        if (mode != ViewMode.Details)
        {
            list.AddHandler(PointerPressedEvent, new PointerEventHandler(OnBandPressed), handledEventsToo: true);
            list.AddHandler(PointerMovedEvent, new PointerEventHandler(OnBandMoved), handledEventsToo: true);
            list.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnBandReleased), handledEventsToo: true);
            list.PointerCaptureLost += (_, _) => EndBand();
        }

        HeaderClip.Visibility = mode == ViewMode.Details ? Visibility.Visible : Visibility.Collapsed;
        _headerShift.X = 0;
        EndBand();
        _list = list;
        list.ItemsSource = _adapter;
        ListHost.Children.Add(list);
        ApplyColumnsEverywhere(); // widths do not depend on the pane size: once per mode change, then on Columns changes
        if (mode == ViewMode.List)
        {
            _listItemWidth = MeasureListItemWidth();
        }
    }

    private void OnListLoaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _list))
        {
            return;
        }

        ApplyPanelItemSize();
        if (_scroller is not null)
        {
            if (_scrollToStartOnLoad)
            {
                ScrollToStart();
            }

            return; // Loaded fires again on re-parenting (layouts, WP-1.10); this list is already wired
        }

        _scroller = Descendant<ScrollViewer>(_list);
        if (_scroller is not null)
        {
            _scroller.ViewChanged += OnViewChanged;
            _scroller.RegisterPropertyChangedCallback(ScrollViewer.ComputedHorizontalScrollBarVisibilityProperty, OnHorizontalBarShown);
            OnHorizontalBarShown(_scroller, ScrollViewer.ComputedHorizontalScrollBarVisibilityProperty);
        }

        _scrollToStartOnLoad = false; // a first-time loaded list starts at the top
    }

    // WinUI's scroll bars float over the content; Explorer's take room. While the horizontal bar shows (Details, List), the
    // list keeps a bottom padding of its height so no row ends under it (owner #2): the ScrollBar style's MinHeight plus the
    // padding of the bar's host in the ScrollViewer template (it is not laid out yet when the bar appears).
    private void OnHorizontalBarShown(DependencyObject sender, DependencyProperty dp)
    {
        if (!ReferenceEquals(sender, _scroller) || _list is null)
        {
            return; // a discarded list of a previous view mode
        }

        if (_scroller.ComputedHorizontalScrollBarVisibility == Visibility.Visible
            && Descendant<ScrollBar>(_scroller, b => b.Orientation == Orientation.Horizontal) is { } bar)
        {
            var host = VisualTreeHelper.GetParent(bar) as Grid;
            _list.Padding = new Thickness(0, 0, 0, bar.MinHeight + (host?.Padding.Top ?? 0) + (host?.Padding.Bottom ?? 0));
        }
        else
        {
            _list.ClearValue(Control.PaddingProperty);
        }
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _scroller))
        {
            return; // a discarded list of a previous view mode
        }

        SyncHeaderOffset();
        if (_renaming is not null && !_committing && !PlaceRenameBox())
        {
            _ = CommitRenameAsync(fromFocusLoss: true); // the row scrolled out of view: commit like Explorer
        }
    }

    private void ApplyPanelItemSize()
    {
        if (_list?.ItemsPanelRoot is not ItemsWrapGrid grid || _vm is null)
        {
            return;
        }

        if (_mode == ViewMode.Icons)
        {
            var cell = ListGeometry.IconCell(_vm.IconSize);
            grid.ItemWidth = cell.Width;
            grid.ItemHeight = cell.Height;
        }
        else if (_mode == ViewMode.List)
        {
            grid.ItemWidth = _listItemWidth;
            grid.ItemHeight = ListRowHeight;
        }
    }

    // Longest names by character count, measured with the list font: cheap on 100k and close to the real maximum.
    private double MeasureListItemWidth()
    {
        if (_vm?.Items.Current is not { } snapshot || snapshot.Visible.Count == 0)
        {
            return ListMaxWidth;
        }

        // O(n) on every Reset (progressive snapshots arrive every 250 ms): longest length first, then measure only the
        // names close to it (proportional fonts make the longest string not always the widest).
        int longest = 0;
        foreach (var entry in snapshot.Visible)
        {
            longest = Math.Max(longest, entry.Name.Length);
        }

        double widest = 0;
        int measured = 0;
        foreach (var entry in snapshot.Visible)
        {
            if (entry.Name.Length < longest - 4)
            {
                continue;
            }

            _measure.Text = entry.Name;
            _measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, _measure.DesiredSize.Width);
            if (++measured >= 24)
            {
                break;
            }
        }

        return Math.Min(ListMaxWidth, widest + 16 + 6 + 8 + 8);
    }

    // ---- rows -------------------------------------------------------------------------------------------------------

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            _rows?.Forget(args.ItemContainer);
            return;
        }

        if (args.Item is FileEntry entry && args.ItemContainer.ContentTemplateRoot is Grid root)
        {
            _rows?.Track(args.ItemContainer, entry);
            Fill(args.ItemContainer, root, entry);
            ApplySelectionBrush(args.ItemContainer);
            ApplyCut(args.ItemContainer, entry);
            args.Handled = true;
        }
    }

    // Inactive pane: grey selection on the row presenter (local value); active: Explorer's (Themes/ExplorerItems.xaml).
    /// <summary>
    /// Items cut to the clipboard (any CF_HDROP with a move effect, Explorer's too) show dimmed until the clipboard changes
    /// (AC-3.14). Null or empty: none. Realized rows update at once.
    /// </summary>
    /// <param name="cutPaths">Full paths of the cut items.</param>
    public void SetCutPaths(IReadOnlySet<string>? cutPaths)
    {
        _cutPaths = cutPaths is { Count: > 0 } ? cutPaths : null;
        foreach (SelectorItem container in _rows?.Rows ?? [])
        {
            if (_rows!.TryGetItem(container, out var entry))
            {
                ApplyCut(container, entry);
            }
        }
    }

    /// <summary>Whether the realized row of <paramref name="name"/> shows dimmed (tests).</summary>
    /// <param name="name">Entry name.</param>
    /// <returns>True when dimmed; false when not dimmed or not realized.</returns>
    public bool IsShownCut(string name)
    {
        foreach (SelectorItem container in _rows?.Rows ?? [])
        {
            if (_rows!.TryGetItem(container, out var entry) && entry.Name == name)
            {
                return container.ContentTemplateRoot is UIElement { Opacity: < 1 };
            }
        }

        return false;
    }

    // Only the row content is ghosted (Explorer): dimming the container also faded the selection fill to the hover colour
    // (qa-wp119 M1). Set on every realization, so a recycled row is reset.
    private void ApplyCut(SelectorItem container, FileEntry entry)
    {
        if (container.ContentTemplateRoot is UIElement root)
        {
            root.Opacity = _cutPaths is { } cut && _vm?.CurrentPath is { } folder && cut.Contains(PathUtil.Combine(folder, entry.Name)) ? CutOpacity : 1;
        }
    }

    private void ApplySelectionBrush(SelectorItem container)
    {
        if (VisualTreeHelper.GetChildrenCount(container) == 0 || VisualTreeHelper.GetChild(container, 0) is not ListViewItemPresenter presenter)
        {
            return;
        }

        // Always a local value: ClearValue also drops the brush the item template set, which left a selected row with no
        // fill at all (owner F16). The swatches give each control's own theme brush (GridView differs in high contrast).
        bool grid = container is GridViewItem;
        Brush selected = (_isActive ? (grid ? GridSelectedSwatch : ListSelectedSwatch) : InactiveSelectionSwatch).Background;
        presenter.SelectedBackground = selected;
        presenter.SelectedPointerOverBackground = _isActive ? (grid ? GridSelectedHoverSwatch : ListSelectedHoverSwatch).Background : selected;
        presenter.SelectedPressedBackground = selected;
        if (!_highContrast)
        {
            // Explorer shows only the fill: no selection bar, check mark or accent border. Set here: the item templates do
            // not take x:Boolean / Thickness resource overrides (Themes/ExplorerItems.xaml); high contrast keeps them.
            presenter.SelectionIndicatorVisualEnabled = false;
            presenter.SelectionCheckMarkVisualEnabled = false;
            presenter.SelectedBorderThickness = new Thickness(0);
        }
    }

    private void Fill(SelectorItem container, Grid root, FileEntry entry)
    {
        var parts = RowParts.Of(root, _mode);
        parts.Name.Text = entry.Name;
        AutomationProperties.SetName(container, entry.Name); // else UIA reads FileEntry.ToString() (record dump)
        if (_mode == ViewMode.Details)
        {
            parts.Date!.Text = FormatDate(entry.LastWriteUtcTicks);
            parts.Type!.Text = _icons!.TypeName(entry);
            parts.Size!.Text = entry.IsDirectory ? string.Empty : SizeFormat.Kb(entry.Size);
            ApplyColumns(root);
        }
        else if (_mode == ViewMode.Icons && _vm is not null)
        {
            parts.IconBox.Width = _vm.IconSize;
            parts.IconBox.Height = _vm.IconSize;
            parts.Placeholder.FontSize = Math.Max(12, _vm.IconSize * 0.75);
        }

        SetIcon(container, parts, entry);
    }

    private void SetIcon(SelectorItem container, RowParts parts, FileEntry entry)
    {
        if (_vm?.Items.Current is not { } snapshot || _rows is null)
        {
            return;
        }

        var size = _mode == ViewMode.Icons ? IconSizes.ForPixels(_vm.IconSize) : IconSize.Small;
        if (_rows.Resolve(container, entry, snapshot.Path, size) is { } bitmap)
        {
            parts.Image.Source = bitmap;
            parts.Placeholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Same-size placeholder first, so the real icon causes no layout shift (AC-1.14).
            parts.Image.Source = null;
            parts.Placeholder.Glyph = entry.IsDirectory ? FolderGlyph : FileGlyph;
            parts.Placeholder.Visibility = Visibility.Visible;
        }
    }

    private void OnIconsReady(IReadOnlyList<IconKey> keys)
    {
        if (_rows is null)
        {
            return;
        }

        foreach (var container in _rows.RowsWaitingFor(keys))
        {
            if (_rows.TryGetItem(container, out var entry) && container.ContentTemplateRoot is Grid root)
            {
                SetIcon(container, RowParts.Of(root, _mode), entry);
            }
        }
    }

    private void RefreshTypeCells()
    {
        if (_mode != ViewMode.Details || _icons is null)
        {
            return;
        }

        foreach (var container in Realized())
        {
            if (_rows is not null && _rows.TryGetItem(container, out var entry) && container.ContentTemplateRoot is Grid root)
            {
                RowParts.Of(root, _mode).Type!.Text = _icons.TypeName(entry);
            }
        }
    }

    private void RefreshRealized()
    {
        foreach (var container in Realized())
        {
            if (_rows is not null && _rows.TryGetItem(container, out var entry) && container.ContentTemplateRoot is Grid root)
            {
                Fill(container, root, entry);
            }
        }
    }

    private IEnumerable<SelectorItem> Realized()
        => _list?.ItemsPanelRoot is { } panel ? panel.Children.OfType<SelectorItem>().ToArray() : [];

    private void OnVisibleRangeChanged(int first, int last)
    {
        if (_list is null || _icons is null || first < 0)
        {
            return;
        }

        var keys = new List<IconKey>();
        for (int i = first; i <= last && i < first + 512; i++)
        {
            if (_list.ContainerFromIndex(i) is SelectorItem container && _rows is not null && _rows.TryGetKey(container, out var waiting))
            {
                keys.Add(waiting);
            }
        }

        if (keys.Count > 0)
        {
            _icons.SetPriority(keys);
        }
    }

    // ---- Details header and columns ----------------------------------------------------------------------------

    private void BuildHeader()
    {
        Header.ColumnDefinitions.Clear();
        Header.Children.Clear();
        string[] keys = ["pane.column.name", "pane.column.date", "pane.column.type", "pane.column.size"];
        for (int i = 0; i < HeaderColumns.Length; i++)
        {
            Header.ColumnDefinitions.Add(new ColumnDefinition());
            var column = HeaderColumns[i];
            var cell = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new Thickness(i == 0 ? 28 : 4, 0, 10, 0) };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = Localizer.Current[keys[i]],
                FontFamily = ListFontFamily,
                FontSize = ListFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = column == SortColumn.Size ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            var arrow = new TextBlock
            {
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            };
            Grid.SetColumn(arrow, 1);
            cell.Children.Add(label);
            cell.Children.Add(arrow);
            _headerArrows[i] = arrow;
            cell.Tapped += (_, _) => _vm?.SetSort(column);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, label.Text);

            var gripper = new ColumnGripper((Style)Resources["ColumnDividerStyle"]);
            int index = i;
            gripper.DragStarted += () => _dragStartWidth = EffectiveWidths()[index];
            gripper.Resized += offset => ResizeColumn(index, _dragStartWidth + offset);
            gripper.AutoFitRequested += () => AutoFitColumn(index);
            _grippers[i] = gripper;

            var host = new Grid();
            host.Children.Add(cell);
            host.Children.Add(gripper);
            Grid.SetColumn(host, i);

            // Half of the grab zone lies over the next column: earlier columns stack on top, so it stays hit-testable.
            Canvas.SetZIndex(host, HeaderColumns.Length - i);
            Header.Children.Add(host);
        }

        UpdateSortArrows();
    }

    private void UpdateSortArrows()
    {
        if (_vm is null)
        {
            return;
        }

        for (int i = 0; i < HeaderColumns.Length; i++)
        {
            _headerArrows[i].Text = HeaderColumns[i] == _vm.SortColumn ? (_vm.SortDescending ? SortDownGlyph : SortUpGlyph) : string.Empty;
        }
    }

    // Every column keeps its stored width, as in Explorer (owner F4): a Name that filled the viewport could never be
    // shrunk by its divider. A wider pane leaves blank space right of Size.
    private double[] EffectiveWidths()
    {
        var c = _vm?.Columns ?? ColumnWidths.Default;
        return [Math.Max(NameMinWidth, c.Name), Math.Max(ColumnMinWidth, c.Date), Math.Max(ColumnMinWidth, c.Type), Math.Max(ColumnMinWidth, c.Size)];
    }

    // Per container fill: reads the cached widths, no allocation.
    private void ApplyColumns(Grid row)
    {
        double total = 0;
        for (int i = 0; i < 4; i++)
        {
            row.ColumnDefinitions[i].Width = new GridLength(_widths[i]);
            total += _widths[i];
        }

        row.Width = total;
    }

    private void ApplyColumnsEverywhere()
    {
        _widths = EffectiveWidths();
        if (_mode != ViewMode.Details || _vm is null)
        {
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            Header.ColumnDefinitions[i].Width = new GridLength(_widths[i]);
        }

        Header.Width = _widths.Sum();
        foreach (var container in Realized())
        {
            if (container.ContentTemplateRoot is Grid root)
            {
                ApplyColumns(root);
            }
        }
    }

    private void ResizeColumn(int index, double requested)
    {
        if (_vm is null)
        {
            return;
        }

        double min = index == 0 ? NameMinWidth : ColumnMinWidth;
        double width = Math.Max(min, requested);
        var c = _vm.Columns;
        _vm.Columns = index switch
        {
            0 => c with { Name = width },
            1 => c with { Date = width },
            2 => c with { Type = width },
            _ => c with { Size = width },
        };
    }

    // Double-click on a divider: widest realized cell of that column (ui-spec §2).
    private void AutoFitColumn(int index)
    {
        double widest = 0;
        foreach (var container in Realized())
        {
            if (container.ContentTemplateRoot is not Grid root)
            {
                continue;
            }

            var parts = RowParts.Of(root, _mode);
            TextBlock? cell = index switch { 0 => parts.Name, 1 => parts.Date, 2 => parts.Type, _ => parts.Size };
            if (cell is null)
            {
                continue;
            }

            _measure.Text = cell.Text;
            _measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, _measure.DesiredSize.Width);
        }

        if (widest > 0)
        {
            double padding = index == 0 ? 6 + 16 + 6 + 4 + 8 : index == 3 ? 4 + 10 + 4 : 8 + 4;
            ResizeColumn(index, widest + padding);
        }
    }

    private void SyncHeaderOffset()
    {
        if (_scroller is not null && _mode == ViewMode.Details)
        {
            _headerShift.X = -_scroller.HorizontalOffset;
        }
    }

    // ---- view-model changes ------------------------------------------------------------------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaneViewModel.ViewMode):
                ShowMode(_vm!.ViewMode);
                break;
            case nameof(PaneViewModel.IconSize) when _mode == ViewMode.Icons:
                ApplyPanelItemSize();
                RefreshRealized();
                break;
            case nameof(PaneViewModel.Columns):
                ApplyColumnsEverywhere();
                break;
            case nameof(PaneViewModel.SortColumn):
            case nameof(PaneViewModel.SortDescending):
                UpdateSortArrows();
                break;
            case nameof(PaneViewModel.CurrentPath):
                _typeAhead.Reset();
                break;
            case nameof(PaneViewModel.IsLoading):
                // A fresh start per load; cleared when loading ends without a new folder (F5, watcher re-list, failure, pane
                // hidden), so that never inflates the next navigation's first paint. Known limit: a navigation superseded
                // while still loading does not toggle IsLoading, so the next one is measured from the first one's start.
                _loadStart = _vm!.IsLoading ? Stopwatch.GetTimestamp() : 0;
                UpdateMessage();
                break;
            case nameof(PaneViewModel.FolderError):
            case nameof(PaneViewModel.Status):
                UpdateMessage();
                break;
        }
    }

    private void OnItemsReset()
    {
        string? from = _resetFrom;
        _resetFrom = null;
        if (_vm?.Items.Current is { } shown && (from is null || !PathUtil.PathEquals(from, shown.Path)))
        {
            // Name-only IndexOf would keep a same-named anchor of the previous folder: a new folder starts at the top.
            // Lay out first (the control re-anchors in that pass), then scroll, synchronously: a deferred scroll could
            // land after the user (or a later snapshot) already scrolled.
            CloseRename(focusList: false);
            CancelSlowClick(); // a same-named entry of the new folder must not be renamed
            ScrollToStart();
            LogFirstPaint(shown);
        }
        else if (_renaming is not null && _vm?.Items.Current?.IndexOfName(_renaming.Name) < 0)
        {
            CloseRename(focusList: false);
        }

        if (_mode == ViewMode.List)
        {
            double width = MeasureListItemWidth();
            if (Math.Abs(width - _listItemWidth) > 0.5)
            {
                _listItemWidth = width;
                ApplyPanelItemSize();
            }
        }

        UpdateMessage();
    }

    // One perf line per folder shown: load start → the next frame after its first rows were set.
    private void LogFirstPaint(EntrySnapshot shown)
    {
        long start = _loadStart;
        _loadStart = 0;
        if (start == 0)
        {
            return;
        }

        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            PerfLog.Logger.LogInformation(
                "Folder first paint {Ms:F0} ms: {Rows} rows, cached {Cached}, final {Final}, {Path}",
                Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                shown.Visible.Count,
                shown.FromCache,
                shown.IsFinal,
                shown.Path);
        };
        CompositionTarget.Rendering += handler;
    }

    private void ScrollToStart()
    {
        // A list outside the live tree (a pane shown after it started loading, WP-1.10) cannot lay out: UpdateLayout fails
        // with E_FAIL there. Scroll when it is loaded instead.
        if (_list is not { IsLoaded: true })
        {
            _scrollToStartOnLoad = true;
            return;
        }

        _scrollToStartOnLoad = false;
        _list.UpdateLayout();
        _scroller?.ChangeView(0, 0, null, disableAnimation: true);
    }

    private void UpdateMessage()
    {
        if (_vm is null)
        {
            return;
        }

        Progress.Visibility = _vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        if (_vm.FolderError is { } error)
        {
            MessageText.Text = error.Kind == FsErrorKind.AccessDenied
                ? Localizer.Current["pane.accessDenied"]
                : _vm.Status?.Text ?? Localizer.Current.Format("pane.error.other", ("path", error.Path));
            RetryLink.Visibility = Visibility.Visible;
            MessagePanel.Visibility = Visibility.Visible;
        }
        else if (!_vm.IsLoading && _vm.Items.Current is not null && _vm.Items.Count == 0)
        {
            MessageText.Text = Localizer.Current["pane.empty"];
            RetryLink.Visibility = Visibility.Collapsed;
            MessagePanel.Visibility = Visibility.Visible;
        }
        else
        {
            MessagePanel.Visibility = Visibility.Collapsed;
        }
    }

    // ---- selection, open, keys ---------------------------------------------------------------------------------

    private void OnSelectionRequested(SelectionRequest request)
    {
        if (_list is null || _adapter is null || _vm?.Items.Current is not { } snapshot)
        {
            return;
        }

        int[] indexes = request.Names.Select(snapshot.IndexOfName).Where(i => i >= 0).Order().ToArray();
        if (indexes.Length == 0)
        {
            return;
        }

        SelectOnly(indexes, request.Focus);
        if (request.BeginRename)
        {
            BeginRename(indexes[0]); // new folder (AC-1.20)
        }
    }

    private void SelectOnly(IReadOnlyList<int> indexes, bool focus)
    {
        if (_list is null || _adapter is null || _adapter.Count == 0)
        {
            return;
        }

        // Through the control (not the model) so realized containers update their selection visuals.
        _list.DeselectRange(new ItemIndexRange(0, (uint)_adapter.Count));
        foreach (int i in indexes)
        {
            _list.SelectRange(new ItemIndexRange(i, 1));
        }

        _list.ScrollIntoView(_adapter[indexes[0]]);
        if (focus)
        {
            _list.UpdateLayout();
            (_list.ContainerFromIndex(indexes[0]) as Control)?.Focus(FocusState.Programmatic);
        }
    }

    private int FocusedIndex()
    {
        if (_list is null || XamlRoot is null)
        {
            return -1;
        }

        return FocusManager.GetFocusedElement(XamlRoot) is SelectorItem item ? _list.IndexFromContainer(item) : -1;
    }

    private FileEntry? EntryToOpen()
    {
        int focused = FocusedIndex();
        if (_adapter?.EntryAt(focused) is { } entry)
        {
            return entry;
        }

        var selected = _vm?.Items.Selection.SelectedEntries();
        return selected is { Count: > 0 } ? selected[0] : null;
    }

    private void Open(FileEntry entry)
    {
        if (_vm is null)
        {
            return;
        }

        _ = OpenSafeAsync(_vm, entry);
    }

    private async Task OpenSafeAsync(PaneViewModel vm, FileEntry entry)
    {
        try
        {
            await vm.OpenAsync(entry);
        }
        catch (ObjectDisposedException)
        {
            // Pane closed while opening.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening {Name} failed", entry.Name);
        }
    }

    // Right-click, Shift+F10, menu key or press-and-hold: the selection follows Explorer, then the pane asks for the menu.
    private void OnListContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (_list is null || IsRenaming)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        bool keyboard = !e.TryGetPosition(_list, out Point inList);
        if (PrepareContextMenu(source, keyboard) is not { } target)
        {
            return;
        }

        e.Handled = true;
        if (keyboard)
        {
            // The focused row's lower left (Explorer opens the keyboard menu there), else the list's corner.
            UIElement anchor = Ancestor<SelectorItem>(source, _list) is { } row ? row : _list;
            inList = anchor.TransformToVisual(_list).TransformPoint(new Point(KeyboardMenuInset, anchor == _list ? KeyboardMenuInset : anchor.ActualSize.Y));
        }

        Point inRoot = _list.TransformToVisual(null).TransformPoint(inList);
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        ContextMenuRequested?.Invoke(this, new PaneContextMenuRequest(target, _list, inList, (int)Math.Round(inRoot.X * scale), (int)Math.Round(inRoot.Y * scale), IsExtendedRequest(keyboard, IsShiftDown())));
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        CancelSlowClick();
        if (OpenRowAt(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Opens the entry of the row that contains <paramref name="source"/>: the double-click handler (tests).
    /// </summary>
    /// <param name="source">The element that was double-tapped.</param>
    /// <returns>True when a row was opened.</returns>
    public bool OpenRowAt(DependencyObject? source)
    {
        // The control never sets a handled container's Content (owner F11): the row's entry is tracked separately.
        if (Ancestor<SelectorItem>(source, _list) is { } row && _rows is not null && _rows.TryGetItem(row, out var entry))
        {
            Open(entry);
            return true;
        }

        return false;
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when !e.KeyStatus.IsMenuKeyDown: // Alt+Enter is the pane's Properties accelerator (WP-3.6)
                if (EntryToOpen() is { } entry)
                {
                    e.Handled = true;
                    Open(entry);
                }

                break;
            case VirtualKey.F2:
                e.Handled = true;
                BeginRename(RenameTarget());
                break;
            case VirtualKey.Back:
                // Explorer: Backspace = Back, but not while a type-ahead prefix is being typed (ui-spec §5).
                e.Handled = true;
                if (!_typeAhead.IsActive(Environment.TickCount64) && _vm?.BackCommand.CanExecute(null) == true)
                {
                    _vm.BackCommand.Execute(null);
                }

                break;
        }
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        long now = Environment.TickCount64;
        if (_vm?.Items.Current is not { } snapshot || char.IsControl(args.Character) || IsControlDown()
            || (args.Character == ' ' && !_typeAhead.IsActive(now)))
        {
            return; // a leading space stays with the list (selection), as in Explorer
        }

        int index = _typeAhead.Next(args.Character, now, snapshot.Visible, FocusedIndex());
        if (index >= 0)
        {
            SelectOnly([index], focus: true);
        }

        args.Handled = true;
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null || !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            return;
        }

        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        e.Handled = true;
        (int steps, _wheelDelta) = ListGeometry.AccumulateWheel(_wheelDelta, delta);
        var (mode, size) = (_vm.ViewMode, _vm.IconSize);
        for (int i = 0; i < Math.Abs(steps); i++)
        {
            (mode, size) = ListGeometry.StepView(mode, size, steps);
        }

        // The View menu's setters (PaneView.ApplyViewChoice): size first, then the mode, so one rebuild shows both.
        _vm.IconSize = size;
        _vm.SetView(mode);
    }

    // ---- rubber band (Icons/List) ------------------------------------------------------------------------------

    private void OnBandPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_list?.ItemsPanelRoot is not ItemsWrapGrid panel || _adapter is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(panel);
        var source = e.OriginalSource as DependencyObject;
        if (!point.Properties.IsLeftButtonPressed || Ancestor<SelectorItem>(source, _list) is not null || Ancestor<ScrollBar>(source, _list) is not null)
        {
            return;
        }

        _banding = true;
        _bandAdditive = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        _bandStart = point.Position;
        _bandLastInList = e.GetCurrentPoint(_list).Position;
        _bandRanges = [];
        StartBandTimer();
        if (!_bandAdditive && _adapter.Count > 0)
        {
            _list.DeselectRange(new ItemIndexRange(0, (uint)_adapter.Count)); // a click on empty space clears (Explorer)
        }

        _list.CapturePointer(e.Pointer);
        _list.Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void OnBandMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_banding || _list?.ItemsPanelRoot is not ItemsWrapGrid panel || _adapter is null)
        {
            return;
        }

        _bandLastInList = e.GetCurrentPoint(_list).Position;
        UpdateBand(panel, e.GetCurrentPoint(panel).Position);
        e.Handled = true;
    }

    // Draws the band and selects what it covers; selection work only when the covered ranges changed.
    private void UpdateBand(ItemsWrapGrid panel, Point current)
    {
        if (_list is null || _adapter is null)
        {
            return;
        }

        var band = new Rect(_bandStart, current);
        DrawBand(panel, band);
        var ranges = ListGeometry.HitRanges(band, new Size(panel.ItemWidth, panel.ItemHeight), new Size(panel.ActualWidth, panel.ActualHeight), _adapter.Count, _mode == ViewMode.Icons);
        if (ranges.SequenceEqual(_bandRanges))
        {
            return;
        }

        _bandRanges = ranges;
        if (!_bandAdditive)
        {
            _list.DeselectRange(new ItemIndexRange(0, (uint)_adapter.Count));
        }

        foreach (var (first, count) in ranges)
        {
            _list.SelectRange(new ItemIndexRange(first, (uint)count));
        }
    }

    // Explorer keeps scrolling while the pointer rests at an edge; PointerMoved alone would stop.
    private void StartBandTimer()
    {
        _bandTimer ??= CreateBandTimer();
        _bandTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateBandTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (!_banding || _list?.ItemsPanelRoot is not ItemsWrapGrid panel || !AutoScroll(_bandLastInList))
            {
                return;
            }

            // The panel moved under a resting pointer: recompute its panel position from the list position.
            var inPanel = _list.TransformToVisual(panel).TransformPoint(_bandLastInList);
            UpdateBand(panel, inPanel);
        };
        return timer;
    }

    private void OnBandReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_banding)
        {
            _list?.ReleasePointerCapture(e.Pointer);
            EndBand();
            e.Handled = true;
        }
    }

    private void EndBand()
    {
        _banding = false;
        _bandTimer?.Stop();
        _bandRanges = [];
        Band.Visibility = Visibility.Collapsed;
    }

    private void DrawBand(UIElement panel, Rect band)
    {
        var bounds = panel.TransformToVisual(BandCanvas).TransformBounds(band);
        double left = Math.Max(0, bounds.Left);
        double top = Math.Max(0, bounds.Top);
        double right = Math.Min(BandCanvas.ActualWidth, bounds.Right);
        double bottom = Math.Min(BandCanvas.ActualHeight, bounds.Bottom);
        Canvas.SetLeft(Band, left);
        Canvas.SetTop(Band, top);
        Band.Width = Math.Max(0, right - left);
        Band.Height = Math.Max(0, bottom - top);
        Band.Visibility = Visibility.Visible;
    }

    // Scrolls one step when the pointer is near an edge in the scroll direction; true when it scrolled.
    private bool AutoScroll(Point inList)
    {
        if (_scroller is null || _list is null)
        {
            return false;
        }

        if (_mode == ViewMode.Icons)
        {
            double dy = inList.Y < AutoScrollEdge ? -AutoScrollStep : inList.Y > _list.ActualHeight - AutoScrollEdge ? AutoScrollStep : 0;
            double target = Math.Clamp(_scroller.VerticalOffset + dy, 0, _scroller.ScrollableHeight);
            if (dy == 0 || target == _scroller.VerticalOffset)
            {
                return false;
            }

            _scroller.ChangeView(null, target, null, disableAnimation: true);
        }
        else
        {
            double dx = inList.X < AutoScrollEdge ? -AutoScrollStep : inList.X > _list.ActualWidth - AutoScrollEdge ? AutoScrollStep : 0;
            double target = Math.Clamp(_scroller.HorizontalOffset + dx, 0, _scroller.ScrollableWidth);
            if (dx == 0 || target == _scroller.HorizontalOffset)
            {
                return false;
            }

            _scroller.ChangeView(target, null, null, disableAnimation: true);
        }

        _list.UpdateLayout();
        return true;
    }

    // ---- slow second click → rename (owner #3) -----------------------------------------------------------------

    private void OnSlowClickPressed(object sender, PointerRoutedEventArgs e)
    {
        // A press that brought the focus into the list only activates it (Explorer); WinUI selects on release, so the
        // selection seen here is the one before this click.
        bool focusWasHere = HasFocusWithin && !_focusEnteredByPointer;
        _focusEnteredByPointer = false;
        bool plainLeft = e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed && e.KeyModifiers == VirtualKeyModifiers.None;
        ArmSlowClick(e.OriginalSource as DependencyObject, plainLeft && focusWasHere, Environment.TickCount64);
    }

    private void OnSlowClickReleased(object sender, PointerRoutedEventArgs e)
    {
        _focusEnteredByPointer = false;
        if (_slowClickName is null)
        {
            return;
        }

        if (_slowClickTimer is null)
        {
            _slowClickTimer = DispatcherQueue.CreateTimer();
            _slowClickTimer.IsRepeating = false;
            _slowClickTimer.Tick += (_, _) => RenameAfterSlowClick();
        }

        _slowClickTimer.Interval = TimeSpan.FromMilliseconds(_uiSettings.DoubleClickTime);
        _slowClickTimer.Start();
    }

    /// <summary>
    /// A press on <paramref name="source"/> (the pointer handler; tests): arms the slow-click rename when it is on the name of
    /// the only selected row and is not the second press of a double-click. Any press re-decides.
    /// </summary>
    /// <param name="source">The element under the pointer.</param>
    /// <param name="eligible">Plain left press, and the list had the focus before it.</param>
    /// <param name="nowTick">Press time (<see cref="Environment.TickCount64"/>).</param>
    /// <returns>True when armed.</returns>
    public bool ArmSlowClick(DependencyObject? source, bool eligible, long nowTick)
    {
        bool secondOfDouble = nowTick - _lastPressTick <= _uiSettings.DoubleClickTime;
        _lastPressTick = nowTick;
        CancelSlowClick();
        if (!eligible || secondOfDouble || _renaming is not null || _vm is null || _rows is null
            || Ancestor<SelectorItem>(source, _list) is not { } row || !_rows.TryGetItem(row, out var entry)
            || row.ContentTemplateRoot is not Grid root || !ReferenceEquals(RowParts.Of(root, _mode).Name, source))
        {
            return false;
        }

        var selection = _vm.Items.Selection;
        if (selection.Count != 1 || !selection.IsSelected(entry.Name))
        {
            return false;
        }

        _slowClickName = entry.Name;
        return true;
    }

    /// <summary>The double-click time passed after an armed click (the timer; tests): renames if that row is still the only selection.</summary>
    public void RenameAfterSlowClick()
    {
        string? name = _slowClickName;
        CancelSlowClick();
        if (name is not null && _renaming is null && HasFocusWithin && _vm?.Items.Selection is { Count: 1 } selection && selection.IsSelected(name))
        {
            BeginRename(name);
        }
    }

    /// <summary>Drops an armed slow-click rename (also when the window loses activation, so it never fires on return).</summary>
    public void CancelSlowClick()
    {
        _slowClickName = null;
        _slowClickTimer?.Stop();
    }

    // ---- inline rename (AC-1.20, AC-3.11) ----------------------------------------------------------------------

    /// <summary>Opens the inline editor on the entry at <paramref name="index"/> (F2, or a new folder).</summary>
    /// <param name="index">View index.</param>
    public void BeginRename(int index)
    {
        if (_list is null || _adapter?.EntryAt(index) is not { } entry)
        {
            return;
        }

        _list.ScrollIntoView(entry);
        _list.UpdateLayout();
        _renaming = entry;
        if (!PlaceRenameBox())
        {
            _renaming = null;
            return;
        }

        RenameBox.Text = entry.Name;
        RenameBox.Visibility = Visibility.Visible;
        EditCanvas.UpdateLayout();

        // Explorer's rename box has no clear (X) button. The template animates its visibility and the TextBox resets its Width
        // to the box height on layout (measured), so cap MaxWidth, which neither touches; MinWidth would win over it, so zero that too.
        if (Descendant<Button>(RenameBox, b => b.Name == "DeleteButton") is { } clear)
        {
            clear.MinWidth = 0;
            clear.MaxWidth = 0;
        }

        RenameBox.Focus(FocusState.Programmatic);

        // Explorer selects the name without its extension for files.
        int dot = entry.IsDirectory ? -1 : entry.Name.LastIndexOf('.');
        RenameBox.Select(0, dot > 0 ? dot : entry.Name.Length);
    }

    /// <summary>
    /// The context menu's target for a right-click on <paramref name="source"/>, adjusting the selection as Explorer does: a row
    /// outside the selection becomes the selection alone; blank space clears it and gives the folder background. From the
    /// keyboard (Shift+F10, menu key) without a focused row, an existing selection is kept.
    /// </summary>
    /// <param name="source">The element under the pointer, or the focused element for the keyboard.</param>
    /// <param name="keyboard">The request came from the keyboard.</param>
    /// <returns>The target, or null when the pane shows no folder.</returns>
    public ContextMenuTarget? PrepareContextMenu(DependencyObject? source, bool keyboard = false)
    {
        if (_vm?.CurrentPath is not { } folder || _list is null || _adapter is null)
        {
            return null;
        }

        if (Ancestor<SelectorItem>(source, _list) is { } row && _rows is not null && _rows.TryGetItem(row, out _))
        {
            int index = _list.IndexFromContainer(row);
            if (index >= 0 && !_adapter.IsSelected(index))
            {
                _list.DeselectRange(new ItemIndexRange(0, (uint)_adapter.Count));
                _list.SelectRange(new ItemIndexRange(index, 1));
            }
        }
        else if (!keyboard || _vm.Items.Selection.Count == 0)
        {
            if (_adapter.Count > 0)
            {
                _list.DeselectRange(new ItemIndexRange(0, (uint)_adapter.Count));
            }

            return new ContextMenuTarget(folder, []);
        }

        return new ContextMenuTarget(folder, [.. _vm.Items.Selection.SelectedEntries().Select(e => e.Name)]);
    }

    /// <summary>
    /// Whether a context menu request asks for the extended menu (ui-spec §5, QA M1): only Shift+right-click. Shift+F10 is a
    /// keyboard request and opens the normal menu, even though Shift is held for it.
    /// </summary>
    /// <param name="keyboard">The request came from the keyboard (Shift+F10, menu key).</param>
    /// <param name="shiftDown">Shift is held.</param>
    /// <returns>True for the extended menu.</returns>
    public static bool IsExtendedRequest(bool keyboard, bool shiftDown) => !keyboard && shiftDown;

    /// <summary>Starts the inline editor on the entry named <paramref name="name"/> (the native menu's Rename).</summary>
    /// <param name="name">Entry name in the current folder.</param>
    /// <returns>True when the editor opened.</returns>
    public bool BeginRename(string name)
    {
        int index = _vm?.Items.Current?.IndexOfName(name) ?? -1;
        if (index < 0)
        {
            return false;
        }

        BeginRename(index);
        return IsRenaming;
    }

    /// <summary>Commits the inline editor (Enter): an invalid name or a failed rename keeps it open.</summary>
    /// <returns>A task that completes when the rename finished.</returns>
    public Task CommitRenameAsync() => CommitRenameAsync(fromFocusLoss: false);

    /// <summary>Commits the inline editor as when the focus leaves it (another window opens): it closes either way, the status says why.</summary>
    /// <returns>A task that completes when the rename finished.</returns>
    public Task CommitRenameOnLeaveAsync() => CommitRenameAsync(fromFocusLoss: true);

    /// <summary>Closes the inline editor without renaming (Esc).</summary>
    public void CancelRename() => CloseRename(focusList: true);

    private int RenameTarget()
    {
        int focused = FocusedIndex();
        if (focused >= 0)
        {
            return focused;
        }

        var selected = _vm?.Items.Selection.SelectedEntries();
        return selected is { Count: > 0 } && _vm?.Items.Current is { } snapshot ? snapshot.IndexOfName(selected[0].Name) : -1;
    }

    // Puts the editor over the entry's name cell; false when the entry has no realized row.
    private bool PlaceRenameBox()
    {
        if (_renaming is not { } entry || _list is null || _vm?.Items.Current is not { } snapshot)
        {
            return false;
        }

        int index = snapshot.IndexOfName(entry.Name);
        if (index < 0 || _list.ContainerFromIndex(index) is not SelectorItem { ContentTemplateRoot: Grid root })
        {
            return false;
        }

        var name = RowParts.Of(root, _mode).Name;
        var bounds = name.TransformToVisual(EditCanvas).TransformBounds(new Rect(0, 0, name.ActualWidth, name.ActualHeight));
        if (bounds.Bottom <= 0 || bounds.Top >= EditCanvas.ActualHeight || bounds.Right <= 0 || bounds.Left >= EditCanvas.ActualWidth)
        {
            return false; // realized in the off-screen buffer but not visible: treat as scrolled away
        }

        double left = Math.Max(0, bounds.X - 4);
        Canvas.SetLeft(RenameBox, left);
        Canvas.SetTop(RenameBox, Math.Max(0, bounds.Y + (bounds.Height / 2) - 13));
        RenameBox.MaxWidth = Math.Max(RenameBox.MinWidth, EditCanvas.ActualWidth - left); // then the text scrolls
        return true;
    }

    private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                _ = CommitRenameAsync(fromFocusLoss: false);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                CloseRename(focusList: true);
                break;
        }
    }

    // AC-3.11: Explorer asks when a file's extension changes (folders and an unchanged extension never ask).
    private static bool ChangesExtension(FileEntry entry, string typed)
    {
        if (entry.IsDirectory)
        {
            return false;
        }

        // An invalid name is rejected first (its status), never asked about (reviewer m4, Explorer's order).
        return PathUtil.CleanName(typed) is { } name
            && !string.Equals(Path.GetExtension(entry.Name), Path.GetExtension(name), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> ShowExtensionDialogAsync()
    {
        if (XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Of(XamlRoot),
            Title = Localizer.Current["rename.extension.title"],
            Content = new TextBlock { Text = Localizer.Current["rename.extension.message"], TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Localizer.Current["rename.extension.yes"],
            CloseButtonText = Localizer.Current["rename.extension.no"],
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // Enter: an invalid name or a failed rename keeps the editor open (AC-3.11). Focus loss: it closes, the status says why.
    private async Task CommitRenameAsync(bool fromFocusLoss)
    {
        if (_renaming is not { } entry || _vm is null || _committing)
        {
            return;
        }

        _committing = true;
        try
        {
            if (ChangesExtension(entry, RenameBox.Text) && !await (ConfirmExtensionChange ?? ShowExtensionDialogAsync)())
            {
                // "No": Enter keeps editing; leaving the editor drops the change (the old name stays).
                if (fromFocusLoss)
                {
                    CloseRename(focusList: false);
                }
                else
                {
                    RenameBox.Focus(FocusState.Programmatic);
                    RenameBox.SelectAll();
                }

                return;
            }

            bool ok = await _vm.RenameAsync(entry, RenameBox.Text);
            if (ok || fromFocusLoss)
            {
                CloseRename(focusList: !fromFocusLoss);
            }
            else
            {
                RenameBox.Focus(FocusState.Programmatic);
                RenameBox.SelectAll();
            }
        }
        catch (ObjectDisposedException)
        {
            // Pane closed meanwhile.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Renaming {Name} failed", entry.Name);
            CloseRename(focusList: false);
        }
        finally
        {
            _committing = false;
        }
    }

    private void CloseRename(bool focusList)
    {
        if (_renaming is null)
        {
            return;
        }

        string name = _renaming.Name;
        _renaming = null;
        RenameBox.Visibility = Visibility.Collapsed;
        if (focusList && _list is not null && _vm?.Items.Current?.IndexOfName(name) is >= 0 and var index)
        {
            (_list.ContainerFromIndex(index) as Control)?.Focus(FocusState.Programmatic);
        }
    }

    // Named parts of a row template, looked up once per template root (stored in Tag).
    private sealed class RowParts
    {
        private RowParts(Grid iconBox, FontIcon placeholder, Image image, TextBlock name, TextBlock? date = null, TextBlock? type = null, TextBlock? size = null)
        {
            IconBox = iconBox;
            Placeholder = placeholder;
            Image = image;
            Name = name;
            Date = date;
            Type = type;
            Size = size;
        }

        public Grid IconBox { get; }

        public FontIcon Placeholder { get; }

        public Image Image { get; }

        public TextBlock Name { get; }

        public TextBlock? Date { get; }

        public TextBlock? Type { get; }

        public TextBlock? Size { get; }

        public static RowParts Of(Grid root, ViewMode mode)
        {
            if (root.Tag is RowParts cached)
            {
                return cached;
            }

            RowParts parts;
            if (mode == ViewMode.Details)
            {
                var nameCell = (Grid)root.Children[0];
                var box = (Grid)nameCell.Children[0];
                parts = new RowParts(box, (FontIcon)box.Children[0], (Image)box.Children[1], (TextBlock)nameCell.Children[1], (TextBlock)root.Children[1], (TextBlock)root.Children[2], (TextBlock)root.Children[3]);
            }
            else
            {
                var box = (Grid)root.Children[0];
                parts = new RowParts(box, (FontIcon)box.Children[0], (Image)box.Children[1], (TextBlock)root.Children[1]);
            }

            root.Tag = parts;
            return parts;
        }
    }
}
