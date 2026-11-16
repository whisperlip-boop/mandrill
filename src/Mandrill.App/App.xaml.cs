using System.Diagnostics;
using Mandrill.App.Composition;
using Mandrill.App.Platform;
using Mandrill.App.Views;
using Mandrill.Core.Diagnostics;
using Mandrill.Core.Index;
using Mandrill.Core.Localization;
using Mandrill.Core.Platform;
using Mandrill.Core.Shell;
using Mandrill.Core.State;
using Mandrill.Core.Update;
using Mandrill.Presentation.Common;
using Mandrill.Presentation.Favorites;
using Mandrill.Presentation.Layout;
using Mandrill.Presentation.Panes;
using Mandrill.Presentation.Settings;
using Mandrill.Presentation.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mandrill.App;

/// <summary>Application entry: startup I/O on a worker, then the main window.</summary>
public sealed partial class App : Application
{
    // Architecture §7 startup: the index starts 1 s after the first frame, so it never competes with the first paint.
    private static readonly TimeSpan IndexStartDelay = TimeSpan.FromSeconds(1);

    // §9: the first context menu in a process takes 0.6-1.2 s (S4); build two 2 s after the first frame, unseen.
    private static readonly TimeSpan MenuPrewarmDelay = TimeSpan.FromSeconds(2);

    private readonly Stopwatch _startup = Stopwatch.StartNew();
    private readonly DateTime _ctorUtc = DateTime.UtcNow;
    private UiLagMonitor? _lag;
    private ILogger _log = NullLogger.Instance;
    private BootstrapResult? _boot;
    private MainWindow? _window;
    private AppServices? _services;
    private PaneViewWiring? _views; // builds a pane's view when the pane is first shown (WP-1.17)
    private SessionCoordinator? _session;
    private DialogLauncher? _dialogs;
    private SearchWindows? _searchWindows;
    private StateFileWatcher? _stateWatcher;
    private int _settingsReloads; // reload requests per file: only the latest one is applied (results can finish out of order)
    private int _favoritesReloads;
    private PaneContextMenus? _contextMenus;
    private Action<IndexStatus>? _indexStatusHandler;
    private bool _windowClosed;
    private bool _restartOnClose;

    /// <summary>Initializes the application and the global error handlers.</summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    /// <inheritdoc />
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Directories, log file and locale tables are disk I/O: keep them off the UI thread.
            var boot = await Task.Run(() => AppBootstrap.StartAsync(AppPaths.CreateDefault()));
            _boot = boot;
            _log = boot.LoggerFactory.CreateLogger<App>();
            var perf = boot.LoggerFactory.CreateLogger(MandrillLogging.PerfCategory);
            PerfLog.Initialize(perf);
            PerfLog.Mark("App ctor", _ctorUtc);
            PerfLog.Mark("bootstrap done");
            _lag = MandrillLogging.IsDebugEnabled ? new UiLagMonitor(DispatcherQueue.GetForCurrentThread(), perf) : null;

            // Composition root (WP-1.8): STA threads, monitors and the home-folder lookup start off the UI thread.
            var dispatcher = new WinUiDispatcher(DispatcherQueue.GetForCurrentThread(), boot.LoggerFactory.CreateLogger<WinUiDispatcher>());
            var settings = new SettingsService(boot.State.Settings, boot.State.SettingsStore, boot.LoggerFactory.CreateLogger<SettingsService>());
            var (services, hidden, font) = await Task.Run(() => (
                AppServices.Create(boot.LoggerFactory, dispatcher, boot.Paths.IndexDirectory, () => settings.Current.Index, boot.Paths.DataDirectory),
                ExplorerSettings.ReadHiddenFiles(),
                ExplorerSettings.ReadIconTitleFont()));
            _services = services;
            PerfLog.Mark("services created");

            var icons = new Icons.IconProvider(boot.LoggerFactory.CreateLogger<Icons.IconProvider>());
            var viewModel = new MainViewModel(boot.LoggerFactory.CreateLogger<MainViewModel>())
            {
                ShowHidden = hidden.ShowHidden,
                StartNewWindow = () => Task.Run(() => AppRestart.StartNewInstance(_log, "new window")), // process start: off the UI thread
            };
            AppTheme.SetPalette(settings.Current.Theme); // before any view is built, so none needs a refresh

            // shortcut: read once; a font change in Windows applies after a restart. Before any view (StaticResource).
            if (font is { } itemFont)
            {
                Resources["MandrillListFontFamily"] = new FontFamily(itemFont.Face);
                Resources["MandrillListFontSize"] = itemFont.Size;
            }

            _log.LogInformation("List font: {Face} {Size} px", font?.Face ?? "Segoe UI (fallback)", font?.Size ?? 12);
            _window = new MainWindow(viewModel, icons, boot.Paths, boot.LoggerFactory.CreateLogger<MainWindow>());
            AppTheme.Apply(_window, AppTheme.Parse(settings.Current.Theme)); // owner round 5 #5: before the first frame
            var languages = new LanguageSetup(boot.Languages, boot.AutoLanguage, () =>
            {
                _restartOnClose = true; // OnWindowClosed starts the new instance once the session and index are saved
                viewModel.ExitCommand.Execute(null);
            });
            _dialogs = new DialogLauncher(_window, viewModel, settings, services.Context.ShellLauncher, new UpdateChecker(boot.LoggerFactory), boot.Paths, icons, _window.ShowStatus, languages, boot.LoggerFactory.CreateLogger<DialogLauncher>());
            PerfLog.Mark("main window created");

            // WP-1.10: 4 panes, re-parented by the layout. WP-1.17: a pane's view and tree are built the first time the pane
            // is shown (hidden panes cost no startup time), then kept; never rebuilt.
            var panes = new PaneViewModel[LayoutManager.MaxPanes];
            for (int i = 0; i < panes.Length; i++)
            {
                panes[i] = services.CreatePane();
            }

            var layout = new LayoutManager(panes, () => settings.LayoutDefaults, boot.LoggerFactory);
            viewModel.AttachLayout(layout);

            // WP-2.2b: one search popup per pane, owned by the main window.
            var clipboard = new WinUiClipboard(boot.LoggerFactory.CreateLogger<WinUiClipboard>()); // shared by search and the panes
            var searchWindows = _searchWindows = new SearchWindows(_window, layout, services, clipboard, boot.LoggerFactory, boot.Paths.SearchIcon);
            PerfLog.Mark("search windows created");

            // WP-3.1: native context menus and Del keys of every pane.
            var window = _window;
            // WP-3.2: the shell's file-operation dialogs are owned by the main window; changed folders re-list at once (AC-3.10).
            services.FileOperations.Owner = WinRT.Interop.WindowNative.GetWindowHandle(window);

            services.OwnerWindow = WinRT.Interop.WindowNative.GetWindowHandle(window); // owner of "Open with" (§15 (d))

            // AC-3.14: cut items (any CF_HDROP with a move effect) show dimmed in every pane until the clipboard changes.
            services.Clipboard.Changed += () => dispatcher.Post(() => _views?.ApplyCut());
            _ = StartClipboardAsync(services);
            services.FileOperations.Completed += result => dispatcher.Post(() =>
            {
                foreach (var pane in panes)
                {
                    pane.ReconcileIfShowing(result.TouchedFolders);
                }
            });
            var contextMenus = _contextMenus = new PaneContextMenus(
                services.ContextMenus,
                services.FileOperations,
                services.ShellCommands,
                clipboard,
                () => WinRT.Interop.WindowNative.GetWindowHandle(window),
                boot.LoggerFactory.CreateLogger<PaneContextMenus>(),
                services.Clipboard,
                MandrillLogging.ForceFallbackMenu);
            var paneLogger = boot.LoggerFactory.CreateLogger<PaneView>();
            PaneView BuildPaneView(PaneViewModel pane, bool deferList)
            {
                var view = new PaneView();
                if (deferList)
                {
                    view.DeferList();
                }

                view.Attach(pane, services.Icons, services.Bitmaps, dispatcher, icons, paneLogger);
                view.AttachTree(services.CreateTree(pane));
                view.AttachDriveList(services.CreateDriveList(pane));
                return view;
            }

            var views = _views = new PaneViewWiring(BuildPaneView, contextMenus, services.Clipboard, pane => searchWindows.Open(pane))
            {
                DeferLists = true, // WP-1.17: lists of panes built before the first frame are revealed after it
            };
            _window.ShowLayout(layout, views.Create);
            PerfLog.Mark("layout shown");
            _session = new SessionCoordinator(_window, layout, boot.State, boot.LoggerFactory, boot.InstanceSlot);
            _session.ApplySavedBounds();
            _window.ExitHandler = _session.RequestCloseAsync;
            _session.StopBackgroundAsync = () => services.Index.StopAsync(CancellationToken.None); // saves what changed, ≤3 s

            // WP-1.12: the favorites bar over favorites.json (loaded at bootstrap); Settings → "Show favorites bar" toggles it.
            var favorites = new FavoritesViewModel(layout, boot.State.Favorites, boot.State.FavoritesStore.SaveAsync, dispatcher, boot.LoggerFactory)
            {
                IsBarVisible = boot.State.ShowFavoritesBar,
            };
            settings.Changed += () => dispatcher.Post(() => favorites.IsBarVisible = settings.Current.ShowFavoritesBar);
            settings.Changed += () => dispatcher.Post(() =>
            {
                var theme = AppTheme.Parse(settings.Current.Theme); // applied at once, also to open search popups
                bool refresh = AppTheme.SetPalette(settings.Current.Theme);
                AppTheme.Apply(window, theme, refresh);
                searchWindows.ApplyTheme(theme, refresh);
            });
            settings.Changed += services.Index.ApplySettings; // AC-2.10 at runtime: excludes and RescanHours (queued to the index thread)
            _ = WatchStateFilesAsync(boot, settings, favorites, dispatcher);
            _window.ShowFavorites(favorites, boot.LoggerFactory.CreateLogger<FavoritesBar>());
            _window.Closed += OnWindowClosed;
            PerfLog.Mark("favorites shown");
            if (_lag is not null && _window.Content is FrameworkElement content)
            {
                content.Loaded += (_, _) => PerfLog.Mark("window content loaded"); // debug runs only (marks)
            }

            _window.Activate();
            PerfLog.Mark("activated");
            if (_lag is not null)
            {
                _ = MarkFirstFrameAsync();
            }

            _ = RevealListsAsync(_window);
            _session.ApplySavedState();
            PerfLog.Mark("saved state applied");
            _ = RefreshTreeRootsAsync(services);
            PerfLog.Mark("tree roots refresh started");
            _ = RestoreAndMeasureAsync(_session, _window, perf);
            PerfLog.Mark("session restore started");
            _ = StartIndexAfterFirstFrameAsync(services, dispatcher);
            _ = PrewarmContextMenuAsync(services, boot.Paths);
            if (!boot.FileLoggingEnabled)
            {
                _window.ShowStatus(Localizer.Current.Format("error.noDataDirectory", ("path", boot.Paths.DataDirectory)));
            }

            perf.LogInformation("Startup: App ctor to main window activated {Ms} ms (language {Language})", _startup.ElapsedMilliseconds, boot.Localizer.Language);
        }
        catch (Exception ex)
        {
            // Without a main window there is nothing to recover into; exit instead of leaving a headless process.
            _log.LogCritical(ex, "Startup failed");
            FlushLog();
            Exit();
        }
    }

    // Requirements §6 "startup to interactive window with restored layout": the frame after the panes were restored.
    private async Task RestoreAndMeasureAsync(SessionCoordinator session, MainWindow window, ILogger perf)
    {
        await session.RestoreAndReportAsync(window.ShowStatus, window.FocusActivePane);
        PerfLog.Mark("session restored");
        await NextFrameAsync();
        PerfLog.Mark("restored layout painted");
        PerfLog.LogCosts();
        perf.LogInformation("Startup: App ctor to restored layout painted {Ms} ms", _startup.ElapsedMilliseconds);
    }

    // WP-1.17: the first frame after Activate (process start → first frame is the startup target).
    private static async Task MarkFirstFrameAsync()
    {
        await NextFrameAsync();
        PerfLog.Mark("first frame");
    }

    // WP-1.17: the lists' first layout (~150 ms per pane) is spread over frames. The first frame paints the window and the
    // panes' frames, toolbars and path boxes; the active pane's list follows in the next frame, then the other visible
    // panes' lists one per frame, each after a Low-priority turn so input in between is handled first.
    private async Task RevealListsAsync(MainWindow window)
    {
        try
        {
            // Rendering fires before the frame is composed: yield after it, or a list shown now joins the same frame.
            await NextFrameAsync();
            await LowPriorityAsync();
            if (_views is not { } views)
            {
                return;
            }

            views.DeferLists = false;
            PaneView? active = window.ActivePaneView;
            var deferred = views.Views.Where(v => v.IsListDeferred).OrderBy(v => ReferenceEquals(v, active) ? 0 : 1).ToList();
            for (int i = 0; i < deferred.Count; i++)
            {
                if (_windowClosed)
                {
                    return;
                }

                PaneView view = deferred[i];
                view.RevealList();
                if (i == 0)
                {
                    PerfLog.Mark("first list revealed");
                }

                // If no frame renders any more (window closed), this waits until the process ends; nothing else waits on it.
                await NextFrameAsync();
                await LowPriorityAsync();
                if (_windowClosed)
                {
                    return;
                }

                // The restore's focus call may have found the list still held back. Only the active pane takes it: focusing
                // another (Alt+N meanwhile) would make that pane active again (qa-wp117 m3).
                view.CompleteDeferredFocus(ReferenceEquals(window.ActivePaneView, view));
            }

            PerfLog.Mark("lists revealed");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Revealing the pane lists failed");
            if (_views is { } views)
            {
                views.DeferLists = false; // panes shown later get their list at once
                if (!_windowClosed)
                {
                    foreach (PaneView view in views.Views)
                    {
                        view.RevealList();
                        view.CompleteDeferredFocus(ReferenceEquals(window.ActivePaneView, view)); // releases held FocusList calls
                    }
                }
            }
        }
    }

    // UI thread: completes after the work queued at Normal priority has run.
    private static Task LowPriorityAsync()
    {
        var turn = new TaskCompletionSource();
        if (!DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () => turn.TrySetResult()))
        {
            turn.TrySetResult(); // shutting down: nothing to yield to
        }

        return turn.Task;
    }

    // UI thread: completes when the next frame is rendered.
    private static Task NextFrameAsync()
    {
        var frame = new TaskCompletionSource();
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            frame.TrySetResult();
        };
        CompositionTarget.Rendering += handler;
        return frame.Task;
    }

    // WP-2.3b: status events arrive on the index thread (≤4/s per state). Each one posts an update that reads the latest
    // status, so the indicator never shows an older state after a newer one.
    private async Task StartIndexAfterFirstFrameAsync(AppServices services, IUiDispatcher dispatcher)
    {
        try
        {
            await NextFrameAsync();
            await Task.Delay(IndexStartDelay);
            if (_windowClosed)
            {
                return;
            }

            _indexStatusHandler = _ => dispatcher.Post(ShowIndexStatus);
            services.Index.StatusChanged += _indexStatusHandler;
            PerfLog.Mark("index start");
            services.Index.Start();
            ShowIndexStatus();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Starting the index failed");
        }
    }

    private async Task StartClipboardAsync(AppServices services)
    {
        try
        {
            await services.Clipboard.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The clipboard listener did not start (Paste stays off)");
        }
    }

    private async Task PrewarmContextMenuAsync(AppServices services, AppPaths paths)
    {
        try
        {
            await NextFrameAsync();
            await Task.Delay(MenuPrewarmDelay);
            if (!_windowClosed)
            {
                PerfLog.Mark("menu prewarm");
                await services.ContextMenus.PrewarmAsync(
                    [new ContextMenuTarget(paths.DataDirectory, []), new ContextMenuTarget(paths.LogDirectory, ["mandrill.log"])],
                    CancellationToken.None);
                PerfLog.Mark("menu prewarm done");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Context menu pre-warm failed");
        }
    }

    private void ShowIndexStatus()
    {
        if (!_windowClosed && _services is { } services && _window is { } window)
        {
            window.ShowIndexStatus(IndexStatusText.From(services.Index.Status));
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _lag?.Dispose();
        _windowClosed = true;
        _log.LogInformation("Main window closed; shutting down");
        try
        {
            if (_indexStatusHandler is { } indexHandler && _services is { } services)
            {
                services.Index.StatusChanged -= indexHandler;
            }

            _searchWindows?.Dispose();
            _contextMenus?.Dispose();
            _window?.DetachFavorites();
            foreach (PaneView view in _views?.Views ?? [])
            {
                view.FolderTree?.Detach();
                view.FileList.Detach();
                view.DriveList.Detach();
            }

            _stateWatcher?.Dispose();
            _ = _session?.DisposeAsync().AsTask();
            _services?.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Shutting down services failed");
        }

        if (_restartOnClose)
        {
            // This process is ending: give its instance number to the new one, so a restarted window takes the same number
            // and is not cascaded like a second window (QA MI F2). A New window keeps its own number, so it cascades.
            _boot?.Dispose();
            AppRestart.StartNewInstance(_log, "restart"); // logs a failure; nothing is left on screen to report it in
        }

        FlushLog();
    }

    // §18 D3: settings or favorites saved by another instance are reloaded (worker) and applied here (UI thread).
    private async Task WatchStateFilesAsync(BootstrapResult boot, SettingsService settings, FavoritesViewModel favorites, IUiDispatcher dispatcher)
    {
        try
        {
            var watcher = await Task.Run(() => new StateFileWatcher(boot.Paths.DataDirectory, boot.LoggerFactory.CreateLogger<StateFileWatcher>()));
            if (_windowClosed)
            {
                watcher.Dispose();
                return;
            }

            watcher.Changed += file => _ = ReloadStateAsync(file, boot.State, settings, favorites, dispatcher);
            _stateWatcher = watcher;

            // A save by another window between this window's start-up load and now raised nothing: catch up once (a file
            // that does not exist yet is nothing to catch up on, no warning).
            _ = ReloadStateAsync(StateFileWatcher.SettingsFileName, boot.State, settings, favorites, dispatcher, catchUp: true);
            _ = ReloadStateAsync(StateFileWatcher.FavoritesFileName, boot.State, settings, favorites, dispatcher, catchUp: true);
        }
        catch (Exception ex)
        {
            // No data folder (it could not be created) or no watcher: this window just does not follow the others.
            _log.LogWarning(ex, "Settings and favorites of other Mandrill windows are not followed");
        }
    }

    // Thread pool (watcher event). A failed reload keeps the current values; the next save by another window retries.
    private async Task ReloadStateAsync(string file, StartupState state, SettingsService settings, FavoritesViewModel favorites, IUiDispatcher dispatcher, bool catchUp = false)
    {
        try
        {
            if (file == StateFileWatcher.SettingsFileName)
            {
                int request = Interlocked.Increment(ref _settingsReloads);
                if (await state.SettingsStore.TryReloadAsync(CancellationToken.None, missingIsExpected: catchUp) is { } next)
                {
                    dispatcher.Post(() =>
                    {
                        if (!_windowClosed && request == Volatile.Read(ref _settingsReloads))
                        {
                            settings.ApplyExternal(next);
                        }
                    });
                }
            }
            else
            {
                int request = Interlocked.Increment(ref _favoritesReloads);
                if (await state.FavoritesStore.TryReloadAsync(CancellationToken.None, missingIsExpected: catchUp) is { } document)
                {
                    dispatcher.Post(() =>
                    {
                        if (!_windowClosed && request == Volatile.Read(ref _favoritesReloads))
                        {
                            favorites.ReplaceAll(document);
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reloading {File} saved by another window failed", file);
        }
    }

    // Quick access and drive labels need disk checks (worker); the tree shows plain drives until they arrive.
    private async Task RefreshTreeRootsAsync(AppServices services)
    {
        try
        {
            await services.TreeRoots.RefreshAsync();
            PerfLog.Mark("tree roots refreshed");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the tree roots failed");
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _log.LogError(e.Exception, "Unhandled UI exception");
        if (_window is null)
        {
            // Startup has not produced a window: let the process terminate rather than run invisibly.
            FlushLog();
            return;
        }

        // Keep running where possible (architecture §10); the user sees a short note instead of a crash.
        e.Handled = true;
        _window.ShowStatus(Localizer.Current["error.unexpected"]);
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        _log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating={Terminating})", e.IsTerminating);
        if (e.IsTerminating)
        {
            FlushLog();
        }
    }

    // Disposing the factory flushes the log writer's queue (up to 1 s); later log calls go nowhere.
    private void FlushLog()
    {
        _log = NullLogger.Instance;
        var boot = _boot;
        _boot = null;
        boot?.LoggerFactory.Dispose();
    }
}
