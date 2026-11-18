using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Mandrill.App.Platform;
using Mandrill.Core.IO;
using Mandrill.Core.Platform;
using Mandrill.Core.Shell;
using Mandrill.Presentation.Common;
using Mandrill.Presentation.Tree;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Mandrill.App.Views;

/// <summary>
/// WinUI TreeView over one pane's <see cref="TreeViewModel"/> (ui-spec §2 tree panel). TreeViewNodes are mirrored by hand
/// from <see cref="TreeNode"/> (children, expander, expansion), so lazy loading and selection stay under the view model's
/// control: Expanding/Collapsed call the view model, a click calls <see cref="TreeViewModel.SelectAsync"/>, and the view
/// model's <see cref="TreeViewModel.SelectedNode"/> drives the control's selection without feeding back. Node icons are
/// shell icons (owner F1) requested per realized <see cref="TreeNodeIcon"/> and refreshed on IconsReady.
/// </summary>
public sealed partial class FolderTreeView : UserControl
{
    private readonly Dictionary<TreeNode, TreeViewNode> _nodes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, TreeNode> _owners = new(ReferenceEqualityComparer.Instance);
    private TreeViewModel? _vm;
    private ILogger _logger = NullLogger.Instance;
    private readonly DispatcherQueueTimer _keyboardTimer;
    private bool _syncingSelection;
    private TreeNode? _keyboardTarget;

    // Realized node icons by the key they wait for: their own key and, for per-path icons, a placeholder key.
    private readonly Dictionary<IconKey, HashSet<TreeNodeIcon>> _iconWaiters = [];
    private readonly Dictionary<TreeNodeIcon, (IconKey Key, IconKey? Placeholder)> _iconKeys = new(ReferenceEqualityComparer.Instance);
    private IIconService? _icons;
    private IconBitmapCache? _bitmaps;
    private Action<IReadOnlyList<IconKey>>? _iconsReadyHandler;

    /// <summary>Creates the view; call <see cref="Attach"/> before use.</summary>
    public FolderTreeView()
    {
        InitializeComponent();
        AppTheme.Track(Resources); // ExplorerItems.xaml scope
        TreeControl.Expanding += OnExpanding;
        TreeControl.Collapsed += OnCollapsed;
        TreeControl.ItemInvoked += OnItemInvoked;
        TreeControl.SelectionChanged += OnSelectionChanged;
        TreeControl.ContextRequested += OnContextRequested;

        // UIA exposes the template's TreeViewList as the tree, not the TreeView that carries the name (QA s2).
        TreeControl.Loaded += (_, _) =>
        {
            if (VisualTreeHelper.GetChildrenCount(TreeControl) > 0 && VisualTreeHelper.GetChild(TreeControl, 0) is TreeViewList list)
            {
                AutomationProperties.SetName(list, AutomationProperties.GetName(TreeControl));
            }
        };
        _keyboardTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _keyboardTimer.Interval = KeyboardNavigationDelay;
        _keyboardTimer.IsRepeating = false;
        _keyboardTimer.Tick += (_, _) => OnKeyboardSelectionSettled();

        // WP-3.4b: nodes are drop targets, with the list's rules and caption (FolderDropTarget).
        _ = new FolderDropTarget(TreeControl, DropTargetAt, request => DropRequested?.Invoke(this, request), () => _logger);
    }

    /// <summary>Items were dropped on a node; the pane runs the copy or move (WP-3.4b).</summary>
    public event EventHandler<PaneDropRequest>? DropRequested;

    /// <summary>Right-click, Shift+F10 or the menu key on a node (WP6): the pane shows the shell menu of that folder or drive.</summary>
    public event EventHandler<PaneContextMenuRequest>? ContextMenuRequested;

    /// <summary>
    /// The drop target for a drop on <paramref name="source"/> (WP-3.4b): the folder of the node under the pointer (see
    /// <see cref="TreeNode.DropTarget"/>); null between nodes, on the separator and on drives that take no drop.
    /// </summary>
    /// <param name="source">The element under the pointer.</param>
    /// <returns>The target folder, or null.</returns>
    public string? DropTargetAt(DependencyObject? source)
    {
        for (var d = source; d is not null && d != TreeControl; d = VisualTreeHelper.GetParent(d))
        {
            if (d is TreeViewItem item)
            {
                return NodeOf(TreeControl.NodeFromContainer(item))?.DropTarget;
            }
        }

        return null;
    }

    /// <summary>
    /// Delay before a selection moved by the keyboard navigates the pane (Explorer behaviour): holding an arrow key walks
    /// the tree without listing every folder on the way.
    /// </summary>
    public static TimeSpan KeyboardNavigationDelay { get; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The tree control (tests).</summary>
    public TreeView Tree => TreeControl;

    /// <summary>The view model shown (null before <see cref="Attach"/>).</summary>
    public TreeViewModel? ViewModel => _vm;

    /// <summary>Realized node icons (tests).</summary>
    public IReadOnlyCollection<TreeNodeIcon> RealizedIcons => _iconKeys.Keys;

    /// <summary>Binds the view to a pane's tree.</summary>
    /// <param name="vm">Tree view model.</param>
    /// <param name="icons">Shell icons (shared with the file lists).</param>
    /// <param name="bitmaps">UI-thread icon bitmaps.</param>
    /// <param name="dispatcher">UI dispatcher (IconsReady arrives off the UI thread).</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="InvalidOperationException">Already attached.</exception>
    public void Attach(TreeViewModel vm, IIconService icons, IconBitmapCache bitmaps, IUiDispatcher dispatcher, ILogger logger)
    {
        if (_vm is not null)
        {
            throw new InvalidOperationException("The tree view is already attached");
        }

        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(bitmaps);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _vm = vm;
        _icons = icons;
        _bitmaps = bitmaps;
        _logger = logger ?? NullLogger.Instance;
        _iconsReadyHandler = keys => dispatcher.Post(() => OnIconsReady(keys), UiPriority.Low);
        icons.IconsReady += _iconsReadyHandler;
        vm.Roots.CollectionChanged += OnRootsChanged;
        vm.PropertyChanged += OnViewModelChanged;
        SyncChildren(vm.Roots, TreeControl.RootNodes);
        SyncSelection();
    }

    /// <summary>The control node mirroring <paramref name="node"/>, or null when it is not shown (tests).</summary>
    /// <param name="node">View model node.</param>
    /// <returns>The control node.</returns>
    public TreeViewNode? NodeFor(TreeNode node) => _nodes.GetValueOrDefault(node);

    /// <summary>Stops following the view model (pane shutdown).</summary>
    public void Detach()
    {
        if (_vm is null)
        {
            return;
        }

        _keyboardTimer.Stop();
        _keyboardTarget = null;
        _vm.Roots.CollectionChanged -= OnRootsChanged;
        _vm.PropertyChanged -= OnViewModelChanged;
        if (_icons is not null && _iconsReadyHandler is not null)
        {
            _icons.IconsReady -= _iconsReadyHandler;
        }

        _iconsReadyHandler = null;
        _iconWaiters.Clear();
        _iconKeys.Clear();
        foreach (TreeNode node in _nodes.Keys.ToList())
        {
            Unhook(node);
        }

        _nodes.Clear();
        _owners.Clear();
        TreeControl.RootNodes.Clear();
        _vm = null;
    }

    /// <summary>Shows the shell icon of <paramref name="icon"/>'s node, or a placeholder until it arrives (UI thread).</summary>
    /// <param name="icon">A realized node icon.</param>
    internal void ApplyIcon(TreeNodeIcon icon)
    {
        ReleaseIcon(icon);
        if (_icons is null || _bitmaps is null || icon.Node is not TreeNode { Kind: not TreeNodeKind.Separator } node)
        {
            icon.Image.Source = null;
            return;
        }

        // Quick access entries may have their own icon (desktop.ini: Desktop, Documents, ...); drives are handled by the
        // service; other folders show the generic folder icon, like the list's ordinary folders.
        IconKey key = _icons.KeyForFolder(node.Path, node.Kind == TreeNodeKind.QuickAccess, IconSize.Small);
        IconKey shown = key;
        IconKey? placeholder = null;
        bool resolved = _icons.TryGet(key, out IconImage? image);
        if ((!resolved || image is null) && key.UniquePath is not null)
        {
            // While a per-path icon loads: the generic drive or folder icon.
            placeholder = !PathUtil.IsDriveRoot(key.UniquePath) ? key.Generic
                : new IconKey(node.DriveKind == DriveKind.Removable ? IconKey.RemovableDriveExtId : IconKey.OtherDriveExtId, null, key.Size);
            if (_icons.TryGet(placeholder.Value, out IconImage? placeholderImage) && placeholderImage is not null)
            {
                image = placeholderImage;
                shown = placeholder.Value;
            }
        }

        _iconKeys[icon] = (key, placeholder);
        Wait(icon, key);
        if (placeholder is { } p)
        {
            Wait(icon, p);
        }

        icon.Image.Source = image is not null ? _bitmaps.Get(shown, image) : null;
    }

    /// <summary>Forgets <paramref name="icon"/> (unloaded or re-applied).</summary>
    /// <param name="icon">A node icon.</param>
    internal void ReleaseIcon(TreeNodeIcon icon)
    {
        if (!_iconKeys.Remove(icon, out var keys))
        {
            return;
        }

        Unwait(icon, keys.Key);
        if (keys.Placeholder is { } p)
        {
            Unwait(icon, p);
        }
    }

    private static TreeNode? NodeOf(TreeViewNode? node) => node?.Content as TreeNode;

    /// <summary>
    /// The shell item a node's menu is for (WP6): a drive under the This PC token (as the drive list does), a folder or Quick
    /// access entry by its parent and name, This PC under the Desktop root (WP6b); null for the separator (no menu).
    /// </summary>
    /// <param name="node">A tree node.</param>
    /// <returns>The target, or null.</returns>
    public static ContextMenuTarget? TargetOf(TreeNode node) => node.Kind switch
    {
        TreeNodeKind.ThisPc => new ContextMenuTarget(ContextMenuTarget.DesktopRoot, [PathUtil.ThisPc]),
        TreeNodeKind.Separator => null,
        _ when PathUtil.IsDriveRoot(node.Path) => new ContextMenuTarget(PathUtil.ThisPc, [node.Path]), // a drive, or Quick access on one (m4)
        TreeNodeKind.Folder or TreeNodeKind.QuickAccess when PathUtil.Parent(node.Path) is { } parent => new ContextMenuTarget(parent, [PathUtil.LeafName(node.Path)]),
        _ => null,
    };

    /// <summary>
    /// What Del deletes from the tree: the selected folder node while focus is in the tree; null for Quick access roots,
    /// drives, This PC, or focus elsewhere.
    /// </summary>
    public ContextMenuTarget? DeleteTarget
        => XamlRoot is not null && FileListView.IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, TreeControl)
            && NodeOf(TreeControl.SelectedNode) is { Kind: TreeNodeKind.Folder } node ? TargetOf(node) : null;

    // The node under the pointer, else (keyboard) the selected one; its menu opens at the pointer or below the node.
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        bool keyboard = !e.TryGetPosition(TreeControl, out Windows.Foundation.Point inTree);
        TreeViewItem? item = null;
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d is not null && d != TreeControl; d = VisualTreeHelper.GetParent(d))
        {
            if (d is TreeViewItem found)
            {
                item = found;
                break;
            }
        }

        TreeViewNode? control = item is not null ? TreeControl.NodeFromContainer(item) : keyboard ? TreeControl.SelectedNode : null; // m1: no menu on blank area
        if (NodeOf(control) is not { } node || TargetOf(node) is not { } target)
        {
            return;
        }

        e.Handled = true;
        if (keyboard)
        {
            UIElement anchor = item ?? TreeControl.ContainerFromNode(control) as UIElement ?? TreeControl;
            inTree = anchor.TransformToVisual(TreeControl).TransformPoint(new Windows.Foundation.Point(8, anchor == TreeControl ? 8 : anchor.ActualSize.Y));
        }

        var inRoot = TreeControl.TransformToVisual(null).TransformPoint(inTree);
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        bool extended = FileListView.IsExtendedRequest(keyboard, FileListView.IsShiftDown());
        ContextMenuRequested?.Invoke(this, new PaneContextMenuRequest(target, TreeControl, inTree, (int)Math.Round(inRoot.X * scale), (int)Math.Round(inRoot.Y * scale), extended, FromTree: true));
    }

    private void Wait(TreeNodeIcon icon, IconKey key)
    {
        if (!_iconWaiters.TryGetValue(key, out var set))
        {
            set = new HashSet<TreeNodeIcon>(ReferenceEqualityComparer.Instance);
            _iconWaiters[key] = set;
        }

        set.Add(icon);
    }

    private void Unwait(TreeNodeIcon icon, IconKey key)
    {
        if (_iconWaiters.TryGetValue(key, out var set))
        {
            set.Remove(icon);
            if (set.Count == 0)
            {
                _iconWaiters.Remove(key);
            }
        }
    }

    private void OnIconsReady(IReadOnlyList<IconKey> keys)
    {
        if (_vm is null)
        {
            return; // detached meanwhile
        }

        foreach (var key in keys)
        {
            if (_iconWaiters.TryGetValue(key, out var set))
            {
                foreach (var icon in set.ToArray())
                {
                    ApplyIcon(icon);
                }
            }
        }
    }

    private void OnRootsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm is not null)
        {
            Apply(_vm.Roots, TreeControl.RootNodes, e);
        }
    }

    private void OnNodeChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is not null && _owners.TryGetValue(sender, out TreeNode? parent) && _nodes.TryGetValue(parent, out TreeViewNode? control))
        {
            Apply(parent.Children, control.Children, e);
        }
    }

    // Single inserts and removals (a listing merge) map 1:1; anything else re-syncs the level.
    private void Apply(ObservableCollection<TreeNode> src, IList<TreeViewNode> dst, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is [TreeNode added] && e.NewStartingIndex >= 0
            && e.NewStartingIndex <= dst.Count && dst.Count == src.Count - 1 && !_nodes.ContainsKey(added))
        {
            dst.Insert(e.NewStartingIndex, GetOrCreate(added));
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is [TreeNode removed] && e.OldStartingIndex >= 0
            && e.OldStartingIndex < dst.Count && ReferenceEquals(NodeOf(dst[e.OldStartingIndex]), removed))
        {
            dst.RemoveAt(e.OldStartingIndex);
            Forget(removed);
            return;
        }

        SyncChildren(src, dst);
    }

    // Makes dst mirror src in order; keeps existing control nodes (their containers and expansion).
    private void SyncChildren(ObservableCollection<TreeNode> src, IList<TreeViewNode> dst)
    {
        for (int i = 0; i < src.Count; i++)
        {
            TreeViewNode control = GetOrCreate(src[i]);
            if (i < dst.Count && ReferenceEquals(dst[i], control))
            {
                continue;
            }

            int at = dst.IndexOf(control);
            if (at >= 0)
            {
                dst.RemoveAt(at);
            }

            dst.Insert(i, control);
        }

        while (dst.Count > src.Count)
        {
            TreeViewNode removed = dst[^1];
            dst.RemoveAt(dst.Count - 1);
            if (NodeOf(removed) is { } node)
            {
                Forget(node);
            }
        }
    }

    private TreeViewNode GetOrCreate(TreeNode node)
    {
        if (_nodes.TryGetValue(node, out TreeViewNode? existing))
        {
            return existing;
        }

        var control = new TreeViewNode
        {
            Content = node,
            HasUnrealizedChildren = node.HasUnrealizedChildren,
            IsExpanded = node.IsExpanded,
        };
        _nodes[node] = control;
        _owners[node.Children] = node;
        node.PropertyChanged += OnNodeChanged;
        node.Children.CollectionChanged += OnNodeChildrenChanged;
        SyncChildren(node.Children, control.Children);
        return control;
    }

    // A node left the tree: drop it and its subtree from the map.
    private void Forget(TreeNode node)
    {
        Unhook(node);
        _nodes.Remove(node);
        _owners.Remove(node.Children);
        foreach (TreeNode child in node.Children)
        {
            if (_nodes.ContainsKey(child))
            {
                Forget(child);
            }
        }
    }

    private void Unhook(TreeNode node)
    {
        node.PropertyChanged -= OnNodeChanged;
        node.Children.CollectionChanged -= OnNodeChildrenChanged;
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TreeNode node || !_nodes.TryGetValue(node, out TreeViewNode? control))
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(TreeNode.HasUnrealizedChildren):
                control.HasUnrealizedChildren = node.HasUnrealizedChildren;
                break;
            case nameof(TreeNode.IsExpanded) when control.IsExpanded != node.IsExpanded:
                control.IsExpanded = node.IsExpanded;
                break;

            // The item's UIA Name is read from node.ToString() on demand: tell listeners it changed (a drive label arrived).
            case nameof(TreeNode.Name) when TreeControl.ContainerFromNode(control) is TreeViewItem item
                && FrameworkElementAutomationPeer.FromElement(item) is { } peer:
                peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, string.Empty, node.Name);
                break;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TreeViewModel.SelectedNode))
        {
            SyncSelection();
        }
    }

    private void SyncSelection()
    {
        // The pane moved (sync) or the selection is reset: a pending keyboard target must not override that navigation.
        _keyboardTimer.Stop();
        _keyboardTarget = null;
        _syncingSelection = true;
        try
        {
            if (_vm?.SelectedNode is { } node && _nodes.TryGetValue(node, out TreeViewNode? control))
            {
                TreeControl.SelectedNode = control;
                BringIntoView(control);
            }
            else
            {
                TreeControl.SelectedNodes.Clear();
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    // Containers of freshly expanded levels exist only after layout: scroll once the dispatcher is idle.
    private void BringIntoView(TreeViewNode control)
        => DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, WinUiDispatcher.Guarded(
            () =>
            {
                if (ReferenceEquals(TreeControl.SelectedNode, control) && TreeControl.ContainerFromNode(control) is UIElement container)
                {
                    container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 });
                }
            },
            _logger));

    private void OnExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (NodeOf(args.Node) is { } node)
        {
            _vm?.Expand(node);
        }
    }

    private void OnCollapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (NodeOf(args.Node) is { } node)
        {
            _vm?.Collapse(node);
        }
    }

    // A selection the user moved (arrow keys; a click also lands here before ItemInvoked) navigates after a short delay.
    // Our own sync sets the selection under _syncingSelection and is ignored; a click navigates at once via ItemInvoked,
    // and the delayed tick then finds the node already selected in the view model and does nothing.
    private void OnSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection || _vm is null || args.AddedItems.Count == 0 || NodeOf(args.AddedItems[0] as TreeViewNode) is not { } node)
        {
            return;
        }

        if (node.Kind == TreeNodeKind.Separator)
        {
            SyncSelection(); // the separator is not selectable
            return;
        }

        if (ReferenceEquals(node, _vm.SelectedNode))
        {
            _keyboardTimer.Stop();
            _keyboardTarget = null;
            return;
        }

        _keyboardTarget = node;
        _keyboardTimer.Stop();
        _keyboardTimer.Start();
    }

    private void OnKeyboardSelectionSettled()
    {
        TreeNode? target = _keyboardTarget;
        _keyboardTarget = null;
        if (_vm is { } vm && target is not null && !ReferenceEquals(vm.SelectedNode, target) && _nodes.ContainsKey(target))
        {
            _ = SelectAsync(vm, target);
        }
    }

    private void OnItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        _keyboardTimer.Stop();
        _keyboardTarget = null;
        if (_syncingSelection || _vm is null || NodeOf(args.InvokedItem as TreeViewNode) is not { } node)
        {
            return;
        }

        if (node.Kind == TreeNodeKind.Separator)
        {
            args.Handled = true;
            SyncSelection(); // the separator is not selectable
            return;
        }

        _ = SelectAsync(_vm, node);
    }

    private async Task SelectAsync(TreeViewModel vm, TreeNode node)
    {
        try
        {
            await vm.SelectAsync(node);
        }
        catch (ObjectDisposedException)
        {
            // Pane closed meanwhile.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Navigating from the tree to {Path} failed", node.Path);
        }
    }
}
