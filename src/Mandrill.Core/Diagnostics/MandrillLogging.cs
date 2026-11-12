using System.Globalization;
using Microsoft.Extensions.Logging;
using NReco.Logging.File;

namespace Mandrill.Core.Diagnostics;

/// <summary>Logging bootstrap: rolling file %APPDATA%\mandrill\logs\mandrill.log (5 MB × 3), UTC, thread id, category.</summary>
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
    /// <returns>A factory the caller disposes on shutdown (flushes the file).</returns>
    public static ILoggerFactory Create(string logFile, bool debug)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(debug ? LogLevel.Debug : LogLevel.Information);
            builder.AddFile(logFile, options =>
            {
                options.Append = true;
                options.FileSizeLimitBytes = FileSizeLimitBytes;
                options.MaxRollingFiles = MaxRollingFiles;
                options.FormatLogEntry = Format;
            });
        });
    }

    /// <summary>Formats one entry: UTC timestamp, level, thread id, category, message, exception.</summary>
    /// <param name="message">The entry.</param>
    /// <returns>The line(s) to write.</returns>
    public static string Format(LogMessage message)
    {
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {LevelText(message.LogLevel)} [{Environment.CurrentManagedThreadId}] {message.LogName}: {message.Message}");
        return message.Exception is null ? line : line + Environment.NewLine + message.Exception;
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
