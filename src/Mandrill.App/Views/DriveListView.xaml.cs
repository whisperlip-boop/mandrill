using System.Collections.Specialized;
using Mandrill.App.Platform;
using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.Platform;
using Mandrill.Core.Shell;
using Mandrill.Presentation.Common;
using Mandrill.Presentation.Panes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Mandrill.App.Views;

/// <summary>
/// The pane's This PC drive list (ADR-0015, PC3) over <see cref="DriveListViewModel"/>: Shell icon and name, type, total
/// and free space. Double-click or Enter opens a drive; right-click or the menu key asks for the drive's shell menu. No
/// drag, drop, rename or background menu. UI thread only.
/// </summary>
public sealed partial class DriveListView : UserControl
{
    private const double KeyboardMenuInset = 8;

    // QA S3-1: the file list's Details rule (FileListView NameMinWidth 120): fixed Type/Total/Free, Name takes the rest.
    private const double NameMinWidth = 120;
    private const double FixedColumnsWidth = 110 + 72 + 72 + (3 * 8) + (2 * 8); // Type, Total, Free, spacing, padding
    private readonly Microsoft.UI.Xaml.Media.TranslateTransform _headerShift = new();
    private ScrollViewer? _scroller;
    private double _nameWidth = NameMinWidth;
    private DriveListViewModel? _vm;
    private IIconService? _icons;
    private IconBitmapCache? _bitmaps;
    private Action<IReadOnlyList<IconKey>>? _iconsReady;
    private ILogger _logger = NullLogger.Instance;
    private string? _selectedRoot;

    /// <summary>Creates the view; call <see cref="Attach"/> before use.</summary>
    public DriveListView()
    {
        InitializeComponent();
        AppTheme.Track(Resources); // ExplorerItems.xaml scope
        List.ContainerContentChanging += (_, args) => Fill(args.ItemContainer, (DriveItem)args.Item);
        List.SelectionChanged += (_, _) => _selectedRoot = (List.SelectedItem as DriveItem)?.Root ?? _selectedRoot;
        List.DoubleTapped += (_, e) => e.Handled = Open(RowAt(e.OriginalSource as DependencyObject) is { } row ? List.ItemFromContainer(row) as DriveItem : null);
        List.KeyDown += OnKeyDown;
        List.ContextRequested += OnContextRequested;
        Header.RenderTransform = _headerShift;
        HeaderClip.SizeChanged += (_, e) =>
        {
            HeaderClipRect.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            ApplyNameWidth(e.NewSize.Width);
            FindScroller(); // cr-s3-2 m3: a view built collapsed (pane started in a folder) has no template at Loaded
        };
        List.Loaded += (_, _) => FindScroller();
    }

    /// <summary>Right-click or the menu key on a drive row: the pane runs the drive's shell menu (PaneContextMenus).</summary>
    public event EventHandler<PaneContextMenuRequest>? ContextMenuRequested;

    /// <summary>The list control (tests, focus).</summary>
    public ListView Rows => List;

    /// <summary>Binds the view to the pane's drive list.</summary>
    /// <param name="vm">The pane's drive list.</param>
    /// <param name="icons">Shell icons.</param>
    /// <param name="bitmaps">UI-thread icon bitmaps.</param>
    /// <param name="dispatcher">UI dispatcher (IconsReady arrives off the UI thread).</param>
    /// <param name="logger">Logger.</param>
    public void Attach(DriveListViewModel vm, IIconService icons, IconBitmapCache bitmaps, IUiDispatcher dispatcher, ILogger logger)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _bitmaps = bitmaps ?? throw new ArgumentNullException(nameof(bitmaps));
        ArgumentNullException.ThrowIfNull(dispatcher);
        _logger = logger ?? NullLogger.Instance;
        _iconsReady = _ => dispatcher.Post(RefillRealized, UiPriority.Low);
        icons.IconsReady += _iconsReady;
        vm.Items.CollectionChanged += OnItemsChanged;
        vm.SelectionRequested += OnSelectionRequested;
        List.ItemsSource = vm.Items;
    }

    /// <summary>Stops following the view model and the icon service (pane shutdown), as the file list and tree do.</summary>
    public void Detach()
    {
        if (_icons is not null && _iconsReady is not null)
        {
            _icons.IconsReady -= _iconsReady;
        }

        if (_vm is not null)
        {
            _vm.Items.CollectionChanged -= OnItemsChanged;
            _vm.SelectionRequested -= OnSelectionRequested;
        }

        _iconsReady = null;
        _vm = null;
        List.ItemsSource = null;
    }

    /// <summary>Focuses the selected row (or the first).</summary>
    public void FocusList()
    {
        if (List.SelectedIndex < 0 && List.Items.Count > 0)
        {
            List.SelectedIndex = 0;
        }

        if (!FocusSelected())
        {
            // Just shown (the pane entered This PC in this tick, QA S3-5): not laid out yet, so it refuses the focus. Once more
            // after layout.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, WinUiDispatcher.Guarded(() => FocusSelected(), _logger));
        }
    }

    private bool FocusSelected() => (List.ContainerFromIndex(Math.Max(0, List.SelectedIndex)) as Control ?? List).Focus(FocusState.Programmatic);

    /// <summary>Opens <paramref name="drive"/> in the pane (double-click, Enter; tests).</summary>
    /// <param name="drive">The drive row; null does nothing.</param>
    /// <returns>True when a drive was opened.</returns>
    public bool Open(DriveItem? drive)
    {
        if (_vm is not { } vm || drive is null)
        {
            return false;
        }

        _ = OpenAsync(vm, drive);
        return true;
    }

    private void OnSelectionRequested(string root) => Select(root, focus: true);

    private void FindScroller()
    {
        if (_scroller is null && FileListView.Descendant<ScrollViewer>(List) is { } scroller)
        {
            _scroller = scroller;
            _scroller.ViewChanged += (_, _) => _headerShift.X = -_scroller.HorizontalOffset; // the header scrolls with the rows
        }
    }

    // Name gets the width the fixed columns leave, never less than the file list's Name minimum (then the rows scroll).
    private void ApplyNameWidth(double viewport)
    {
        double width = Math.Max(NameMinWidth, Math.Floor(viewport - FixedColumnsWidth));
        if (width == _nameWidth)
        {
            return;
        }

        _nameWidth = width;
        Header.ColumnDefinitions[0].Width = new GridLength(width);
        RefillRealized();
    }

    // The row (container) an input event came from; null for the empty area below the rows.
    private SelectorItem? RowAt(DependencyObject? source)
    {
        for (DependencyObject? d = source; d is not null && d != List; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is SelectorItem item)
            {
                return item;
            }
        }

        return null;
    }

    private static string? Size(long? bytes) => bytes is { } b ? SizeFormat.Bytes(b) : null;

    // A row's UIA Name (Narrator): name, type and the sizes once known.
    private static string RowName(DriveItem d) => d.TotalBytes is { } total && d.FreeBytes is { } free
        ? Localizer.Current.Format("thispc.item.name", ("name", d.Name), ("type", d.Type), ("free", SizeFormat.Bytes(free)), ("total", SizeFormat.Bytes(total)))
        : Localizer.Current.Format("thispc.item.nameNoSpace", ("name", d.Name), ("type", d.Type));

    private async Task OpenAsync(DriveListViewModel vm, DriveItem drive)
    {
        try
        {
            await vm.OpenAsync(drive);
        }
        catch (ObjectDisposedException)
        {
            // Pane closed meanwhile.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening {Root} failed", drive.Root);
        }
    }

    private void Fill(SelectorItem container, DriveItem drive)
    {
        if (container.ContentTemplateRoot is not Grid root)
        {
            return;
        }

        root.ColumnDefinitions[0].Width = new GridLength(_nameWidth);
        var nameCell = (Grid)root.Children[0];
        ((TextBlock)nameCell.Children[1]).Text = drive.Name;
        ((TextBlock)root.Children[1]).Text = drive.Type;
        ((TextBlock)root.Children[2]).Text = Size(drive.TotalBytes) ?? string.Empty;
        ((TextBlock)root.Children[3]).Text = Size(drive.FreeBytes) ?? string.Empty;
        AutomationProperties.SetName(container, RowName(drive));
        ((Image)nameCell.Children[0]).Source = IconOf(drive);
    }

    // The tree's icon: the Shell icon by path (autorun.inf icons), the stock drive icon of its kind while it loads.
    private Microsoft.UI.Xaml.Media.ImageSource? IconOf(DriveItem drive)
    {
        if (_icons is null || _bitmaps is null)
        {
            return null;
        }

        IconKey key = _icons.KeyForFolder(drive.Root, perItem: false, IconSize.Small);
        if (_icons.TryGet(key, out IconImage? image) && image is not null)
        {
            return _bitmaps.Get(key, image);
        }

        var placeholder = new IconKey(drive.Kind == DriveKind.Removable ? IconKey.RemovableDriveExtId : IconKey.OtherDriveExtId, null, key.Size);
        return _icons.TryGet(placeholder, out IconImage? stock) && stock is not null ? _bitmaps.Get(placeholder, stock) : null;
    }

    private void RefillRealized()
    {
        if (_vm is null)
        {
            return;
        }

        for (int i = 0; i < _vm.Items.Count; i++)
        {
            if (List.ContainerFromIndex(i) is SelectorItem container)
            {
                Fill(container, _vm.Items[i]);
            }
        }
    }

    // A row replaced by its sizes (or a reload) keeps the selection the user had.
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // QA S3-6: read the root when the queued work runs: an Up from another drive sets it after this reload (otherwise
        // the drive selected before would win again).
        DispatcherQueue.TryEnqueue(WinUiDispatcher.Guarded(
            () =>
            {
                if (_selectedRoot is { } root)
                {
                    Select(root, focus: false);
                }
            },
            _logger));
    }

    private void Select(string root, bool focus)
    {
        if (_vm is null)
        {
            return;
        }

        int index = -1;
        for (int i = 0; i < _vm.Items.Count && index < 0; i++)
        {
            index = PathUtil.PathEquals(_vm.Items[i].Root, root) ? i : -1;
        }

        if (index < 0)
        {
            return;
        }

        _selectedRoot = root;
        if (List.SelectedIndex != index)
        {
            List.SelectedIndex = index;
        }

        if (focus)
        {
            List.ScrollIntoView(_vm.Items[index]);
            (List.ContainerFromIndex(index) as Control ?? List).Focus(FocusState.Programmatic);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && Open(List.SelectedItem as DriveItem))
        {
            e.Handled = true;
        }
    }

    // Right-click, Shift+F10 or the menu key: the row under the pointer (or the selected one) gets the drive's shell menu.
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        bool keyboard = !e.TryGetPosition(List, out Point inList);
        SelectorItem? row = RowAt(e.OriginalSource as DependencyObject);

        if ((row is not null ? List.ItemFromContainer(row) : List.SelectedItem) is not DriveItem drive)
        {
            return; // no background menu at This PC
        }

        e.Handled = true;
        Select(drive.Root, focus: false);
        if (keyboard)
        {
            UIElement anchor = row ?? (List.ContainerFromItem(drive) as UIElement) ?? List;
            inList = anchor.TransformToVisual(List).TransformPoint(new Point(KeyboardMenuInset, anchor == List ? KeyboardMenuInset : anchor.ActualSize.Y));
        }

        Point inRoot = List.TransformToVisual(null).TransformPoint(inList);
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        var target = new ContextMenuTarget(PathUtil.ThisPc, [drive.Root]);
        bool extended = FileListView.IsExtendedRequest(keyboard, FileListView.IsShiftDown());
        ContextMenuRequested?.Invoke(this, new PaneContextMenuRequest(target, List, inList, (int)Math.Round(inRoot.X * scale), (int)Math.Round(inRoot.Y * scale), extended));
    }
}
