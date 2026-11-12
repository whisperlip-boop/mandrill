using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mandrill.App.Views;

/// <summary>A drop on a pane's list or tree: what was dropped where, and what to do (WP-3.4, AC-3.17–3.21).</summary>
/// <param name="Sources">Full paths of the dropped items.</param>
/// <param name="Target">The target folder (a folder row or tree node, else the pane's folder).</param>
/// <param name="Action">Copy or Move (Explorer's rules, AC-3.18).</param>
public sealed record PaneDropRequest(IReadOnlyList<string> Sources, string Target, DropAction Action);

/// <summary>
/// The drop side of a pane's file list or folder tree (WP-3.4, 3.4b, architecture §7 "Drag &amp; drop", ADR-0011, S3): the drag
/// is read once per DragEnter, DragOver decides with <see cref="DragDropRules.Decide"/> and shows "Move to/Copy to" or why the
/// drop is refused, and Drop hands the decided copy or move to the pane. The element decides only the target folder under
/// the pointer. UI thread.
/// </summary>
public sealed class FolderDropTarget
{
    private readonly Func<DependencyObject?, string?> _targetAt;
    private readonly Action<PaneDropRequest> _drop;
    private readonly Func<ILogger> _log;

    // The drag's sources, read once per drag in DragEnter (architect note 1): DragOver then only compares strings. DragEnter
    // bubbles up from every row the pointer crosses, so the view it was read from is remembered. _dragHasFiles: the drag
    // carries a file format, so no paths means virtual items (a zip's contents) and the caption says they cannot be dropped.
    private IReadOnlyList<string> _dragSources = [];
    private DataPackageView? _dragView;
    private bool _dragHasFiles;

    /// <summary>Makes <paramref name="element"/> a drop target.</summary>
    /// <param name="element">The list or tree.</param>
    /// <param name="targetAt">The target folder for the element under the pointer; null where nothing may be dropped.</param>
    /// <param name="drop">Runs a decided drop (the pane's copy or move).</param>
    /// <param name="logger">The element's logger (read at use: a list may be built before its pane attaches).</param>
    public FolderDropTarget(UIElement element, Func<DependencyObject?, string?> targetAt, Action<PaneDropRequest> drop, Func<ILogger> logger)
    {
        ArgumentNullException.ThrowIfNull(element);
        _targetAt = targetAt ?? throw new ArgumentNullException(nameof(targetAt));
        _drop = drop ?? throw new ArgumentNullException(nameof(drop));
        _log = logger ?? throw new ArgumentNullException(nameof(logger));
        element.AllowDrop = true;

        // handledEventsToo: a TreeViewItem or list container may mark the drag events handled for its own reordering.
        element.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(OnDragEnter), handledEventsToo: true);
        element.AddHandler(UIElement.DragOverEvent, new DragEventHandler(OnDragOver), handledEventsToo: true);
        element.AddHandler(UIElement.DropEvent, new DragEventHandler(OnDrop), handledEventsToo: true);
    }

    /// <summary>
    /// What a drop of <paramref name="sources"/> into <paramref name="target"/> does with these modifiers: null when it is
    /// rejected (onto itself, into a descendant, nothing to drop), there is nothing to do (same folder) or the source allows
    /// no matching effect.
    /// </summary>
    /// <param name="sources">The dragged items.</param>
    /// <param name="target">The target folder.</param>
    /// <param name="ctrl">Ctrl is held.</param>
    /// <param name="shift">Shift is held.</param>
    /// <param name="allowed">The effects the source allows.</param>
    /// <returns>The action, or null.</returns>
    public static DropAction? PlanDrop(IReadOnlyList<string> sources, string target, bool ctrl, bool shift, DropEffects allowed = DropEffects.Copy | DropEffects.Move)
        => DragDropRules.Decide(sources, target, ctrl, shift, own: true, allowed).Action is var action and not DropAction.None ? action : null;

    /// <summary>
    /// The caption while hovering (QA M1, AC-3.19): "Move to/Copy to {folder}", or why the drop is refused (onto itself or into
    /// a subfolder, virtual items); null hides it (nothing to do, no target).
    /// </summary>
    /// <param name="outcome">The decided drop; null when the drag carries no paths.</param>
    /// <param name="target">The target folder.</param>
    /// <param name="hasFiles">The drag carries a file format (no paths then means virtual items).</param>
    /// <returns>The caption, or null.</returns>
    public static string? DropCaption(DropOutcome? outcome, string target, bool hasFiles)
    {
        ArgumentNullException.ThrowIfNull(target);
        return outcome switch
        {
            null => hasFiles ? Localizer.Current["drag.refused.noItems"] : null,
            { Action: DropAction.Copy } => Localizer.Current.Format("drag.copyTo", ("folder", PathUtil.LeafName(target))),
            { Action: DropAction.Move } => Localizer.Current.Format("drag.moveTo", ("folder", PathUtil.LeafName(target))),
            { Check: DropCheck.OntoItself or DropCheck.IntoDescendant } => Localizer.Current["drag.refused.descendant"],
            _ => null,
        };
    }

    // Architect note 2: compare long, normalized paths (Explorer may hand over 8.3 names). Resolving a short name reads the
    // disk, so it runs off the UI thread.
    private static Task<IReadOnlyList<string>> CanonicalAsync(IReadOnlyList<string> paths)
        => Task.Run<IReadOnlyList<string>>(() => [.. paths.Select(p => FolderAccessGrant.Canonicalize(p, FolderAccessGrant.ToLongPath) ?? p)]);

    private static DropEffects Allowed(DragEventArgs e)
        => (e.AllowedOperations.HasFlag(DataPackageOperation.Copy) ? DropEffects.Copy : DropEffects.None)
            | (e.AllowedOperations.HasFlag(DataPackageOperation.Move) ? DropEffects.Move : DropEffects.None);

    private static bool Ctrl(DragEventArgs e) => e.Modifiers.HasFlag(DragDropModifiers.Control);

    private static bool Shift(DragEventArgs e) => e.Modifiers.HasFlag(DragDropModifiers.Shift);

    // The paths of a drop: Mandrill's own format, then FileDrop (CF_HDROP; WP-3.0: 52-84 ms per 1,000), then StorageItems.
    // Virtual items (a zip's contents) have no paths and are left out. UI thread (the view must stay on its thread, WP-3.0).
    private async Task<IReadOnlyList<string>> ReadPathsAsync(DataPackageView view, bool all)
    {
        // QA m1: all paths of the string formats (cheap), so hovering refuses what the drop would refuse. StorageItems cost
        // 2.1 s per 1,000 (S3): DragEnter reads only the first of them; Drop reads them all.
        if (view.Contains(DragDropRules.PathsFormat) && await view.GetDataAsync(DragDropRules.PathsFormat) is string text)
        {
            return await CanonicalAsync(DragDropRules.ParsePaths(text));
        }

        if (view.Contains("FileDrop") && await view.GetDataAsync("FileDrop") is IRandomAccessStream stream)
        {
            using var input = stream.AsStreamForRead();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            var parsed = DragDropRules.ParseFileDrop(buffer.ToArray());
            if (parsed.Count > 0)
            {
                return await CanonicalAsync(parsed);
            }
        }

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            // GetStorageItemsAsync stays on the UI thread (DataPackageView rule); .Path per item does not (§7, S3: 2.1 s per
            // 1,000; the items are agile).
            var items = await view.GetStorageItemsAsync();
            var chosen = all ? items : items.Take(1).ToList();
            var paths = await Task.Run(() => chosen.Select(i => i.Path).ToList());
            int virtualItems = paths.Count(string.IsNullOrEmpty);
            if (virtualItems > 0)
            {
                _log().LogInformation("A drag carries {Count} item(s) without a file-system path (ignored)", virtualItems);
            }

            return await CanonicalAsync([.. paths.Where(p => !string.IsNullOrEmpty(p))]);
        }

        return [];
    }

    private async void OnDragEnter(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(e.DataView, _dragView))
        {
            return; // the same drag entering another row: already read
        }

        var deferral = e.GetDeferral();
        try
        {
            _dragView = e.DataView;
            _dragHasFiles = e.DataView.Contains("FileDrop") || e.DataView.Contains(StandardDataFormats.StorageItems);
            _dragSources = await ReadPathsAsync(e.DataView, all: false);
        }
        catch (Exception ex)
        {
            _dragSources = [];
            _log().LogWarning(ex, "Reading a drag failed");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // shortcut: decides against the raw target (Drop canonicalizes it, off the UI thread); a node named by an 8.3 or
        // alias path may show a caption that differs from the drop's outcome. Upgrade: canonicalize TreeNode paths when built.
        string? target = _targetAt(e.OriginalSource as DependencyObject);
        var outcome = target is null || _dragSources.Count == 0 ? null : DragDropRules.Decide(_dragSources, target, Ctrl(e), Shift(e), own: true, Allowed(e));
        e.AcceptedOperation = outcome?.Action switch
        {
            DropAction.Copy => DataPackageOperation.Copy,
            DropAction.Move => DataPackageOperation.Move,
            _ => DataPackageOperation.None,
        };

        // QA M1 (AC-3.19): a refused drop never reaches Drop (the effect is None), so the reason shows here while hovering.
        string? caption = target is null ? null : DropCaption(outcome, target, _dragHasFiles);
        e.DragUIOverride.Caption = caption ?? string.Empty;
        e.DragUIOverride.IsCaptionVisible = caption is not null; // nothing left over from the previous row
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            string? target = _targetAt(e.OriginalSource as DependencyObject);
            if (target is null)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            bool own = e.DataView.Contains(DragDropRules.PathsFormat);
            var sources = await ReadPathsAsync(e.DataView, all: true);

            // Architect note 3 (3.4b): a tree node may name its folder another way (Quick access): canonical like the sources.
            target = (await CanonicalAsync([target]))[0];

            // Lead m6 (Explorer) is in Decide: Ctrl-drag into the own folder copies ("name - Copy"); a move leaves out the
            // items already in the target (QA m3).
            var outcome = DragDropRules.Decide(sources, target, Ctrl(e), Shift(e), own, Allowed(e));
            if (outcome.Action == DropAction.None)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                if (outcome.Check != DropCheck.SameFolder)
                {
                    _log().LogInformation("Drop rejected: {Check} into {Target}", outcome.Check, target);
                }

                return;
            }

            _drop(new PaneDropRequest(outcome.Sources, target, outcome.Action));

            // §7 / N1: Mandrill performs the move itself, so an external source must not delete as well (optimized move).
            e.AcceptedOperation = outcome.Reported switch
            {
                DropEffects.Copy => DataPackageOperation.Copy,
                DropEffects.Move => DataPackageOperation.Move,
                _ => DataPackageOperation.None,
            };
        }
        catch (Exception ex)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            _log().LogError(ex, "A drop failed");
        }
        finally
        {
            _dragSources = [];
            _dragView = null;
            _dragHasFiles = false;
            deferral.Complete();
        }
    }
}
