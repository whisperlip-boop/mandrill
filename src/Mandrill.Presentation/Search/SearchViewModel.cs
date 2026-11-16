using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mandrill.Core.Index;
using Mandrill.Core.IO;
using Mandrill.Core.Search;
using Mandrill.Core.Shell;
using Mandrill.Presentation.Common;
using Mandrill.Presentation.Panes;
using Microsoft.Extensions.Logging;

namespace Mandrill.Presentation.Search;

/// <summary>What the search popup shows (ui-spec §3 states).</summary>
public enum SearchState
{
    /// <summary>Nothing searched yet: the hint.</summary>
    Initial,

    /// <summary>Running: "Searching… N found" and the progress bar.</summary>
    Searching,

    /// <summary>Finished (or stopped) with results: "N results · s".</summary>
    Done,

    /// <summary>Finished without results: "No files match …".</summary>
    NoResults,

    /// <summary>Failed: "Search failed. See log." with Retry.</summary>
    Error,
}

/// <summary>
/// The search popup of one pane (architecture §3 Presentation.Search, AC-2.13–2.25). A search runs in the pane's current
/// folder; Refresh repeats the last one in its original folder. A new search cancels the running one, and batches carry
/// the request id, so a stale search never shows (AC-2.19). Results follow the pane's effective show-hidden; they arrive
/// in walk order, and a header sort is applied when the search is done (on a worker). Opening a result that no longer
/// exists removes it, reports it to the index and says so (AC-2.23). UI thread.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject, IDisposable
{
    /// <summary>Display cap (ui-spec §3: 100,000 rows, said in the footer).</summary>
    public const int DisplayCap = 100_000;

    // WP-3.5 (lead Q2): a native verb finishes after its menu closed (the delete confirmation, the shell's own copy engine),
    // so the hits' folder is followed this long after the command and every change in it re-checks the hits still shown.
    private static readonly TimeSpan FollowTime = TimeSpan.FromMinutes(1);

    private readonly PaneViewModel _pane;
    private readonly PaneContext _context;
    private readonly ISearchService _search;
    private readonly ISearchIndex _index;
    private readonly IClipboardText _clipboard;
    private readonly Func<string, PathStat> _stat;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _running;
    private long _requestId;
    private bool _disposed;
    private FollowedHits? _follow;

    /// <summary>Creates the view model.</summary>
    /// <param name="pane">The pane the popup belongs to (its folder is the scope; results open in it).</param>
    /// <param name="context">The panes' shared services (launcher, hidden-files setting, dispatcher, logging).</param>
    /// <param name="search">Search engine.</param>
    /// <param name="index">The index (stale results are reported to it).</param>
    /// <param name="clipboard">Clipboard for "Copy path".</param>
    /// <param name="stat">Stats a result before "Open containing folder" (worker thread); null for <see cref="PathStat.Win32"/>.</param>
    public SearchViewModel(PaneViewModel pane, PaneContext context, ISearchService search, ISearchIndex index, IClipboardText clipboard, Func<string, PathStat>? stat = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _stat = stat ?? PathStat.Win32;
        _dispatcher = context.Dispatcher;
        _logger = context.LoggerFactory.CreateLogger<SearchViewModel>();
        _pane.PropertyChanged += OnPaneChanged;
    }

    /// <summary>The query being edited (bound to the query box).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial string QueryText { get; set; } = string.Empty;

    /// <summary>What the popup shows.</summary>
    [ObservableProperty]
    public partial SearchState State { get; private set; } = SearchState.Initial;

    /// <summary>True while a search runs ("Stop" instead of "Search").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>Rows found so far (footer).</summary>
    [ObservableProperty]
    public partial int Found { get; private set; }

    /// <summary>More results exist than <see cref="DisplayCap"/> ("Showing first 100,000. Refine the query.").</summary>
    [ObservableProperty]
    public partial bool Capped { get; private set; }

    /// <summary>Part of the scope was listed from disk (yellow "indexing" strip, AC-2.24, E-2.7, E-2.11).</summary>
    [ObservableProperty]
    public partial bool ShowIndexingNotice { get; private set; }

    /// <summary>Folders skipped for lack of access (E-2.13).</summary>
    [ObservableProperty]
    public partial int AccessDenied { get; private set; }

    /// <summary>Some folders vanished or could not be read (e.g. a removed drive, E-2.10).</summary>
    [ObservableProperty]
    public partial bool Partial { get; private set; }

    /// <summary>Duration of the last finished search.</summary>
    [ObservableProperty]
    public partial TimeSpan Elapsed { get; private set; }

    /// <summary>Folder of the last search ("Scope: … (including subfolders)").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyPropertyChangedFor(nameof(DisplayScope))]
    public partial string? Scope { get; private set; }

    /// <summary>The scope line's folder: the last search's, or before the first search the pane's folder (what Search searches).</summary>
    public string? DisplayScope => Scope ?? _pane.CurrentPath;

    /// <summary>True before the first search while the pane shows This PC: nothing to search (ADR-0015, search.scope.thisPc).</summary>
    public bool AtThisPc => Scope is null && _pane.IsThisPc;

    /// <summary>Query of the last search ("No files match …").</summary>
    [ObservableProperty]
    public partial string? SearchedText { get; private set; }

    /// <summary>A message for the user (a result that no longer exists, a failed open); null for none.</summary>
    [ObservableProperty]
    public partial PaneStatus? Status { get; private set; }

    /// <summary>Column of the header sort; null = arrival order (the default).</summary>
    [ObservableProperty]
    public partial SearchColumn? SortColumn { get; private set; }

    /// <summary>Descending header sort.</summary>
    [ObservableProperty]
    public partial bool SortDescending { get; private set; }

    /// <summary>The result rows.</summary>
    public SearchResults Results { get; } = new();

    /// <summary>Id of the search the rows belong to: a menu choice made for rows of an earlier search is dropped (WP-3.5).</summary>
    public long SearchId => _requestId;

    /// <summary>
    /// The native menu's target for <paramref name="hits"/> (WP-3.5, architecture §7): their common folder and names, or null
    /// when they come from different folders (mixed parents: the fallback menu) or there are none.
    /// </summary>
    /// <param name="hits">Selected results.</param>
    /// <returns>The target, or null.</returns>
    public static ContextMenuTarget? MenuTarget(IReadOnlyList<SearchHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0)
        {
            return null;
        }

        string folder = hits[0].Directory;
        return hits.All(h => PathUtil.PathEquals(h.Directory, folder)) ? new ContextMenuTarget(folder, [.. hits.Select(h => h.Name)]) : null;
    }

    /// <summary>Shows a message in the footer (a failed shell command from the results' menu).</summary>
    /// <param name="status">The message.</param>
    public void ReportStatus(PaneStatus status) => Status = status ?? throw new ArgumentNullException(nameof(status));

    /// <summary>
    /// Re-checks <paramref name="hits"/> on disk after a shell command acted on them (WP-3.5, lead Q2): the ones that are gone
    /// leave the list and are reported to the index, without a message (the user removed them). With
    /// <paramref name="follow"/> (a native verb, which may finish later), hits of one folder are re-checked again on every
    /// change in that folder for a minute, until a new search, another command or closing the popup. UI thread.
    /// </summary>
    /// <param name="hits">The results the command acted on.</param>
    /// <param name="follow">Keep re-checking on changes in their folder.</param>
    /// <returns>A task that completes when the first re-check was applied.</returns>
    public Task RecheckAsync(IReadOnlyList<SearchHit> hits, bool follow)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0 || _disposed)
        {
            return Task.CompletedTask;
        }

        long id = _requestId;
        if (follow && MenuTarget(hits) is { } target)
        {
            StartFollow(target.Folder, hits, id);
        }

        return RecheckNowAsync([.. hits], id);
    }

    /// <summary>
    /// Searches <see cref="QueryText"/> (trimmed) in the pane's current folder and all subfolders, cancelling a running
    /// search. An empty query does nothing (AC-2.14).
    /// </summary>
    /// <returns>A task that completes when the search ended (results applied on the UI thread).</returns>
    public Task StartSearchAsync()
    {
        string text = QueryText.Trim();
        return text.Length == 0 || _pane.CurrentPath is not { } scope ? Task.CompletedTask : RunAsync(scope, text);
    }

    /// <summary>Repeats the last search in its original folder against the current index (AC-2.22, Retry after an error).</summary>
    /// <returns>A task that completes when the search ended.</returns>
    public Task RefreshAsync() => Scope is { } scope && SearchedText is { } text ? RunAsync(scope, text) : Task.CompletedTask;

    /// <summary>
    /// A reader instance loads the index on its first search (architecture §18 D2): when <paramref name="indexLoaded"/>
    /// completes later, the search that ran meanwhile (on the empty or partial index) runs once more. Nothing when it has
    /// completed already, no search ran, or the popup closed.
    /// </summary>
    /// <param name="indexLoaded">The index's <c>EnsureLoadedAsync</c> task.</param>
    /// <returns>A task that completes after the repeated search started.</returns>
    public async Task RefreshWhenLoadedAsync(Task indexLoaded)
    {
        ArgumentNullException.ThrowIfNull(indexLoaded);
        if (indexLoaded.IsCompleted)
        {
            return;
        }

        await indexLoaded.ConfigureAwait(false);
        RunOnUi(() =>
        {
            if (!_disposed)
            {
                Forget(RefreshAsync(), "Repeating the search after the index loaded");
            }
        });
    }

    /// <summary>Esc: stops a running search; false when nothing ran (the view closes the popup).</summary>
    /// <returns>True when a search was stopped.</returns>
    public bool HandleEscape()
    {
        if (!IsRunning)
        {
            return false;
        }

        Stop();
        return true;
    }

    /// <summary>Opens a result (AC-2.20): a folder in the pane, a file through the shell. The popup stays open.</summary>
    /// <param name="hit">The result.</param>
    /// <returns>A task that completes when the open was handed off.</returns>
    public async Task OpenAsync(SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        string path = PathUtil.Combine(hit.Directory, hit.Name);
        if (hit.IsDirectory)
        {
            await NavigatePaneAsync(hit, path, null).ConfigureAwait(true);
            return;
        }

        OpenResult result;
        try
        {
            result = await _context.ShellLauncher.OpenAsync(path, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening search result {Path} failed", path);
            result = OpenResult.Failed(new FsError(FsErrorKind.Other, path, 0));
        }

        RunOnUi(() =>
        {
            switch (result.Outcome)
            {
                case OpenOutcome.NavigateTo when result.Directory is { } target:
                    Forget(NavigatePaneAsync(hit, target, null), "Opening the folder of a search result");
                    break;
                case OpenOutcome.Failed when result.Error is { Kind: FsErrorKind.NotFound }:
                    Missing(hit, path);
                    break;
                case OpenOutcome.Failed:
                    _logger.LogWarning("Cannot open search result {Path}: {Error}", path, result.Error);
                    Status = new PaneStatus(result.Error is { Kind: FsErrorKind.NoAssociation } ? "pane.error.noAssociation" : "pane.error.open", true, ("path", path));
                    break;
            }
        });
    }

    /// <summary>"Open containing folder" (AC-2.21): shows the result's folder in the pane and selects the result.</summary>
    /// <param name="hit">The result.</param>
    /// <returns>A task that completes when the pane navigated (or failed).</returns>
    public async Task OpenContainingFolderAsync(SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);

        // A file that vanished while its folder stays would open the folder and select nothing (QA G2): stat it first.
        string path = PathUtil.Combine(hit.Directory, hit.Name);
        PathStat stat;
        try
        {
            stat = await Task.Run(() => _stat(path), _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (stat.State == PathState.Missing)
        {
            RunOnUi(() => Missing(hit, path));
            return;
        }

        var navigated = new TaskCompletionSource<Task>();
        RunOnUi(() => navigated.TrySetResult(NavigatePaneAsync(hit, hit.Directory, hit.Name)));
        await (await navigated.Task.ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>"Copy path" (AC-2.21, the results' menus): the full paths, one per line, unquoted (lead Q5).</summary>
    /// <param name="hits">Selected results.</param>
    public void CopyPath(IReadOnlyList<SearchHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count > 0)
        {
            _clipboard.SetText(string.Join("\r\n", hits.Select(h => PathUtil.Combine(h.Directory, h.Name))));
        }
    }

    /// <summary>Ctrl+Shift+C (WP-3.6, lead Q5): Explorer's "Copy as path", every path quoted, one per line.</summary>
    /// <param name="hits">Selected results.</param>
    public void CopyAsPath(IReadOnlyList<SearchHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count > 0)
        {
            _clipboard.SetText(PathUtil.CopyAsPathText(hits.Select(h => PathUtil.Combine(h.Directory, h.Name))));
        }
    }

    /// <summary>
    /// Header click: sorts by <paramref name="column"/> (the same column again flips the direction). While a search runs,
    /// the sort is applied when it is done.
    /// </summary>
    /// <param name="column">Column.</param>
    /// <returns>A task that completes when the sorted rows are shown.</returns>
    public Task SortByAsync(SearchColumn column)
    {
        SortDescending = SortColumn == column && !SortDescending;
        SortColumn = column;
        return IsRunning ? Task.CompletedTask : ApplySortAsync(_requestId);
    }

    /// <summary>Cancels a running search.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pane.PropertyChanged -= OnPaneChanged;
        EndFollow(_follow);
        _running?.Cancel();
        _lifetime.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private void Search() => _ = StartSearchAsync();

    private bool CanSearch() => !string.IsNullOrWhiteSpace(QueryText) && !_pane.IsThisPc; // This PC: nothing to search (AC-1.41)

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _running?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh() => _ = RefreshAsync();

    private bool CanRefresh() => Scope is not null;

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.IsThisPc))
        {
            SearchCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName is nameof(PaneViewModel.CurrentPath) or nameof(PaneViewModel.IsThisPc) && Scope is null)
        {
            OnPropertyChanged(nameof(DisplayScope));
            OnPropertyChanged(nameof(AtThisPc));
        }
    }

    private async Task RunAsync(string scope, string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _running?.Cancel(); // the superseded search ends on its own and disposes its source
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _running = cts;
        long id = ++_requestId;

        EndFollow(_follow); // the followed rows are gone with the old results
        Results.Clear();
        Found = 0;
        Elapsed = TimeSpan.Zero;
        Capped = false;
        ShowIndexingNotice = false;
        AccessDenied = 0;
        Partial = false;
        Status = null;
        Scope = scope;
        SearchedText = text;
        State = SearchState.Searching;
        IsRunning = true;

        var explorer = _context.HiddenFiles();
        var query = new SearchQuery(scope, text, DisplayCap, _pane.ShowHiddenOverride ?? explorer.ShowHidden, explorer.ShowProtectedSystem);
        SearchOutcome? outcome = null;
        bool failed = false;
        var watch = Stopwatch.StartNew(); // the time of a stopped or superseded search, which has no outcome (QA B3)
        try
        {
            // Batches and state flags go to the UI below input, in order; the completion is queued behind them.
            outcome = await _search.SearchAsync(
                query,
                batch => _dispatcher.Post(() => ApplyBatch(id, batch), UiPriority.Low),
                progress => _dispatcher.Post(() => ApplyProgress(id, progress), UiPriority.Low),
                cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Stopped by the user, superseded or closed: keep what arrived.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed");
            failed = true;
        }

        var done = new TaskCompletionSource();
        _dispatcher.Post(
            () =>
            {
                try
                {
                    Complete(id, outcome, failed, watch.Elapsed);
                }
                finally
                {
                    // Disposed on the UI thread, where Stop reads _running: never cancel a disposed source.
                    if (ReferenceEquals(_running, cts))
                    {
                        _running = null;
                    }

                    cts.Dispose();
                    done.TrySetResult();
                }
            },
            UiPriority.Low);
        await done.Task.ConfigureAwait(false);
        await ApplySortAfterDoneAsync(id).ConfigureAwait(false);
    }

    private void ApplyBatch(long id, IReadOnlyList<SearchHit> batch)
    {
        if (id != _requestId || _disposed)
        {
            return; // a stale search (AC-2.19)
        }

        Results.Append(batch);
        Found = Results.Count;
    }

    // QA G1: the strip and the skipped-folder count show while the walk runs, and stay after Stop.
    private void ApplyProgress(long id, SearchProgress progress)
    {
        if (id != _requestId || _disposed)
        {
            return;
        }

        ShowIndexingNotice = progress.LiveUsed;
        AccessDenied = progress.AccessDenied;
        Partial = progress.Partial;
    }

    private void Complete(long id, SearchOutcome? outcome, bool failed, TimeSpan elapsed)
    {
        if (id != _requestId || _disposed)
        {
            return;
        }

        IsRunning = false;
        if (failed)
        {
            State = SearchState.Error;
            return;
        }

        if (outcome is { } o)
        {
            Capped = o.Capped;
            ShowIndexingNotice = o.LiveUsed;
            AccessDenied = o.AccessDenied;
            Partial = o.Partial;
            Elapsed = o.Elapsed;
        }
        else
        {
            Elapsed = elapsed; // stopped: the flags stay as the last progress report left them
        }

        Found = Results.Count;
        State = Results.Count > 0 ? SearchState.Done : SearchState.NoResults;
    }

    private Task ApplySortAfterDoneAsync(long id)
    {
        var pending = new TaskCompletionSource<Task>();
        _dispatcher.Post(() => pending.TrySetResult(id == _requestId && !IsRunning && SortColumn is not null ? ApplySortAsync(id) : Task.CompletedTask), UiPriority.Low);
        return pending.Task.Unwrap();
    }

    // Sorts a copy on a worker; shows it only if no newer search or sort replaced the rows meanwhile.
    private async Task ApplySortAsync(long id)
    {
        if (SortColumn is not { } column || Results.Count < 2)
        {
            return;
        }

        bool descending = SortDescending;
        List<SearchHit> rows = Results.Snapshot();
        await Task.Run(() =>
        {
            IComparer<SearchHit> comparer = SearchResults.ComparerFor(column);
            rows.Sort(descending ? Comparer<SearchHit>.Create((a, b) => comparer.Compare(b, a)) : comparer);
        }).ConfigureAwait(false);

        var shown = new TaskCompletionSource();
        _dispatcher.Post(() =>
        {
            try
            {
                if (id == _requestId && !_disposed && SortColumn == column && SortDescending == descending && Results.Count == rows.Count)
                {
                    Results.ReplaceWith(rows);
                }
            }
            finally
            {
                shown.TrySetResult(); // a throwing ReplaceWith (logged by the dispatcher guard) must not hang the sort
            }
        });
        await shown.Task.ConfigureAwait(false);
    }

    private async Task NavigatePaneAsync(SearchHit hit, string folder, string? select)
    {
        NavigationResult result;
        try
        {
            result = await _pane.NavigateAndSelectAsync(folder, select).ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            return; // the pane closed meanwhile
        }

        if (result is { Outcome: NavigationOutcome.Failed, Error.Kind: FsErrorKind.NotFound })
        {
            RunOnUi(() => Missing(hit, select is null ? folder : PathUtil.Combine(hit.Directory, hit.Name)));
        }
    }

    // AC-2.23: the result is gone; drop it, tell the index, say so.
    private void Missing(SearchHit hit, string path)
    {
        if (_disposed)
        {
            return;
        }

        Results.Remove(hit);
        Found = Results.Count;
        ShowNoResultsWhenEmpty();
        _index.ReportMissing(path);
        Status = new PaneStatus("search.error.missing", true, ("path", path));
    }

    private async Task RecheckNowAsync(IReadOnlyList<SearchHit> hits, long id)
    {
        HashSet<SearchHit> gone;
        try
        {
            gone = await Task.Run(() => hits.Where(h => _stat(PathUtil.Combine(h.Directory, h.Name)).State == PathState.Missing).ToHashSet(), _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (gone.Count == 0)
        {
            return;
        }

        var applied = new TaskCompletionSource();
        RunOnUi(() =>
        {
            try
            {
                DropGone(gone, id);
            }
            finally
            {
                applied.TrySetResult();
            }
        });
        await applied.Task.ConfigureAwait(false);
    }

    // Rows of a later search are not these rows: nothing is dropped then.
    private void DropGone(IReadOnlySet<SearchHit> gone, long id)
    {
        if (_disposed || id != _requestId)
        {
            return;
        }

        foreach (var hit in Results.RemoveAll(gone))
        {
            _index.ReportMissing(PathUtil.Combine(hit.Directory, hit.Name));
        }

        Found = Results.Count;
        ShowNoResultsWhenEmpty();
        if (_follow is { } follow && follow.SearchId == id)
        {
            follow.Hits.ExceptWith(gone);
            if (follow.Hits.Count == 0)
            {
                EndFollow(follow);
            }
        }
    }

    // Reviewer-wp35 m3: the last row gone (deleted, or found missing) shows the "No files match" state, not "0 results".
    private void ShowNoResultsWhenEmpty()
    {
        if (Results.Count == 0 && !IsRunning && State == SearchState.Done)
        {
            State = SearchState.NoResults;
        }
    }

    private void StartFollow(string folder, IReadOnlyList<SearchHit> hits, long id)
    {
        EndFollow(_follow);
        var follow = new FollowedHits(id, hits);
        try
        {
            // A zero scan time: on a drive that is polled (removable, network letter) the folder is checked every 2 s.
            follow.Watch = _context.ChangeFeed.WatchDirectory(folder, _ => _dispatcher.Post(() => OnFollowedChange(follow), UiPriority.Low), () => TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            // Best effort (e.g. a UNC folder the feed does not watch): the first re-check and AC-2.23 on open remain.
            _logger.LogInformation("Search results in {Folder} are not followed after a shell command: {Error}", folder, ex.Message);
            follow.Dispose();
            return;
        }

        _follow = follow;
        _ = Task.Delay(FollowTime, follow.End.Token).ContinueWith(
            _ => _dispatcher.Post(() => EndFollow(follow), UiPriority.Low),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void OnFollowedChange(FollowedHits follow)
    {
        if (ReferenceEquals(_follow, follow) && !_disposed)
        {
            Forget(RecheckNowAsync([.. follow.Hits], follow.SearchId), "Re-checking search results");
        }
    }

    private void EndFollow(FollowedHits? follow)
    {
        if (follow is null)
        {
            return;
        }

        if (ReferenceEquals(_follow, follow))
        {
            _follow = null;
        }

        follow.Dispose();
    }

    // Fire-and-forget with a log entry instead of an unobserved task exception.
    private void Forget(Task task, string what)
        => task.ContinueWith(t => _logger.LogError(t.Exception, "{What} failed", what), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.Post(action);
        }
    }

    // The hits a native verb acted on, followed through their folder's change feed (UI thread).
    private sealed class FollowedHits(long searchId, IReadOnlyList<SearchHit> hits) : IDisposable
    {
        private bool _disposed;

        public long SearchId { get; } = searchId;

        public HashSet<SearchHit> Hits { get; } = [.. hits];

        public CancellationTokenSource End { get; } = new();

        public IDisposable? Watch { get; set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            End.Cancel();
            End.Dispose();
            Watch?.Dispose();
        }
    }
}
