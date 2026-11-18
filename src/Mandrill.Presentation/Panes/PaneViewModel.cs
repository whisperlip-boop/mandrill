using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.Platform;
using Mandrill.Core.Scanning;
using Mandrill.Core.Shell;
using Mandrill.Core.State;
using Mandrill.Core.Watching;
using Mandrill.Presentation.Common;
using Mandrill.Presentation.Navigation;
using Microsoft.Extensions.Logging;

namespace Mandrill.Presentation.Panes;

/// <summary>
/// One pane: navigation and history, the shown listing, live updates, view state and errors (architecture §3, §7).
/// UI thread only; scanner, watcher and drive callbacks are marshaled through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class PaneViewModel : ObservableObject, IDisposable
{
    private readonly PaneScanner _scanner;
    private readonly PaneContext _context;
    private readonly PaneScanSink _sink;
    private readonly ILogger _logger;
    private readonly FsErrorLog _fsErrors;

    // Cancels shell and file operations still running when the pane is disposed.
    private readonly CancellationTokenSource _lifetime = new();
    private PathHistory _pathHistory = new();
    private Request? _request;
    private IDisposable? _watch;
    private string? _watchedPath;
    private object? _watchToken;
    private long _lastScanTicks;
    private bool _dirtyWhileLoading;
    private bool _resortWhenDone;
    private (string Name, bool Rename)? _pendingSelect;

    // A navigation superseded an unfinished update of the shown folder (or a deferred one); if it fails without leaving
    // the folder, that change must still be picked up: the watcher will not fire again (AC-1.15).
    private bool _reconcileIfKept;
    private bool _visible = true;
    private bool _disposed;
    private string? _justGranted; // the re-open after a grant must not ask again if it is still denied (one shot)

    /// <summary>Creates a pane.</summary>
    /// <param name="scanner">This pane's scanner. The caller (LayoutManager) owns and disposes it; the pane only cancels it.</param>
    /// <param name="context">Shared services.</param>
    public PaneViewModel(PaneScanner scanner, PaneContext context)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(context);
        _scanner = scanner;
        _context = context;
        _logger = context.LoggerFactory.CreateLogger<PaneViewModel>();
        _fsErrors = new FsErrorLog(_logger);
        _sink = new PaneScanSink(context.Dispatcher, OnSnapshot, OnFailed);
        ActiveTab = new PaneTab(0, string.Empty, ViewMode, SortState.Default) { IsActive = true };
        Tabs.Add(ActiveTab);
        context.Drives.Changed += OnDrivesChanged;
    }

    /// <summary>Raised when the view should select (and optionally rename) entries (architecture §5).</summary>
    public event Action<SelectionRequest>? SelectionRequested;

    /// <summary>
    /// Every This PC commit, F5 included (ADR-0015); the argument is the drive root to select (Up from a drive root), else
    /// null. UI thread.
    /// </summary>
    public event Action<string?>? ThisPcShown;

    /// <summary>
    /// A navigation into a folder on a local fixed drive was denied (owner F14): the view asks, as Explorer does, whether
    /// to get permanent access, and calls <see cref="GrantAccessAndRetryAsync"/> on Continue. The argument is the folder.
    /// </summary>
    public event Action<string>? AccessPromptRequested;

    /// <summary>The list shown by the view.</summary>
    public PaneItemsSource Items { get; } = new();

    /// <summary>Committed folder; null before the first navigation and at This PC (<see cref="IsThisPc"/>).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand), nameof(RefreshCommand), nameof(RetryCommand))]
    [NotifyPropertyChangedFor(nameof(LocationText))]
    public partial string? CurrentPath { get; private set; }

    /// <summary>True while the pane shows This PC (ADR-0015): no folder, so every file-system command is off.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand), nameof(RefreshCommand))]
    [NotifyPropertyChangedFor(nameof(LocationText), nameof(CanGoUp))]
    public partial bool IsThisPc { get; private set; }

    /// <summary>What the pane shows, for people: the localized "This PC" there, else <see cref="CurrentPath"/>.</summary>
    public string? LocationText => IsThisPc ? Localizer.Current["thispc.name"] : CurrentPath;

    /// <summary>Path box text (two-way). Reset to <see cref="CurrentPath"/> on commit and on <see cref="RevertPathText"/>.</summary>
    [ObservableProperty]
    public partial string PathText { get; set; } = string.Empty;

    /// <summary>The last typed path was invalid or missing (red outline; AC-1.18).</summary>
    [ObservableProperty]
    public partial bool PathError { get; private set; }

    /// <summary>The committed folder cannot be listed (e.g. access denied with Retry, E-1.1); null when listing works.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial FsError? FolderError { get; private set; }

    /// <summary>A scan is running (progress bar).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Status-bar message of this pane; null when none.</summary>
    [ObservableProperty]
    public partial PaneStatus? Status { get; private set; }

    /// <summary>Shows a message in this pane's status bar (a failed shell command from a context menu, WP-3.1).</summary>
    /// <param name="status">The message.</param>
    public void ReportStatus(PaneStatus status) => Status = status ?? throw new ArgumentNullException(nameof(status));

    /// <summary>View mode (AC-1.21).</summary>
    [ObservableProperty]
    public partial ViewMode ViewMode { get; set; } = PaneState.Default.ViewMode;

    /// <summary>Sort column (AC-1.22).</summary>
    [ObservableProperty]
    public partial SortColumn SortColumn { get; private set; } = SortState.Default.Column;

    /// <summary>Sort direction.</summary>
    [ObservableProperty]
    public partial bool SortDescending { get; private set; }

    /// <summary>Icons-view icon size in px (16–256).</summary>
    [ObservableProperty]
    public partial int IconSize { get; set; } = PaneState.Default.IconSize;

    /// <summary>Whether the tree panel is expanded.</summary>
    [ObservableProperty]
    public partial bool TreeVisible { get; set; } = PaneState.Default.TreeVisible;

    /// <summary>Tree panel width in DIPs.</summary>
    [ObservableProperty]
    public partial double TreeWidth { get; set; } = PaneState.Default.TreeWidth;

    /// <summary>Details column widths.</summary>
    [ObservableProperty]
    public partial ColumnWidths Columns { get; set; } = ColumnWidths.Default;

    /// <summary>
    /// Session-only "show hidden files" override, set from MainViewModel.ShowHidden (View menu); null follows Explorer
    /// (architecture §10). A change re-filters without I/O.
    /// </summary>
    [ObservableProperty]
    public partial bool? ShowHiddenOverride { get; set; }

    /// <summary>Path-box dropdown entries, most recent first (AC-1.18).</summary>
    public IReadOnlyList<string> PathHistory => _pathHistory.Items;

    /// <summary>Whether Back is possible (AC-1.16).</summary>
    public bool CanGoBack => ActiveTab.History.CanGoBack;

    /// <summary>Whether Forward is possible (AC-1.16).</summary>
    public bool CanGoForward => ActiveTab.History.CanGoForward;

    /// <summary>Whether Up is possible: from a folder, and from a drive root to This PC (AC-1.17); not at This PC.</summary>
    public bool CanGoUp => !IsThisPc && CurrentPath is { } p && (PathUtil.Parent(p) is not null || PathUtil.IsDriveRoot(p));

    /// <summary>
    /// Navigates to typed or programmatic text. Typed text may contain %VAR% and '/'. Completes when the folder is shown
    /// (or its nearest ancestor), when it fails, or when a newer navigation supersedes it.
    /// </summary>
    /// <param name="text">Path text.</param>
    /// <param name="origin">Why the pane navigates.</param>
    /// <returns>The outcome.</returns>
    public Task<NavigationResult> NavigateAsync(string text, NavOrigin origin = NavOrigin.Typed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (PathUtil.IsThisPc(text) || string.Equals(text?.Trim(), Localizer.Current["thispc.name"], StringComparison.OrdinalIgnoreCase))
        {
            return StartNavigation(PathUtil.ThisPc, origin, fallback: false); // ADR-0015: the token or the typed name
        }

        if (!PathUtil.TryNormalize(text, out string path, out FsError? error, expandEnvironment: origin == NavOrigin.Typed))
        {
            FsError failure = error ?? new FsError(FsErrorKind.InvalidPath, text ?? string.Empty, 0);
            if (origin == NavOrigin.Typed)
            {
                PathError = true;
            }

            Status = ErrorStatus(failure);
            return Task.FromResult(new NavigationResult(NavigationOutcome.Failed, text, Error: failure));
        }

        return StartNavigation(path, origin, fallback: origin != NavOrigin.Typed);
    }

    /// <summary>
    /// Navigates to <paramref name="folder"/> and selects <paramref name="name"/> in it once listed (search results:
    /// "Open containing folder", architecture §3). No fallback to an ancestor: a folder that no longer exists fails with
    /// <see cref="FsErrorKind.NotFound"/> and the pane stays where it was (an error status, no red path box), so the caller
    /// can drop the stale result.
    /// </summary>
    /// <param name="folder">Folder to show (a normalized path).</param>
    /// <param name="name">Entry to select and focus; null to select nothing.</param>
    /// <returns>The outcome.</returns>
    public Task<NavigationResult> NavigateAndSelectAsync(string folder, string? name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!PathUtil.TryNormalize(folder, out string path, out FsError? error))
        {
            return Task.FromResult(new NavigationResult(NavigationOutcome.Failed, folder, Error: error ?? new FsError(FsErrorKind.InvalidPath, folder, 0)));
        }

        return StartNavigation(path, NavOrigin.Open, fallback: false, select: name is null ? null : (name, false), keepOnFailure: true);
    }

    /// <summary>
    /// Restores view state and tabs and navigates to the saved folder (AC-1.33). An empty path opens the home folder.
    /// Saved tabs replace the pane's tabs (empty histories); null <see cref="PaneState.Tabs"/> keeps them and their history.
    /// </summary>
    /// <param name="state">Saved pane state (normalized); its top-level path, view mode and sort are the active tab's.</param>
    /// <param name="fallbackToAncestor">Open the nearest existing ancestor when the folder is gone.</param>
    /// <returns>The navigation outcome.</returns>
    public Task<NavigationResult> RestoreAsync(PaneState state, bool fallbackToAncestor)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Tabs is { Count: > 0 })
        {
            ResetTabs(state);
        }

        ViewMode = state.ViewMode;
        SortColumn = state.Sort.Column;
        SortDescending = state.Sort.Descending;
        TreeVisible = state.TreeVisible;
        TreeWidth = state.TreeWidth;
        IconSize = state.IconSize;
        Columns = state.Columns;
        _pathHistory = new PathHistory(state.PathHistory);
        OnPropertyChanged(nameof(PathHistory));
        string? path = state.Path.Length > 0 ? state.Path : _context.HomeFolder;
        return path is null
            ? Task.FromResult(new NavigationResult(NavigationOutcome.Failed))
            : StartNavigation(path, NavOrigin.Restore, fallbackToAncestor);
    }

    /// <summary>Snapshot of the persisted pane state (AC-1.33).</summary>
    /// <returns>The state.</returns>
    public PaneState ToState()
    {
        var shown = new TabState(ShownLocation, ViewMode, new SortState(SortColumn, SortDescending), ActiveTab.Color);
        return new(
            shown.Path,
            shown.ViewMode,
            shown.Sort,
            TreeVisible,
            TreeWidth,
            IconSize,
            Columns,
            _pathHistory.Snapshot(),
            Tabs.Select(t => t == ActiveTab ? shown : t.ToState()).ToList(),
            Tabs.IndexOf(ActiveTab));
    }

    /// <summary>Opens an entry: folders navigate; files go to the shell (a folder shortcut navigates).</summary>
    /// <param name="entry">Entry of the shown folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the open was handed off.</returns>
    public async Task OpenAsync(FileEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (CurrentPath is not { } dir)
        {
            return;
        }

        string path = PathUtil.Combine(dir, entry.Name);
        if (entry.IsDirectory)
        {
            await StartNavigation(path, NavOrigin.Open, fallback: true).ConfigureAwait(true);
            return;
        }

        OpenResult result;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            result = await _context.ShellLauncher.OpenAsync(path, linked.Token).ConfigureAwait(false);
            if (result is { Outcome: OpenOutcome.Failed, Error.Kind: FsErrorKind.NoAssociation } && _context.OpenWith is { } openWith
                && await openWith(path, linked.Token).ConfigureAwait(false))
            {
                return; // the user picked an app in "Open with" (or closed it): nothing to report
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // The launcher reports failures as results; anything thrown is a bug that must not kill the pane.
            _logger.LogError(ex, "Opening {Path} failed", path);
            result = OpenResult.Failed(new FsError(FsErrorKind.Other, path, 0));
        }

        RunOnUi(() =>
        {
            if (_disposed)
            {
                return;
            }

            switch (result.Outcome)
            {
                case OpenOutcome.NavigateTo when result.Directory is { } target:
                    _ = StartNavigation(target, NavOrigin.Open, fallback: true);
                    break;
                case OpenOutcome.Failed:
                    _logger.LogWarning("Cannot open {Path}: {Error}", path, result.Error);
                    Status = new PaneStatus(result.Error is { Kind: FsErrorKind.NoAssociation } ? "pane.error.noAssociation" : "pane.error.open", true, ("path", path));
                    break;
            }
        });
    }

    /// <summary>Creates "New folder" (numbered if needed) in the shown folder and asks the view to rename it (AC-1.20).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the folder was created or the failure reported.</returns>
    public Task CreateFolderAsync(CancellationToken ct = default) => CreateItemAsync(folder: true, ct);

    /// <summary>Creates "New Text Document.txt" (numbered if needed) and asks the view to rename it (WP-3.2 fallback menu).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the file was created or the failure reported.</returns>
    public Task CreateFileAsync(CancellationToken ct = default) => CreateItemAsync(folder: false, ct);

    /// <summary>
    /// After a copy, move or delete (AC-3.10): re-lists the shown folder when it is one of <paramref name="folders"/>, without
    /// waiting for the watcher (polled drives, 250 ms coalescing). UI thread.
    /// </summary>
    /// <param name="folders">Folders whose contents changed.</param>
    public void ReconcileIfShowing(IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (!_disposed && CurrentPath is { } path && FolderError is null && folders.Any(f => PathUtil.PathEquals(f, path)))
        {
            StartUpdate(ScanKind.Reconcile, null);
        }
    }

    private async Task CreateItemAsync(bool folder, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (CurrentPath is not { } dir || FolderError is not null)
        {
            return;
        }

        FsResult<string> result;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            result = folder
                ? await _context.FileOperations.CreateUniqueFolderAsync(dir, Localizer.Current["pane.newFolder"], linked.Token).ConfigureAwait(false)
                : await _context.FileOperations.CreateUniqueFileAsync(dir, Localizer.Current["pane.newTextDocument"], linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating a {Kind} in {Path} failed", folder ? "folder" : "file", dir);
            result = FsResult<string>.Failure(new FsError(FsErrorKind.Other, dir, 0));
        }

        RunOnUi(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (!result.Succeeded || result.Value is not { } created)
            {
                _logger.LogWarning("Creating a {Kind} in {Path} failed: {Error}", folder ? "folder" : "file", dir, result.Error);
                Status = new PaneStatus(folder ? "pane.error.createFolder" : "pane.error.createFile", true, ("path", dir));
                return;
            }

            _logger.LogInformation("Created {Path}", created);
            if (CurrentPath is { } now && PathUtil.PathEquals(now, dir))
            {
                _pendingSelect = (PathUtil.LeafName(created), true);
                StartUpdate(ScanKind.Partial, [PathUtil.LeafName(created)]);
            }
        });
    }

    /// <summary>
    /// Renames <paramref name="entry"/> in the shown folder (inline editor, AC-1.20; AC-3.11 name rules). The name is
    /// cleaned like Explorer (surrounding spaces and trailing dots dropped); characters Windows forbids or a reserved
    /// device name set an error status and return false, so the editor stays open. On success the folder is updated for
    /// the old and new names and the renamed entry is selected.
    /// </summary>
    /// <param name="entry">Entry of the shown folder.</param>
    /// <param name="newName">Typed name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when renamed (or unchanged); false when the name was rejected or the rename failed.</returns>
    public async Task<bool> RenameAsync(FileEntry entry, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(newName);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (CurrentPath is not { } dir)
        {
            return false;
        }

        if (PathUtil.CleanName(newName) is not { } name)
        {
            Status = new PaneStatus("pane.error.invalidName", true, ("name", newName.Trim()));
            return false;
        }

        if (string.Equals(name, entry.Name, StringComparison.Ordinal))
        {
            return true;
        }

        string path = PathUtil.Combine(dir, entry.Name);
        FsResult<string> result;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            result = await _context.FileOperations.RenameAsync(path, name, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Renaming {Path} failed", path);
            result = FsResult<string>.Failure(new FsError(FsErrorKind.Other, path, 0));
        }

        RunOnUi(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (result.Error is { Win32Code: 1223 })
            {
                // ERROR_CANCELLED: the user chose Skip/Cancel in the shell's dialog; not an error (reviewer m3).
                _logger.LogInformation("Renaming {Path} to {Name} was cancelled", path, name);
                return;
            }

            if (!result.Succeeded || result.Value is not { } renamed)
            {
                _logger.LogWarning("Renaming {Path} to {Name} failed: {Error}", path, name, result.Error);
                Status = ErrorStatus(result.Error ?? new FsError(FsErrorKind.Other, path, 0));
                return;
            }

            if (CurrentPath is { } now && PathUtil.PathEquals(now, dir))
            {
                string leaf = PathUtil.LeafName(renamed);
                _pendingSelect = (leaf, false);
                StartUpdate(ScanKind.Partial, [entry.Name, leaf]);
            }
        });
        return result.Succeeded;
    }

    /// <summary>Changes the view mode (no rescan).</summary>
    /// <param name="mode">New mode.</param>
    public void SetView(ViewMode mode) => ViewMode = mode;

    /// <summary>Sorts by <paramref name="column"/>; the same column again toggles the direction (no I/O).</summary>
    /// <param name="column">Sort column.</param>
    public void SetSort(SortColumn column)
    {
        if (column == SortColumn)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }

        if (column == SortColumn.Type && _context.EnsureTypeNames is not null && Items.Current is { } shown)
        {
            _ = ResortByTypeAsync(shown);
            return;
        }

        Resort();
    }

    /// <summary>Shell type names changed (WP-1.6 TypeNamesReady, marshaled to the UI thread): re-sorts a Type-sorted pane.</summary>
    public void OnTypeNamesChanged()
    {
        if (SortColumn == SortColumn.Type)
        {
            Resort();
        }
    }

    /// <summary>Puts the committed path back into the path box (Esc) and clears the error outline.</summary>
    public void RevertPathText()
    {
        PathText = LocationText ?? string.Empty;
        PathError = false;
    }

    /// <summary>
    /// Shows or hides the pane (layout changes, architecture §6). Hidden panes cancel scans and drop their watch subscription
    /// but keep all state; showing again re-subscribes and reconciles.
    /// </summary>
    /// <param name="visible">New visibility.</param>
    /// <param name="reload">
    /// When showing: reconcile the shown folder (default). False when the caller navigates the pane right away (favorite
    /// restore), so the pane does not start a scan that the navigation supersedes at once.
    /// </param>
    public void SetVisible(bool visible, bool reload = true)
    {
        if (visible == _visible)
        {
            return;
        }

        _visible = visible;
        if (!visible)
        {
            _scanner.Cancel();
            Supersede();
            _request = null;
            IsLoading = false;

            // Showing again reloads the folder, which covers any deferred work; a pending selection belongs to the
            // navigation that was just cancelled.
            _dirtyWhileLoading = false;
            _resortWhenDone = false;
            _reconcileIfKept = false;
            _pendingSelect = null;
            Unsubscribe();
        }
        else if (CurrentPath is { } path)
        {
            Subscribe(path);
            if (reload)
            {
                Reload(path);
            }
        }
    }

    /// <summary>Stops scans, watching and drive notifications.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel(); // not disposed: an operation finishing later may still read its token
        _context.Drives.Changed -= OnDrivesChanged;
        _scanner.Cancel();
        Supersede();
        Unsubscribe();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (ActiveTab.History.Back() is { } target)
        {
            NotifyHistory();
            _ = StartNavigation(target, NavOrigin.Back, fallback: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Forward()
    {
        if (ActiveTab.History.Forward() is { } target)
        {
            NotifyHistory();
            _ = StartNavigation(target, NavOrigin.Forward, fallback: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up()
    {
        if (CurrentPath is { } path && PathUtil.Parent(path) is { } parent)
        {
            // Explorer selects the folder you came from.
            _pendingSelect = (PathUtil.LeafName(path), false);
            _ = StartNavigation(parent, NavOrigin.Up, fallback: true);
        }
        else if (CurrentPath is { } root && PathUtil.IsDriveRoot(root))
        {
            _ = StartNavigation(PathUtil.ThisPc, NavOrigin.Up, fallback: false, select: (root, false)); // and selects that drive
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        if (IsThisPc)
        {
            _ = StartNavigation(PathUtil.ThisPc, NavOrigin.Refresh, fallback: false); // F5: the drive list re-reads
        }
        else if (CurrentPath is { } path)
        {
            if (_watch is not null)
            {
                Subscribe(path, renew: true); // F5 also re-resolves a junction's or symlink's real path for the watcher
            }

            Reload(path);
        }
    }

    /// <summary>
    /// Continue in the permission prompt (owner F14): grants the current user access through an elevated icacls, then
    /// re-opens the folder. A declined UAC prompt leaves the access-denied state; a failed grant adds a status message.
    /// </summary>
    /// <param name="folder">The denied folder (from <see cref="AccessPromptRequested"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome of the grant.</returns>
    public async Task<FolderAccessResult> GrantAccessAndRetryAsync(string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (_context.FolderAccess is not { } access)
        {
            return FolderAccessResult.Failed;
        }

        FolderAccessResult result = await access.GrantAsync(folder, ct).ConfigureAwait(true);
        switch (result)
        {
            case FolderAccessResult.Granted when !_disposed && PathUtil.PathEquals(CurrentPath, folder):
                _justGranted = folder;
                _ = StartNavigation(folder, NavOrigin.Refresh, fallback: true);
                break;
            case FolderAccessResult.Failed:
                Status = new PaneStatus("pane.error.grantFailed", true, ("path", folder));
                break;
        }

        return result;
    }

    // Explorer offers the prompt for local folders only: a removable or network drive keeps the plain state, and so does a
    // drive root (architecture §14 R16). System locations (%WINDIR%, System Volume Information …) get it since owner #10,
    // with a read-only grant (FolderAccessGrant.RightsFor).
    private bool ShouldOfferAccess(string folder)
    {
        string? granted = _justGranted;
        _justGranted = null;
        if (_context.FolderAccess is null || PathUtil.PathEquals(granted, folder) || folder.Length < 3)
        {
            return false;
        }

        string root = folder[..3];
        if (!_context.Drives.Current.Any(d => d.Kind == DriveKind.Fixed && PathUtil.PathEquals(d.Root, root)))
        {
            return false;
        }

        if (PathUtil.IsDriveRoot(folder))
        {
            _logger.LogInformation("Access denied to {Folder}: a drive root, no permission prompt", folder);
            return false;
        }

        return true;
    }

    /// <summary>Retry after an error state (E-1.1): re-opens the committed folder; history is unchanged.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry()
    {
        if (CurrentPath is { } path)
        {
            _ = StartNavigation(path, NavOrigin.Refresh, fallback: true);
        }
    }

    private bool CanRefresh() => CurrentPath is not null || IsThisPc;

    private bool CanRetry() => CurrentPath is not null && FolderError is not null;

    // Error state or nothing shown → re-open (a cold scan of the same folder); otherwise reconcile with a diff (F5).
    private void Reload(string path)
    {
        if (FolderError is not null || Items.Current is null)
        {
            _ = StartNavigation(path, NavOrigin.Refresh, fallback: true);
        }
        else
        {
            StartUpdate(ScanKind.Reconcile, null, userInitiated: true); // F5 / Refresh
        }
    }

    private Task<NavigationResult> StartNavigation(string path, NavOrigin origin, bool fallback, PaneStatus? statusOnCommit = null, (string Name, bool Rename)? select = null, bool keepOnFailure = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (PathUtil.IsThisPc(path))
        {
            return Task.FromResult(ShowThisPc(origin, statusOnCommit, select?.Name));
        }

        if (_dirtyWhileLoading || _resortWhenDone || (_request is { Final: false } running && (running.Kind == RequestKind.Update || running.Committed)))
        {
            _reconcileIfKept = true;
        }

        Supersede();
        if (origin is not (NavOrigin.Up or NavOrigin.Refresh))
        {
            _pendingSelect = null;
        }

        if (select is not null)
        {
            _pendingSelect = select; // set after the reset above, before the scan can deliver a snapshot
        }

        var request = new Request(RequestKind.Navigate, origin, path, statusOnCommit) { KeepOnFailure = keepOnFailure };
        _request = request;
        if (origin is not (NavOrigin.Tab or NavOrigin.Refresh) && !PathUtil.PathEquals(path, CurrentPath))
        {
            // Another folder starts unfiltered (ui-spec §2b); a failure that keeps this folder puts the filter back.
            request.FilterBefore = (FilterText, IsFilterOpen);
            SetFilterSilently(string.Empty, open: false);
        }

        _dirtyWhileLoading = false;
        _resortWhenDone = false;
        IsLoading = true;
        request.Id = _scanner.Scan(new ScanRequest(path, CurrentView(), ScanKind.Cold, FallbackToAncestor: fallback), _sink);
        return request.Completion.Task;
    }

    private void StartUpdate(ScanKind kind, IReadOnlyCollection<string>? names, bool userInitiated = false)
    {
        if (CurrentPath is not { } path || !_visible || _disposed)
        {
            return;
        }

        if (_request is { Final: false })
        {
            // Superseding a running request would lose its changes (e.g. a Partial's names): reconcile once it is done.
            _dirtyWhileLoading = true;
            return;
        }

        var request = new Request(RequestKind.Update, NavOrigin.Refresh, path, null) { UserInitiated = userInitiated };
        _request = request;
        IsLoading = kind != ScanKind.Partial;
        request.Id = _scanner.Scan(new ScanRequest(path, CurrentView(), kind, names), _sink);
    }

    private void Resort()
    {
        if (Items.Current is null || _disposed)
        {
            return;
        }

        if (_request is { Final: false })
        {
            // A running navigation or update must finish first; it already uses the new view only if it started after.
            _resortWhenDone = true;
            return;
        }

        var request = new Request(RequestKind.Update, NavOrigin.Refresh, CurrentPath ?? string.Empty, null);
        _request = request;
        try
        {
            request.Id = _scanner.Resort(CurrentView(), _sink);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Nothing to re-sort yet");
            _request = null;
        }
    }

    // Pending names would sort as "" (architecture §5): resolve the shown extension ids first, then re-sort.
    private async Task ResortByTypeAsync(EntrySnapshot shown)
    {
        int[] ids = shown.Visible.Where(e => !e.IsDirectory).Select(e => e.ExtId).Distinct().ToArray();
        try
        {
            await _context.EnsureTypeNames!(ids, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // pane disposed
        }
        catch (Exception ex)
        {
            // Sorting still works with the cached names; TypeNamesReady re-sorts when the rest arrive.
            _logger.LogWarning(ex, "Resolving type names before a Type sort failed");
        }

        RunOnUi(() =>
        {
            if (!_disposed && SortColumn == SortColumn.Type)
            {
                Resort();
            }
        });
    }

    private ViewSpec CurrentView()
    {
        HiddenFilesSetting explorer;
        try
        {
            explorer = _context.HiddenFiles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading Explorer's hidden-file setting failed; hiding hidden files");
            explorer = new HiddenFilesSetting(false, false);
        }

        return new ViewSpec(SortColumn, SortDescending, ShowHiddenOverride ?? explorer.ShowHidden, explorer.ShowProtectedSystem, FilterMatcher);
    }

    private void OnSnapshot(long id, EntrySnapshot snapshot)
    {
        if (_disposed || _request is not { } request || request.Id != id)
        {
            return; // superseded (AC-1.12, E-1.10)
        }

        if (request.Kind == RequestKind.Navigate && !request.Committed)
        {
            Commit(request, snapshot.Path, snapshot.RedirectedFrom);
        }

        Items.Apply(snapshot); // per-item diff when it applies to the shown version, otherwise one Reset

        if (_pendingSelect is { } pending && snapshot.IndexOfName(pending.Name) >= 0)
        {
            _pendingSelect = null;
            SelectionRequested?.Invoke(new SelectionRequest([pending.Name], Focus: true, BeginRename: pending.Rename));
        }

        if (snapshot.IsFinal)
        {
            request.Final = true;
            IsLoading = false;
            if (PathUtil.PathEquals(snapshot.Path, _justGranted))
            {
                _justGranted = null; // the re-open after the grant worked: a later denial of that folder asks again
            }

            Interlocked.Exchange(ref _lastScanTicks, snapshot.Elapsed.Ticks);
            if (_pendingSelect is not null && !_resortWhenDone && !_dirtyWhileLoading)
            {
                // The name never appeared (e.g. a hidden child after Up): a later snapshot must not select it out of the blue.
                _pendingSelect = null;
            }

            if (_resortWhenDone)
            {
                _resortWhenDone = false;
                Resort();
            }
            else if (_dirtyWhileLoading)
            {
                _dirtyWhileLoading = false;
                StartUpdate(ScanKind.Reconcile, null);
            }
        }
    }

    private void OnFailed(long id, FsError error)
    {
        if (_disposed || _request is not { } request || request.Id != id)
        {
            return;
        }

        request.Final = true;
        IsLoading = false;
        _dirtyWhileLoading = false;
        _resortWhenDone = false;
        _pendingSelect = null;
        _fsErrors.Report("List", error);
        if (request.Kind == RequestKind.Navigate && !request.Committed)
        {
            if (error.Kind == FsErrorKind.AccessDenied)
            {
                // E-1.1: the folder is entered and shows "Access denied" with Retry; Back works.
                // With FallbackToAncestor the denied folder may be the ancestor the scanner redirected to.
                string denied = PathUtil.IsSameOrUnder(request.Path, error.Path) ? error.Path : request.Path;
                Commit(request, denied, PathUtil.PathEquals(denied, request.Path) ? null : request.Path);
                ShowFolderError(error);
                if (ShouldOfferAccess(denied))
                {
                    AccessPromptRequested?.Invoke(denied);
                }
            }
            else if (request.Origin == NavOrigin.Typed || request.KeepOnFailure)
            {
                if (request.FilterBefore is { } filter)
                {
                    SetFilterSilently(filter.Text, filter.Open); // the shown listing still has it
                }

                PathError = request.Origin == NavOrigin.Typed;
                Status = ErrorStatus(error);
                request.Completion.TrySetResult(new NavigationResult(NavigationOutcome.Failed, request.Path, Error: error));
                ReconcileIfKept();
            }
            else
            {
                // Even the drive root is gone (E-1.2, E-1.9; owner, stage 3): This PC is the last resort; it cannot fail.
                request.Completion.TrySetResult(new NavigationResult(NavigationOutcome.Failed, request.Path, Error: error));
                // Replace the history entry only if it already points at the dead target (Back/Forward/Retry/Gone/Tab);
                // a failed Open/Tree/Favorite/Restore never moved it, so the folder the user was in stays (AC-1.16).
                bool replace = request.Origin is NavOrigin.Back or NavOrigin.Forward or NavOrigin.Fallback or NavOrigin.Refresh or NavOrigin.Tab;
                _ = StartNavigation(PathUtil.ThisPc, replace ? NavOrigin.Fallback : request.Origin, fallback: false, statusOnCommit: ErrorStatus(error));
            }

            return;
        }

        // The shown folder failed to refresh (also a cached navigation whose re-list failed after the commit).
        switch (error.Kind)
        {
            case FsErrorKind.NotFound or FsErrorKind.NotADirectory or FsErrorKind.NotReady:
                // On polled (non-fixed) folders this is how a removal shows up: same as Gone (architecture §7).
                HandleGone();
                break;
            case FsErrorKind.AccessDenied:
                ShowFolderError(error);
                // A cached re-visit commits at once and only the re-list is denied (ACL changed since): Explorer asks too, and
                // on F5. A watcher re-list never pops a dialog on its own (Explorer reports on the next user action).
                if ((request.Kind == RequestKind.Navigate || request.UserInitiated) && CurrentPath is { } shown && ShouldOfferAccess(shown))
                {
                    AccessPromptRequested?.Invoke(shown);
                }

                break;
            default:
                Status = ErrorStatus(error);
                break;
        }
    }

    private void Commit(Request request, string path, string? redirectedFrom)
    {
        request.Committed = true;
        _reconcileIfKept = false;
        switch (request.Origin)
        {
            case NavOrigin.Back or NavOrigin.Forward or NavOrigin.Fallback or NavOrigin.Tab:
                if (!PathUtil.PathEquals(ActiveTab.History.Current, path))
                {
                    ActiveTab.History.ReplaceCurrent(path);
                }

                break;
            case NavOrigin.Refresh:
                break;
            default:
                ActiveTab.History.Navigate(path);
                break;
        }

        _pathHistory.Add(path);
        if (!PathUtil.PathEquals(path, _justGranted))
        {
            _justGranted = null; // the one-shot "just granted" marker belongs to the re-open of that folder only
        }

        IsThisPc = false;
        CurrentPath = path;
        ActiveTab.Location = path;
        PathText = path;
        PathError = false;
        FolderError = null;
        Subscribe(path);
        Status = redirectedFrom is not null
            ? new PaneStatus("pane.status.redirected", false, ("missing", redirectedFrom), ("path", path))
            : request.StatusOnCommit;
        NotifyHistory();
        OnPropertyChanged(nameof(PathHistory));
        request.Completion.TrySetResult(new NavigationResult(NavigationOutcome.Committed, path, redirectedFrom));
    }

    // ADR-0015: This PC has no folder, so it commits at once: no scan, no watch, no disk access; it cannot fail. The drive
    // list (DriveListViewModel) loads on ThisPcShown. The path-box history never gets the token.
    private NavigationResult ShowThisPc(NavOrigin origin, PaneStatus? status, string? select)
    {
        StopShowingFolder();
        SetFilterSilently(string.Empty, open: false); // no filter at This PC
        switch (origin)
        {
            case NavOrigin.Back or NavOrigin.Forward or NavOrigin.Fallback or NavOrigin.Tab:
                if (!PathUtil.IsThisPc(ActiveTab.History.Current))
                {
                    ActiveTab.History.ReplaceCurrent(PathUtil.ThisPc);
                }

                break;
            case NavOrigin.Refresh:
                break;
            default:
                ActiveTab.History.Navigate(PathUtil.ThisPc);
                break;
        }

        IsThisPc = true;
        CurrentPath = null;
        ActiveTab.Location = PathUtil.ThisPc;
        PathText = LocationText ?? string.Empty;
        PathError = false;
        FolderError = null;
        IsLoading = false;
        Status = status;
        NotifyHistory();
        ThisPcShown?.Invoke(select);
        return new NavigationResult(NavigationOutcome.Committed, PathUtil.ThisPc);
    }

    // Drops the shown folder's request, watch and listing (This PC, and a tab switch before it navigates).
    private void StopShowingFolder()
    {
        Supersede();
        _request = null;
        _scanner.Cancel();
        _pendingSelect = null;
        _dirtyWhileLoading = false;
        _resortWhenDone = false;
        _reconcileIfKept = false;
        Unsubscribe();
        Items.Clear();
    }

    // The pane stayed on its folder after a failed navigation: catch up on an update that navigation superseded.
    private void ReconcileIfKept()
    {
        if (_reconcileIfKept && CurrentPath is { } path)
        {
            _reconcileIfKept = false;
            Reload(path);
        }
    }

    private void ShowFolderError(FsError error)
    {
        Items.Clear();
        FolderError = error;
        Status = ErrorStatus(error);
    }

    private void HandleGone()
    {
        if (CurrentPath is { } path)
        {
            _ = StartNavigation(path, NavOrigin.Fallback, fallback: true);
        }
    }

    private void Subscribe(string path, bool renew = false)
    {
        if (!_visible || (!renew && _watch is not null && PathUtil.PathEquals(_watchedPath, path)))
        {
            return;
        }

        var token = new object();
        IDisposable? subscription = null;
        try
        {
            // Watcher callbacks run on pool threads and must not block: only post to the UI thread.
            subscription = _context.ChangeFeed.WatchDirectory(
                path,
                change => _context.Dispatcher.Post(() => OnDirChange(token, change), UiPriority.Low),
                () => TimeSpan.FromTicks(Interlocked.Read(ref _lastScanTicks)));
        }
        catch (Exception ex)
        {
            // Runs inside a dispatcher callback: a feed failure (e.g. during shutdown) must not crash the app. The pane
            // still works; it only misses live updates until the next navigation.
            _logger.LogWarning(ex, "Cannot watch {Path}", path);
        }

        // Subscribe first, then drop the old one, so the root watcher is not closed and reopened.
        Unsubscribe();
        _watch = subscription;
        _watchedPath = subscription is null ? null : path;
        _watchToken = token;
    }

    private void Unsubscribe()
    {
        _watchToken = null;
        _watchedPath = null;
        IDisposable? old = _watch;
        _watch = null;
        old?.Dispose();
    }

    private void OnDirChange(object token, DirChange change)
    {
        if (_disposed || !ReferenceEquals(token, _watchToken))
        {
            return;
        }

        if (change.Gone)
        {
            HandleGone();
        }
        else
        {
            StartUpdate(change.Names is null ? ScanKind.Reconcile : ScanKind.Partial, change.Names);
        }
    }

    private void OnDrivesChanged(IReadOnlyList<DriveDescriptor> drives)
        => _context.Dispatcher.Post(() =>
        {
            if (_disposed || !_visible || CurrentPath is not { } path || path.Length < 3)
            {
                return;
            }

            string root = path[..3];
            if (PathUtil.IsDriveRoot(root) && !drives.Any(d => PathUtil.PathEquals(d.Root, root)))
            {
                // E-1.4 (owner, stage 3): the pane's drive disappeared: show This PC, where the other drives are.
                _ = StartNavigation(PathUtil.ThisPc, NavOrigin.Fallback, fallback: false, statusOnCommit: new PaneStatus("pane.status.driveRemoved", false, ("drive", root.TrimEnd('\\')))); // "E:", as Windows words it
            }
        });

    private void Supersede()
    {
        _request?.Completion.TrySetResult(new NavigationResult(NavigationOutcome.Superseded, _request.Path));
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }

    private void RunOnUi(Action action)
    {
        if (_context.Dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _context.Dispatcher.Post(action);
        }
    }

    partial void OnShowHiddenOverrideChanged(bool? value) => Resort();

    partial void OnIconSizeChanged(int value)
    {
        int clamped = Math.Clamp(value, PaneState.MinIconSize, PaneState.MaxIconSize);
        if (clamped != value)
        {
            IconSize = clamped;
        }
    }

    private static PaneStatus ErrorStatus(FsError error)
    {
        string key = error.Kind switch
        {
            FsErrorKind.NotFound => "pane.error.notFound",
            FsErrorKind.AccessDenied => "pane.error.accessDenied",
            FsErrorKind.NotADirectory => "pane.error.notADirectory",
            FsErrorKind.NotReady => "pane.error.notReady",
            FsErrorKind.InvalidPath => "pane.error.invalidPath",
            FsErrorKind.AlreadyExists => "pane.error.alreadyExists",
            FsErrorKind.InUse => "pane.error.inUse",
            FsErrorKind.PathTooLong => "pane.error.pathTooLong",
            FsErrorKind.Unsupported => "pane.error.unsupported",
            FsErrorKind.NoAssociation => "pane.error.noAssociation",
            _ => "pane.error.other",
        };
        return new PaneStatus(key, true, ("path", error.Path));
    }

    private enum RequestKind
    {
        Navigate,
        Update,
    }

    /// <summary>The request whose results the pane accepts; every other id is dropped.</summary>
    private sealed class Request(RequestKind kind, NavOrigin origin, string path, PaneStatus? statusOnCommit)
    {
        public long Id { get; set; }

        // A failed navigation keeps the shown folder instead of falling back home (search results, NavigateAndSelectAsync).
        public bool KeepOnFailure { get; init; }

        // An update the user asked for (F5 / Refresh), as opposed to a watcher re-list or a re-sort.
        public bool UserInitiated { get; init; }

        public RequestKind Kind { get; } = kind;

        public NavOrigin Origin { get; } = origin;

        public string Path { get; } = path;

        public PaneStatus? StatusOnCommit { get; } = statusOnCommit;

        public bool Committed { get; set; }

        public bool Final { get; set; }

        // The filter this navigation cleared (ui-spec §2b).
        public (string Text, bool Open)? FilterBefore { get; set; }

        public TaskCompletionSource<NavigationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
