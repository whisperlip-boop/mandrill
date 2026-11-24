using System.ComponentModel;
using System.Globalization;
using Mandrill.App.Platform;
using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.Platform;
using Mandrill.Core.Search;
using Mandrill.Core.Shell;
using Mandrill.Presentation.Common;
using Mandrill.Presentation.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Mandrill.App.Views;

/// <summary>
/// The search popup's content (ui-spec §3, architecture §3 WP-2.2b) over a <see cref="SearchViewModel"/>: query row
/// (Enter searches, the button reads "Stop" while running), scope line, indexing strip, a virtualized 5-column result
/// list with shell icons (<see cref="RowIconBinder{TItem}"/>), header sort, context menu and footer. Keyboard: Enter on a
/// row opens it, Esc stops a running search and otherwise asks to close, Down moves from the query to the results,
/// Ctrl+F focuses the query, Alt+S searches, Ctrl+Shift+C copies the selected paths quoted (Explorer's "Copy as path",
/// WP-3.6). Hosted by SearchWindow; App.Tests host it in the off-screen window.
/// </summary>
public sealed partial class SearchView : UserControl
{
    private const double ColumnMinWidth = 40;
    private const string FolderGlyph = "";
    private const string FileGlyph = "";
    private const string SortUpGlyph = "";
    private const string SortDownGlyph = "";

    private static readonly SearchColumn[] HeaderColumns = [SearchColumn.Name, SearchColumn.Extension, SearchColumn.Path, SearchColumn.Size, SearchColumn.Modified];
    private static readonly string[] HeaderKeys = ["search.column.name", "search.column.extension", "search.column.path", "search.column.size", "search.column.modified"];

    // Resizable, not persisted. They sum to 710 so all five headers and a full date-time fit the default 760 px popup
    // (744 client - 16 padding, minus a scrollbar) in ko and en (owner round 6 #6; ui-spec §3 had 240/70/300/90/150).
    private static readonly double[] DefaultWidths = [190, 80, 200, 90, 150];
    private readonly double[] _widths = [.. DefaultWidths];
    private readonly TextBlock[] _arrows = new TextBlock[5];
    private readonly ColumnGripper[] _grippers = new ColumnGripper[5];
    private readonly TranslateTransform _headerShift = new();
    private SearchViewModel? _vm;
    private IIconService? _icons;
    private RowIconBinder<SearchHit>? _rows;
    private SearchListAdapter? _adapter;
    private ILogger _logger = NullLogger.Instance;
    private Action<IReadOnlyList<IconKey>>? _iconsReadyHandler;
    private EventHandler? _canSearchChangedHandler;
    private ScrollViewer? _scroller;
    private SelectorItem? _contextRow;
    private double _dragStartWidth;
    private bool _syncingQuery;
    private bool _selectionPending;
    private IUiDispatcher? _dispatcher;

    /// <summary>Creates the view; call <see cref="Attach"/> before use.</summary>
    public SearchView()
    {
        InitializeComponent();
        AppTheme.Track(Resources); // ExplorerItems.xaml scope
        Header.RenderTransform = _headerShift;
        HeaderClip.SizeChanged += (_, e) => HeaderClipRect.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        FileListView.ClipToBounds(Results);
        BuildHeader();
        ApplyColumns();

        QueryBox.AddHandler(KeyDownEvent, new KeyEventHandler(OnQueryKeyDown), handledEventsToo: true);
        QueryBox.TextChanged += (_, _) =>
        {
            if (!_syncingQuery && _vm is not null)
            {
                _vm.QueryText = QueryBox.Text;
            }
        };
        SearchButton.Click += (_, _) => SearchOrStop();
        RefreshButton.Click += (_, _) => _vm?.RefreshCommand.Execute(null);
        RetryLink.Click += (_, _) => _vm?.RefreshCommand.Execute(null);
        Results.ContainerContentChanging += OnContainerContentChanging;
        Results.DoubleTapped += (_, e) =>
        {
            if (OpenRowAt(e.OriginalSource as DependencyObject))
            {
                e.Handled = true;
            }
        };
        // handledEventsToo: ListViewBase marks Enter handled itself, as in the file list.
        Results.AddHandler(KeyDownEvent, new KeyEventHandler(OnResultsKeyDown), handledEventsToo: true);
        Results.ContextRequested += OnResultsContextRequested;
        Results.DragItemsStarting += OnDragItemsStarting;
        Results.Loaded += (_, _) =>
        {
            if (_scroller is null && FileListView.Descendant<ScrollViewer>(Results) is { } scroller)
            {
                _scroller = scroller;
                _scroller.ViewChanged += (_, _) => _headerShift.X = -_scroller.HorizontalOffset;
                FileListView.ReserveHorizontalBar(_scroller);
            }
        };
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, FocusQuery);
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Menu, SearchOrStop);
        AddAccelerator(VirtualKey.C, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, CopySelectedPaths);
    }

    /// <summary>Raised when Esc is pressed while no search runs (the window closes, ui-spec §3 "second Esc closes").</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Raised for a right-click, Shift+F10 or the menu key on a result row (WP-3.5, AC-3.5): the popup's
    /// SearchContextMenus shows the native menu or the fallback. Blank space under the rows opens no menu.
    /// </summary>
    public event EventHandler<SearchContextMenuRequest>? ContextMenuRequested;

    /// <summary>The query box (tests).</summary>
    public TextBox Query => QueryBox;

    /// <summary>The result list (tests).</summary>
    public ListView ResultList => Results;

    /// <summary>The footer text (tests).</summary>
    public string FooterTextValue => FooterText.Text;

    /// <summary>The selection summary on the right of the footer, "" with no selection (tests).</summary>
    public string SelectionSummaryValue => SelectionSummary.Text;

    /// <summary>The search button's text: "Search" or "Stop" (tests).</summary>
    public string SearchButtonText => SearchButton.Content as string ?? string.Empty;

    /// <summary>Whether the search button can be clicked (tests).</summary>
    public bool IsSearchButtonEnabled => SearchButton.IsEnabled;

    /// <summary>The grey text centred over the list, "" when hidden (tests).</summary>
    public string CentreTextValue => CentreText.Visibility == Visibility.Visible ? CentreText.Text : string.Empty;

    /// <summary>The scope line (tests).</summary>
    public string ScopeTextValue => ScopeText.Text;

    /// <summary>Whether the Retry link shows (tests).</summary>
    public bool IsRetryVisible => RetryLink.Visibility == Visibility.Visible;

    /// <summary>Whether the yellow indexing strip shows (tests).</summary>
    public bool IsIndexingStripVisible => IndexingStrip.Visibility == Visibility.Visible;

    /// <summary>The header dividers, one per column (tests).</summary>
    public IReadOnlyList<ColumnGripper> ColumnGrippers => _grippers;

    /// <summary>The list's source with its selection (tests).</summary>
    public SearchListAdapter? Adapter => _adapter;

    /// <summary>Binds the view to a pane's search.</summary>
    /// <param name="vm">The search view model.</param>
    /// <param name="icons">Shell icons.</param>
    /// <param name="bitmaps">UI-thread icon bitmaps.</param>
    /// <param name="dispatcher">UI dispatcher (IconsReady arrives off the UI thread).</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="InvalidOperationException">Already attached.</exception>
    public void Attach(SearchViewModel vm, IIconService icons, IconBitmapCache bitmaps, IUiDispatcher dispatcher, ILogger logger)
    {
        if (_vm is not null)
        {
            throw new InvalidOperationException("The search view is already attached");
        }

        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(bitmaps);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _vm = vm;
        _icons = icons;
        _logger = logger ?? NullLogger.Instance;
        _rows = new RowIconBinder<SearchHit>(icons, bitmaps);
        _iconsReadyHandler = keys => dispatcher.Post(() => OnIconsReady(keys), UiPriority.Low);
        icons.IconsReady += _iconsReadyHandler;
        vm.PropertyChanged += OnViewModelChanged;
        _canSearchChangedHandler = (_, _) => SyncButtons();
        vm.SearchCommand.CanExecuteChanged += _canSearchChangedHandler;
        _dispatcher = dispatcher;
        _adapter = new SearchListAdapter(vm.Results);
        _adapter.SelectionChanged += ScheduleSelectionSummary;
        vm.SelectionStatus.Changed += ScheduleSelectionSummary; // the single item's info tip arrived (WP3)
        Results.ItemsSource = _adapter;
        SyncQuery();
        SyncAll();
    }

    private void ScheduleSelectionSummary()
    {
        if (!_selectionPending && _dispatcher is { } dispatcher)
        {
            _selectionPending = true; // a Ctrl+A or Shift range is one update, as the pane's ScheduleCount
            dispatcher.Post(UpdateSelectionSummary, UiPriority.Low);
        }
    }

    private void UpdateSelectionSummary()
    {
        _selectionPending = false;
        if (_vm is not { } vm)
        {
            return;
        }

        int count = _adapter?.SelectedCount ?? 0;
        string? single = count == 1 && _adapter!.SelectedHits() is [var hit] ? Path.Combine(hit.Directory, hit.Name) : null;
        vm.SelectionStatus.Update(count, count > 0 ? _adapter!.SelectedBytes() : 0, single, vm.Scope); // the scope's drive (WP4)
        SelectionSummary.Text = vm.SelectionStatus.Summary;
        SelectionTip.Text = vm.SelectionStatus.Tip.Length == 0 ? string.Empty : "| " + vm.SelectionStatus.Tip;
        TipColumn.Width = SelectionTip.Text.Length == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
    }

    /// <summary>Unbinds the list and unsubscribes from the long-lived icon service and the view model (window closed).</summary>
    public void Detach()
    {
        if (_icons is not null && _iconsReadyHandler is not null)
        {
            _icons.IconsReady -= _iconsReadyHandler;
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.SelectionStatus.Changed -= ScheduleSelectionSummary;
            if (_canSearchChangedHandler is not null)
            {
                _vm.SearchCommand.CanExecuteChanged -= _canSearchChangedHandler;
            }
        }

        Results.ItemsSource = null;
        _adapter?.Dispose();
        _adapter = null;
        _iconsReadyHandler = null;
        _canSearchChangedHandler = null;
        _rows?.Clear();
    }

    /// <summary>Focuses the query box with its text selected (opening the popup, Ctrl+F).</summary>
    public void FocusQuery()
    {
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();
    }

    /// <summary>Enter in the query box: searches (an empty query does nothing, AC-2.14).</summary>
    public void SubmitQuery()
    {
        // TextChanged is raised asynchronously: a paste followed by Enter must not search the previous text.
        if (_vm is not null && _vm.QueryText != QueryBox.Text)
        {
            _vm.QueryText = QueryBox.Text;
        }

        if (_vm?.SearchCommand.CanExecute(null) == true)
        {
            _vm.SearchCommand.Execute(null);
        }
    }

    /// <summary>Esc: stops a running search, otherwise raises <see cref="CloseRequested"/>.</summary>
    /// <returns>True when a search was stopped.</returns>
    public bool Escape()
    {
        if (_vm?.HandleEscape() == true)
        {
            return true;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        return false;
    }

    /// <summary>Opens the result of the row that contains <paramref name="source"/>: the double-click handler (tests).</summary>
    /// <param name="source">The element that was double-tapped.</param>
    /// <returns>True when a row was opened.</returns>
    public bool OpenRowAt(DependencyObject? source)
    {
        if (_vm is not null && Ancestor<SelectorItem>(source, Results) is { } row && _rows is not null && _rows.TryGetItem(row, out var hit))
        {
            _ = RunSafeAsync(_vm.OpenAsync(hit), "Opening a search result");
            return true;
        }

        return false;
    }

    /// <summary>Enter on the results: opens the focused row (one result, ui-spec §3).</summary>
    /// <returns>A task that completes when the open was handed off.</returns>
    public Task OpenFocusedAsync() => FocusedHit() is { } hit && _vm is not null ? _vm.OpenAsync(hit) : Task.CompletedTask;

    /// <summary>Ctrl+Shift+C (WP-3.6, lead Q5): Explorer's "Copy as path" of the selected results, every path quoted.</summary>
    public void CopySelectedPaths()
    {
        if (_vm is not null)
        {
            _vm.CopyAsPath([.. SelectedHits()]);
        }
    }

    /// <summary>
    /// The context menu opens on the row that contains <paramref name="source"/>: a row outside the selection becomes the
    /// selection alone (as in Explorer), so Open, Open containing folder and Copy path act on what the user sees (QA B1).
    /// </summary>
    /// <param name="source">The element that was right-clicked (or had focus for Shift+F10 / the menu key).</param>
    /// <returns>False on blank space (no row): no menu opens there.</returns>
    public bool PrepareContextMenu(DependencyObject? source)
    {
        _contextRow = Ancestor<SelectorItem>(source, Results);
        if (_contextRow is null || _adapter is not { } adapter)
        {
            return false;
        }

        int index = Results.IndexFromContainer(_contextRow);
        if (index >= 0 && !adapter.IsSelected(index))
        {
            if (adapter.Count > 0)
            {
                Results.DeselectRange(new ItemIndexRange(0, (uint)adapter.Count));
            }

            Results.SelectRange(new ItemIndexRange(index, 1));
        }

        return true;
    }

    /// <summary>
    /// The menu request for a context menu opened on <paramref name="source"/> (<see cref="PrepareContextMenu"/> first): the
    /// selected results and the row it was opened on, placed at <paramref name="pointInList"/>, or for the keyboard (null)
    /// near the row's lower left corner like Explorer. The ContextRequested handler and tests.
    /// </summary>
    /// <param name="source">The element that was right-clicked (or had focus for Shift+F10 / the menu key).</param>
    /// <param name="pointInList">The pointer in the result list; null for a keyboard request.</param>
    /// <param name="shiftDown">Shift is held (Shift+right-click: extended verbs; Shift+F10 is a keyboard request).</param>
    /// <returns>The request, or null on blank space.</returns>
    public SearchContextMenuRequest? PrepareContextMenuRequest(DependencyObject? source, Point? pointInList, bool shiftDown)
    {
        if (!PrepareContextMenu(source) || _vm is null || ContextHit() is not { } focused)
        {
            return null;
        }

        bool keyboard = pointInList is null;
        Point inList = pointInList ?? default;
        if (keyboard)
        {
            UIElement anchor = _contextRow is { } row ? row : Results;
            inList = anchor.TransformToVisual(Results).TransformPoint(
                new Point(FileListView.KeyboardMenuInset, anchor == Results ? FileListView.KeyboardMenuInset : anchor.ActualSize.Y));
        }

        Point inRoot = Results.TransformToVisual(null).TransformPoint(inList);
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        return new SearchContextMenuRequest(
            [.. SelectedHits()],
            focused,
            _vm.SearchId,
            Results,
            inList,
            (int)Math.Round(inRoot.X * scale),
            (int)Math.Round(inRoot.Y * scale),
            FileListView.IsExtendedRequest(keyboard, shiftDown));
    }

    /// <summary>
    /// The paths a drag out of the results carries (AC-3.20, the panes' rule, qa-wp34 M2): the selection when every dragged
    /// row is selected; a dragged row outside the selection becomes the selection alone and only it is dragged. Results
    /// from several folders drag together.
    /// </summary>
    /// <param name="dragged">The rows the list reports as dragged.</param>
    /// <returns>Full paths, in list order; empty when nothing can be dragged.</returns>
    public IReadOnlyList<string> PrepareDragOut(IReadOnlyList<SearchHit> dragged)
    {
        ArgumentNullException.ThrowIfNull(dragged);
        if (_adapter is not { } adapter || _vm is null)
        {
            return [];
        }

        // A set of the selection (reviewer-wp35 m2): the same "first dragged row outside the selection" test as the pane,
        // without a scan of the rows per dragged item.
        var selected = adapter.SelectedHits();
        var selectedSet = new HashSet<SearchHit>(selected);
        if (dragged.FirstOrDefault(h => !selectedSet.Contains(h)) is { } pressed)
        {
            int index = _vm.Results.IndexOf(pressed);
            if (index < 0)
            {
                return [];
            }

            if (adapter.Count > 0)
            {
                Results.DeselectRange(new ItemIndexRange(0, (uint)adapter.Count));
            }

            Results.SelectRange(new ItemIndexRange(index, 1));
            return [PathUtil.Combine(pressed.Directory, pressed.Name)];
        }

        return [.. selected.Select(h => PathUtil.Combine(h.Directory, h.Name))];
    }

    /// <summary>Header click on column <paramref name="index"/> (tests call it like the header does).</summary>
    /// <param name="index">Column index (Filename, Extension, Path, Size, Date modified).</param>
    /// <returns>A task that completes when the sorted rows are shown.</returns>
    public Task SortByColumnAsync(int index) => _vm is null ? Task.CompletedTask : _vm.SortByAsync(HeaderColumns[index]);

    private static T? Ancestor<T>(DependencyObject? start, DependencyObject? stopAt)
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

    // A search hit as a listed entry for the icon service (folders get the generic folder icon: hits carry no attributes).
    // The icon's extension follows the Extension cell's rule (QA B2): no extension there, no type icon here.
    private static FileEntry EntryOf(SearchHit hit)
        => new(
            hit.Name,
            hit.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal,
            hit.Size,
            hit.Modified.Ticks,
            SearchResults.ExtensionOf(hit).IsEmpty ? ExtensionTable.None : ExtensionTable.Shared.IdOf(hit.Name));

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers, ScopeOwner = this };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private void SearchOrStop()
    {
        if (_vm is null)
        {
            return;
        }

        if (_vm.IsRunning)
        {
            _vm.StopCommand.Execute(null);
        }
        else
        {
            SubmitQuery();
        }
    }

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when !e.KeyStatus.IsMenuKeyDown: // Alt+Enter is never Enter (WP-3.6, as in the file list)
                e.Handled = true;
                SubmitQuery();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                Escape();
                break;
            case VirtualKey.Down when _adapter is { Count: > 0 } adapter:
                e.Handled = true;
                MoveFocusToResults(adapter);
                break;
        }
    }

    private void OnResultsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when !e.KeyStatus.IsMenuKeyDown: // Alt+Enter does not open a result (WP-3.6)
                e.Handled = true;
                _ = RunSafeAsync(OpenFocusedAsync(), "Opening a search result");
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                Escape();
                break;
        }
    }

    // Down from the query box: the first selected row (row 0 when nothing is selected) gets the keyboard focus. The
    // selection goes through Select/DeselectRange so the adapter stays its only owner (as in FileListView).
    private void MoveFocusToResults(SearchListAdapter adapter)
    {
        if (adapter.SelectedCount == 0)
        {
            Results.SelectRange(new ItemIndexRange(0, 1));
        }

        int first = adapter.GetSelectedRanges() is [var range, ..] ? range.FirstIndex : 0;
        Results.ScrollIntoView(adapter[first]);
        Results.UpdateLayout();
        (Results.ContainerFromIndex(first) as Control)?.Focus(FocusState.Keyboard);
    }

    // The right-clicked row (made the selection by PrepareContextMenu), else the focused one.
    private SearchHit? ContextHit()
        => _contextRow is { } row && _rows is not null && _rows.TryGetItem(row, out var hit) ? hit : FocusedHit();

    private void OnResultsContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        // Owner rule (WP-3.1, Explorer): blank space under the rows gets no item menu.
        e.Handled = true;
        Point? at = e.TryGetPosition(Results, out Point inList) ? inList : null;
        if (PrepareContextMenuRequest(e.OriginalSource as DependencyObject, at, FileListView.IsShiftDown()) is { } request)
        {
            ContextMenuRequested?.Invoke(this, request);
        }
    }

    // Drag out (AC-3.20): the paths are read from the adapter's ranges, e.Items only for the dragged row (S2/S3).
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = PrepareDragOut([.. e.Items.OfType<SearchHit>()]);
        if (paths.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        DragOutData.Fill(e.Data, paths, _logger);
    }

    private SearchHit? FocusedHit()
    {
        if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is SelectorItem row && _rows is not null && _rows.TryGetItem(row, out var hit))
        {
            return hit;
        }

        return _adapter?.SelectedHits() is [var first, ..] ? first : null;
    }

    private IEnumerable<SearchHit> SelectedHits()
    {
        // The adapter's ranges, never ListView.SelectedItems (reads every item at 100k rows; banned).
        var selected = new List<SearchHit>(_adapter?.SelectedHits() ?? []);
        if (selected.Count == 0 && FocusedHit() is { } focused)
        {
            selected.Add(focused);
        }

        return selected;
    }

    private async Task RunSafeAsync(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{What} failed", what);
        }
    }

    // ---- rows -------------------------------------------------------------------------------------------------------

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (_rows is null)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            _rows.Forget(args.ItemContainer);
            return;
        }

        if (args.Item is SearchHit hit && args.ItemContainer.ContentTemplateRoot is Grid root)
        {
            _rows.Track(args.ItemContainer, hit);
            Fill(args.ItemContainer, root, hit);
            args.Handled = true;
        }
    }

    private void Fill(SelectorItem row, Grid root, SearchHit hit)
    {
        var parts = RowParts.Of(root);
        parts.Name.Text = hit.Name;
        AutomationProperties.SetName(row, hit.Name); // else UIA reads SearchHit.ToString() (record dump)
        parts.Extension.Text = SearchResults.ExtensionOf(hit).ToString();
        parts.Path.Text = hit.Directory;
        parts.Size.Text = hit.IsDirectory ? string.Empty : SizeFormat.Kb(hit.Size);
        parts.Modified.Text = hit.Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        ApplyColumns(root);
        SetIcon(row, parts, hit);
    }

    private void SetIcon(SelectorItem row, RowParts parts, SearchHit hit)
    {
        if (_rows?.Resolve(row, EntryOf(hit), hit.Directory, IconSize.Small) is { } bitmap)
        {
            parts.Image.Source = bitmap;
            parts.Placeholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            parts.Image.Source = null; // same-size placeholder, no layout shift
            parts.Placeholder.Glyph = hit.IsDirectory ? FolderGlyph : FileGlyph;
            parts.Placeholder.Visibility = Visibility.Visible;
        }
    }

    private void OnIconsReady(IReadOnlyList<IconKey> keys)
    {
        if (_rows is null)
        {
            return;
        }

        foreach (var row in _rows.RowsWaitingFor(keys))
        {
            if (_rows.TryGetItem(row, out var hit) && row.ContentTemplateRoot is Grid root)
            {
                SetIcon(row, RowParts.Of(root), hit);
            }
        }
    }

    // ---- header and columns -------------------------------------------------------------------------------------------

    private void BuildHeader()
    {
        for (int i = 0; i < HeaderColumns.Length; i++)
        {
            Header.ColumnDefinitions.Add(new ColumnDefinition());
            int index = i;
            var cell = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new Thickness(i == 0 ? 28 : 4, 0, 10, 0) };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = Localizer.Current[HeaderKeys[i]],
                FontFamily = (FontFamily)Application.Current.Resources["MandrillListFontFamily"], // Explorer's item font (App.xaml)
                FontSize = (double)Application.Current.Resources["MandrillListFontSize"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HeaderColumns[i] == SearchColumn.Size ? HorizontalAlignment.Right : HorizontalAlignment.Left,
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
            _arrows[i] = arrow;
            cell.Tapped += (_, _) => _ = RunSafeAsync(SortByColumnAsync(index), "Sorting search results");
            AutomationProperties.SetName(cell, label.Text);

            var gripper = new ColumnGripper((Style)Resources["ColumnDividerStyle"]);
            gripper.DragStarted += () => _dragStartWidth = _widths[index];
            gripper.Resized += offset => ResizeColumn(index, _dragStartWidth + offset);
            gripper.AutoFitRequested += () => ResizeColumn(index, DefaultWidths[index]);
            _grippers[i] = gripper;

            var host = new Grid();
            host.Children.Add(cell);
            host.Children.Add(gripper);
            Grid.SetColumn(host, i);
            Canvas.SetZIndex(host, HeaderColumns.Length - i); // the grab zone's half over the next column stays on top
            Header.Children.Add(host);
        }
    }

    private void ResizeColumn(int index, double requested)
    {
        _widths[index] = Math.Max(ColumnMinWidth, requested);
        ApplyColumns();
    }

    private void ApplyColumns()
    {
        for (int i = 0; i < _widths.Length; i++)
        {
            Header.ColumnDefinitions[i].Width = new GridLength(_widths[i]);
        }

        Header.Width = _widths.Sum();
        if (Results.ItemsPanelRoot is { } panel)
        {
            foreach (var row in panel.Children.OfType<SelectorItem>())
            {
                if (row.ContentTemplateRoot is Grid root)
                {
                    ApplyColumns(root);
                }
            }
        }
    }

    private void ApplyColumns(Grid root)
    {
        for (int i = 0; i < _widths.Length; i++)
        {
            root.ColumnDefinitions[i].Width = new GridLength(_widths[i]);
        }

        root.Width = _widths.Sum();
    }

    // ---- view-model state -----------------------------------------------------------------------------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.QueryText))
        {
            SyncQuery();
            return;
        }

        SyncAll();
    }

    private void SyncQuery()
    {
        if (_vm is not null && QueryBox.Text != _vm.QueryText)
        {
            _syncingQuery = true;
            try
            {
                QueryBox.Text = _vm.QueryText;
            }
            finally
            {
                _syncingQuery = false;
            }
        }
    }

    private void SyncAll()
    {
        if (_vm is null)
        {
            return;
        }

        ScopeText.Text = _vm.AtThisPc ? Localizer.Current["search.scope.thisPc"] : SearchText.Scope(_vm.DisplayScope);
        CentreText.Text = _vm.AtThisPc ? Localizer.Current["search.scope.thisPc"] : SearchText.Centre(_vm.State, _vm.SearchedText); // S3-3
        CentreText.Visibility = CentreText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        IndexingStrip.Visibility = _vm.ShowIndexingNotice ? Visibility.Visible : Visibility.Collapsed;
        Busy.Visibility = _vm.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        RetryLink.Visibility = _vm.State == SearchState.Error ? Visibility.Visible : Visibility.Collapsed;
        FooterText.Text = SearchText.Footer(_vm);
        for (int i = 0; i < HeaderColumns.Length; i++)
        {
            _arrows[i].Text = HeaderColumns[i] == _vm.SortColumn ? (_vm.SortDescending ? SortDownGlyph : SortUpGlyph) : string.Empty;
        }

        SyncButtons();
    }

    private void SyncButtons()
    {
        if (_vm is null)
        {
            return;
        }

        SearchButton.Content = Localizer.Current[_vm.IsRunning ? "search.stop" : "search.search"];
        SearchButton.IsEnabled = _vm.IsRunning || _vm.SearchCommand.CanExecute(null);
        RefreshButton.IsEnabled = !_vm.IsRunning && _vm.RefreshCommand.CanExecute(null);
    }

    // Named parts of a result row, looked up once per template root (stored in Tag).
    private sealed class RowParts
    {
        private RowParts(Grid root)
        {
            var first = (Grid)root.Children[0];
            var iconBox = (Grid)first.Children[0];
            Placeholder = (FontIcon)iconBox.Children[0];
            Image = (Image)iconBox.Children[1];
            Name = (TextBlock)first.Children[1];
            Extension = (TextBlock)root.Children[1];
            Path = (TextBlock)root.Children[2];
            Size = (TextBlock)root.Children[3];
            Modified = (TextBlock)root.Children[4];
        }

        public FontIcon Placeholder { get; }

        public Image Image { get; }

        public TextBlock Name { get; }

        public TextBlock Extension { get; }

        public TextBlock Path { get; }

        public TextBlock Size { get; }

        public TextBlock Modified { get; }

        public static RowParts Of(Grid root)
        {
            if (root.Tag is RowParts cached)
            {
                return cached;
            }

            var parts = new RowParts(root);
            root.Tag = parts;
            return parts;
        }
    }
}
