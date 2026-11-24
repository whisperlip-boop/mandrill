using Mandrill.App.Platform;
using Mandrill.Core.Index;
using Mandrill.Core.IO;
using Mandrill.Core.Platform;
using Mandrill.Core.Scanning;
using Mandrill.Core.Search;
using Mandrill.Core.Shell;
using Mandrill.Core.State;
using Mandrill.Core.Watching;
using Mandrill.Presentation.Common;
using Mandrill.Presentation.Panes;
using Mandrill.Presentation.Tree;
using Microsoft.Extensions.Logging;

namespace Mandrill.App.Composition;

/// <summary>
/// Composition root (architecture §13 row 1.8): shared Core services, created once per process, and the panes built on
/// them. Each pane's scanner sorts with the shell type names (<c>EntrySorter(typeNames.Get)</c>); a Type sort resolves the
/// shown extension ids first, and TypeNamesReady re-sorts Type-sorted panes. The index service (WP-2.3b) is created here
/// and started by the App 1 s after the first frame; the close path stops it (it saves what changed, ≤3 s).
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ShellSta _iconsSta;
    private readonly ShellSta _miscSta;
    private readonly ContextMenuService _contextMenus;
    private readonly ShellInfoTips _infoTips;
    private readonly DriveMonitor _drives;
    private readonly ChangeFeed _changeFeed;
    private readonly DirectoryLister _lister;
    private readonly DirectoryCache _cache = new();
    private readonly List<(PaneViewModel Pane, PaneScanner Scanner, Action Handler)> _panes = [];
    private readonly List<TreeViewModel> _trees = [];
    private readonly List<DriveListViewModel> _driveLists = [];
    private TreeRoots? _treeRoots;
    private TreeLoader? _treeLoader;
    private bool _disposed;

    private AppServices(ILoggerFactory loggerFactory, IUiDispatcher dispatcher, string? homeFolder, string indexDirectory, Func<IndexSettings> indexSettings, string dataDirectory)
    {
        _loggerFactory = loggerFactory;
        Dispatcher = dispatcher;
        _iconsSta = new ShellSta("icons", loggerFactory);
        _miscSta = new ShellSta("misc", loggerFactory);
        _drives = new DriveMonitor(loggerFactory.CreateLogger<DriveMonitor>());
        _changeFeed = new ChangeFeed(_drives, loggerFactory.CreateLogger<ChangeFeed>());
        _lister = new DirectoryLister(ExtensionTable.Shared, loggerFactory.CreateLogger<DirectoryLister>());
        TypeNames = new TypeNameCache(_iconsSta, ExtensionTable.Shared, loggerFactory);
        Icons = new IconService(_iconsSta, TypeNames, _drives, ExtensionTable.Shared, loggerFactory);
        Index = new IndexService(new IndexServiceOptions
        {
            IndexDirectory = indexDirectory,
            Drives = _drives,
            Source = new Win32IndexSource(loggerFactory.CreateLogger<Win32IndexSource>()),
            Settings = indexSettings,
            LoggerFactory = loggerFactory,
            ChangeFeed = _changeFeed, // the panes' root watchers (§8.3)
            IgnoredPaths = [dataDirectory], // the index's own writes and the logs
            IncludeDrive = IndexDriveFilter.FromEnvironment(), // null unless a MANDRILL_DEBUG perf run sets MANDRILL_INDEX_DRIVES
            LowLatencyGcWhileBuilding = IndexDriveFilter.LowLatencyGcFromEnvironment() ?? false, // WP-2.7 A/B; default decided by the measurement
        });
        Search = new SearchService(Index, () => indexSettings().Exclude, loggerFactory); // WP-2.2b: excludes from settings
        FileOperations = new ShellFileOperations(loggerFactory);
        _contextMenus = new ContextMenuService(loggerFactory); // WP-3.1: its own "menu" and "menu-verbs" STAs
        ShellCommands = new ShellCommands(_miscSta, loggerFactory);
        Clipboard = new ClipboardService(loggerFactory); // WP-3.3: its own long-lived "clipboard" STA
        Launcher = new ShellLauncher(_miscSta, loggerFactory);
        _infoTips = new ShellInfoTips(loggerFactory); // WP3: its own "infotip" STA
        Context = new PaneContext
        {
            ChangeFeed = _changeFeed,
            Drives = _drives,
            FileOperations = FileOperations, // WP-3.2: the shell's IFileOperation
            ShellLauncher = Launcher,
            OpenWith = (path, ct) => ShellCommands.OpenWithAsync(path, OwnerWindow, ct), // WP-3.3: no associated app -> "Open with"
            FolderAccess = new ElevatedFolderAccess(loggerFactory), // owner F14: elevation on demand only, on its own STA
            InfoTips = _infoTips,
            Dispatcher = dispatcher,
            HomeFolder = homeFolder,
            HiddenFiles = ExplorerSettings.ReadHiddenFiles,
            LoggerFactory = loggerFactory,
            EnsureTypeNames = (ids, ct) => TypeNames.EnsureResolvedAsync(ids, ct),
            TypeName = Icons.TypeName, // thread-safe cache lookup
        };
    }

    /// <summary>UI dispatcher shared by all panes.</summary>
    public IUiDispatcher Dispatcher { get; }

    /// <summary>Shell type names (icons STA).</summary>
    public TypeNameCache TypeNames { get; }

    /// <summary>Shell icons (icons STA).</summary>
    public IconService Icons { get; }

    /// <summary>The search index (not started until <see cref="IIndexService.Start"/>; status events arrive on the index thread).</summary>
    public IIndexService Index { get; }

    /// <summary>Filename search over the index (live listing where it does not reach), shared by the search windows.</summary>
    public ISearchService Search { get; }

    /// <summary>The native shell context menu and shell verbs without a menu (Del), on the "menu" STA.</summary>
    public IContextMenuService ContextMenus => _contextMenus;

    /// <summary>Copy, move, delete, rename, new folder/file through IFileOperation (owner window set by the app).</summary>
    public ShellFileOperations FileOperations { get; }

    /// <summary>Files on the clipboard (Cut, Copy, Paste) and its change listener (started by the app).</summary>
    public ClipboardService Clipboard { get; }

    /// <summary>The panes' shell launcher.</summary>
    public ShellLauncher Launcher { get; }

    /// <summary>The main window's handle, set by the app once the window exists (owner of "Open with").</summary>
    public nint OwnerWindow { get; set; }

    /// <summary>Open terminal here (misc STA); Properties is on <see cref="ContextMenus"/>.</summary>
    public ShellCommands ShellCommands { get; }

    /// <summary>UI-thread icon bitmaps shared by all panes.</summary>
    public IconBitmapCache Bitmaps { get; } = new();

    /// <summary>Services handed to every pane.</summary>
    public PaneContext Context { get; }

    /// <summary>Tree roots shared by all panes (WP-1.9); call <see cref="TreeRoots.RefreshAsync"/> once at startup. UI thread.</summary>
    public TreeRoots TreeRoots
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _treeRoots ??= new TreeRoots(
                _drives,
                Dispatcher,
                shellName: (root, ct) => _miscSta.InvokeAsync(() => Drives.TryGetShellDisplayName(root), TimeSpan.FromSeconds(5), ct),
                loggerFactory: _loggerFactory);
        }
    }

    /// <summary>Creates the services. Worker thread: resolves the home folder and starts the STA threads and monitors.</summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="dispatcher">UI dispatcher (created on the UI thread beforehand).</param>
    /// <param name="indexDirectory">The index folder (<c>AppPaths.IndexDirectory</c>).</param>
    /// <param name="indexSettings">Current index settings (read on the index thread).</param>
    /// <param name="dataDirectory">The app's data folder (<c>%APPDATA%\mandrill</c>); its changes are not indexed.</param>
    /// <returns>The services.</returns>
    public static AppServices Create(ILoggerFactory loggerFactory, IUiDispatcher dispatcher, string indexDirectory, Func<IndexSettings> indexSettings, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrEmpty(indexDirectory);
        ArgumentNullException.ThrowIfNull(indexSettings);
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        string? home = KnownFolders.HomeOrSystemRoot(out bool usedFallback);
        if (usedFallback)
        {
            loggerFactory.CreateLogger<AppServices>().LogWarning("The user profile folder is unknown; panes fall back to {Root}", home);
        }

        return new AppServices(loggerFactory, dispatcher, home, indexDirectory, indexSettings, dataDirectory);
    }

    /// <summary>Creates a pane with its own scanner. UI thread.</summary>
    /// <returns>The pane.</returns>
    public PaneViewModel CreatePane()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var scanner = new PaneScanner(_lister, _cache, new EntrySorter(TypeNames.Get), _loggerFactory);
        var pane = new PaneViewModel(scanner, Context);
        Action handler = () => Dispatcher.Post(() =>
        {
            if (!_disposed)
            {
                pane.OnTypeNamesChanged();
            }
        });
        TypeNames.TypeNamesReady += handler;
        _panes.Add((pane, scanner, handler));
        return pane;
    }

    /// <summary>Creates the folder tree of <paramref name="pane"/> over the shared roots and loader. UI thread.</summary>
    /// <param name="pane">A pane from <see cref="CreatePane"/>.</param>
    /// <returns>The tree (disposed with the services).</returns>
    public TreeViewModel CreateTree(PaneViewModel pane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _treeLoader ??= new TreeLoader(_lister, _loggerFactory);
        var tree = new TreeViewModel(pane, TreeRoots, _treeLoader, Dispatcher, ExplorerSettings.ReadHiddenFiles, _loggerFactory);
        _trees.Add(tree);
        return tree;
    }

    /// <summary>Creates the This PC drive list of <paramref name="pane"/> over the shared roots (ADR-0015). UI thread.</summary>
    /// <param name="pane">A pane from <see cref="CreatePane"/>.</param>
    /// <returns>The list (disposed with the services).</returns>
    public DriveListViewModel CreateDriveList(PaneViewModel pane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var drives = new DriveListViewModel(pane, TreeRoots, Dispatcher, loggerFactory: _loggerFactory);
        _driveLists.Add(drives);
        return drives;
    }

    /// <summary>Stops panes, monitors and STA threads (window closed). The index only gets a stop request here; the close path awaits its save first.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (TreeViewModel tree in _trees)
        {
            tree.Dispose();
        }

        _trees.Clear();
        foreach (DriveListViewModel drives in _driveLists)
        {
            drives.Dispose();
        }

        _driveLists.Clear();
        _treeRoots?.Dispose();
        foreach (var (pane, scanner, handler) in _panes)
        {
            TypeNames.TypeNamesReady -= handler;
            pane.Dispose();
            scanner.Dispose();
        }

        _panes.Clear();
        Index.Dispose(); // before the drive monitor it listens to
        _contextMenus.Dispose();
        Clipboard.Dispose();
        _infoTips.Dispose();
        Icons.Dispose();
        TypeNames.Dispose();
        _changeFeed.Dispose();
        _drives.Dispose();
        _iconsSta.Dispose();
        _miscSta.Dispose();
    }
}
