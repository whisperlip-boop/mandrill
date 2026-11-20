using Mandrill.Core.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;

namespace Mandrill.App.Views;

/// <summary>Drag and drop of the file list (WP-3.4, architecture §7 "Drag &amp; drop", ADR-0011, S3, WP-3.0).</summary>
public sealed partial class FileListView
{
    /// <summary>Items were dropped on the list; the pane runs the copy or move (the view only decides the target and effect).</summary>
    public event EventHandler<PaneDropRequest>? DropRequested;

    /// <summary>
    /// The drop target for a drop on <paramref name="source"/>: the folder of a folder row, otherwise (a file row, blank space)
    /// the pane's folder (lead Q5).
    /// </summary>
    /// <param name="source">The element under the pointer.</param>
    /// <returns>The target folder, or null when the pane shows no folder.</returns>
    public string? DropTargetAt(DependencyObject? source)
    {
        if (_vm?.CurrentPath is not { } folder)
        {
            return null;
        }

        return _list is not null && Ancestor<SelectorItem>(source, _list) is { } row && _rows is not null
            && _rows.TryGetItem(row, out var entry) && entry.IsDirectory
            ? PathUtil.Combine(folder, entry.Name)
            : folder;
    }

    /// <summary>
    /// The names a drag out carries (QA M2, Explorer): the selection when every dragged row is selected; a row outside the
    /// selection (pressed and dragged without a click first) becomes the selection alone and only it is dragged.
    /// </summary>
    /// <param name="dragged">The rows the list reports as dragged.</param>
    /// <returns>The names, in list order; empty when nothing can be dragged.</returns>
    public IReadOnlyList<string> PrepareDragOut(IReadOnlyList<FileEntry> dragged)
    {
        ArgumentNullException.ThrowIfNull(dragged);
        if (_vm is null || _list is null || _adapter is null)
        {
            return [];
        }

        var selection = _vm.Items.Selection;
        if (dragged.FirstOrDefault(x => !selection.IsSelected(x.Name)) is { } pressed)
        {
            int index = _vm.Items.Current?.IndexOfName(pressed.Name) ?? -1;
            if (index < 0)
            {
                return [];
            }

            if (_adapter.Count > 0)
            {
                _list.DeselectRange(new ItemIndexRange(0, (uint)_adapter.Count));
            }

            _list.SelectRange(new ItemIndexRange(index, 1));
            return [pressed.Name];
        }

        return [.. selection.SelectedEntries().Select(x => x.Name)];
    }

    // The drop side is shared with the folder tree (FolderDropTarget, 3.4b); the list decides only the target.
    private void AttachDragDrop(ListViewBase list)
    {
        list.CanDragItems = true;
        list.DragItemsStarting += OnDragItemsStarting;
        _ = new FolderDropTarget(list, DropTargetAt, request => DropRequested?.Invoke(this, request), () => _logger); // kept by list's events
    }

    // Drag out: the paths from the selection model (S2/S3; e.Items only to find a pressed row outside it), Mandrill's own
    // format, StorageItems resolved when a target asks.
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        CancelSlowClick();
        // A press off the icon and name is a rubber band: should the control start its drag before CanDragItems = false
        // took effect, cancel it here.
        if (_bandPressed || _vm?.CurrentPath is not { } folder)
        {
            e.Cancel = true;
            return;
        }

        var names = PrepareDragOut([.. e.Items.OfType<FileEntry>()]);
        if (names.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        DragOutData.Fill(e.Data, [.. names.Select(n => PathUtil.Combine(folder, n))], _logger);
    }
}
