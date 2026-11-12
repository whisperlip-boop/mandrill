using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.Platform;
using Mandrill.Presentation.Common;
using Mandrill.Presentation.Panes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Presentation.Tree;

/// <summary>
/// Folder tree of one pane (AC-1.6–1.9, ui-spec §2), composed beside its <see cref="PaneViewModel"/>. Each pane has its own
/// nodes and expansion state over the shared <see cref="TreeRoots"/>. Selecting a node navigates that pane only; when the
/// pane navigates by other means, the tree expands to and selects its folder (the deepest matching root wins, so a folder
/// under Documents syncs under Quick access). Children load lazily on a worker (<see cref="TreeLoader"/>). Phase 1
/// limitation: nodes do not watch the file system; a collapsed node re-lists on its next expand, and a sync re-lists a
/// folder whose expected child is missing. Collapsing a node while a sync waits for its listing lets the sync expand it again
/// (accepted for Phase 1). UI thread only.
/// </summary>
public sealed partial class TreeViewModel : ObservableObject, IDisposable
{
    private readonly PaneViewModel _pane;
    private readonly TreeRoots _roots;
    private readonly TreeLoader _loader;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<HiddenFilesSetting> _hiddenFiles;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private TreeNode? _separator;
    private TreeNode? _thisPc;
    private long _syncId;
    private bool _disposed;
    private bool _itemsCheckPending;

    /// <summary>Creates the tree of <paramref name="pane"/> and syncs it to the pane's folder.</summary>
    /// <param name="pane">The pane this tree navigates. The caller owns both and disposes the tree first.</param>
    /// <param name="roots">Shared roots.</param>
    /// <param name="loader">Shared folder loader.</param>
    /// <param name="dispatcher">UI dispatcher.</param>
    /// <param name="hiddenFiles">Explorer's hidden-file setting (the pane's override wins, same rule as the list).</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public TreeViewModel(PaneViewModel pane, TreeRoots roots, TreeLoader loader, IUiDispatcher dispatcher, Func<HiddenFilesSetting> hiddenFiles, ILoggerFactory? loggerFactory = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _hiddenFiles = hiddenFiles ?? throw new ArgumentNullException(nameof(hiddenFiles));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TreeViewModel>();
        _pane.PropertyChanged += OnPanePropertyChanged;
        _pane.Items.CollectionChanged += OnItemsChanged;
        _roots.Changed += OnRootsChanged;
        RebuildRoots();
    }

    /// <summary>Root nodes: Quick access, a separator, then drives.</summary>
    public ObservableCollection<TreeNode> Roots { get; } = [];

    /// <summary>Node of the pane's current folder; null when it is not in the tree (e.g. a hidden folder while hidden files are off).</summary>
    [ObservableProperty]
    public partial TreeNode? SelectedNode { get; private set; }

    /// <summary>The view expands <paramref name="node"/> (TreeView Expanding): lists its subfolders on first expand.</summary>
    /// <param name="node">Node of this tree.</param>
    public void Expand(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_disposed || node.Kind == TreeNodeKind.Separator)
        {
            return;
        }

        node.IsExpanded = true;
        EnsureLoaded(node, null, UiPriority.Normal); // user-visible: not behind list updates
    }

    /// <summary>The view collapses <paramref name="node"/>; its next expand re-lists it (children are merged, not rebuilt).</summary>
    /// <param name="node">Node of this tree.</param>
    public void Collapse(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.IsExpanded = false;
        if (!node.IsLoading)
        {
            node.IsLoaded = false;
        }
    }

    /// <summary>A single click on <paramref name="node"/> (AC-1.8): selects it and navigates this pane only.</summary>
    /// <param name="node">Node of this tree.</param>
    /// <param name="ct">Stops waiting for the navigation; the navigation itself continues (a newer one supersedes it).</param>
    /// <returns>A task that completes when the navigation ended and the selection matches the pane's folder.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before the navigation ended.</exception>
    public async Task SelectAsync(TreeNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_disposed || node.Kind == TreeNodeKind.Separator)
        {
            return;
        }

        _syncId++; // a sync walk still waiting for a listing must not override the click
        SelectedNode = node;
        await _pane.NavigateAsync(node.Path, NavOrigin.Tree).WaitAsync(ct).ConfigureAwait(false);

        // The pane may show an ancestor (folder gone) or stay where it was (failure, supersession): follow the pane.
        _dispatcher.Post(() =>
        {
            if (!_disposed && ReferenceEquals(SelectedNode, node) && !PathUtil.PathEquals(_pane.CurrentPath, node.Path))
            {
                Sync();
            }
        });
    }

    /// <summary>Toggles this pane's tree panel (toolbar button 0, Ctrl+B; AC-1.7).</summary>
    [RelayCommand]
    private void ToggleTree() => _pane.TreeVisible = !_pane.TreeVisible;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pane.PropertyChanged -= OnPanePropertyChanged;
        _pane.Items.CollectionChanged -= OnItemsChanged;
        _roots.Changed -= OnRootsChanged;
        _lifetime.Cancel(); // not disposed: a listing finishing later may still read its token
    }

    // Path segments from root to target, excluding the root: C:\a\b under C:\ → [C:\a, C:\a\b].
    private static List<string> ChainBelow(string root, string path)
    {
        var chain = new List<string>();
        for (string? p = path; p is not null && !PathUtil.PathEquals(p, root); p = PathUtil.Parent(p))
        {
            chain.Add(p);
        }

        chain.Reverse();
        return chain;
    }

    private static bool SameRoot(TreeNode node, TreeNodeKind kind, string path)
        => node.Kind == kind && PathUtil.PathEquals(node.Path, path);

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaneViewModel.CurrentPath) or nameof(PaneViewModel.IsThisPc):
                Sync();
                break;
            case nameof(PaneViewModel.TreeVisible) when _pane.TreeVisible:
                Sync();
                break;
            case nameof(PaneViewModel.ShowHiddenOverride):
                RefreshLoaded();
                break;
        }
    }

    // QA s2 (g): a subfolder renamed, created or deleted in the list re-lists the loaded tree node of the shown folder,
    // which otherwise kept the old names until collapsed. Once per batch of list notifications. The list and the tree filter
    // alike (FileEntry.IsVisible with the pane's override ?? Explorer's hidden flag and Explorer's protected-system flag,
    // both from ExplorerSettings.ReadHiddenFiles), and a re-list raises no list notification, so this cannot loop.
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_itemsCheckPending)
        {
            return;
        }

        _itemsCheckPending = true;
        _dispatcher.Post(
            () =>
            {
                _itemsCheckPending = false;
                if (!_disposed && SelectedNode is { IsLoaded: true, IsLoading: false } node && _pane.Items.Current is { } shown
                    && PathUtil.PathEquals(node.Path, shown.Path) && IsAttached(node)
                    && !shown.Visible.Where(f => f.IsDirectory).Select(f => f.Name).ToHashSet(StringComparer.Ordinal).SetEquals(node.Children.Select(c => c.Name)))
                {
                    Reload(node, null);
                }
            },
            UiPriority.Low);
    }

    private void OnRootsChanged()
    {
        if (!_disposed)
        {
            RebuildRoots();
        }
    }

    // Keeps existing root nodes (and their expansion) where the root still exists; inserts new ones in order.
    private void RebuildRoots()
    {
        var before = new HashSet<TreeNode>(TopNodes(), ReferenceEqualityComparer.Instance);
        var desired = new List<TreeNode>();
        foreach (QuickAccessRoot qa in _roots.QuickAccess)
        {
            TreeNode node = Roots.FirstOrDefault(n => SameRoot(n, TreeNodeKind.QuickAccess, qa.Path))
                ?? new TreeNode(TreeNodeKind.QuickAccess, qa.Path, TreeRoots.NameOf(qa.Folder), null, knownFolder: qa.Folder);
            desired.Add(node);
        }

        if (desired.Count > 0 && _roots.Drives.Count > 0)
        {
            desired.Add(_separator ??= new TreeNode(TreeNodeKind.Separator, string.Empty, string.Empty, null));
        }

        // WP2b: the drives are children of one "This PC" node (expanded at first), as in Explorer's tree.
        if (_roots.Drives.Count > 0)
        {
            _thisPc ??= new TreeNode(TreeNodeKind.ThisPc, PathUtil.ThisPc, Localizer.Current["thispc.name"], null) { IsExpanded = true };
            desired.Add(_thisPc);
            var drives = new List<TreeNode>();
            foreach (DriveRoot drive in _roots.Drives)
            {
                TreeNode? node = _thisPc.Children.FirstOrDefault(n => SameRoot(n, TreeNodeKind.Drive, drive.Root));
                if (node is null)
                {
                    node = new TreeNode(TreeNodeKind.Drive, drive.Root, TreeRoots.NameOf(drive), _thisPc, driveKind: drive.Kind);
                }
                else
                {
                    node.Name = TreeRoots.NameOf(drive); // label may have arrived or changed
                }

                drives.Add(node);
            }

            Reconcile(_thisPc.Children, drives);
        }
        else
        {
            _thisPc?.Children.Clear(); // no local drive: This PC leaves the roots and must not keep stale sync roots
        }

        Reconcile(Roots, desired);
        if (SelectedNode is { } selected && !IsAttached(selected))
        {
            SelectedNode = null;
        }

        // A deeper root that just appeared (e.g. Quick access resolved after the pane committed at startup) takes over the
        // selection (deepest root wins). Existing roots never move a selection the user made by clicking another branch.
        if (SelectedNode is { } current && _pane.CurrentPath is { } path && FindRoot(path) is { } best
            && !ReferenceEquals(RootOf(current), best) && !before.Contains(best))
        {
            SelectedNode = null;
        }

        if (SelectedNode is null)
        {
            Sync();
        }
    }

    // Moves, inserts and removes so that list equals desired; kept nodes keep their expansion and children.
    private static void Reconcile(ObservableCollection<TreeNode> list, List<TreeNode> desired)
    {
        for (int i = 0; i < desired.Count; i++)
        {
            int at = list.IndexOf(desired[i]);
            if (at < 0)
            {
                list.Insert(i, desired[i]);
            }
            else if (at != i)
            {
                list.Move(at, i);
            }
        }

        while (list.Count > desired.Count)
        {
            list.RemoveAt(list.Count - 1);
        }
    }

    // The Quick access or drive node a node hangs under (a drive's parent, This PC, is not a sync root).
    private static TreeNode RootOf(TreeNode node)
    {
        while (node.Parent is { Kind: not TreeNodeKind.ThisPc } parent)
        {
            node = parent;
        }

        return node;
    }

    // Sync roots: the Quick access nodes and the drives under This PC.
    private IEnumerable<TreeNode> TopNodes()
        => Roots.Where(n => n.Kind == TreeNodeKind.QuickAccess).Concat(_thisPc?.Children ?? []);

    private bool IsAttached(TreeNode node)
    {
        TreeNode root = node;
        while (root.Parent is { } parent)
        {
            if (!parent.Children.Contains(root))
            {
                return false;
            }

            root = parent;
        }

        return Roots.Contains(root);
    }

    private TreeNode? FindRoot(string path)
    {
        TreeNode? best = null;
        foreach (TreeNode root in TopNodes())
        {
            if (PathUtil.IsSameOrUnder(path, root.Path)
                && (best is null || root.Path.Length > best.Path.Length))
            {
                best = root;
            }
        }

        return best;
    }

    // AC-1.8: expand to and select the pane's folder. A newer sync supersedes a running one.
    private void Sync()
    {
        if (!_disposed && _pane.TreeVisible && _pane.IsThisPc)
        {
            _syncId++; // a walk still waiting for a listing must not move the selection off This PC
            SelectedNode = _thisPc;
            return;
        }

        if (_disposed || !_pane.TreeVisible || _pane.CurrentPath is not { } path)
        {
            return;
        }

        if (SelectedNode is { } selected && PathUtil.PathEquals(selected.Path, path) && IsAttached(selected))
        {
            return;
        }

        long id = ++_syncId;
        if (FindRoot(path) is not { } root)
        {
            SelectedNode = null;
            return;
        }

        if (root.Parent is { } thisPc)
        {
            thisPc.IsExpanded = true; // the user may have collapsed This PC
        }

        Step(id, root, ChainBelow(root.Path, path), 0, retried: false);
    }

    private void Step(long id, TreeNode node, List<string> chain, int index, bool retried)
    {
        if (_disposed || id != _syncId)
        {
            return;
        }

        if (index == chain.Count)
        {
            SelectedNode = node;
            return;
        }

        EnsureLoaded(node, Next, UiPriority.Low);

        void Next()
        {
            if (_disposed || id != _syncId)
            {
                return;
            }

            TreeNode? child = node.Children.FirstOrDefault(c => PathUtil.PathEquals(c.Path, chain[index]));
            if (child is null)
            {
                if (!retried)
                {
                    // The listing may predate the folder: re-list once before giving up.
                    Reload(node, () => Step(id, node, chain, index, retried: true));
                }
                else
                {
                    SelectedNode = null; // e.g. a hidden folder while hidden files are off
                }

                return;
            }

            node.IsExpanded = true;
            Step(id, child, chain, index + 1, retried: false);
        }
    }

    private void EnsureLoaded(TreeNode node, Action? then, UiPriority priority)
    {
        if (node.IsLoaded || node.Kind == TreeNodeKind.ThisPc) // This PC's children are the drives (RebuildRoots), never listed
        {
            then?.Invoke();
            return;
        }

        if (then is not null)
        {
            node.Waiters.Add(then);
        }

        if (!node.IsLoading)
        {
            _ = LoadAsync(node, priority);
        }
    }

    private void Reload(TreeNode node, Action? then)
    {
        if (node.Kind == TreeNodeKind.ThisPc)
        {
            then?.Invoke();
            return;
        }

        node.IsLoaded = false;
        if (then is not null)
        {
            node.Waiters.Add(then);
        }

        _ = LoadAsync(node, UiPriority.Low);
    }

    // Hidden-file setting changed: expanded nodes re-list now, collapsed ones on their next expand.
    private void RefreshLoaded()
    {
        var stack = new Stack<TreeNode>(Roots);
        while (stack.Count > 0)
        {
            TreeNode node = stack.Pop();
            foreach (TreeNode child in node.Children)
            {
                stack.Push(child);
            }

            if (node.IsExpanded && node.IsLoaded)
            {
                Reload(node, null);
            }
            else if (!node.IsLoading)
            {
                node.IsLoaded = false;
            }
        }

        SelectedNode = SelectedNode is { } s && IsAttached(s) ? s : null;
        Sync();
    }

    private async Task LoadAsync(TreeNode node, UiPriority priority)
    {
        long version = ++node.LoadVersion;
        node.IsLoading = true;
        HiddenFilesSetting explorer = ReadHiddenFiles();
        bool showHidden = _pane.ShowHiddenOverride ?? explorer.ShowHidden;
        TreeListing listing;
        try
        {
            listing = await _loader.ListAsync(node.Path, showHidden, explorer.ShowProtectedSystem, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Listing subfolders of {Path} failed unexpectedly", node.Path);
            listing = new TreeListing(node.Path, [], new FsError(FsErrorKind.Other, node.Path, 0));
        }

        _dispatcher.Post(() => Apply(node, version, listing), priority);
    }

    private void Apply(TreeNode node, long version, TreeListing listing)
    {
        if (_disposed || version != node.LoadVersion)
        {
            return; // a newer listing of this node is running
        }

        Merge(node, listing.Folders);
        node.IsLoaded = true;
        node.IsLoading = false;
        node.HasUnrealizedChildren = node.Children.Count > 0;
        if (node.Waiters.Count > 0)
        {
            Action[] waiters = [.. node.Waiters];
            node.Waiters.Clear();
            foreach (Action waiter in waiters)
            {
                waiter();
            }
        }
    }

    // Both lists are in Explorer name order, so after removals the old children are a subsequence of the new names:
    // kept nodes keep their expansion and children.
    private void Merge(TreeNode node, IReadOnlyList<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.Ordinal);
        bool lostSelection = false;
        for (int i = node.Children.Count - 1; i >= 0; i--)
        {
            TreeNode child = node.Children[i];
            if (!wanted.Contains(child.Name))
            {
                lostSelection |= SelectedNode is { } s && PathUtil.IsSameOrUnder(s.Path, child.Path);
                node.Children.RemoveAt(i);
            }
        }

        for (int i = 0; i < names.Count; i++)
        {
            if (i < node.Children.Count && string.Equals(node.Children[i].Name, names[i], StringComparison.Ordinal))
            {
                continue;
            }

            node.Children.Insert(i, new TreeNode(TreeNodeKind.Folder, PathUtil.Combine(node.Path, names[i]), names[i], node));
        }

        if (lostSelection)
        {
            SelectedNode = null;
        }
    }

    private HiddenFilesSetting ReadHiddenFiles()
    {
        try
        {
            return _hiddenFiles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading Explorer's hidden-file setting failed; hiding hidden folders");
            return new HiddenFilesSetting(false, false);
        }
    }
}
