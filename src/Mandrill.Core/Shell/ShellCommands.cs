using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mandrill.Core.Shell;

/// <summary>
/// "Open terminal here" (WP-3.1, AC-3.6) on the misc STA: Windows Terminal, cmd as the fallback. Properties runs on the
/// context menu's verbs STA (<see cref="IContextMenuService.ShowPropertiesAsync"/>), where a blocking sheet is allowed.
/// </summary>
public sealed class ShellCommands
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ShellSta _sta;
    private readonly ILogger _logger;

    /// <summary>Creates the commands on <paramref name="sta"/>.</summary>
    /// <param name="sta">The misc STA.</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public ShellCommands(ShellSta sta, ILoggerFactory? loggerFactory = null)
    {
        _sta = sta ?? throw new ArgumentNullException(nameof(sta));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ShellCommands>();
    }

    /// <summary>
    /// Opens Windows Terminal in <paramref name="folder"/> (AC-3.6), else cmd. The folder is the process's working folder, so
    /// spaces, non-ASCII names and a trailing backslash ("C:\") never go through command-line quoting.
    /// </summary>
    /// <param name="folder">The pane's folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when a terminal started.</returns>
    public async Task<bool> OpenTerminalAsync(string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            return await _sta.InvokeAsync(
                () =>
                {
                    if (Execute("wt.exe", "-d .", folder, out int wtError))
                    {
                        return true;
                    }

                    _logger.LogInformation("Windows Terminal did not start ({Error}): cmd fallback in {Folder}", wtError, folder);
                    if (Execute("cmd.exe", null, folder, out int cmdError))
                    {
                        return true;
                    }

                    _logger.LogError("Open terminal failed in {Folder}: {Error}", folder, cmdError);
                    return false;
                },
                Timeout,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Open terminal failed in {Folder}", folder);
            return false;
        }
    }

    /// <summary>
    /// Shows the shell's "Open with" dialog for <paramref name="path"/> (WP-3.3; also for a type with no associated app,
    /// replacing P1's error, §15 (d)). Modal to <paramref name="owner"/>; runs on its own short-lived STA without a hang limit.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="owner">Owner window (the main window).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the dialog ran (an app was chosen or the user cancelled); false when it could not be shown.</returns>
    public async Task<bool> OpenWithAsync(string path, nint owner, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var sta = new StaWorker("open-with", _logger);
            // QA m3: not the caller's token: a pane closing must not end the dialog under the user (its STA would quit).
            int hr = await sta.InvokeAsync(() => ShowOpenWith(path, owner), System.Threading.Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
            if (hr < 0 && hr != unchecked((int)0x8007_04C7))
            {
                _logger.LogError("Open with failed for {Path}: 0x{Hr:X8}", path, hr);
                return false;
            }

            _logger.LogInformation("Open with for {Path}: 0x{Hr:X8}", path, hr);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Open with failed for {Path}", path);
            return false;
        }
    }

    private static unsafe int ShowOpenWith(string path, nint owner)
    {
        fixed (char* file = path)
        {
            var info = new OPENASINFO
            {
                pcszFile = file,
                oaifInFlags = OPEN_AS_INFO_FLAGS.OAIF_ALLOW_REGISTRATION | OPEN_AS_INFO_FLAGS.OAIF_REGISTER_EXT | OPEN_AS_INFO_FLAGS.OAIF_EXEC,
            };
            return PInvoke.SHOpenWithDialog((Windows.Win32.Foundation.HWND)owner, &info).Value;
        }
    }

    private static unsafe bool Execute(string file, string? parameters, string directory, out int error)
    {
        fixed (char* f = file)
        fixed (char* p = parameters)
        fixed (char* d = directory)
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                fMask = PInvoke.SEE_MASK_NOASYNC | PInvoke.SEE_MASK_FLAG_NO_UI,
                lpFile = f,
                lpParameters = p,
                lpDirectory = d,
                nShow = (int)SHOW_WINDOW_CMD.SW_SHOWNORMAL,
            };
            bool ok = PInvoke.ShellExecuteEx(&info);
            error = ok ? 0 : Marshal.GetLastPInvokeError();
            return ok;
        }
    }
}
