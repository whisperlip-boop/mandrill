using Mandrill.App.Views;
using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.Search;
using Mandrill.Core.Shell;
using Mandrill.Presentation.Panes;
using Mandrill.Presentation.Search;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Mandrill.App.Composition;

/// <summary>
/// The context menu of one search popup's results (WP-3.5, architecture §7, AC-3.5). Results of one folder get the native
/// shell menu (no Rename: the results have no inline editor) with Open containing folder and Copy path appended; results
/// of several folders, or a native menu that cannot be shown, get the fallback menu. After a shell command the results it
/// acted on are re-checked and the vanished ones leave the list (lead Q2). UI thread.
/// </summary>
public sealed class SearchContextMenus : IDisposable
{
    private readonly IContextMenuService _menus;
    private readonly IFileOperations _files;
    private readonly Func<nint> _owner;
    private readonly ILogger _logger;
    private readonly bool _forceFallback;
    private SearchView? _view;
    private SearchViewModel? _vm;
    private CancellationTokenSource? _inFlight;
    private bool _disposed;

    /// <summary>Creates the handler.</summary>
    /// <param name="menus">The native menu service.</param>
    /// <param name="files">File operations (the fallback's Delete).</param>
    /// <param name="owner">
    /// The popup's window handle: the menu, Properties sheets, verb and file-operation dialogs are owned by it (§7; closing
    /// the popup closes them too).
    /// </param>
    /// <param name="logger">Logger.</param>
    /// <param name="forceFallback">Debug only: always show the fallback menu (MandrillLogging.ForceFallbackMenu).</param>
    public SearchContextMenus(IContextMenuService menus, IFileOperations files, Func<nint> owner, ILogger logger, bool forceFallback = false)
    {
        _menus = menus ?? throw new ArgumentNullException(nameof(menus));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _forceFallback = forceFallback;
    }

    /// <summary>Handles the menus of <paramref name="view"/> over <paramref name="vm"/>.</summary>
    /// <param name="view">The search view.</param>
    /// <param name="vm">Its view model.</param>
    /// <exception cref="InvalidOperationException">Already attached.</exception>
    public void Attach(SearchView view, SearchViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(vm);
        if (_view is not null)
        {
            throw new InvalidOperationException("The search menus are already attached");
        }

        _view = view;
        _vm = vm;
        view.ContextMenuRequested += OnContextMenuRequested;
    }

    /// <summary>Closes an open native menu and stops listening (the popup closed).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelInFlight();
        if (_view is not null)
        {
            _view.ContextMenuRequested -= OnContextMenuRequested;
        }
    }

    /// <summary>Mandrill's own items at the end of the native menu (AC-3.5): Open containing folder, Copy path.</summary>
    /// <param name="vm">The search view model.</param>
    /// <param name="request">The request.</param>
    /// <returns>The items' texts and actions, in menu order.</returns>
    public IReadOnlyList<(string Text, Action Run)> NativeCommands(SearchViewModel vm, SearchContextMenuRequest request)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(request);
        return
        [
            (Localizer.Current["search.menu.openFolder"], () => _ = RunSafeAsync(vm.OpenContainingFolderAsync(request.Focused), "Open containing folder")),
            (Localizer.Current["search.menu.copyPath"], () => vm.CopyPath(request.Hits)),
        ];
    }

    /// <summary>
    /// The fallback menu (lead Q3): Open and Open containing folder on the row the menu was opened on, Copy path and Delete on
    /// all selected results (one operation across folders), Properties for a single result.
    /// </summary>
    /// <param name="vm">The search view model.</param>
    /// <param name="request">The request.</param>
    /// <returns>The flyout.</returns>
    public MenuFlyout BuildFallback(SearchViewModel vm, SearchContextMenuRequest request)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(request);
        bool single = request.Hits.Count == 1;
        var flyout = new MenuFlyout();
        void Add(string key, bool enabled, Action click)
        {
            var item = new MenuFlyoutItem { Text = Localizer.Current[key], IsEnabled = enabled };
            item.Click += (_, _) => click();
            flyout.Items.Add(item);
        }

        Add("search.menu.open", true, () => _ = RunSafeAsync(vm.OpenAsync(request.Focused), "Open from the search fallback menu"));
        Add("search.menu.openFolder", true, () => _ = RunSafeAsync(vm.OpenContainingFolderAsync(request.Focused), "Open containing folder"));
        Add("search.menu.copyPath", true, () => vm.CopyPath(request.Hits));
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("context.delete", true, () => _ = RunSafeAsync(DeleteAsync(vm, request.Hits), "Delete from the search fallback menu"));
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("context.properties", single, () => _ = RunSafeAsync(PropertiesAsync(vm, request.Hits[0]), "Properties from the search fallback menu"));
        return flyout;
    }

    /// <summary>
    /// Runs a context menu request: the native menu for results of one folder (or the fallback), then what the user chose.
    /// A choice for rows of an earlier search is dropped. The view's ContextMenuRequested handler and tests.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>A task that completes when the choice was handled.</returns>
    public async Task HandleRequestAsync(SearchContextMenuRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_vm is not { } vm || _disposed || request.Hits.Count == 0)
        {
            return;
        }

        // A new right-click supersedes the one in flight (reviewer-wp31 B3).
        CancelInFlight();
        using var mine = new CancellationTokenSource();
        _inFlight = mine;
        var commands = NativeCommands(vm, request);
        ContextMenuResult result;
        try
        {
            result = SearchViewModel.MenuTarget(request.Hits) is not { } target ? ContextMenuResult.Fallback(FallbackReason.MixedParents)
                : _forceFallback ? ContextMenuResult.Fallback(FallbackReason.BuildFailed)
                : await _menus.ShowAsync(
                    new ContextMenuRequest(target, _owner(), request.ClientX, request.ClientY, request.Extended, [.. commands.Select(c => c.Text)], CanRename: false),
                    mine.Token);
        }
        catch (OperationCanceledException) when (mine.IsCancellationRequested)
        {
            return; // superseded, or the popup closed
        }
        finally
        {
            if (ReferenceEquals(_inFlight, mine))
            {
                _inFlight = null;
            }
        }

        if (_disposed || vm.SearchId != request.SearchId)
        {
            return; // the popup closed or shows another search's rows now
        }

        switch (result.Outcome)
        {
            case ContextMenuOutcome.Open when request.Hits.Count == 1:
                // Like a double-click: a folder opens in the pane, a file through the shell (AC-2.20).
                await vm.OpenAsync(request.Hits[0]);
                break;
            case ContextMenuOutcome.Command when result.Command >= 0 && result.Command < commands.Count:
                commands[result.Command].Run();
                break;
            case ContextMenuOutcome.Invoked:
                await vm.RecheckAsync(request.Hits, follow: true); // lead Q2: a deleted or moved result leaves the list
                break;
            case ContextMenuOutcome.Failed:
                ReportFailure(vm, result.Verb ?? "Context menu command", result.HResult);
                break;
            case ContextMenuOutcome.Rename:
                _logger.LogInformation("Rename from a search result's menu is not supported"); // CMF_CANRENAME is off
                break;
            case ContextMenuOutcome.Fallback:
                _logger.LogInformation("Fallback context menu on search results ({Reason}, {Count} item(s))", result.Reason, request.Hits.Count);
                BuildFallback(vm, request).ShowAt(request.Anchor, new FlyoutShowOptions { Position = request.AnchorPoint });
                break;
        }
    }

    // Lead Q3: one IFileOperation across folders (Recycle Bin, Explorer's confirmation), then the gone rows leave the list.
    // §7 owner rule (architect4 m1): its dialogs are owned by the popup, above it and modal to it.
    private async Task DeleteAsync(SearchViewModel vm, IReadOnlyList<SearchHit> hits)
    {
        var result = await _files.DeleteAsync([.. hits.Select(h => PathUtil.Combine(h.Directory, h.Name))], permanent: false, _owner(), CancellationToken.None);
        if (result.Outcome == FileOperationOutcome.Failed)
        {
            ReportFailure(vm, "Delete", result.Failures.Count > 0 ? result.Failures[0].HResult : 0);
        }

        if (result.Outcome != FileOperationOutcome.Aborted)
        {
            await vm.RecheckAsync(hits, follow: false);
        }
    }

    private async Task PropertiesAsync(SearchViewModel vm, SearchHit hit)
    {
        if (!await _menus.ShowPropertiesAsync(PathUtil.Combine(hit.Directory, hit.Name), _owner(), CancellationToken.None))
        {
            ReportFailure(vm, "Properties", 0);
        }
    }

    private void ReportFailure(SearchViewModel vm, string what, int hresult)
    {
        _logger.LogWarning("{What} on search results failed: 0x{Hr:X8}", what, hresult);
        vm.ReportStatus(new PaneStatus("context.error.failed", true));
    }

    private void CancelInFlight()
    {
        if (_inFlight is { } previous)
        {
            previous.Cancel();
            _menus.Cancel();
            _inFlight = null;
        }
    }

    private void OnContextMenuRequested(object? sender, SearchContextMenuRequest request)
        => _ = RunSafeAsync(HandleRequestAsync(request), "Search results context menu");

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
}
