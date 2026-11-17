using System.ComponentModel;
using Mandrill.App.Views;
using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.Shell;
using Mandrill.Presentation.Panes;
using Mandrill.Presentation.Search;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Mandrill.App.Composition;

/// <summary>
/// The panes' context menus and Del keys (WP-3.1, architecture §7, AC-3.1–3.4, 3.6, 3.8): the native shell menu through
/// <see cref="IContextMenuService"/>; Rename → the inline editor; a fallback → the WinUI menu (entries of later work packages
/// disabled); Del/Shift+Del → IFileOperation (WP-3.2: Recycle Bin, or permanent after the shell's warning). A pane that navigates closes its open native menu. UI thread.
/// </summary>
public sealed class PaneContextMenus : IDisposable
{
    private readonly IContextMenuService _menus;
    private readonly IFileOperations _files;
    private readonly IClipboardService _clipboardFiles;
    private readonly ShellCommands _commands;
    private readonly IClipboardText _clipboard;
    private readonly Func<nint> _owner;
    private readonly ILogger _logger;
    private readonly List<(PaneView View, PropertyChangedEventHandler OnPane)> _attached = [];
    private readonly HashSet<PaneView> _deleting = [];
    private readonly bool _forceFallback;
    private PaneView? _inFlightView;
    private CancellationTokenSource? _inFlight;

    /// <summary>Creates the handler.</summary>
    /// <param name="menus">The native menu service.</param>
    /// <param name="files">File operations (Del, the fallback's Delete).</param>
    /// <param name="commands">Open terminal here (fallback and own native items).</param>
    /// <param name="clipboard">Clipboard for "Copy path".</param>
    /// <param name="owner">The main window's handle (dialogs of invoked verbs are owned by it).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="forceFallback">Debug only: always show the fallback menu (MandrillLogging.ForceFallbackMenu).</param>
    /// <param name="clipboardFiles">Files on the clipboard (Cut, Copy, Paste).</param>
    public PaneContextMenus(IContextMenuService menus, IFileOperations files, ShellCommands commands, IClipboardText clipboard, Func<nint> owner, ILogger logger, IClipboardService clipboardFiles, bool forceFallback = false)
    {
        _forceFallback = forceFallback;
        _menus = menus ?? throw new ArgumentNullException(nameof(menus));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _clipboardFiles = clipboardFiles ?? throw new ArgumentNullException(nameof(clipboardFiles));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Handles the menus and Del keys of <paramref name="view"/> (bound to its pane).</summary>
    /// <param name="view">The pane view.</param>
    public void Attach(PaneView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        view.ContextMenuRequested += OnContextMenuRequested;
        view.DeleteRequested += OnDeleteRequested;
        view.ClipboardRequested += OnClipboardRequested;
        view.DropRequested += OnDropRequested;
        view.PropertiesRequested += OnPropertiesRequested;
        PropertyChangedEventHandler onPane = (_, e) =>
        {
            if (e.PropertyName is nameof(PaneViewModel.CurrentPath) or nameof(PaneViewModel.IsThisPc) && ReferenceEquals(_inFlightView, view))
            {
                CancelInFlight(); // §9: only this pane's menu of its previous folder closes (reviewer B2)
            }
        };
        if (view.ViewModel is { } vm)
        {
            vm.PropertyChanged += onPane;
        }

        _attached.Add((view, onPane));
    }

    /// <summary>Closes an open native menu and stops listening.</summary>
    public void Dispose()
    {
        CancelInFlight();
        foreach (var (view, onPane) in _attached)
        {
            view.ContextMenuRequested -= OnContextMenuRequested;
            view.DeleteRequested -= OnDeleteRequested;
            view.ClipboardRequested -= OnClipboardRequested;
            view.DropRequested -= OnDropRequested;
            view.PropertiesRequested -= OnPropertiesRequested;
            if (view.ViewModel is { } vm)
            {
                vm.PropertyChanged -= onPane;
            }
        }

        _attached.Clear();
    }

    /// <summary>The fallback menu (AC-3.4) for <paramref name="target"/>; entries that do not apply are disabled.</summary>
    /// <param name="view">The pane view (rename, new folder, open).</param>
    /// <param name="target">Items or background.</param>
    /// <returns>The flyout.</returns>
    public MenuFlyout BuildFallback(PaneView view, ContextMenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(target);
        bool items = !target.IsBackground;
        bool single = target.Names.Count == 1;
        var flyout = new MenuFlyout();
        void Add(string key, bool enabled, Action? click)
        {
            var item = new MenuFlyoutItem { Text = Localizer.Current[key], IsEnabled = enabled && click is not null };
            if (click is not null)
            {
                item.Click += (_, _) => click();
            }

            flyout.Items.Add(item);
        }

        Add("context.open", single, () => _ = RunSafeAsync(OpenAsync(view, target.Names[0]), "Open from the fallback menu"));
        bool singleFile = single && EntryOf(view, target.Names[0]) is { IsDirectory: false };
        bool singleFolder = single && EntryOf(view, target.Names[0]) is { IsDirectory: true };
        Add("context.openWith", singleFile, () => _ = RunSafeAsync(CheckAsync(view, _commands.OpenWithAsync(PathUtil.Combine(target.Folder, target.Names[0]), _owner(), CancellationToken.None), "Open with"), "Open with"));
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("context.cut", items, () => _ = RunSafeAsync(PutOnClipboardAsync(view, target, cut: true), "Cut"));
        Add("context.copy", items, () => _ = RunSafeAsync(PutOnClipboardAsync(view, target, cut: false), "Copy"));
        Add(
            "context.paste",
            _clipboardFiles.Current is not null && (!items || singleFolder) && view.ViewModel is { FolderError: null },
            () => _ = RunSafeAsync(PasteAsync(view, items ? PathUtil.Combine(target.Folder, target.Names[0]) : target.Folder), "Paste"));
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("context.delete", items, () => _ = RunSafeAsync(CheckAsync(view, DeleteAsync(target, permanent: false), "Delete"), "Delete from the fallback menu"));
        Add("context.rename", single, () => view.FileList.BeginRename(target.Names[0]));
        Add("context.newFolder", !items, view.RequestNewFolder);
        Add("context.newFile", !items, view.RequestNewFile);
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("context.copyPath", true, () => _clipboard.SetText(string.Join("\r\n", items ? target.Names.Select(n => PathUtil.Combine(target.Folder, n)) : [target.Folder])));
        Add("context.terminal", true, () => _ = RunSafeAsync(CheckAsync(view, _commands.OpenTerminalAsync(target.Folder, CancellationToken.None), "Open terminal"), "Open terminal"));
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("context.properties", true, () => _ = RunSafeAsync(PropertiesAsync(view, target), "Properties from the fallback menu"));
        return flyout;
    }

    /// <summary>
    /// Mandrill's own items at the end of the native menu (architecture §7, AC-3.6): Paste for the background while the
    /// clipboard holds files, Open in Terminal for the background and a single folder (Windows Terminal, cmd fallback), Copy
    /// path for items (lead's WP-3.1 list; the background's path is in the path box and in the fallback menu).
    /// </summary>
    /// <param name="view">The pane view (to tell a folder item from a file).</param>
    /// <param name="target">Items or background.</param>
    /// <returns>The items' texts and actions, in menu order.</returns>
    public IReadOnlyList<(string Text, Action Run)> NativeCommands(PaneView view, ContextMenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(target);
        var commands = new List<(string Text, Action Run)>();

        // The shell's background menu has no Paste (Explorer's comes from its own view, not the folder's menu); a folder
        // item's menu has the shell's own.
        if (target.IsBackground && _clipboardFiles.Current is not null && view.ViewModel is { FolderError: null })
        {
            commands.Add((Localizer.Current["context.paste"], () => _ = RunSafeAsync(PasteAsync(view, target.Folder), "Paste")));
        }

        string? terminalFolder = target.IsBackground ? target.Folder
            : target.Names.Count == 1 && EntryOf(view, target.Names[0]) is { IsDirectory: true } ? PathUtil.Combine(target.Folder, target.Names[0])
            : null;
        if (terminalFolder is not null)
        {
            commands.Add((Localizer.Current["context.terminal"], () => _ = RunSafeAsync(CheckAsync(view, _commands.OpenTerminalAsync(terminalFolder, CancellationToken.None), "Open terminal"), "Open terminal")));
        }

        if (!target.IsBackground)
        {
            commands.Add((Localizer.Current["context.copyPath"], () => _clipboard.SetText(string.Join("\r\n", target.Names.Select(n => PathUtil.Combine(target.Folder, n))))));
        }

        return commands;
    }

    private static FileEntry? EntryOf(PaneView view, string name)
        => view.ViewModel is { } vm && vm.Items.Current is { } shown && shown.IndexOfName(name) is >= 0 and var index ? vm.Items[index] as FileEntry : null;

    private static async Task OpenAsync(PaneView view, string name)
    {
        if (view.ViewModel is { } vm && vm.Items.Current is { } shown && shown.IndexOfName(name) is >= 0 and var index && vm.Items[index] is FileEntry entry)
        {
            await vm.OpenAsync(entry);
        }
    }

    // WP-3.2 (lead Q1): Delete goes through IFileOperation (Recycle Bin with undo, or permanent after the native warning).
    private async Task<ContextMenuResult> DeleteAsync(ContextMenuTarget target, bool permanent)
    {
        var result = await _files.DeleteAsync([.. target.Names.Select(n => PathUtil.Combine(target.Folder, n))], permanent, CancellationToken.None);
        return result.Outcome switch
        {
            FileOperationOutcome.Failed => new ContextMenuResult(ContextMenuOutcome.Failed, "delete", HResult: result.Failures.Count > 0 ? result.Failures[0].HResult : 0),
            FileOperationOutcome.Aborted => ContextMenuResult.Dismissed,
            _ => new ContextMenuResult(ContextMenuOutcome.Invoked, "delete"),
        };
    }

    private Task PropertiesAsync(PaneView view, ContextMenuTarget target)
        => target.IsBackground
            ? CheckAsync(view, _menus.ShowPropertiesAsync(target.Folder, _owner(), CancellationToken.None), "Properties")
            : CheckAsync(view, _menus.InvokeVerbAsync(target, "properties", _owner(), false, CancellationToken.None), "Properties");

    // Reviewer B5 (lead): a failed shell command tells the user in the pane's status bar; the HRESULT goes to the log.
    private async Task CheckAsync(PaneView view, Task<ContextMenuResult> task, string what)
    {
        var result = await task;
        if (result.Outcome == ContextMenuOutcome.Failed)
        {
            ReportFailure(view, what, result.HResult);
        }
    }

    private async Task CheckAsync(PaneView view, Task<bool> task, string what)
    {
        if (!await task)
        {
            ReportFailure(view, what, 0);
        }
    }

    private void ReportFailure(PaneView view, string what, int hresult)
    {
        _logger.LogWarning("{What} failed: 0x{Hr:X8}", what, hresult);
        view.ViewModel?.ReportStatus(new PaneStatus("context.error.failed", true));
    }

    private async Task RunSafeAsync(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            // A menu click must never take the app down; the services log their own failures.
            _logger.LogError(ex, "{What} failed", what);
        }
    }

    private void OnContextMenuRequested(object? sender, PaneContextMenuRequest request)
    {
        if (sender is PaneView view)
        {
            _ = RunSafeAsync(HandleRequestAsync(view, request), "Context menu");
        }
    }

    // Ends the request in flight: its pending build or track is skipped (never shown), an open native menu closes. A verb
    // picked in that menu at the very moment it is superseded is dropped, not run (accepted: the user already clicked again).
    private void CancelInFlight()
    {
        if (_inFlight is { } previous)
        {
            previous.Cancel();
            _menus.Cancel();
            _inFlight = null;
            _inFlightView = null;
        }
    }

    /// <summary>
    /// Runs a context menu request of <paramref name="view"/>: the native menu (or the fallback), then what the user chose.
    /// A result for a folder the pane has left meanwhile is dropped (QA m1). The view's ContextMenuRequested handler and tests.
    /// </summary>
    /// <param name="view">The pane view.</param>
    /// <param name="request">The request.</param>
    /// <returns>A task that completes when the choice was handled.</returns>
    public async Task HandleRequestAsync(PaneView view, PaneContextMenuRequest request)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(request);
        // Reviewer B3: a new right-click supersedes the one in flight (a slow build never pops up later at the old point).
        CancelInFlight();
        using var mine = new CancellationTokenSource();
        _inFlight = mine;
        _inFlightView = view;
        // A This PC drive row or a tree node (WP6) is not an item of the pane's list: the shell's own menu only.
        bool outside = request.FromTree || PathUtil.IsThisPc(request.Target.Folder);
        var commands = outside ? [] : NativeCommands(view, request.Target);
        ContextMenuResult result;
        try
        {
            result = _forceFallback
                ? ContextMenuResult.Fallback(FallbackReason.BuildFailed)
                : await _menus.ShowAsync(
                    outside
                        ? new ContextMenuRequest(request.Target, _owner(), request.ClientX, request.ClientY, request.Extended, Commands: null, CanRename: false)
                        : new ContextMenuRequest(request.Target, _owner(), request.ClientX, request.ClientY, request.Extended, [.. commands.Select(c => c.Text)],
                            OwnTerminal: commands.Any(c => c.Text == Localizer.Current["context.terminal"])),
                    mine.Token);
        }
        catch (OperationCanceledException) when (mine.IsCancellationRequested)
        {
            return; // superseded, or its pane navigated
        }
        finally
        {
            if (ReferenceEquals(_inFlight, mine))
            {
                _inFlight = null;
                _inFlightView = null;
            }
        }

        if (view.ViewModel is not { } vm)
        {
            return;
        }

        // A list item is relative to the pane's location: dropped once the pane shows another one. A tree node is a full path.
        if (!request.FromTree && !PathUtil.PathEquals(vm.ToState().Path, request.Target.Folder))
        {
            return; // the pane shows another location now (a folder, or This PC): nothing may act on a same-named item there
        }

        if (outside)
        {
            await HandleOutsideResultAsync(view, vm, request, result);
            return;
        }

        switch (result.Outcome)
        {
            case ContextMenuOutcome.Open when request.Target.Names.Count == 1:
                // QA N1: like a double-click: a folder navigates this pane, a file opens through the shell.
                if (EntryOf(view, request.Target.Names[0]) is { } entry)
                {
                    await vm.OpenAsync(entry);
                }

                break;
            case ContextMenuOutcome.Command when result.Command >= 0 && result.Command < commands.Count:
                commands[result.Command].Run();
                break;
            case ContextMenuOutcome.Failed:
                ReportFailure(view, result.Verb ?? "Context menu command", result.HResult);
                break;
            case ContextMenuOutcome.Rename when request.Target.Names.Count == 1:
                view.FileList.BeginRename(request.Target.Names[0]);
                break;
            case ContextMenuOutcome.NewFolder:
                view.RequestNewFolder(); // Ctrl+Shift+N's path: unique name, selected, inline editor; failures in the status bar
                break;
            case ContextMenuOutcome.Fallback:
                _logger.LogInformation("Fallback context menu ({Reason}) in {Folder}", result.Reason, request.Target.Folder);
                BuildFallback(view, request.Target).ShowAt(request.Anchor, new FlyoutShowOptions { Position = request.AnchorPoint });
                break;
        }
    }

    // A drive row's or a tree node's menu: Open navigates the pane there; the fallback offers Properties only (ADR-0015 §6).
    private async Task HandleOutsideResultAsync(PaneView view, PaneViewModel vm, PaneContextMenuRequest request, ContextMenuResult result)
    {
        string folder = request.Target.Folder;
        string path = folder == ContextMenuTarget.DesktopRoot || PathUtil.IsThisPc(folder) ? request.Target.Names[0] : PathUtil.Combine(folder, request.Target.Names[0]);
        switch (result.Outcome)
        {
            case ContextMenuOutcome.Open when PathUtil.IsThisPc(path):
                await vm.NavigateAsync(path, NavOrigin.Open); // WP6b: the This PC node
                break;
            case ContextMenuOutcome.Open:
                await vm.NavigateAndSelectAsync(path, null); // CF-2: a failing drive or folder keeps the pane where it is
                break;
            case ContextMenuOutcome.Fallback when PathUtil.IsThisPc(path):
                // WP6b: no fallback for This PC (a Properties sheet for it is not a file-system item; unverified), no menu.
                _logger.LogInformation("No fallback menu for This PC ({Reason})", result.Reason);
                break;
            case ContextMenuOutcome.Failed:
                ReportFailure(view, result.Verb ?? "Context menu command", result.HResult);
                break;
            case ContextMenuOutcome.Fallback:
                _logger.LogInformation("Fallback menu ({Reason}) for {Path}", result.Reason, path);
                var flyout = new MenuFlyout();
                var properties = new MenuFlyoutItem { Text = Localizer.Current["context.properties"] };
                properties.Click += (_, _) => _ = RunSafeAsync(CheckAsync(view, _menus.ShowPropertiesAsync(path, _owner(), CancellationToken.None), "Properties"), "Properties");
                flyout.Items.Add(properties);
                flyout.ShowAt(request.Anchor, new FlyoutShowOptions { Position = request.AnchorPoint });
                break;
        }
    }

    // QA m2: a repeated Del (auto-repeat, double press) is ignored while this pane's delete is still running.
    private async Task DeleteOnceAsync(PaneView pane, ContextMenuTarget target, bool permanent)
    {
        if (!_deleting.Add(pane))
        {
            return;
        }

        try
        {
            await CheckAsync(pane, DeleteAsync(target, permanent), permanent ? "Shift+Del" : "Del");
        }
        finally
        {
            _deleting.Remove(pane);
        }
    }

    // AC-3.13: CF_HDROP + Preferred DropEffect, so Explorer pastes them too.
    private async Task PutOnClipboardAsync(PaneView view, ContextMenuTarget target, bool cut)
    {
        if (!await _clipboardFiles.SetFilesAsync([.. target.Names.Select(n => PathUtil.Combine(target.Folder, n))], cut, CancellationToken.None))
        {
            ReportFailure(view, cut ? "Cut" : "Copy", 0);
        }
    }

    /// <summary>
    /// Pastes the clipboard's files into <paramref name="folder"/> (AC-3.13–3.15): cut → move (into the items' own folder:
    /// nothing to do), then the clipboard is emptied like Explorer; copied → copy ("name - Copy" in their own folder).
    /// </summary>
    /// <param name="view">The pane view (status messages).</param>
    /// <param name="folder">Destination folder.</param>
    /// <returns>A task that completes when the paste finished.</returns>
    public async Task PasteAsync(PaneView view, string folder)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(folder);
        if (view.ViewModel is { FolderError: not null })
        {
            return; // QA m4: the pane shows an error, not a folder (like New folder)
        }

        if (_clipboardFiles.Current is not { } files)
        {
            return; // AC-3.15: no files on the clipboard
        }

        // A cut item already in the destination stays where it is (Explorer); only the others move.
        var paths = files.Cut
            ? [.. files.Paths.Where(p => PathUtil.Parent(p) is not { } parent || !PathUtil.PathEquals(parent, folder))]
            : files.Paths;
        if (paths.Count == 0)
        {
            return;
        }

        var result = files.Cut
            ? await _files.MoveAsync(paths, folder, CancellationToken.None)
            : await _files.CopyAsync(paths, folder, CancellationToken.None);
        if (result.Outcome == FileOperationOutcome.Failed)
        {
            ReportFailure(view, files.Cut ? "Paste (move)" : "Paste (copy)", result.Failures.Count > 0 ? result.Failures[0].HResult : 0);
        }
        else if (files.Cut && result.Outcome == FileOperationOutcome.Completed)
        {
            await _clipboardFiles.EmptyAfterCutPasteAsync(files, CancellationToken.None);
        }
    }

    /// <summary>Runs a drop (WP-3.4, AC-3.21): copy or move through IFileOperations; a failure shows in the pane's status.</summary>
    /// <param name="view">The pane view.</param>
    /// <param name="request">What was dropped where.</param>
    /// <returns>A task that completes when the operation finished.</returns>
    public async Task DropAsync(PaneView view, PaneDropRequest request)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(request);
        var result = request.Action == DropAction.Copy
            ? await _files.CopyAsync(request.Sources, request.Target, CancellationToken.None)
            : await _files.MoveAsync(request.Sources, request.Target, CancellationToken.None);
        if (result.Outcome == FileOperationOutcome.Failed)
        {
            ReportFailure(view, request.Action == DropAction.Copy ? "Drop (copy)" : "Drop (move)", result.Failures.Count > 0 ? result.Failures[0].HResult : 0);
        }
    }

    private void OnDropRequested(object? sender, PaneDropRequest request)
    {
        if (sender is PaneView view)
        {
            _ = RunSafeAsync(DropAsync(view, request), "Drop");
        }
    }

    private void OnClipboardRequested(object? sender, PaneClipboardAction action)
    {
        if (sender is not PaneView { ViewModel: { CurrentPath: { } folder } vm } view)
        {
            return;
        }

        if (action == PaneClipboardAction.Paste)
        {
            _ = RunSafeAsync(PasteAsync(view, folder), "Ctrl+V");
            return;
        }

        var names = vm.Items.Selection.SelectedEntries().Select(e => e.Name).ToArray();
        if (names.Length == 0)
        {
            return;
        }

        if (action == PaneClipboardAction.CopyAsPath)
        {
            _clipboard.SetText(PathUtil.CopyAsPathText(names.Select(n => PathUtil.Combine(folder, n))));
            return;
        }

        _ = RunSafeAsync(PutOnClipboardAsync(view, new ContextMenuTarget(folder, names), action == PaneClipboardAction.Cut), action.ToString());
    }

    // WP-3.6 (Explorer): Alt+Enter shows the selection's Properties, or the folder's with nothing selected.
    private void OnPropertiesRequested(object? sender, EventArgs e)
    {
        if (sender is PaneView { ViewModel: { CurrentPath: { } folder } vm } view)
        {
            var names = vm.Items.Selection.SelectedEntries().Select(x => x.Name).ToArray();
            _ = RunSafeAsync(PropertiesAsync(view, new ContextMenuTarget(folder, names)), "Alt+Enter");
        }
    }

    private void OnDeleteRequested(object? sender, bool permanent)
    {
        if (sender is PaneView { ViewModel: { CurrentPath: { } folder } vm } pane)
        {
            var names = vm.Items.Selection.SelectedEntries().Select(e => e.Name).ToArray();
            if (names.Length > 0)
            {
                _ = RunSafeAsync(DeleteOnceAsync(pane, new ContextMenuTarget(folder, names), permanent), "Del");
            }
        }
    }
}
