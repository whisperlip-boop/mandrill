using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Mandrill.Core.Platform;

/// <summary>
/// Starts a new Mandrill process: the language picker's "Restart now" (after this one saved its session) or File &gt; New
/// window (architecture §18 D7). It inherits the environment, so it uses the same data folder.
/// </summary>
public static class AppRestart
{
    /// <summary>Starts this executable again with the same environment. Never throws.</summary>
    /// <param name="logger">Logs the outcome.</param>
    /// <param name="reason">Why, for the log ("restart", "new window"): the two are told apart there.</param>
    /// <returns>True when the new process was started.</returns>
    public static bool StartNewInstance(ILogger logger, string reason)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            logger.LogError("Starting a new instance failed ({Reason}): the executable path is unknown", reason);
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
            logger.LogInformation("Started a new instance {Exe} (pid {Pid}, {Reason})", exe, process?.Id, reason);
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogError(ex, "Starting a new instance failed ({Reason}): could not start {Exe}", reason, exe);
            return false;
        }
    }
}
