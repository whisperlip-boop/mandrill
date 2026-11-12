using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Mandrill.Core.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mandrill.Core.Shell;

/// <summary>How a request for access to a folder ended (owner F14).</summary>
public enum FolderAccessResult
{
    /// <summary>The current user now has access; open the folder again.</summary>
    Granted,

    /// <summary>The user declined the UAC prompt; nothing changed.</summary>
    Cancelled,

    /// <summary>The grant could not be made (see the log).</summary>
    Failed,
}

/// <summary>
/// Explorer's "You don't currently have permission to access this folder → Continue" (owner F14): adds the current user
/// with Full control inherited by subfolders and files (for a system location, owner #10: read and execute on that folder
/// only) to a folder's ACL. Elevation is asked for only then, never at start ("no admin required" stays true).
/// </summary>
public interface IFolderAccess
{
    /// <summary>Grants the current user <see cref="FolderAccessGrant.RightsFor(string, string)"/> on <paramref name="folder"/> through an elevated icacls (UAC prompt); refused under a reparse point.</summary>
    /// <param name="folder">Normalized folder path on a local fixed drive.</param>
    /// <param name="ct">Cancellation token for the wait.</param>
    /// <returns>Granted, Cancelled (UAC declined) or Failed.</returns>
    Task<FolderAccessResult> GrantAsync(string folder, CancellationToken ct);
}

/// <summary>The icacls command line for <see cref="IFolderAccess"/> (pure, unit-tested).</summary>
public static partial class FolderAccessGrant
{
    /// <summary>
    /// <c>"folder" /grant *SID:(OI)(CI)F /C /Q</c>: Full control for the SID, inherited by files (OI) and subfolders (CI),
    /// like Explorer's Continue (<see cref="RightsFor(string, string)"/>: a system location gets <see cref="ReadAndExecute"/> instead). No /T: the inheritable entry reaches existing children through inheritance, while /T would
    /// rewrite every descendant explicitly (slow on big trees). The SID form avoids quoting and localized account names.
    /// </summary>
    /// <param name="folder">Normalized absolute folder path.</param>
    /// <param name="sid">String SID (S-1-...).</param>
    /// <param name="requested">The path the user asked for before <see cref="Canonicalize"/>; null when it is <paramref name="folder"/>.</param>
    /// <returns>The arguments.</returns>
    /// <exception cref="ArgumentException">The path cannot be quoted safely, or the SID is malformed.</exception>
    public static string BuildArguments(string folder, string sid, string? requested = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(sid);
        if (folder.Contains('"', StringComparison.Ordinal) || !PathUtil.TryNormalize(folder, out string normalized, out _))
        {
            throw new ArgumentException($"Not a folder path icacls can take: {folder}", nameof(folder));
        }

        if (normalized[3..].Split('\\').Any(s => s.EndsWith('.') || s.EndsWith(' ')))
        {
            // icacls takes a Win32 path, which drops a trailing dot or space: the grant would hit another folder.
            throw new ArgumentException($"A folder name ending in a dot or space cannot be granted safely: {folder}", nameof(folder));
        }

        if (PathUtil.IsDriveRoot(normalized))
        {
            // Defence in depth (R16): an inheritable entry on a root would be propagated over the whole drive.
            throw new ArgumentException($"A drive root never gets a grant: {normalized}", nameof(folder));
        }

        if (!SidPattern().IsMatch(sid))
        {
            throw new ArgumentException($"Not a SID: {sid}", nameof(sid));
        }

        // Belt and braces: a trailing '\' would escape the closing quote in the C runtime's argument parsing (roots are
        // refused above, so a normalized path never ends with one today).
        string quoted = normalized.EndsWith('\\') ? normalized + "\\" : normalized;
        return $"\"{quoted}\" /grant *{sid}:{RightsFor(normalized, requested ?? normalized)} /C /Q";
    }

    /// <summary>Rights for an ordinary folder: Full control, like Explorer's Continue.</summary>
    public const string FullControl = "(OI)(CI)F";

    /// <summary>
    /// Rights for a system location (owner #10, lead decision): read and list on that folder only, what entering it needs.
    /// Not inherited: the files inside (SAM, SECURITY) stay unreadable and each subfolder asks again.
    /// </summary>
    public const string ReadAndExecute = "RX";

    /// <summary>The rights a grant for <paramref name="folder"/> gives (logged with every grant).</summary>
    /// <param name="folder">Folder path.</param>
    /// <returns><see cref="ReadAndExecute"/> for a system location, else <see cref="FullControl"/>.</returns>
    public static string RightsFor(string folder) => RightsFor(folder, folder);

    /// <summary>
    /// The stricter rights of <paramref name="folder"/> and the path it was <paramref name="requested"/> as: a system
    /// location gets the read-only grant whichever form names it (C:\PROGRA~1\x), so its long form must not get more.
    /// </summary>
    /// <param name="folder">Canonical folder path.</param>
    /// <param name="requested">The path before <see cref="Canonicalize"/>.</param>
    /// <returns><see cref="ReadAndExecute"/> when either is a system location, else <see cref="FullControl"/>.</returns>
    public static string RightsFor(string folder, string requested)
        => IsSystemLocation(folder) || IsSystemLocation(requested) ? ReadAndExecute : FullControl;

    /// <summary>
    /// True when <paramref name="folder"/> or any parent is a reparse point (junction, symlink, mount point): icacls follows
    /// it, so the grant would land on the target, not the folder the user saw. An unreadable attribute counts as one
    /// (fail closed). Disk I/O.
    /// </summary>
    /// <param name="folder">Normalized absolute folder path.</param>
    /// <returns>True to refuse the grant.</returns>
    public static bool HasReparsePoint(string folder)
    {
        try
        {
            for (DirectoryInfo? dir = new(PathUtil.ToIo(folder)); dir is not null; dir = dir.Parent)
            {
                if ((File.GetAttributes(dir.FullName) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    // Folder names that mark system data on any fixed drive (architecture §14 R16).
    private static readonly HashSet<string> SystemFolderNames = new(PathUtil.Comparer)
    {
        "System Volume Information",
        "$Recycle.Bin",
        "Recovery",
        "Config.Msi",
    };

    private static readonly string? WindowsDirectory = NormalizedOrNull(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>
    /// True for system locations, which get a read-only grant (owner #10; a drive root gets no grant, see
    /// <see cref="BuildArguments"/>): a drive
    /// root, the %WINDIR% subtree, or a drive's top-level System Volume Information, $Recycle.Bin, Recovery or Config.Msi
    /// folder and everything below it. A user folder of one of those names deeper in the tree is allowed, and so are other
    /// users' profile folders (the owner's use case). A segment that looks like an 8.3 short name ("SYSTEM~1": '~' then a
    /// digit) counts as system too: it may alias a guarded folder. <see cref="Canonicalize"/> turns it into the long name
    /// first where the disk can be asked; a real long name of that shape is refused (rare, accepted).
    /// </summary>
    /// <param name="folder">Folder path.</param>
    /// <param name="windowsDirectory">%WINDIR%; null skips that rule.</param>
    /// <returns>True for a system location. Unparsable paths count as system.</returns>
    public static bool IsSystemLocation(string folder, string? windowsDirectory)
    {
        if (!PathUtil.TryNormalize(folder, out string path, out _) || PathUtil.IsDriveRoot(path))
        {
            return true;
        }

        if (NormalizedOrNull(windowsDirectory) is { } windows && PathUtil.IsSameOrUnder(path, windows))
        {
            return true;
        }

        string[] segments = path[3..].Split('\\');
        return SystemFolderNames.Contains(segments[0]) // the first folder under the root
            || segments.Any(s => ShortNamePattern().IsMatch(s));
    }

    /// <summary>
    /// The folder with every 8.3 short-name segment replaced by its long name, so the system-location check and icacls see
    /// the real folder (C:\SYSTEM~1 is System Volume Information). Paths without such a segment need no disk access.
    /// </summary>
    /// <param name="folder">Folder path.</param>
    /// <param name="longPath">Resolves a normalized path to its long form, or null (Win32: <see cref="ToLongPath"/>).</param>
    /// <returns>The normalized long path, or null when it cannot be resolved (then refuse).</returns>
    public static string? Canonicalize(string folder, Func<string, string?> longPath)
    {
        ArgumentNullException.ThrowIfNull(longPath);
        if (!PathUtil.TryNormalize(folder, out string path, out _))
        {
            return null;
        }

        if (!path.Split('\\').Any(s => ShortNamePattern().IsMatch(s)))
        {
            return path;
        }

        string? resolved = longPath(path);
        return resolved is not null && PathUtil.TryNormalize(resolved, out string full, out _) && !full.Split('\\').Any(s => ShortNamePattern().IsMatch(s))
            ? full
            : null;
    }

    /// <summary>GetLongPathNameW of a normalized path (needs list access to each parent only); null on failure. Disk I/O.</summary>
    /// <param name="path">Normalized path.</param>
    /// <returns>The long path, or null.</returns>
    public static string? ToLongPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Span<char> buffer = stackalloc char[32768];
        uint length = PInvoke.GetLongPathName(@"\\?\" + path, buffer);
        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        string result = buffer[..(int)length].ToString();
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }

    /// <summary><see cref="IsSystemLocation(string, string?)"/> with this machine's %WINDIR%.</summary>
    /// <param name="folder">Folder path.</param>
    /// <returns>True for a system location.</returns>
    public static bool IsSystemLocation(string folder) => IsSystemLocation(folder, WindowsDirectory);

    /// <summary>The current user's SID, or null when unavailable.</summary>
    /// <returns>The string SID.</returns>
    public static string? CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }

    private static string? NormalizedOrNull(string? path)
        => !string.IsNullOrEmpty(path) && PathUtil.TryNormalize(path, out string normalized, out _) ? normalized : null;

    // An 8.3 alias segment: '~' followed by a digit (SYSTEM~1, RECOVE~2).
    [GeneratedRegex(@"~[0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex ShortNamePattern();

    [GeneratedRegex(@"^S-1-[0-9]+(-[0-9]+)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();
}

/// <summary>
/// <see cref="IFolderAccess"/> through ShellExecuteExW "runas" of %SystemRoot%\System32\icacls.exe, with a hidden console,
/// then a wait for its exit code. Never throws for a failed grant: it is a value, logged with folder, SID, rights and exit
/// code (R16). ShellExecuteEx returns only after the UAC prompt is answered, so each grant runs on its own short-lived STA:
/// the shared misc STA (shell opens) is never blocked, even when the prompt is not on the secure desktop. There is no
/// consent timeout (UAC cancels on its own); only the caller's cancellation ends the wait.
/// </summary>
public sealed class ElevatedFolderAccess : IFolderAccess
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the user declined the UAC prompt

    // StaWorker needs a finite ceiling; a day stands for "until the user answers or the caller cancels".
    private static readonly TimeSpan NoConsentTimeout = TimeSpan.FromDays(1);

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public ElevatedFolderAccess(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ElevatedFolderAccess>();
    }

    /// <inheritdoc/>
    public async Task<FolderAccessResult> GrantAsync(string folder, CancellationToken ct)
    {
        string? sid = FolderAccessGrant.CurrentUserSid();

        // The long form first (a short-name alias like C:\SYSTEM~1 must meet the R16 guard as the folder it is), and the
        // long form is what icacls gets. Disk I/O, so off the caller's thread.
        string? canonical = await Task.Run(() => FolderAccessGrant.Canonicalize(folder, FolderAccessGrant.ToLongPath), ct).ConfigureAwait(false);
        if (canonical is null)
        {
            _logger.LogWarning("Access grant for {Folder} (SID {Sid}, {Rights}) refused: its long path name could not be resolved", folder, sid, FolderAccessGrant.RightsFor(folder));
            return FolderAccessResult.Failed;
        }

        string requested = folder;
        folder = canonical;
        string rights = FolderAccessGrant.RightsFor(folder, requested);
        // shortcut: check-then-act, a junction swapped in during the UAC prompt is followed; upgrade to a handle-based elevated helper if grants ever run unattended
        if (await Task.Run(() => FolderAccessGrant.HasReparsePoint(folder), ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Access grant for {Folder} (SID {Sid}, {Rights}) refused: it or a parent is a reparse point, or its attributes could not be read", folder, sid, rights);
            return FolderAccessResult.Failed;
        }

        string arguments;
        try
        {
            arguments = FolderAccessGrant.BuildArguments(folder, sid ?? string.Empty, requested);
        }
        catch (ArgumentException ex)
        {
            // A drive root (R16) or a path icacls cannot take: refused before any elevation.
            _logger.LogWarning("Access grant for {Folder} (SID {Sid}, {Rights}) refused: {Reason}", folder, sid, rights, ex.Message);
            return FolderAccessResult.Failed;
        }

        string icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");
        _logger.LogInformation("Access grant for {Folder} (SID {Sid}, {Rights}): icacls {Arguments}", folder, sid, rights, arguments);
        (bool started, int lastError, nint process) launch;
        try
        {
            using var sta = new ShellSta("grant", _loggerFactory);
            launch = await sta.InvokeAsync(() => StartElevated(icacls, arguments, folder, sid, rights, ct), NoConsentTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Access grant for {Folder}: the elevated start failed", folder);
            return FolderAccessResult.Failed;
        }

        if (!launch.started)
        {
            if (launch.lastError == ErrorCancelled)
            {
                _logger.LogInformation("Access grant for {Folder} (SID {Sid}, {Rights}): UAC prompt declined, error {Error}", folder, sid, rights, launch.lastError);
                return FolderAccessResult.Cancelled;
            }

            _logger.LogWarning("Access grant for {Folder} (SID {Sid}, {Rights}): ShellExecuteEx failed, error {Error}", folder, sid, rights, launch.lastError);
            return FolderAccessResult.Failed;
        }

        using var handle = new SafeProcessHandle(launch.process, ownsHandle: true);
        uint exitCode = await WaitForExitAsync(handle, ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            _logger.LogWarning("Access grant for {Folder} (SID {Sid}, {Rights}): icacls exited with {ExitCode}", folder, sid, rights, exitCode);
            return FolderAccessResult.Failed;
        }

        _logger.LogInformation("Access granted for {Folder} (SID {Sid}, {Rights}): icacls exited with {ExitCode}", folder, sid, rights, exitCode);
        return FolderAccessResult.Granted;
    }

    // STA: ShellExecuteExW returns once the user answered the consent prompt (NOASYNC) with the process handle. If the
    // caller gave up meanwhile, icacls may still have started (the user approved late): logged, and the handle closed here,
    // since nobody would close it otherwise.
    private unsafe (bool Started, int LastError, nint Process) StartElevated(string file, string arguments, string folder, string? sid, string rights, CancellationToken ct)
    {
        fixed (char* verb = "runas")
        fixed (char* path = file)
        fixed (char* args = arguments)
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                fMask = PInvoke.SEE_MASK_NOASYNC | PInvoke.SEE_MASK_FLAG_NO_UI | PInvoke.SEE_MASK_NOCLOSEPROCESS,
                lpVerb = verb,
                lpFile = path,
                lpParameters = args,
                nShow = (int)SHOW_WINDOW_CMD.SW_HIDE,
            };
            if (!PInvoke.ShellExecuteEx(&info))
            {
                return (false, Marshal.GetLastPInvokeError(), 0);
            }

            if (ct.IsCancellationRequested)
            {
                _logger.LogWarning("Access grant for {Folder} (SID {Sid}, {Rights}): approved after the caller stopped waiting; icacls runs unobserved", folder, sid, rights);
                PInvoke.CloseHandle(info.hProcess);
                return (false, ErrorCancelled, 0);
            }

            return (true, 0, (nint)info.hProcess.Value);
        }
    }

    private static async Task<uint> WaitForExitAsync(SafeProcessHandle process, CancellationToken ct)
    {
        using var exited = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false) };
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(exited, (_, _) => done.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await done.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }

        return PInvoke.GetExitCodeProcess(process, out uint code) ? code : uint.MaxValue;
    }
}
