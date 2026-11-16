using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mandrill.Core.Diagnostics;

/// <summary>
/// Logging bootstrap: %APPDATA%\mandrill\logs\mandrill.log shared by every instance (5 MB × 3, <see cref="SharedFileLoggerProvider"/>),
/// UTC, pid and instance number, thread id, category.
/// </summary>
public static class MandrillLogging
{
    /// <summary>Category for timing measurements.</summary>
    public const string PerfCategory = "Mandrill.Perf";

    /// <summary>Maximum size of one log file in bytes.</summary>
    public const long FileSizeLimitBytes = 5_000_000;

    /// <summary>Number of rolled files kept.</summary>
    public const int MaxRollingFiles = 3;

    /// <summary>True when MANDRILL_DEBUG=1 (Debug level and diagnostics).</summary>
    public static bool IsDebugEnabled => Environment.GetEnvironmentVariable("MANDRILL_DEBUG") == "1";

    /// <summary>
    /// Debug-only: every context menu shows Mandrill's fallback menu (MANDRILL_DEBUG=1 and MANDRILL_FORCE_FALLBACK_MENU=1),
    /// so the owner can check it by hand (WP-3.1 QA); never on in normal use.
    /// </summary>
    public static bool ForceFallbackMenu => IsDebugEnabled && Environment.GetEnvironmentVariable("MANDRILL_FORCE_FALLBACK_MENU") == "1";

    /// <summary>Creates the logger factory. The log directory must exist (<see cref="AppPaths.EnsureCreated"/>).</summary>
    /// <param name="logFile">Path of the active log file.</param>
    /// <param name="debug">True to log at Debug level.</param>
    /// <param name="instance">This process's instance number (line tag; 0 = none).</param>
    /// <returns>A factory the caller disposes on shutdown (flushes the file).</returns>
    public static ILoggerFactory Create(string logFile, bool debug, int instance = 0)
    {
        var provider = new SharedFileLoggerProvider(logFile, instance);
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(debug ? LogLevel.Debug : LogLevel.Information);
            // Registered through a factory so the container disposes it with the logger factory (that flushes the queue).
            builder.Services.AddSingleton<ILoggerProvider>(_ => provider);
        });
    }

    /// <summary>Formats one entry: UTC timestamp, level, pid#instance, thread id, category, message, exception.</summary>
    internal static string Format(LogLevel level, string tag, string category, string message, Exception? exception)
    {
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {LevelText(level)} [{tag} {Environment.CurrentManagedThreadId}] {category}: {message}");
        return exception is null ? line : line + Environment.NewLine + exception;
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };
}
