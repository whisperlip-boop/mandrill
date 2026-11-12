using Mandrill.Presentation.Tree;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mandrill.App.Views;

/// <summary>Picks the separator template for the Quick access / drives separator and the node template otherwise.</summary>
public sealed partial class TreeItemTemplateSelector : DataTemplateSelector
{
    /// <summary>Template for folders, Quick access entries and drives.</summary>
    public DataTemplate? NodeTemplate { get; set; }

    /// <summary>Template for the separator line.</summary>
    public DataTemplate? SeparatorTemplate { get; set; }

    /// <inheritdoc />
    protected override DataTemplate? SelectTemplateCore(object item)
        => (item as TreeViewNode)?.Content is TreeNode { Kind: TreeNodeKind.Separator } ? SeparatorTemplate : NodeTemplate;

    /// <inheritdoc />
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>Disables the separator's container so keyboard navigation skips it and it cannot be selected.</summary>
public sealed partial class TreeItemStyleSelector : StyleSelector
{
    /// <summary>Style for the separator container (disabled, not a tab stop).</summary>
    public Style? SeparatorStyle { get; set; }

    /// <inheritdoc />
    protected override Style? SelectStyleCore(object item, DependencyObject container)
        => (item as TreeViewNode)?.Content is TreeNode { Kind: TreeNodeKind.Separator } ? SeparatorStyle : null;
}

/// <summary>
/// Shell icon of a tree node (owner F1): when its node changes (container reuse) or it loads, it asks its
/// <see cref="FolderTreeView"/> for the icon, so only realized nodes request icons; the tree refreshes it when the icon
/// arrives (IconsReady) and forgets it when it unloads.
/// </summary>
public sealed partial class TreeNodeIcon : Grid
{
    /// <summary>The <see cref="TreeNode"/> shown (bound to the tree item's content).</summary>
    public static readonly DependencyProperty NodeProperty =
        DependencyProperty.Register(nameof(Node), typeof(object), typeof(TreeNodeIcon), new PropertyMetadata(null, (d, _) => ((TreeNodeIcon)d).Request()));

    private FolderTreeView? _owner;

    /// <summary>Creates the 16 px icon.</summary>
    public TreeNodeIcon()
    {
        Width = 16;
        Height = 16;
        Image = new Image { Stretch = Stretch.Uniform };
        Children.Add(Image);
        Loaded += (_, _) => Request();
        Unloaded += (_, _) =>
        {
            if (IsLoaded)
            {
                return; // a late Unloaded after re-parenting (layout switch): Loaded already re-requested the icon
            }

            _owner?.ReleaseIcon(this);
            _owner = null;
        };
    }

    /// <summary>The node shown (a <see cref="TreeNode"/>).</summary>
    public object? Node
    {
        get => GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    /// <summary>The image the icon is drawn into.</summary>
    public Image Image { get; }

    private void Request()
    {
        if (!IsLoaded)
        {
            return; // Loaded requests it
        }

        _owner ??= FindOwner();
        _owner?.ApplyIcon(this);
    }

    private FolderTreeView? FindOwner()
    {
        for (DependencyObject? d = VisualTreeHelper.GetParent(this); d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FolderTreeView tree)
            {
                return tree;
            }
        }

        return null;
    }
}
