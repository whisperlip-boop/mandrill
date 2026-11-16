using System.ComponentModel;
using System.Diagnostics;
using Mandrill.App.Platform;
using Mandrill.App.Views;
using Mandrill.Core.Diagnostics;
using Mandrill.Presentation.Layout;
using Mandrill.Presentation.Panes;
using Mandrill.Presentation.Search;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mandrill.App.Composition;

/// <summary>
/// The search popups, one per pane (architecture §3 WP-2.2b): Ctrl+F or the pane's Search button opens it or raises the
/// open one. Closing disposes its view model (results are not kept); the next open prefills the last query. A pane that
/// leaves the layout closes its popup; <see cref="Dispose"/> closes them all (main window closed). UI thread.
/// </summary>
public sealed class SearchWindows : IDisposable
{
    private readonly Window _owner;
    private readonly LayoutManager _layout;
    private readonly AppServices _services;
    private readonly IClipboardText _clipboard;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly string _iconPath;
    private readonly Dictionary<PaneViewModel, Popup> _open = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PaneViewModel, string> _lastQuery = new(ReferenceEqualityComparer.Instance);
    private int _created;
    private bool _disposed;

    /// <summary>Creates the manager.</summary>
    /// <param name="owner">The main window (owner of the popups).</param>
    /// <param name="layout">The layout (a pane that leaves it closes its popup).</param>
    /// <param name="services">Search, index, icons and the panes' shared services.</param>
    /// <param name="clipboard">Clipboard for "Copy path".</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="iconPath">The popups' title bar icon.</param>
    public SearchWindows(Window owner, LayoutManager layout, AppServices services, IClipboardText clipboard, ILoggerFactory loggerFactory, string iconPath)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<SearchWindows>();
        _iconPath = iconPath ?? throw new ArgumentNullException(nameof(iconPath));
        _layout.LayoutChanged += CloseHiddenPanes;
    }

    /// <summary>Opens the search popup of <paramref name="pane"/>, or raises it when it is open.</summary>
    /// <param name="pane">The pane.</param>
    public void Open(PaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        if (_disposed)
        {
            return;
        }

        if (_open.TryGetValue(pane, out var existing))
        {
            existing.Window.Raise();
            return;
        }

        long start = Stopwatch.GetTimestamp();
        Task indexLoaded = _services.Index.EnsureLoadedAsync(); // a reader loads the index now (§18 D2); not awaited
        SearchViewModel? vm = null;
        SearchView? view = null;
        SearchWindow? window = null;
        SearchContextMenus? menus = null;
        PropertyChangedEventHandler? titleHandler = null;
        try
        {
            vm = new SearchViewModel(pane, _services.Context, _services.Search, _services.Index, _clipboard)
            {
                QueryText = _lastQuery.GetValueOrDefault(pane, string.Empty),
            };
            view = new SearchView();
            var created = window = new SearchWindow(view, _owner, SearchText.Title(pane.LocationText), _iconPath);
            if (!created.IsOwned)
            {
                _logger.LogWarning("The search window is not owned by the main window");
            }

            titleHandler = (_, e) =>
            {
                if (e.PropertyName == nameof(PaneViewModel.LocationText))
                {
                    created.Title = SearchText.Title(pane.LocationText); // ui-spec §3: "Search – <pane folder>" (or This PC)
                }
            };
            pane.PropertyChanged += titleHandler;

            // Attached last: the long-lived icon service and the view model must not root a window that failed to open.
            view.Attach(vm, _services.Icons, _services.Bitmaps, _services.Dispatcher, _logger);

            // WP-3.5: the results' native menu, owned by this popup (its Properties sheets and verb dialogs come up over it).
            menus = new SearchContextMenus(
                _services.ContextMenus,
                _services.FileOperations,
                () => WinRT.Interop.WindowNative.GetWindowHandle(created),
                _loggerFactory.CreateLogger<SearchContextMenus>(),
                MandrillLogging.ForceFallbackMenu);
            menus.Attach(view, vm);
            _open[pane] = new Popup(created, vm);
            created.Activate();
            _ = vm.RefreshWhenLoadedAsync(indexLoaded);
            var (openVm, openView, openTitle, openMenus) = (vm, view, titleHandler, menus);
            created.Closed += (_, _) =>
            {
                pane.PropertyChanged -= openTitle;
                _lastQuery[pane] = openVm.QueryText;
                _open.Remove(pane);
                openMenus.Dispose();
                openView.Detach();
                openVm.Dispose();
            };
            LogCreated(view, start, Stopwatch.GetElapsedTime(start), ++_created);
        }
        catch (Exception ex)
        {
            // A window that cannot be created must not take the app down; the pane stays usable.
            _logger.LogError(ex, "Opening the search window failed");
            if (titleHandler is not null)
            {
                pane.PropertyChanged -= titleHandler;
            }

            _open.Remove(pane);
            menus?.Dispose();
            view?.Detach();
            vm?.Dispose();
            CloseQuietly(window);
        }
    }

    /// <summary>Applies the Theme setting to the open popups (owner round 5 #5); new ones copy the main window's.</summary>
    /// <param name="theme">Light, Dark, or Default (follow Windows).</param>
    /// <param name="refresh">The high-contrast palette changed (<see cref="AppTheme.Apply"/>).</param>
    public void ApplyTheme(ElementTheme theme, bool refresh)
    {
        foreach (var popup in _open.Values)
        {
            AppTheme.Apply(popup.Window, theme, refresh);
        }
    }

    /// <summary>Closes every popup (main window closed).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _layout.LayoutChanged -= CloseHiddenPanes;
        foreach (var popup in _open.Values.ToArray())
        {
            popup.Window.Close();
        }
    }

    private void CloseQuietly(SearchWindow? window)
    {
        try
        {
            window?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing a search window that failed to open failed");
        }
    }

    // Lead's WP-2.2b ask: the popup's UI-thread cost (XAML load + window show), measured to its first frame after the
    // view entered the new window; "sync" is the part inside Open. The first popup of a session pays the type load.
    private static void LogCreated(SearchView view, long start, TimeSpan sync, int ordinal)
    {
        EventHandler<object>? rendered = null;
        rendered = (_, _) =>
        {
            CompositionTarget.Rendering -= rendered;
            PerfLog.Logger.LogInformation(
                "Search popup created in {Ms:F0} ms (sync {SyncMs:F0} ms, popup #{Ordinal} of this session)",
                Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                sync.TotalMilliseconds,
                ordinal);
        };
        if (view.IsLoaded)
        {
            CompositionTarget.Rendering += rendered;
            return;
        }

        RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            view.Loaded -= loaded;
            CompositionTarget.Rendering += rendered;
        };
        view.Loaded += loaded;
    }

    // Visible panes are Pool[0..VisibleCount): a popup of any other pane closes with it.
    private void CloseHiddenPanes()
    {
        for (int i = _layout.VisibleCount; i < _layout.Pool.Count; i++)
        {
            if (_open.TryGetValue(_layout.Pool[i], out var popup))
            {
                popup.Window.Close();
            }
        }
    }

    private sealed record Popup(SearchWindow Window, SearchViewModel ViewModel);
}
