using System.Runtime.InteropServices;
using Mandrill.Core.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mandrill.Core.Shell;

/// <summary>
/// Opens items through the shell on the "misc" STA (AC-1.26, AC-1.27, architecture §7 "Open"): a folder or a shortcut to
/// a folder becomes <see cref="OpenOutcome.NavigateTo"/>; anything else (including a shortcut to a file, which keeps its
/// arguments and working folder) goes to ShellExecuteExW with SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI, so no shell error
/// dialog appears. A type with no association fails with ERROR_NO_ASSOCIATION instead of opening Explorer's "Open with"
/// dialog (AC-1.27). Failures are logged and returned as <see cref="FsError"/> values.
/// </summary>
public sealed class ShellLauncher : IShellLauncher
{
    private const int ErrorTimeout = 1460;           // ERROR_TIMEOUT
    private const int ErrorNoAssociation = 1155;     // ERROR_NO_ASSOCIATION
    private const int ErrorCancelled = 1223;         // ERROR_CANCELLED (e.g. the user declined a UAC prompt)
    private const int ShellPathLimit = 260;          // MAX_PATH: the shell takes no \\?\ paths
    private const uint ResolveTimeoutMs = 1000;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly ShellSta _sta;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;

    /// <summary>Creates the launcher.</summary>
    /// <param name="sta">The "misc" STA.</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    /// <param name="timeout">Maximum time one open may block the STA (default 15 s) before the worker is replaced.</param>
    public ShellLauncher(ShellSta sta, ILoggerFactory? loggerFactory = null, TimeSpan? timeout = null)
    {
        _sta = sta ?? throw new ArgumentNullException(nameof(sta));
        _timeout = timeout ?? DefaultTimeout;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ShellLauncher>();
    }

    /// <inheritdoc/>
    public async Task<OpenResult> OpenAsync(string path, CancellationToken ct)
    {
        if (!PathUtil.TryNormalize(path, out string normalized, out var invalid))
        {
            // UNC paths come back as Unsupported, so nothing below ever sees a \\server path.
            return Fail(invalid ?? new FsError(FsErrorKind.InvalidPath, string.Empty, 123));
        }

        OpenResult result;
        try
        {
            result = await _sta.InvokeAsync(() => OpenOnSta(normalized), _timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Fail(new FsError(FsErrorKind.Other, normalized, ErrorTimeout));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Worker disposed/not started, or an unexpected shell exception: still a value, never a throw (§2).
            _logger.LogError(ex, "Open {Path} failed unexpectedly", normalized);
            return OpenResult.Failed(new FsError(FsErrorKind.Other, normalized, 0));
        }

        return result.Outcome == OpenOutcome.Failed && result.Error is not null ? Fail(result.Error) : result;
    }

    /// <inheritdoc/>
    public async Task<bool> OpenUrlAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length != 0)
        {
            throw new ArgumentException("Only an absolute https URL without user info can be opened.", nameof(url));
        }

        string text = url.AbsoluteUri;
        OpenResult result;
        try
        {
            result = await _sta.InvokeAsync(() => Execute(text, null, text), _timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            result = OpenResult.Failed(new FsError(FsErrorKind.Other, text, ErrorTimeout));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Open {Url} failed unexpectedly", text);
            return false;
        }

        if (result.Error is not null)
        {
            Fail(result.Error);
        }

        return result.Outcome == OpenOutcome.Launched;
    }

    /// <summary>Maps a ShellExecuteExW failure (GetLastError, or the SE_ERR_* value in hInstApp) to an error kind.</summary>
    /// <param name="win32Code">Last error; 0 when the shell set none.</param>
    /// <param name="seErr">SE_ERR_* from hInstApp (≤ 32), or 0.</param>
    /// <returns>Kind and the code to report.</returns>
    internal static (FsErrorKind Kind, int Code) MapExecuteError(int win32Code, int seErr)
    {
        int code = win32Code != 0 ? win32Code : seErr switch
        {
            2 => 2,                         // SE_ERR_FNF
            3 => 3,                         // SE_ERR_PNF
            5 => 5,                         // SE_ERR_ACCESSDENIED
            27 or 31 => ErrorNoAssociation, // SE_ERR_ASSOCINCOMPLETE, SE_ERR_NOASSOC
            32 => 1157,                     // SE_ERR_DLLNOTFOUND → ERROR_DLL_NOT_FOUND
            _ => 0,
        };
        return (FsErrors.KindOf(code), code);
    }

    /// <summary>
    /// Result for a failed ShellExecuteExW: the user cancelling (declined UAC prompt) is not an error and reads as
    /// <see cref="OpenOutcome.Launched"/> (nothing to report); everything else is <see cref="OpenOutcome.Failed"/>.
    /// </summary>
    /// <param name="win32Code">Last error; 0 when the shell set none.</param>
    /// <param name="seErr">SE_ERR_* from hInstApp (≤ 32), or 0.</param>
    /// <param name="path">The path that was opened.</param>
    /// <returns>The result.</returns>
    internal static OpenResult ExecuteFailed(int win32Code, int seErr, string path)
    {
        if (win32Code == ErrorCancelled)
        {
            return OpenResult.Launched;
        }

        var (kind, code) = MapExecuteError(win32Code, seErr);
        return OpenResult.Failed(new FsError(kind, path, code));
    }

    /// <summary>
    /// A path the shell accepts (shorter than MAX_PATH): long paths get an 8.3 directory part, but the file name is kept,
    /// so the extension (and with it the association) stays the real one.
    /// </summary>
    /// <param name="path">Normalized drive path (no UNC; <see cref="PathUtil.TryNormalize"/> rejects those).</param>
    /// <returns>The shell path, or null when it cannot be made short enough (8.3 names disabled).</returns>
    internal static string? ToShellPath(string path)
    {
        if (path.Length < ShellPathLimit)
        {
            return path;
        }

        string? directory = Path.GetDirectoryName(path);
        string? shortDirectory = directory is null ? null : ShortPath(directory);
        if (shortDirectory is null)
        {
            return null;
        }

        string candidate = PathUtil.Combine(shortDirectory, Path.GetFileName(path));
        return candidate.Length < ShellPathLimit ? candidate : null;
    }

    private OpenResult Fail(FsError error)
    {
        _logger.LogWarning("Open failed: {Kind} (Win32 {Code}) for {Path}", error.Kind, error.Win32Code, error.Path);
        return OpenResult.Failed(error);
    }

    // STA thread.
    private OpenResult OpenOnSta(string path)
    {
        string io = PathUtil.ToIo(path);
        if (Directory.Exists(io))
        {
            return OpenResult.NavigateTo(path);
        }

        if (!File.Exists(io))
        {
            return OpenResult.Failed(new FsError(FsErrorKind.NotFound, path, 2));
        }

        string? shellPath = ToShellPath(path);
        if (shellPath is null)
        {
            // 8.3 names disabled on this volume: "path too long for the Windows shell" (architecture §9).
            return OpenResult.Failed(new FsError(FsErrorKind.PathTooLong, path, 206));
        }

        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            && ResolveShortcut(shellPath) is { } target
            && PathUtil.TryNormalize(target, out string targetPath, out _)
            && Directory.Exists(PathUtil.ToIo(targetPath)))
        {
            return OpenResult.NavigateTo(targetPath);
        }

        if (!HasShellAssociation(path))
        {
            // Unassociated types would fall back to the "Unknown" class, whose default verb is the Open-with dialog.
            return OpenResult.Failed(new FsError(FsErrorKind.NoAssociation, path, ErrorNoAssociation));
        }

        return Execute(shellPath, Path.GetDirectoryName(shellPath), path);
    }

    // True when ShellExecute has a class for the type other than the "Unknown" fallback.
    private static bool HasShellAssociation(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Length <= 1)
        {
            return false;
        }

        var hr = PInvoke.AssocQueryKey(ASSOCF.ASSOCF_INIT_IGNOREUNKNOWN, ASSOCKEY.ASSOCKEY_SHELLEXECCLASS, extension, null, out var key);
        key.Dispose();
        return hr.Succeeded;
    }

    private static string? ShortPath(string path)
    {
        Span<char> buffer = stackalloc char[32768];
        uint length = PInvoke.GetShortPathName(@"\\?\" + path, buffer);
        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        string shortPath = buffer[..(int)length].ToString();
        if (shortPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + shortPath[8..];
        }

        return shortPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? shortPath[4..] : shortPath;
    }

    // Target path of a shortcut, or null (non-file target such as a Control Panel item, or an unreadable link).
    private unsafe string? ResolveShortcut(string lnkPath)
    {
        object? instance = null;
        try
        {
            instance = new ShellLink();
            var link = (IShellLinkW)instance;
            fixed (char* p = lnkPath)
            {
                ((IPersistFile)instance).Load(p, STGM.STGM_READ);
            }

            try
            {
                // NO_UI: the high word is the search timeout in ms; NOUPDATE: never rewrite the user's .lnk file.
                link.Resolve(default, (uint)(SLR_FLAGS.SLR_NO_UI | SLR_FLAGS.SLR_NOUPDATE) | (ResolveTimeoutMs << 16));
            }
            catch (COMException ex)
            {
                // The stored path is still usable; Execute reports a missing target.
                _logger.LogDebug("Shortcut {Path}: Resolve failed (0x{Hr:X8})", lnkPath, ex.HResult);
            }

            const int Capacity = 1024;
            char* target = stackalloc char[Capacity];
            target[0] = '\0';
            target[Capacity - 1] = '\0';
            // S_FALSE: the link has no file-system path (e.g. a Control Panel item). Read the buffer only on S_OK.
            var hr = link.GetPath(target, Capacity - 1, null, 0);
            if (hr.Value != 0)
            {
                return null;
            }

            string text = new(target);
            return text.Length > 0 ? text : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or FileNotFoundException)
        {
            _logger.LogWarning("Shortcut {Path} could not be read: {Error}", lnkPath, ex.Message);
            return null;
        }
        finally
        {
            if (instance is not null)
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
    }

    // STA thread. directory: working folder, or null (a URL has none).
    private unsafe OpenResult Execute(string shellPath, string? directory, string path)
    {
        fixed (char* file = shellPath)
        fixed (char* dir = directory)
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                fMask = PInvoke.SEE_MASK_NOASYNC | PInvoke.SEE_MASK_FLAG_NO_UI,
                lpFile = file,
                lpDirectory = dir,
                nShow = (int)SHOW_WINDOW_CMD.SW_SHOWNORMAL,
            };
            if (PInvoke.ShellExecuteEx(&info))
            {
                return OpenResult.Launched;
            }

            // The generated ShellExecuteExW import has SetLastError = true.
            int lastError = Marshal.GetLastPInvokeError();
            int seErr = (int)(nint)info.hInstApp.Value;
            if (lastError == ErrorCancelled)
            {
                _logger.LogInformation("Open {Path}: cancelled by the user", path);
            }

            return ExecuteFailed(lastError, seErr is > 0 and <= 32 ? seErr : 0, path);
        }
    }
}
