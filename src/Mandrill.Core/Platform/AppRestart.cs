using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Mandrill.Core.Platform;

/// <summary>Starts a new Mandrill process (the language picker's "Restart now", after this one saved its session).</summary>
public static class AppRestart
{
    /// <summary>Starts this executable again with the same environment. Never throws.</summary>
    /// <param name="logger">Logs the outcome.</param>
    /// <returns>True when the new process was started.</returns>
    public static bool StartNewInstance(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            logger.LogError("Restart failed: the executable path is unknown");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
            logger.LogInformation("Restart: started {Exe} (pid {Pid})", exe, process?.Id);
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogError(ex, "Restart failed: could not start {Exe}", exe);
            return false;
        }
    }
}
