using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mandrill.Core.Platform;

namespace Mandrill.Presentation.Tree;

/// <summary>Kind of tree node (ui-spec §2 tree panel).</summary>
public enum TreeNodeKind
{
    /// <summary>A Quick access root (Desktop, Documents, Downloads, Pictures).</summary>
    QuickAccess,

    /// <summary>The line between Quick access and the drives; not selectable.</summary>
    Separator,

    /// <summary>"This PC" (ADR-0015): the parent of the drives; Path is PathUtil.ThisPc, its children are not listed from disk.</summary>
    ThisPc,

    /// <summary>A drive root, a child of the This PC node.</summary>
    Drive,

    /// <summary>A folder below a root.</summary>
    Folder,
}

/// <summary>
/// One node of a pane's folder tree. Children load lazily on first expand (AC-1.6); the expander
/// (<see cref="HasUnrealizedChildren"/>) disappears once the folder is found to have no subfolders. UI thread only.
/// </summary>
public sealed partial class TreeNode : ObservableObject
{
    internal TreeNode(TreeNodeKind kind, string path, string name, TreeNode? parent, KnownFolder? knownFolder = null, DriveKind? driveKind = null)
    {
        Kind = kind;
        Path = path;
        Name = name;
        Parent = parent;
        KnownFolder = knownFolder;
        DriveKind = driveKind;
        HasUnrealizedChildren = kind != TreeNodeKind.Separator;
    }

    /// <summary>Node kind.</summary>
    public TreeNodeKind Kind { get; }

    /// <summary>Folder path (normalized); empty for the separator.</summary>
    public string Path { get; }

    /// <summary>Parent node; null for roots.</summary>
    public TreeNode? Parent { get; }

    /// <summary>Known folder of a Quick access root (icon lookup); else null.</summary>
    public KnownFolder? KnownFolder { get; }

    /// <summary>Drive kind of a drive root (icon lookup); else null.</summary>
    public DriveKind? DriveKind { get; }

    /// <summary>
    /// The folder a drop on this node goes into (WP-3.4b, architect note 3): a folder, a Quick access folder, or a fixed or
    /// removable drive; null for the separator and other drives (optical, unknown, network).
    /// </summary>
    public string? DropTarget => Kind switch
    {
        TreeNodeKind.Folder or TreeNodeKind.QuickAccess => Path,
        TreeNodeKind.Drive when DriveKind is Core.Platform.DriveKind.Fixed or Core.Platform.DriveKind.Removable => Path,
        _ => null,
    };

    /// <summary>Loaded subfolders in Explorer name order.</summary>
    public ObservableCollection<TreeNode> Children { get; } = [];

    /// <summary>Display name (localized for roots; the folder name otherwise).</summary>
    [ObservableProperty]
    public partial string Name { get; internal set; }

    /// <summary>The display name: TreeViewItem's UIA Name falls back to its node content's ToString().</summary>
    /// <returns><see cref="Name"/>.</returns>
    public override string ToString() => Name;

    /// <summary>True while the node may have subfolders that are not loaded yet (shows the expander).</summary>
    [ObservableProperty]
    public partial bool HasUnrealizedChildren { get; internal set; }

    /// <summary>Whether the node is expanded (set by the view on expand/collapse and by sync-to-path).</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; internal set; }

    /// <summary>True while the subfolders are being listed.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; internal set; }

    /// <summary>Children reflect a listing that is still current (cleared on collapse so the next expand re-lists).</summary>
    internal bool IsLoaded { get; set; }

    /// <summary>Version of the newest listing request; older results are dropped.</summary>
    internal long LoadVersion { get; set; }

    /// <summary>Callbacks waiting for the running listing (sync-to-path).</summary>
    internal List<Action> Waiters { get; } = [];
}
