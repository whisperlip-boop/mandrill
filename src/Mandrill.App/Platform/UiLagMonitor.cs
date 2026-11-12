using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Mandrill.App.Platform;

/// <summary>
/// UI responsiveness monitor (requirements §6 "no UI-thread stall > 100 ms", architecture §10): a 50 ms timer on the UI
/// thread; a tick that arrives more than 100 ms late is a stall and is logged. A summary (count, longest) is logged on
/// <see cref="Dispose"/>. Started only with MANDRILL_DEBUG=1, so it costs nothing in normal use. Lateness is measured
/// between ticks, so a timer that Windows throttles (window minimized, power saving) also counts as a stall: read the
/// numbers from a foreground window.
/// </summary>
public sealed class UiLagMonitor : IDisposable
{
    /// <summary>Timer interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    /// <summary>Lateness above which a tick counts as a stall.</summary>
    public static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherQueueTimer _timer;
    private readonly ILogger _logger;
    private long _last;
    private TimeSpan _lastPause = GC.GetTotalPauseDuration();
    private int _lastGen0 = GC.CollectionCount(0);
    private int _lastGen1 = GC.CollectionCount(1);
    private int _lastGen2 = GC.CollectionCount(2);
    private string _lastPhase = PerfLog.Phase;
    private int _stalls;
    private double _longestMs;
    private bool _disposed;

    /// <summary>Starts the monitor on <paramref name="queue"/>'s thread.</summary>
    /// <param name="queue">The UI thread's queue.</param>
    /// <param name="logger">Perf logger.</param>
    public UiLagMonitor(DispatcherQueue queue, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timer = queue.CreateTimer();
        _timer.Interval = Interval;
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
        _last = Stopwatch.GetTimestamp();
        _timer.Start();
        _logger.LogInformation("UI lag monitor started (interval {Interval} ms, stall > {Threshold} ms)", Interval.TotalMilliseconds, StallThreshold.TotalMilliseconds);
    }

    /// <summary>Lateness of a tick in milliseconds: the gap since the previous tick minus the interval.</summary>
    /// <param name="gap">Time between two ticks.</param>
    /// <returns>The lateness (never negative).</returns>
    public static double Lateness(TimeSpan gap) => Math.Max(0, (gap - Interval).TotalMilliseconds);

    /// <summary>The startup steps a stall spans (WP-1.17): one name, or "first -> last" when a mark fell inside it.</summary>
    /// <param name="before">Phase at the previous tick.</param>
    /// <param name="after">Phase at the late tick.</param>
    /// <returns>The text for the stall line.</returns>
    public static string PhaseSpan(string before, string after)
        => string.Equals(before, after, StringComparison.Ordinal) ? before : $"{before} -> {after}";

    /// <summary>Stops the timer and logs the summary.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _logger.LogInformation("UI lag monitor: {Count} stall(s) > {Threshold} ms, longest {Longest:F0} ms", _stalls, StallThreshold.TotalMilliseconds, _longestMs);
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        double late = Lateness(Stopwatch.GetElapsedTime(_last, now));
        _last = now;

        // GC activity since the previous tick (WP-2.7): tells a GC pause apart from work on the UI thread.
        TimeSpan pause = GC.GetTotalPauseDuration();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        double pauseMs = (pause - _lastPause).TotalMilliseconds;
        int d0 = gen0 - _lastGen0, d1 = gen1 - _lastGen1, d2 = gen2 - _lastGen2;
        (_lastPause, _lastGen0, _lastGen1, _lastGen2) = (pause, gen0, gen1, gen2);
        string phase = PerfLog.Phase;
        string phases = PhaseSpan(_lastPhase, phase);
        _lastPhase = phase;
        if (late > StallThreshold.TotalMilliseconds)
        {
            _stalls++;
            _longestMs = Math.Max(_longestMs, late);
            // The phase text follows the closing parenthesis: Measure-Index.ps1 parses the line up to it.
            _logger.LogWarning("UI stall {Ms:F0} ms (GC pause {PauseMs:F0} ms, gen0 {Gen0} gen1 {Gen1} gen2 {Gen2}, latency mode {Mode}) in {Phases}", late, pauseMs, d0, d1, d2, System.Runtime.GCSettings.LatencyMode, phases);
        }
    }
}
