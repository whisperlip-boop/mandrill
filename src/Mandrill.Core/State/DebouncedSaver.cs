using Microsoft.Extensions.Logging;

namespace Mandrill.Core.State;

/// <summary>
/// Saves the latest snapshot a fixed delay after the first unsaved change (architecture §7: session 2 s after a change).
/// Later changes replace the pending snapshot but do not postpone the save, so a kill loses at most the delay.
/// Call <see cref="FlushAsync"/> on exit. Thread-safe; saves never overlap.
/// </summary>
/// <typeparam name="T">Immutable snapshot type.</typeparam>
public sealed class DebouncedSaver<T> : IAsyncDisposable
    where T : class
{
    private readonly Func<T, CancellationToken, Task> _save;
    private readonly TimeSpan _delay;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    // Not disposed: it never allocates a wait handle, and a late timer callback may still enter it after DisposeAsync.
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private T? _pending;
    private ITimer? _timer;
    private bool _disposed;

    /// <summary>Creates a saver.</summary>
    /// <param name="save">Writes one snapshot (for example <see cref="StateStore{T}.SaveAsync"/>); runs on the thread pool.</param>
    /// <param name="delay">Delay after the first unsaved change.</param>
    /// <param name="timeProvider">Clock and timers (<see cref="TimeProvider.System"/>; a fake in tests).</param>
    /// <param name="logger">Logger for save failures.</param>
    public DebouncedSaver(Func<T, CancellationToken, Task> save, TimeSpan delay, TimeProvider timeProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _save = save;
        _delay = delay;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Whether a snapshot is waiting to be saved (including one kept after a failed save).</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>Records the latest state; starts the delay if no save is scheduled yet.</summary>
    /// <param name="snapshot">Immutable snapshot built on the caller's thread.</param>
    /// <exception cref="ObjectDisposedException">The saver has been disposed.</exception>
    public void Update(T snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending = snapshot;
            _timer ??= _timeProvider.CreateTimer(static s => ((DebouncedSaver<T>)s!).OnTimer(), this, _delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Saves the pending snapshot now and cancels the scheduled save (exit path).</summary>
    /// <param name="ct">Cancellation token (the exit time limit).</param>
    /// <returns>True when nothing was pending or the save succeeded; false when it failed (logged, snapshot kept).</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public Task<bool> FlushAsync(CancellationToken ct) => SavePendingAsync(ct);

    /// <summary>
    /// Stops the timer and waits (unbounded) for an in-flight save to finish, even after a cancelled flush.
    /// A pending snapshot is dropped (logged); flush first.
    /// </summary>
    /// <returns>A task that completes when no save is running.</returns>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            if (_pending is not null)
            {
                _logger.LogWarning("Unsaved {Type} snapshot dropped on dispose", typeof(T).Name);
                _pending = null;
            }
        }

        await _saveLock.WaitAsync().ConfigureAwait(false);
        _saveLock.Release();
    }

    private void OnTimer() => _ = SaveFromTimerAsync();

    private async Task SaveFromTimerAsync()
    {
        try
        {
            await SavePendingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A timer callback has no caller to observe the task; never let an exception escape unobserved.
            _logger.LogError(ex, "Scheduled save of {Type} failed", typeof(T).Name);
        }
    }

    private async Task<bool> SavePendingAsync(CancellationToken ct)
    {
        await _saveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Take the snapshot inside the save lock so the newest state is always written last.
            T? snapshot;
            lock (_gate)
            {
                snapshot = _pending;
                _pending = null;
                _timer?.Dispose();
                _timer = null;
            }

            if (snapshot is null)
            {
                return true;
            }

            try
            {
                await _save(snapshot, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                // Keep it for the next Update/Flush unless a newer one arrived. No timer restart: a broken disk must not loop.
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _pending ??= snapshot;
                    }
                }

                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

                _logger.LogError(ex, "Saving {Type} failed; retrying on the next change or flush", typeof(T).Name);
                return false;
            }
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
