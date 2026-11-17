using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Mandrill.Core.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mandrill.Core.Shell;

/// <summary>
/// <see cref="IContextMenuService"/> (architecture §7/§9, S4). Build: SHParseDisplayName → IShellFolder →
/// GetUIObjectOf(IContextMenu) for items, CreateViewObject for the background → QueryContextMenu. Track: SetForegroundWindow +
/// TrackPopupMenuEx(TPM_RETURNCMD) on a hidden owner window whose WndProc forwards the owner-drawn and submenu messages to
/// IContextMenu3/2 and swallows every exception. Invoke: InvokeCommand with the main window as owner. Menus run on the "menu"
/// STA, verbs without a menu (Del, the fallback menu) on "menu-verbs", so a shell confirmation on one never blocks the other.
/// Each build, track and invoke is its own work item; sessions are keyed by request id, so interleaved requests never take
/// over each other's menu. COM objects are released on the STA as soon as they are no longer needed.
/// </summary>
public sealed class ContextMenuService : IContextMenuService, IDisposable
{
    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;
    private const uint FirstOwnCommand = 0x8000; // §7: Mandrill's items above the shell's range

    // CMIC_MASK_UNICODE / ASYNCOK are #define aliases of SEE_MASK_* that CsWin32 does not emit; TrackPopupMenuEx takes a uint.
    private const uint CmicMaskUnicode = 0x0000_4000;
    private const uint CmicMaskAsyncOk = 0x0010_0000;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const uint CancelMessage = PInvoke.WM_APP + 0x31;
    private const int EAbort = unchecked((int)0x8000_4004);
    private const int EFail = unchecked((int)0x8000_4005);
    private const int EInvalidArg = unchecked((int)0x8007_0057);
    private const int VkShift = 0x10;
    private const int ErrorCancelled = unchecked((int)0x8007_04C7);
    private const int CopyEngineUserCancelled = unchecked((int)0x8027_0000);
    private const string OwnerClass = "MandrillContextMenuOwner";

    // The background menu's New folder items: the New submenu's Folder, and Windows 11's top-level item (an Explorer command
    // added at WM_INITMENUPOPUP; outside Explorer it creates "New folder" in the PARENT of the shown folder).
    // shortcut: that GUID was read from the menu on Windows 10.0.26300; if a build changes it, that item still creates the
    // folder in the parent, as in 1.0.1. Upgrade: match the item by its command's canonical name on more builds.
    private static readonly string[] NewFolderVerbs = ["NewFolder", "{0001DEAD-9BF7-4CFA-8A5C-DE8679340002}"];

    // Windows Terminal's "Open in Terminal" (an Explorer command added at WM_INITMENUPOPUP): its canonical verb is its CLSID,
    // read from the menu on Windows 10.0.26300 and the same one the Files project filters on.
    private const string TerminalVerb = "{9F156763-7844-4DC4-B2B1-901F640F5155}";

    // An open menu, or a verb's own modal UI (WP-3.1 spike: Shift+"delete" blocks InvokeCommand until its confirmation
    // closes), stays as long as the user wants: only builds count against the hang limit (architecture §7).
    private static readonly TimeSpan UserLimit = TimeSpan.FromHours(1);
    private static readonly WNDPROC s_ownerProc = OwnerProc; // kept alive: the class holds only the function pointer
    private static readonly Lazy<bool> s_classRegistered = new(RegisterOwnerClass);

    // The WndProc is static (one class per process); the app has one service, tests several: the latest logger wins.
    private static ILogger s_logger = NullLogger.Instance;

    [ThreadStatic]
    private static Dictionary<long, MenuSession>? t_sessions;

    [ThreadStatic]
    private static MenuSession? t_tracking;

    [ThreadStatic]
    private static HWND t_owner;

    private readonly ShellSta _sta;
    private readonly ShellSta _verbSta;
    private readonly ILogger _logger;
    private readonly TimeSpan _buildLimit;
    private readonly TimeSpan _hangLimit;
    private readonly ConcurrentDictionary<long, byte> _abandoned = new();
    private long _nextId;
    private long _trackingId;
    private nint _ownerWindow;
    private int _abandonedBuilds;

    /// <summary>Creates the service and its STAs.</summary>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    /// <param name="buildLimit">How long the UI waits for a menu before the fallback (AC-3.3: 2 s).</param>
    /// <param name="hangLimit">How long one build may run before its thread is replaced (§7: 10 s).</param>
    public ContextMenuService(ILoggerFactory? loggerFactory = null, TimeSpan? buildLimit = null, TimeSpan? hangLimit = null)
    {
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<ContextMenuService>();
        s_logger = _logger;
        _sta = new ShellSta("menu", factory);
        _verbSta = new ShellSta("menu-verbs", factory);
        _sta.Replaced += () => Interlocked.Exchange(ref _ownerWindow, 0); // the hung thread's window is gone with it
        _buildLimit = buildLimit ?? TimeSpan.FromSeconds(2);
        _hangLimit = hangLimit ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Menus whose build finished after the UI had given up on them (discarded unseen; tests).</summary>
    internal int AbandonedBuilds => Volatile.Read(ref _abandonedBuilds);

    /// <summary>Hung menu threads replaced so far (tests).</summary>
    internal int Replacements => _sta.Replacements;

    /// <summary>Runs on the STA at the start of every build (tests: simulates a slow or hung shell extension).</summary>
    internal Action? BeforeBuild { get; set; }

    /// <inheritdoc/>
    public async Task<ContextMenuResult> ShowAsync(ContextMenuRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        long id = Interlocked.Increment(ref _nextId);
        var build = _sta.InvokeAsync(() => Build(id, request.Target, request.Owner, request.Extended, forMenu: true, request.Commands, request.CanRename, request.OwnTerminal), _hangLimit, ct);
        var limit = Task.Delay(_buildLimit, ct);
        if (await Task.WhenAny(build, limit).ConfigureAwait(false) != build)
        {
            // The UI shows the fallback now (AC-3.3) or gave up (ct): the build ends on its own and is dropped unseen.
            Abandon(id, build);
            ct.ThrowIfCancellationRequested();
            _logger.LogDebug("Context menu took longer than {Limit} ms to build (busy menu thread or slow extension): fallback menu", _buildLimit.TotalMilliseconds);
            return ContextMenuResult.Fallback(FallbackReason.Timeout);
        }

        int hr;
        try
        {
            hr = await build.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded or cancelled while building (reviewer R1): never rely on which cancelled task WhenAny saw first.
            Abandon(id, build);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Context menu build failed for {Folder}", request.Target.Folder);
            return ContextMenuResult.Fallback(ex is TimeoutException ? FallbackReason.Timeout : FallbackReason.BuildFailed, ex.HResult);
        }

        if (hr < 0)
        {
            _logger.LogWarning("Context menu build failed for {Folder}: 0x{Hr:X8}", request.Target.Folder, hr);
            return ContextMenuResult.Fallback(FallbackReason.BuildFailed, hr);
        }

        TrackResult tracked;
        try
        {
            tracked = await _sta.InvokeAsync(() => Track(id, request, ct), UserLimit, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DropLater(id); // Track never ran: its session is still stored
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Context menu tracking failed in {Folder}", request.Target.Folder);
            return ContextMenuResult.Fallback(FallbackReason.BuildFailed, ex.HResult);
        }

        if (tracked.Result is { } result)
        {
            return result;
        }

        try
        {
            return await _sta.InvokeAsync(() => InvokeChosen(id, tracked, request), UserLimit, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DropLater(id);
            throw;
        }
        catch (Exception ex) when (IsCancelledByUser(ex))
        {
            return ContextMenuResult.Dismissed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Context menu verb {Verb} failed", tracked.Verb);
            return new ContextMenuResult(ContextMenuOutcome.Failed, tracked.Verb, HResult: ex.HResult);
        }
    }

    /// <inheritdoc/>
    public async Task<ContextMenuResult> InvokeVerbAsync(ContextMenuTarget target, string verb, nint owner, bool shift, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        if (target.IsBackground)
        {
            throw new ArgumentException("A verb needs items", nameof(target));
        }

        long id = Interlocked.Increment(ref _nextId);
        try
        {
            int hr = await _verbSta.InvokeAsync(() => Build(id, target, owner, extended: shift, forMenu: false), _hangLimit, ct).ConfigureAwait(false);
            if (hr < 0)
            {
                _logger.LogWarning("Shell verb {Verb}: menu build failed in {Folder}: 0x{Hr:X8}", verb, target.Folder, hr);
                return new ContextMenuResult(ContextMenuOutcome.Failed, verb, HResult: hr);
            }

            return await _verbSta.InvokeAsync(
                () =>
                {
                    if (TakeSession(id) is not { } session)
                    {
                        _logger.LogWarning("Shell verb {Verb}: session {Id} is gone", verb, id);
                        return new ContextMenuResult(ContextMenuOutcome.Failed, verb);
                    }

                    try
                    {
                        Invoke(session, verb, null, owner, default, shift, target.Folder);
                        _logger.LogInformation("Shell verb {Verb} on {Count} item(s) in {Folder}", verb, target.Names.Count, target.Folder);
                        return new ContextMenuResult(ContextMenuOutcome.Invoked, verb);
                    }
                    finally
                    {
                        Release(session);
                    }
                },
                UserLimit,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCancelledByUser(ex))
        {
            return ContextMenuResult.Dismissed; // "No" in the shell's own confirmation
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Shell verb {Verb} failed in {Folder}", verb, target.Folder);
            return new ContextMenuResult(ContextMenuOutcome.Failed, verb, HResult: ex.HResult);
        }
    }

    /// <inheritdoc/>
    public void Cancel()
    {
        // Request-scoped: only the menu open right now closes, never one opened after this call.
        long tracking = Volatile.Read(ref _trackingId);
        nint owner = Volatile.Read(ref _ownerWindow);
        if (tracking != 0 && owner != 0)
        {
            PInvoke.PostMessage((HWND)owner, CancelMessage, (WPARAM)(nuint)tracking, default);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ShowPropertiesAsync(string path, nint owner, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            // ShellExecuteEx "properties" may block until the sheet closes: a short-lived STA per call (like the grant STA),
            // so an open sheet never queues Del or another verb behind it (lead, reviewer R2).
            using var sta = new StaWorker("menu-properties", _logger);
            return await sta.InvokeAsync(() => ExecuteProperties(path, owner), UserLimit, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Properties failed for {Path}", path);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task PrewarmAsync(IReadOnlyList<ContextMenuTarget> targets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        foreach (var target in targets)
        {
            long id = Interlocked.Increment(ref _nextId);
            try
            {
                int hr = await _sta.InvokeAsync(
                    () =>
                    {
                        int built = Build(id, target, 0, extended: false, forMenu: true);
                        Release(TakeSession(id));
                        return built;
                    },
                    _hangLimit,
                    ct).ConfigureAwait(false);
                _logger.LogDebug("Context menu pre-warm for {Folder}: 0x{Hr:X8}", target.Folder, hr);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Context menu pre-warm failed for {Folder}", target.Folder);
            }
        }
    }

    /// <summary>Closes an open native menu and stops both STAs.</summary>
    public void Dispose()
    {
        Cancel();
        Interlocked.Exchange(ref _ownerWindow, 0);
        _sta.Dispose();
        _verbSta.Dispose();
    }

    /// <summary>The texts of the top-level items of a target's menu (mnemonics removed), without showing it (tests).</summary>
    /// <param name="target">Items or background.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The texts.</returns>
    internal Task<IReadOnlyList<string>> TopLevelTextsAsync(ContextMenuTarget target, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _nextId);
        return _sta.InvokeAsync<IReadOnlyList<string>>(
            () =>
            {
                Marshal.ThrowExceptionForHR(Build(id, target, 0, extended: false, forMenu: true));
                var session = TakeSession(id)!;
                try
                {
                    return [.. MenuTexts(session.Menu)];
                }
                finally
                {
                    Release(session);
                }
            },
            _hangLimit,
            ct);
    }

    /// <summary>The ids of the top-level items of a target's menu with Mandrill's own items, without showing it (tests).</summary>
    /// <param name="target">Items or background.</param>
    /// <param name="commands">Mandrill's own items.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The command ids in menu order (separators and submenus left out).</returns>
    internal Task<IReadOnlyList<uint>> CommandIdsAsync(ContextMenuTarget target, IReadOnlyList<string> commands, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _nextId);
        return _sta.InvokeAsync<IReadOnlyList<uint>>(
            () =>
            {
                Marshal.ThrowExceptionForHR(Build(id, target, 0, extended: false, forMenu: true, commands));
                var session = TakeSession(id)!;
                try
                {
                    var ids = new List<uint>();
                    int count = PInvoke.GetMenuItemCount(session.Menu);
                    for (int i = 0; i < count; i++)
                    {
                        uint command = PInvoke.GetMenuItemID(session.Menu, i);
                        if (command is not (0 or uint.MaxValue))
                        {
                            ids.Add(command);
                        }
                    }

                    return ids;
                }
                finally
                {
                    Release(session);
                }
            },
            _hangLimit,
            ct);
    }

    /// <summary>The canonical verbs of the top-level items of a target's menu, without showing it (tests).</summary>
    /// <param name="target">Items or background.</param>
    /// <param name="extended">Include extended verbs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verbs; items without a canonical verb are left out.</returns>
    internal Task<IReadOnlyList<string>> VerbsAsync(ContextMenuTarget target, bool extended, CancellationToken ct) => VerbsAsync(target, extended, canRename: true, ct);

    /// <summary>The canonical verbs of a target's menu as built for a caller with or without an inline editor (tests).</summary>
    /// <param name="target">Items or background.</param>
    /// <param name="extended">Include extended verbs.</param>
    /// <param name="canRename">CMF_CANRENAME (false for search results).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verbs; items without a canonical verb are left out.</returns>
    internal Task<IReadOnlyList<string>> VerbsAsync(ContextMenuTarget target, bool extended, bool canRename, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _nextId);
        return _sta.InvokeAsync<IReadOnlyList<string>>(
            () =>
            {
                Marshal.ThrowExceptionForHR(Build(id, target, 0, extended, forMenu: true, canRename: canRename));
                var session = TakeSession(id)!;
                try
                {
                    var verbs = new List<string>();
                    int count = PInvoke.GetMenuItemCount(session.Menu);
                    for (int i = 0; i < count; i++)
                    {
                        uint command = PInvoke.GetMenuItemID(session.Menu, i);
                        if (command is >= FirstCommand and <= LastCommand && VerbOf(session.Handler, command - FirstCommand) is { } verb)
                        {
                            verbs.Add(verb);
                        }
                    }

                    return verbs;
                }
                finally
                {
                    Release(session);
                }
            },
            _hangLimit,
            ct);
    }

    private static LRESULT OwnerProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        // S4: a throw out of a WndProc kills the process; shell extensions return E_NOTIMPL/E_FAIL here routinely.
        try
        {
            if (msg == CancelMessage)
            {
                if (t_tracking is { } open && (long)(nuint)wParam.Value == open.Id)
                {
                    PInvoke.EndMenu();
                }

                return default;
            }

            if (msg is PInvoke.WM_INITMENUPOPUP or PInvoke.WM_DRAWITEM or PInvoke.WM_MEASUREITEM or PInvoke.WM_MENUCHAR && t_tracking is { } session)
            {
                try
                {
                    unsafe
                    {
                        if (session.Handler is IContextMenu3 menu3)
                        {
                            LRESULT result = default;
                            menu3.HandleMenuMsg2(msg, wParam, lParam, &result);
                            return result;
                        }
                    }

                    if (session.Handler is IContextMenu2 menu2)
                    {
                        menu2.HandleMenuMsg(msg, wParam, lParam);
                        return default;
                    }
                }
                finally
                {
                    // Also when the handler throws (a WM_INITMENUPOPUP failure is routinely logged below on Windows 11).
                    if (msg == PInvoke.WM_INITMENUPOPUP && (nint)wParam.Value == (nint)session.Menu)
                    {
                        if (session.OwnTerminal)
                        {
                            RemoveShellDuplicates(session.Menu, offset => VerbOf(session.Handler, offset));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            s_logger.LogDebug(ex, "Context menu message 0x{Msg:X} not handled by the shell extension", msg);
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static unsafe bool RegisterOwnerClass()
    {
        fixed (char* className = OwnerClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = s_ownerProc,
                hInstance = PInvoke.GetModuleHandle((char*)null),
                lpszClassName = className,
            };
            return PInvoke.RegisterClassEx(in wc) != 0;
        }
    }

    private static bool IsCancelledByUser(Exception ex) => ex.HResult is ErrorCancelled or CopyEngineUserCancelled;

    // Best effort: many third-party items have no canonical verb, and the marshaler maps their HRESULTs to many exception types.
    private static string? VerbOf(IContextMenu handler, uint offset)
    {
        unsafe
        {
            char* buffer = stackalloc char[256];
            try
            {
                handler.GetCommandString(offset, PInvoke.GCS_VERBW, null, (PSTR)(byte*)buffer, 256);
                string verb = new(buffer);
                return verb.Length == 0 ? null : verb;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static MenuSession? TakeSession(long id)
        => t_sessions is { } sessions && sessions.Remove(id, out var session) ? session : null;

    // Destroys the menu and releases the handler on this (its) STA thread.
    private static void Release(MenuSession? session)
    {
        if (session is null)
        {
            return;
        }

        PInvoke.DestroyMenu(session.Menu);
        ReleaseCom(session.Handler);
    }

    private static void ReleaseCom(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.FinalReleaseComObject(com);
        }
    }

    private static unsafe void Invoke(MenuSession session, string? verb, uint? offset, nint owner, System.Drawing.Point screen, bool shift, string folder)
    {
        byte[] ansiVerb = Encoding.ASCII.GetBytes((verb ?? string.Empty) + "\0");
        fixed (byte* verbA = ansiVerb)
        fixed (char* verbW = verb)
        // The Desktop root and This PC are no directories (architect's advisory): verbs get no working directory there.
        fixed (char* directory = folder == ContextMenuTarget.DesktopRoot || PathUtil.IsThisPc(folder) ? null : folder)
        {
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = (uint)sizeof(CMINVOKECOMMANDINFOEX),
                fMask = CmicMaskUnicode | CmicMaskAsyncOk | (offset is null ? 0 : PInvoke.CMIC_MASK_PTINVOKE) | (shift ? PInvoke.CMIC_MASK_SHIFT_DOWN : 0),
                hwnd = (HWND)owner,
                lpVerb = offset is { } o ? (PCSTR)(byte*)(nuint)o : (PCSTR)verbA,
                lpVerbW = offset is { } w ? (PCWSTR)(char*)(nuint)w : (PCWSTR)verbW,
                lpDirectoryW = directory,
                nShow = (int)SHOW_WINDOW_CMD.SW_SHOWNORMAL,
                ptInvoke = screen,
            };
            session.Handler.InvokeCommand((CMINVOKECOMMANDINFO*)&info);
        }
    }

    private unsafe bool ExecuteProperties(string path, nint owner)
    {
        fixed (char* file = path)
        fixed (char* verb = "properties")
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                fMask = PInvoke.SEE_MASK_INVOKEIDLIST | PInvoke.SEE_MASK_NOASYNC | PInvoke.SEE_MASK_FLAG_NO_UI,
                hwnd = (HWND)owner,
                lpVerb = verb,
                lpFile = file,
                nShow = (int)SHOW_WINDOW_CMD.SW_SHOWNORMAL,
            };
            if (PInvoke.ShellExecuteEx(&info))
            {
                return true;
            }

            _logger.LogError("Properties failed for {Path}: {Error}", path, Marshal.GetLastPInvokeError());
            return false;
        }
    }

    // Drops a stored session whose next step will not run (its request was cancelled between work items).
    private void DropLater(long id)
        => _ = _sta.InvokeAsync(
            () =>
            {
                Release(TakeSession(id));
                return true;
            },
            _hangLimit,
            CancellationToken.None).ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    // A build that never completes (hung, its thread abandoned) keeps its 8-byte _abandoned entry: bounded by hung builds.
    private void Abandon(long id, Task<int> build)
    {
        _abandoned[id] = 0;
        _ = build.ContinueWith(
            finished =>
            {
                // A build that finished just before it was abandoned stored its session: drop it on the STA.
                _ = _sta.InvokeAsync(
                    () =>
                    {
                        if (TakeSession(id) is { } late)
                        {
                            Release(late);
                            Interlocked.Increment(ref _abandonedBuilds);
                        }

                        _abandoned.TryRemove(id, out _);
                        return true;
                    },
                    _hangLimit,
                    CancellationToken.None).ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    // The hidden top-level owner of this STA thread (a replaced thread gets its own; the OS destroys it with the thread).
    private unsafe HWND EnsureOwner()
    {
        if (!t_owner.IsNull)
        {
            Interlocked.Exchange(ref _ownerWindow, (nint)t_owner);
            return t_owner;
        }

        if (!s_classRegistered.Value)
        {
            throw new InvalidOperationException("The context menu owner class could not be registered");
        }

        fixed (char* className = OwnerClass)
        {
            t_owner = PInvoke.CreateWindowEx(0, className, default, WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0, default, default, PInvoke.GetModuleHandle((char*)null), null);
        }

        if (t_owner.IsNull)
        {
            throw new InvalidOperationException("The context menu owner window could not be created");
        }

        Interlocked.Exchange(ref _ownerWindow, (nint)t_owner);
        return t_owner;
    }

    // Builds the target's menu into this thread's session table under `id`. Returns S_OK or the failing HRESULT.
    private unsafe int Build(long id, ContextMenuTarget target, nint owner, bool extended, bool forMenu, IReadOnlyList<string>? commands = null, bool canRename = true, bool ownTerminal = false)
    {
        BeforeBuild?.Invoke();

        // QA m3 / §7: items of one folder only; a sub-path or an empty name is "mixed parents" -> the fallback menu. Under
        // This PC (ADR-0015) the items are drive roots, which This PC's folder parses ("C:\\").
        bool drives = PathUtil.IsThisPc(target.Folder);
        bool root = target.Folder == ContextMenuTarget.DesktopRoot; // WP6b: only This PC itself, nothing else, no background
        if (root
            ? target.Names.Count != 1 || !PathUtil.IsThisPc(target.Names[0])
            : target.Names.Any(n => n.Length == 0 || (drives ? !PathUtil.IsDriveRoot(n) : n.Contains('\\') || n.Contains('/'))))
        {
            return EInvalidArg;
        }

        HWND hwnd = forMenu ? EnsureOwner() : (HWND)owner;
        ITEMIDLIST* folderPidl = null;
        var children = new List<nint>();
        object? folderObject = null;
        IContextMenu? handler = null;
        HMENU menu = default;
        bool stored = false;
        try
        {
            Guid folderIid = typeof(IShellFolder).GUID;
            Guid menuIid = typeof(IContextMenu).GUID;
            HRESULT hr;
            if (root)
            {
                hr = PInvoke.SHGetDesktopFolder(out IShellFolder desktop);
                if (hr.Failed)
                {
                    return hr.Value;
                }

                folderObject = desktop;
            }
            else
            {
                hr = PInvoke.SHParseDisplayName(target.Folder, null, out folderPidl, 0, out _);
                if (hr.Failed)
                {
                    return hr.Value;
                }

                hr = PInvoke.SHBindToObject(null, folderPidl, null, &folderIid, out folderObject);
                if (hr.Failed)
                {
                    return hr.Value;
                }
            }

            var folder = (IShellFolder)folderObject;
            object handlerObject;
            if (target.IsBackground)
            {
                folder.CreateViewObject(hwnd, &menuIid, out void* view);
                handlerObject = Marshal.GetObjectForIUnknown((nint)view);
                Marshal.Release((nint)view);
            }
            else
            {
                foreach (string name in target.Names)
                {
                    ITEMIDLIST* child;
                    uint attributes = 0;
                    fixed (char* text = name)
                    {
                        folder.ParseDisplayName(hwnd, null, text, null, &child, ref attributes);
                    }

                    children.Add((nint)child);
                }

                var array = stackalloc ITEMIDLIST*[children.Count];
                for (int i = 0; i < children.Count; i++)
                {
                    array[i] = (ITEMIDLIST*)children[i];
                }

                folder.GetUIObjectOf(hwnd, (uint)children.Count, array, &menuIid, null, out handlerObject);
            }

            handler = (IContextMenu)handlerObject;
            menu = PInvoke.CreatePopupMenu();
            uint flags = PInvoke.CMF_NORMAL | PInvoke.CMF_EXPLORE | (forMenu && canRename ? PInvoke.CMF_CANRENAME : 0) | (extended ? PInvoke.CMF_EXTENDEDVERBS : 0);
            hr = handler.QueryContextMenu(menu, 0, FirstCommand, LastCommand, flags);
            if (hr.Failed)
            {
                return hr.Value;
            }

            if (commands is { Count: > 0 })
            {
                // §7: Mandrill's own items at the end, after a separator, above the shell's id range (id = 0x8000 + index).
                // QA N3: the shell's Open in Terminal goes when the menu opens (RemoveShellDuplicates). A failed append
                // gives the fallback menu instead of a partial list.
                if (!PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null))
                {
                    return EFail;
                }

                for (int i = 0; i < commands.Count; i++)
                {
                    fixed (char* text = commands[i])
                    {
                        if (!PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, FirstOwnCommand + (uint)i, text))
                        {
                            return EFail;
                        }
                    }
                }
            }

            if (_abandoned.ContainsKey(id))
            {
                // The UI already showed its fallback for this request (AC-3.3): drop the late menu unseen.
                Interlocked.Increment(ref _abandonedBuilds);
                return EAbort;
            }

            (t_sessions ??= []).Add(id, new MenuSession(id, handler, menu, ownTerminal));
            stored = true;
            return 0;
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
            // Intentionally every exception (all carry a failing HResult): any shell or extension failure means "no menu".
            return ex.HResult; // a missing item is FileNotFoundException, a denied one UnauthorizedAccessException, ...
        }
        finally
        {
            if (!stored)
            {
                if (!menu.IsNull)
                {
                    PInvoke.DestroyMenu(menu);
                }

                ReleaseCom(handler);
            }

            if (!ReferenceEquals(folderObject, handler))
            {
                ReleaseCom(folderObject); // not needed once the handler exists (a namespace may hand back the same object)
            }
            foreach (nint child in children)
            {
                PInvoke.ILFree((ITEMIDLIST*)child);
            }

            if (folderPidl is not null)
            {
                PInvoke.ILFree(folderPidl);
            }
        }
    }

    // A menu text without its accelerator, its CJK-style access key "(&T)" and its mnemonic "&", for comparing items.
    private static string Plain(string text)
    {
        int tab = text.IndexOf('\t', StringComparison.Ordinal);
        string label = tab >= 0 ? text[..tab] : text;
        int key = label.IndexOf("(&", StringComparison.Ordinal);
        if (key >= 0 && key + 3 < label.Length && label[key + 3] == ')')
        {
            label = label.Remove(key, 4);
        }

        return label.Replace("&", string.Empty, StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// QA N3 (lead): removes the shell's "Open in Terminal" (by its canonical verb, so in any display language) when Mandrill
    /// appended its own, so the command is in the menu once and ours, the known path, stays. Runs when the menu opens, after
    /// the shell added its lazy Explorer-command items (it arrives at WM_INITMENUPOPUP, not at QueryContextMenu).
    /// </summary>
    /// <param name="menu">The top-level menu.</param>
    /// <param name="verbOf">The canonical verb of a shell item by its offset from the first command id, or null.</param>
    internal static void RemoveShellDuplicates(HMENU menu, Func<uint, string?> verbOf)
    {
        // shortcut: separators around a removed item stay; trim them if a menu ever shows a double line.
        for (int i = PInvoke.GetMenuItemCount(menu) - 1; i >= 0; i--)
        {
            uint id = PInvoke.GetMenuItemID(menu, i);
            if (id is >= FirstCommand and <= LastCommand && string.Equals(verbOf(id - FirstCommand), TerminalVerb, StringComparison.OrdinalIgnoreCase))
            {
                PInvoke.DeleteMenu(menu, (uint)i, MENU_ITEM_FLAGS.MF_BYPOSITION);
            }
        }
    }

    private static unsafe HashSet<string> MenuTexts(HMENU menu)
    {
        var texts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        char* buffer = stackalloc char[256];
        int count = PInvoke.GetMenuItemCount(menu);
        for (int i = 0; i < count; i++)
        {
            int length = PInvoke.GetMenuString(menu, (uint)i, buffer, 256, MENU_ITEM_FLAGS.MF_BYPOSITION);
            if (length > 0)
            {
                texts.Add(Plain(new string(buffer, 0, length)));
            }
        }

        return texts;
    }

    private unsafe TrackResult Track(long id, ContextMenuRequest request, CancellationToken ct)
    {
        if (TakeSession(id) is not { } session)
        {
            _logger.LogWarning("Context menu session {Id} is gone before tracking", id);
            return new TrackResult(0, null, ContextMenuResult.Fallback(FallbackReason.BuildFailed));
        }

        bool keep = false;
        try
        {
            HWND owner = EnsureOwner();
            var point = new System.Drawing.Point(request.ClientX, request.ClientY);
            PInvoke.ClientToScreen((HWND)request.Owner, ref point);

            // S4: without the foreground the menu ignores the keyboard and outside clicks; a real right-click guarantees it.
            if (!PInvoke.SetForegroundWindow(owner))
            {
                _logger.LogInformation("Context menu could not take the foreground: fallback menu");
                return new TrackResult(0, null, ContextMenuResult.Fallback(FallbackReason.NotForeground));
            }

            t_tracking = session;
            Interlocked.Exchange(ref _trackingId, id);
            uint command;
            bool shift;
            try
            {
                // QA N4: a cancel that arrived after the work item started but before Cancel() could see the menu.
                if (ct.IsCancellationRequested)
                {
                    return new TrackResult(0, null, ContextMenuResult.Dismissed);
                }

                command = (uint)PInvoke.TrackPopupMenuEx(session.Menu, TpmReturnCmd | TpmRightButton, point.X, point.Y, owner, null).Value;

                // QA M1: Shift as held when the item was chosen (Explorer), not when the menu opened.
                shift = PInvoke.GetKeyState(VkShift) < 0;
            }
            finally
            {
                Interlocked.CompareExchange(ref _trackingId, 0, id);
                t_tracking = null;
            }

            PInvoke.PostMessage(owner, PInvoke.WM_NULL, default, default); // documented: lets the menu close cleanly
            if (request.Owner != 0)
            {
                PInvoke.SetForegroundWindow((HWND)request.Owner); // QA N2: the keyboard goes back to the main window
            }

            if (command >= FirstOwnCommand && command - FirstOwnCommand < (uint)(request.Commands?.Count ?? 0))
            {
                return new TrackResult(command, null, new ContextMenuResult(ContextMenuOutcome.Command, Command: (int)(command - FirstOwnCommand)));
            }

            if (command is < FirstCommand or > LastCommand)
            {
                return new TrackResult(0, null, ContextMenuResult.Dismissed);
            }

            string? verb = VerbOf(session.Handler, command - FirstCommand);
            if (string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
            {
                return new TrackResult(command, verb, new ContextMenuResult(ContextMenuOutcome.Rename, verb));
            }

            if (request.Target.IsBackground && request.CanRename && NewFolderVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
            {
                return new TrackResult(command, verb, new ContextMenuResult(ContextMenuOutcome.NewFolder, verb));
            }

            if (request.Target.Names.Count == 1 && verb is not null
                && (verb.Equals("open", StringComparison.OrdinalIgnoreCase) || verb.Equals("explore", StringComparison.OrdinalIgnoreCase)))
            {
                return new TrackResult(command, verb, new ContextMenuResult(ContextMenuOutcome.Open, verb));
            }

            (t_sessions ??= []).Add(id, session); // the chosen verb runs in the next work item
            keep = true;
            return new TrackResult(command, verb, null) { Shift = shift };
        }
        finally
        {
            if (!keep)
            {
                Release(session);
            }
        }
    }

    private ContextMenuResult InvokeChosen(long id, TrackResult tracked, ContextMenuRequest request)
    {
        uint command = tracked.Command;
        string? verb = tracked.Verb;
        if (TakeSession(id) is not { } session)
        {
            _logger.LogWarning("Context menu session {Id} is gone before its verb", id);
            return new ContextMenuResult(ContextMenuOutcome.Failed, verb);
        }

        try
        {
            var point = new System.Drawing.Point(request.ClientX, request.ClientY);
            PInvoke.ClientToScreen((HWND)request.Owner, ref point);
            Invoke(session, verb, command - FirstCommand, request.Owner, point, tracked.Shift, request.Target.Folder);
            _logger.LogInformation("Context menu verb {Verb} in {Folder}", verb ?? $"#{command}", request.Target.Folder);
            return new ContextMenuResult(ContextMenuOutcome.Invoked, verb);
        }
        finally
        {
            Release(session);
        }
    }

    // OwnTerminal: Mandrill appended its own Open in Terminal, so RemoveShellDuplicates drops the shell's.
    private sealed record MenuSession(long Id, IContextMenu Handler, HMENU Menu, bool OwnTerminal);

    private sealed record TrackResult(uint Command, string? Verb, ContextMenuResult? Result)
    {
        // Shift held when the item was chosen (passed to InvokeCommand as CMIC_MASK_SHIFT_DOWN).
        public bool Shift { get; init; }
    }
}
