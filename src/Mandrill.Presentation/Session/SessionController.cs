using System.ComponentModel;
using Mandrill.Core.Localization;
using Mandrill.Core.State;
using Mandrill.Presentation.Layout;
using Mandrill.Presentation.Panes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Presentation.Session;

/// <summary>Converts between the saved <see cref="SessionState"/> and <see cref="LayoutManager"/>'s <see cref="LayoutSnapshot"/>.</summary>
public static class SessionMapper
{
    /// <summary>The layout part of a saved session.</summary>
    /// <param name="session">Normalized session.</param>
    /// <returns>The snapshot to restore.</returns>
    public static LayoutSnapshot ToSnapshot(SessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new LayoutSnapshot(session.Layout, session.ActivePane, session.Splitters, session.Panes);
    }

    /// <summary>The session document for a captured layout and window placement.</summary>
    /// <param name="layout">Captured layout.</param>
    /// <param name="window">Window placement, or null when unknown (first frame not shown yet).</param>
    /// <returns>The session to save.</returns>
    public static SessionState ToSession(LayoutSnapshot layout, WindowPlacement? window)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new SessionState(window, layout.Layout, layout.ActivePane, layout.Splitters, layout.Panes);
    }
}

/// <summary>Outcome of a session restore.</summary>
/// <param name="Message">One non-modal status message for folders that no longer exist, or null.</param>
/// <param name="MissingPaths">Saved folders that were replaced by an ancestor or the home folder (logged, shown in the tooltip).</param>
public sealed record SessionRestoreResult(string? Message, IReadOnlyList<string> MissingPaths);

/// <summary>
/// Session persistence (AC-1.33, architecture §7 "Session restore"): restores the layout and the panes from
/// <see cref="SessionState"/>, then saves the session 2 s after the first unsaved change (later changes do not postpone it,
/// so a kill loses at most 2 s) and on <see cref="FlushAsync"/> at exit. Changes = layout, active pane, splitters, window
/// placement (<see cref="NotifyChanged"/>), and each pane's committed path, view, sort, tree, icon size, columns and path
/// history. UI thread only.
/// </summary>
public sealed class SessionController : IAsyncDisposable
{
    /// <summary>Delay after the first unsaved change (architecture §7).</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> PersistedPaneProperties =
    [
        nameof(PaneViewModel.CurrentPath),
        nameof(PaneViewModel.IsThisPc),
        nameof(PaneViewModel.ViewMode),
        nameof(PaneViewModel.SortColumn),
        nameof(PaneViewModel.SortDescending),
        nameof(PaneViewModel.TreeVisible),
        nameof(PaneViewModel.TreeWidth),
        nameof(PaneViewModel.IconSize),
        nameof(PaneViewModel.Columns),
        nameof(PaneViewModel.PathHistory),
    ];

    private readonly LayoutManager _layout;
    private readonly Func<WindowPlacement?> _window;
    private readonly DebouncedSaver<SessionState> _saver;
    private readonly ILogger _logger;
    private bool _listening;
    private bool _restoreStarted;

    // Saved state per pane at restore time: a pane that has no folder yet (still restoring, or its restore failed) is saved
    // with it instead of an empty path, so closing during a slow restore never loses the user's folder (AC-1.33). Keyed by
    // the pane itself, not its index: Remove pane during the restore reorders the pool.
    private readonly Dictionary<PaneViewModel, PaneState> _restored = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    /// <summary>Creates the controller.</summary>
    /// <param name="layout">The layout manager (owns the 4 panes).</param>
    /// <param name="save">Writes the session (for example <c>StateStore&lt;SessionState&gt;.SaveAsync</c>); runs on the thread pool.</param>
    /// <param name="window">Current window placement (normal bounds + maximized), or null when unknown.</param>
    /// <param name="timeProvider">Clock and timers (<see cref="TimeProvider.System"/>; a fake in tests).</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public SessionController(LayoutManager layout, Func<SessionState, CancellationToken, Task> save, Func<WindowPlacement?> window, TimeProvider timeProvider, ILoggerFactory? loggerFactory = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        ArgumentNullException.ThrowIfNull(save);
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<SessionController>();
        _saver = new DebouncedSaver<SessionState>(save, SaveDelay, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)), factory.CreateLogger<DebouncedSaver<SessionState>>());
    }

    /// <summary>
    /// Restores the layout and the panes, then starts saving changes. A saved folder that no longer exists opens its nearest
    /// existing ancestor (or the home folder); all such folders are reported in one message (E-1.2, ui-spec §4).
    /// </summary>
    /// <param name="session">Normalized session.</param>
    /// <param name="firstRun">True when no session file existed (first-run tree defaults).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The restore result.</returns>
    public async Task<SessionRestoreResult> RestoreAsync(SessionState session, bool firstRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposed, this);
        NavigationResult[] results;
        _restoreStarted = true;
        _restored.Clear();
        for (int i = 0; i < session.Panes.Count && i < _layout.Pool.Count; i++)
        {
            _restored[_layout.Pool[i]] = session.Panes[i];
        }
        try
        {
            results = await _layout.RestoreAsync(SessionMapper.ToSnapshot(session), firstRun, ct).ConfigureAwait(true);
        }
        finally
        {
            // Save from now on even if a pane failed: its state (and the layout) still change and must be kept.
            StartListening();
        }

        var missing = new List<string>();
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i].RedirectedFrom is { } from)
            {
                missing.Add(from);
            }
            else if (results[i].Outcome == NavigationOutcome.Failed && i < session.Panes.Count && session.Panes[i].Path.Length > 0)
            {
                missing.Add(session.Panes[i].Path);
            }
        }

        if (missing.Count == 0)
        {
            return new SessionRestoreResult(null, []);
        }

        _logger.LogInformation("Session restore: {Count} saved folder(s) no longer exist: {Paths}", missing.Count, string.Join("; ", missing));
        return new SessionRestoreResult(Localizer.Current.Plural("restore.missing", missing.Count), missing);
    }

    /// <summary>Something outside the panes changed (window moved, resized, maximized): schedule a save.</summary>
    public void NotifyChanged()
    {
        if (_listening && !_disposed)
        {
            _saver.Update(Capture());
        }
    }

    /// <summary>The session as it would be saved now.</summary>
    /// <returns>The session.</returns>
    public SessionState Capture()
    {
        var layout = _layout.Capture();
        if (_restored.Count > 0 && layout.Panes.Any(p => p.Path.Length == 0))
        {
            // Capture lists the visible panes in Pool order. A pane that navigated anywhere has a path and keeps its own state.
            var panes = layout.Panes.ToArray();
            for (int i = 0; i < panes.Length && i < _layout.Pool.Count; i++)
            {
                if (panes[i].Path.Length == 0 && _restored.TryGetValue(_layout.Pool[i], out var saved) && saved.Path.Length > 0)
                {
                    panes[i] = saved;
                }
            }

            layout = layout with { Panes = panes };
        }

        return SessionMapper.ToSession(layout, _window());
    }

    /// <summary>
    /// Saves the current session now (window closing), also while a restore is still running (a pane on a slow drive must
    /// not cost the whole session). Waits for a save already running.
    /// </summary>
    /// <param name="ct">Cancellation token (the caller bounds the wait, architecture §7: 1 s).</param>
    /// <returns>True when saved (or nothing to save); false when the save failed.</returns>
    public Task<bool> FlushAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_restoreStarted)
        {
            _saver.Update(Capture());
        }

        return _saver.FlushAsync(ct);
    }

    /// <summary>Stops listening and disposes the saver (an unsaved change is dropped; call <see cref="FlushAsync"/> first).</summary>
    /// <returns>A task that completes when the saver stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopListening();
        await _saver.DisposeAsync().ConfigureAwait(false);
    }

    private void StartListening()
    {
        if (_listening || _disposed)
        {
            return;
        }

        _listening = true;
        _layout.LayoutChanged += NotifyChanged;
        _layout.ActiveChanged += NotifyChanged;
        _layout.SplittersChanged += NotifyChanged;
        foreach (var pane in _layout.Pool)
        {
            pane.PropertyChanged += OnPaneChanged;
        }
    }

    private void StopListening()
    {
        if (!_listening)
        {
            return;
        }

        _listening = false;
        _layout.LayoutChanged -= NotifyChanged;
        _layout.ActiveChanged -= NotifyChanged;
        _layout.SplittersChanged -= NotifyChanged;
        foreach (var pane in _layout.Pool)
        {
            pane.PropertyChanged -= OnPaneChanged;
        }
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && PersistedPaneProperties.Contains(name))
        {
            NotifyChanged();
        }
    }
}
