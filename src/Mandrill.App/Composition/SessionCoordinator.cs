using Mandrill.App.Platform;
using Mandrill.Core.Diagnostics;
using Mandrill.Core.Platform;
using Mandrill.Core.State;
using Mandrill.Presentation.Layout;
using Mandrill.Presentation.Session;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Mandrill.App.Composition;

/// <summary>
/// App side of the session (AC-1.33, architecture §7 "Session restore"): window placement (saved normal bounds +
/// maximized, clamped to a visible display), restore through <see cref="SessionController"/>, and the exit flush: the first
/// close is cancelled, the session is saved with a 1 s limit while <see cref="StopBackgroundAsync"/> runs (the index save,
/// ≤3 s, architecture §4 shutdown), then the window closes. UI thread.
/// </summary>
public sealed class SessionCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan CloseSaveLimit = TimeSpan.FromSeconds(1);

    private readonly Window _window;
    private readonly StartupState _state;
    private readonly SessionController _controller;
    private readonly ILogger _logger;
    private readonly int _instanceSlot;
    private RectInt32? _normalBounds;
    private CloseState _close;

    /// <summary>Creates the coordinator and starts tracking the window.</summary>
    /// <param name="window">Main window.</param>
    /// <param name="layout">Layout manager (attached to the main view model by the caller).</param>
    /// <param name="state">Settings, session and stores loaded at startup.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="instanceSlot">This process's instance number (<c>BootstrapResult.InstanceSlot</c>): the cascade offset.</param>
    public SessionCoordinator(Window window, LayoutManager layout, StartupState state, ILoggerFactory loggerFactory, int instanceSlot = 1)
    {
        _instanceSlot = instanceSlot;
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<SessionCoordinator>();
        _controller = new SessionController(layout, (s, ct) => state.SessionStore.SaveAsync(s, ct), CurrentPlacement, TimeProvider.System, loggerFactory);
        window.AppWindow.Changed += OnWindowChanged;
        window.AppWindow.Closing += OnClosing;
    }

    /// <summary>Background work to stop before the window closes, alongside the session save (bounds its own wait). Never awaited past a throw.</summary>
    public Func<Task>? StopBackgroundAsync { get; set; }

    /// <summary>
    /// Moves and sizes the window to the saved normal bounds (before Activate), offset for a second or later instance
    /// (§18 D5); first run keeps the default placement.
    /// </summary>
    public void ApplySavedBounds()
    {
        if (_state.Session.Window is not { } saved)
        {
            return;
        }

        // Placement is cosmetic: a failure here must never stop the app (it did: "Startup failed", WP-1.14).
        try
        {
            double scale = WindowOwner.Scale(WinRT.Interop.WindowNative.GetWindowHandle(_window));
            if (WindowPlacementRules.Fit(WindowPlacementRules.Cascade(saved, _instanceSlot, scale), DisplayWorkAreas()) is { } rect)
            {
                _window.AppWindow.MoveAndResize(rect);
                _normalBounds = rect;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restoring the window placement failed; using the default placement");
        }
    }

    /// <summary>Work areas of all displays, the primary display first (the placement fallback centres the window there).</summary>
    /// <returns>The work areas; empty when the system reports no display.</returns>
    public static IReadOnlyList<RectInt32> DisplayWorkAreas()
    {
        // DisplayArea.FindAll() cannot be enumerated (foreach/LINQ throws InvalidCastException in the CsWinRT projection
        // of WinAppSDK 2.6); Count and the indexer work.
        var displays = DisplayArea.FindAll();
        var areas = new List<RectInt32>(displays.Count);
        RectInt32? primary = DisplayArea.Primary?.WorkArea;
        if (primary is { } p)
        {
            areas.Add(p);
        }

        for (int i = 0; i < displays.Count; i++)
        {
            RectInt32 area = displays[i].WorkArea;
            if (area != primary)
            {
                areas.Add(area);
            }
        }

        return areas;
    }

    /// <summary>Maximizes the window if it was maximized when saved (after Activate, so the normal bounds stay known).</summary>
    public void ApplySavedState()
    {
        // First run (or a window never moved): the default placement is the normal bounds until the user changes them,
        // so a window maximized in this session restores maximized with sensible normal bounds next time.
        if (_normalBounds is null && _window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            var p = _window.AppWindow.Position;
            var s = _window.AppWindow.Size;
            _normalBounds = new RectInt32(p.X, p.Y, s.Width, s.Height);
        }

        if (_state.Session.Window is { Maximized: true } && _window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    /// <summary>
    /// File → Exit: saves the session (≤1 s), then closes. Does not rely on AppWindow.Closing firing for a programmatic
    /// close; if it does fire, the Closing handler lets the final close through.
    /// </summary>
    /// <returns>A task that completes when the close was requested.</returns>
    public async Task RequestCloseAsync()
    {
        if (_close == CloseState.Flushing)
        {
            return; // already saving; the close follows
        }

        if (_close == CloseState.Done)
        {
            return; // Close() was already called by the flush that finished
        }

        await FlushThenCloseAsync().ConfigureAwait(true);
    }

    /// <summary>Restores the layout and panes, then saves changes (2 s debounce).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The restore result (one status message for folders that no longer exist).</returns>
    public Task<SessionRestoreResult> RestoreAsync(CancellationToken ct) => _controller.RestoreAsync(_state.Session, _state.FirstRun, ct);

    /// <summary>Restores the session, shows the "folders no longer exist" message if any, then focuses the active pane. Never throws.</summary>
    /// <param name="showStatus">Status-bar message.</param>
    /// <param name="focus">Focuses the active pane after the restore.</param>
    /// <returns>A task that completes when the panes are restored.</returns>
    public async Task RestoreAndReportAsync(Action<string> showStatus, Action focus)
    {
        ArgumentNullException.ThrowIfNull(showStatus);
        ArgumentNullException.ThrowIfNull(focus);
        try
        {
            var result = await RestoreAsync(CancellationToken.None).ConfigureAwait(true);
            if (result.Message is { } message)
            {
                showStatus(message);
            }

            focus();
        }
        catch (Exception ex)
        {
            // Startup continues with whatever the panes show; the next change saves a fresh session.
            _logger.LogError(ex, "Restoring the session failed");
        }
    }

    /// <summary>Stops tracking and disposes the controller (after the close flush).</summary>
    /// <returns>A task that completes when stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        _window.AppWindow.Changed -= OnWindowChanged;
        _window.AppWindow.Closing -= OnClosing;
        await _controller.DisposeAsync().ConfigureAwait(true);
    }

    // Live bounds whenever the window is in its normal state, so the result does not depend on which AppWindow.Changed
    // events the first show raises; maximized/minimized keep the last normal bounds.
    private WindowPlacement? CurrentPlacement()
    {
        var appWindow = _window.AppWindow;
        var presenter = appWindow.Presenter as OverlappedPresenter;
        if (presenter is { State: OverlappedPresenterState.Restored } && appWindow.IsVisible)
        {
            _normalBounds = new RectInt32(appWindow.Position.X, appWindow.Position.Y, appWindow.Size.Width, appWindow.Size.Height);
        }

        if (_normalBounds is not { } r)
        {
            return null;
        }

        return new WindowPlacement(r.X, r.Y, r.Width, r.Height, presenter is { State: OverlappedPresenterState.Maximized });
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
        {
            _controller.NotifyChanged();
        }
    }

    // AppWindow.Closing: cancel, save with a 1 s limit, then close for real (architecture §7). Repeated requests while
    // saving (double-click on X, Alt+F4 twice) stay cancelled; only the final self-initiated close goes through.
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_close == CloseState.Done)
        {
            return;
        }

        args.Cancel = true;
        if (_close == CloseState.Flushing)
        {
            return;
        }

        await FlushThenCloseAsync().ConfigureAwait(true);
    }

    private async Task FlushThenCloseAsync()
    {
        _close = CloseState.Flushing;
        try
        {
            await Task.WhenAll(FlushWithLimitAsync(), StopBackgroundSafelyAsync()).ConfigureAwait(true);
        }
        finally
        {
            _close = CloseState.Done;
            try
            {
                _window.Close();
            }
            catch (Exception ex)
            {
                // Callers are async void (Closing, Exit): nothing may escape.
                _logger.LogError(ex, "Closing the main window failed");
            }
        }
    }

    // Never throws: the window has to close regardless of the background stop.
    private async Task StopBackgroundSafelyAsync()
    {
        if (StopBackgroundAsync is not { } stop)
        {
            return;
        }

        try
        {
            await stop().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stopping background work on exit failed");
        }
    }

    // Never throws: the window has to close regardless of the save.
    private async Task FlushWithLimitAsync()
    {
        try
        {
            using var limit = new CancellationTokenSource(CloseSaveLimit);
            bool saved = await _controller.FlushAsync(limit.Token).ConfigureAwait(true);
            _logger.LogInformation("Session {Result} on exit", saved ? "saved" : "not saved");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Saving the session on exit took longer than {Ms} ms; closing anyway", (long)CloseSaveLimit.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the session on exit failed");
        }
    }

    private enum CloseState
    {
        None,
        Flushing,
        Done,
    }
}
