using Mandrill.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Mandrill.Core.Watching;

/// <summary>
/// Polls the drive letters every 2 s (GetLogicalDrives/GetDriveType: no media access) and raises <see cref="Changed"/>
/// when the list differs (E-1.4). Thread-safe.
/// </summary>
public sealed class DriveMonitor : IDriveMonitor, IDisposable
{
    private readonly Func<IReadOnlyList<DriveDescriptor>> _source;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly ITimer _timer;
    private IReadOnlyList<DriveDescriptor> _current;
    private volatile bool _disposed;

    /// <summary>Creates a monitor over <see cref="Drives.GetDrives"/> with a 2 s interval and reads the list once.</summary>
    /// <param name="logger">Logger.</param>
    public DriveMonitor(ILogger<DriveMonitor> logger)
        : this(Drives.GetDrives, TimeSpan.FromSeconds(2), TimeProvider.System, logger)
    {
    }

    /// <summary>Creates a monitor with an explicit source and clock (tests).</summary>
    internal DriveMonitor(Func<IReadOnlyList<DriveDescriptor>> source, TimeSpan interval, TimeProvider timeProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _interval = interval;
        _logger = logger;
        _current = TryRead() ?? [];

        // One-shot, re-armed after each poll, so a slow poll never overlaps the next one.
        _timer = timeProvider.CreateTimer(static s => ((DriveMonitor)s!).Poll(), this, interval, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc/>
    public event Action<IReadOnlyList<DriveDescriptor>>? Changed;

    /// <inheritdoc/>
    public IReadOnlyList<DriveDescriptor> Current => Volatile.Read(ref _current);

    /// <summary>Stops polling. Does not wait: a poll already running may still raise <see cref="Changed"/> once.</summary>
    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<DriveDescriptor>? drives = TryRead();
        if (drives is not null && !drives.SequenceEqual(Current))
        {
            IReadOnlyList<DriveDescriptor> previous = Current;
            Volatile.Write(ref _current, drives);
            _logger.LogInformation(
                "Drives changed: added [{Added}], removed [{Removed}]",
                string.Join(", ", drives.Except(previous).Select(d => $"{d.Root} {d.Kind}")),
                string.Join(", ", previous.Except(drives).Select(d => $"{d.Root} {d.Kind}")));
            Raise(drives);
        }

        try
        {
            if (!_disposed)
            {
                _timer.Change(_interval, Timeout.InfiniteTimeSpan);
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check and the re-arm (shutdown); an exception here would crash the pool thread.
        }
    }

    private void Raise(IReadOnlyList<DriveDescriptor> drives)
    {
        foreach (Action<IReadOnlyList<DriveDescriptor>> handler in Changed?.GetInvocationList().Cast<Action<IReadOnlyList<DriveDescriptor>>>() ?? [])
        {
            try
            {
                handler(drives);
            }
            catch (Exception ex)
            {
                // One failing subscriber must not starve the others or stop polling.
                _logger.LogError(ex, "Drive change handler failed");
            }
        }
    }

    private IReadOnlyList<DriveDescriptor>? TryRead()
    {
        try
        {
            return _source();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the drive list failed");
            return null;
        }
    }
}
