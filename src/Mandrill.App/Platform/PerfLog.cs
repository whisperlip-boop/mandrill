using System.ComponentModel;
using System.Diagnostics;
using Mandrill.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.App.Platform;

/// <summary>
/// The <c>Mandrill.Perf</c> logger for UI-side timings (first rows painted, startup, UI stalls), set once at startup.
/// tools/perf/Measure-Mandrill.ps1 reads these lines (architecture §10 Logging, requirements §6). UI thread.
/// </summary>
public static class PerfLog
{
    // Debug runs only: the startup marks are for measurements (WP-1.17), not for every user's log.
    private static readonly bool MarksEnabled = MandrillLogging.IsDebugEnabled;

    private static readonly Lazy<DateTime> ProcessStartUtc = new(ReadProcessStart);

    // UI thread only (like the rest of startup): bucket → total Stopwatch ticks and calls.
    private static readonly Dictionary<string, (long Ticks, int Calls)> Costs = new(StringComparer.Ordinal);

    /// <summary>The perf logger; a null logger until <see cref="Initialize"/>.</summary>
    public static ILogger Logger { get; private set; } = NullLogger.Instance;

    /// <summary>The last startup step marked (WP-1.17); <see cref="UiLagMonitor"/> tags its stall lines with it.</summary>
    public static string Phase { get; private set; } = "start";

    /// <summary>Sets the logger (App startup).</summary>
    /// <param name="logger">Logger for the <c>Mandrill.Perf</c> category.</param>
    public static void Initialize(ILogger logger) => Logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Records that a startup step is reached: sets <see cref="Phase"/> and, with MANDRILL_DEBUG=1, logs
    /// <c>Startup: {step} at {ms} ms after process start</c>.
    /// </summary>
    /// <param name="step">Short step name (log only, not shown to users).</param>
    /// <param name="atUtc">When the step was reached; null = now.</param>
    public static void Mark(string step, DateTime? atUtc = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(step);
        Phase = step;
        if (MarksEnabled)
        {
            Logger.LogInformation("Startup: {Step} at {Ms:F0} ms after process start", step, SinceProcessStart(atUtc ?? DateTime.UtcNow));
        }
    }

    /// <summary>Starts timing a startup cost bucket (WP-1.17); 0 when marks are off (debug runs only).</summary>
    /// <returns>A start stamp for <see cref="EndCost"/>.</returns>
    public static long BeginCost() => MarksEnabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Adds the time since <paramref name="start"/> to <paramref name="bucket"/>; nothing when it is 0.</summary>
    /// <param name="bucket">Bucket name (log only).</param>
    /// <param name="start">Stamp from <see cref="BeginCost"/>.</param>
    public static void EndCost(string bucket, long start)
    {
        if (start == 0)
        {
            return;
        }

        long elapsed = Stopwatch.GetTimestamp() - start;
        Costs[bucket] = Costs.TryGetValue(bucket, out var cost) ? (cost.Ticks + elapsed, cost.Calls + 1) : (elapsed, 1);
    }

    /// <summary>Logs every cost bucket ("Startup cost: …") and clears them; called once startup is painted.</summary>
    public static void LogCosts()
    {
        foreach (var (bucket, cost) in Costs.OrderByDescending(c => c.Value.Ticks))
        {
            Logger.LogInformation("Startup cost: {Bucket} {Ms:F1} ms in {Calls} call(s)", bucket, cost.Ticks * 1000.0 / Stopwatch.Frequency, cost.Calls);
        }

        Costs.Clear();
    }

    /// <summary>Milliseconds from process start to <paramref name="utc"/>; -1 when the start time is unknown.</summary>
    /// <param name="utc">A UTC time.</param>
    /// <returns>The milliseconds.</returns>
    public static double SinceProcessStart(DateTime utc)
        => ProcessStartUtc.Value == DateTime.MinValue ? -1 : (utc - ProcessStartUtc.Value).TotalMilliseconds;

    private static DateTime ReadProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return DateTime.MinValue; // perf data only: the marks then show -1
        }
    }
}
