using Mandrill.Presentation.Common;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Mandrill.App.Platform;

/// <summary><see cref="IUiDispatcher"/> over the window's <see cref="DispatcherQueue"/> (architecture §4).</summary>
public sealed class WinUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;
    private readonly ILogger _logger;

    /// <summary>Wraps <paramref name="queue"/>.</summary>
    /// <param name="queue">The UI thread's queue.</param>
    /// <param name="logger">Logger for work dropped during shutdown.</param>
    public WinUiDispatcher(DispatcherQueue queue, ILogger<WinUiDispatcher> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public bool HasThreadAccess => _queue.HasThreadAccess;

    /// <inheritdoc/>
    public void Post(Action action, UiPriority priority = UiPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);
        var queuePriority = priority == UiPriority.Low ? DispatcherQueuePriority.Low : DispatcherQueuePriority.Normal;
        if (!_queue.TryEnqueue(queuePriority, Guarded(action, _logger)))
        {
            // Only fails while the queue shuts down (window closed); nothing can show the result any more.
            _logger.LogDebug("UI work dropped: the dispatcher queue is shutting down");
        }
    }

    /// <summary>
    /// <paramref name="action"/> as a callback for a direct <see cref="DispatcherQueue.TryEnqueue(DispatcherQueueHandler)"/>:
    /// a callback's exception never reaches Application.UnhandledException (CoreMessaging fail-fasts, 0xC000027B, before the
    /// log is written), so it is logged and the app keeps running, as App.OnUnhandledException does for other UI exceptions.
    /// </summary>
    /// <param name="action">The UI work.</param>
    /// <param name="logger">Gets the exception.</param>
    /// <returns>The guarded callback.</returns>
    public static DispatcherQueueHandler Guarded(Action action, ILogger logger) => () =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in queued UI work");
        }
    };
}
