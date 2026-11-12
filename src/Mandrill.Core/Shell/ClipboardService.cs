using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mandrill.Core.Shell;

/// <summary>
/// <see cref="IClipboardService"/> on a dedicated long-lived "clipboard" STA with a message-only window registered through
/// AddClipboardFormatListener (WP-3.0: WM_CLIPBOARDUPDATE 6–15 ms after a change). OpenClipboard is retried 5 × 20 ms.
/// </summary>
public sealed class ClipboardService : IClipboardService, IDisposable
{
    private const uint CfHDrop = 15;
    private const uint DropEffectMove = 2;
    private const uint DropEffectCopy = 1;
    private const string ListenerClass = "MandrillClipboardListener";
    private const nuint RetryTimer = 1;
    private const uint RetryMilliseconds = 250;
    private const int MaxRetries = 8; // about 2 s
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // A read of the clipboard (a delayed-render owner may hang it) is a work item with this limit, so a hang marks the worker
    // hung and the next call replaces it (QA N1).
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly WNDPROC s_listenerProc = ListenerProc; // kept alive: the class holds only the function pointer
    private static readonly Lazy<bool> s_classRegistered = new(RegisterListenerClass);

    // The listener's WndProc is static; it reaches the one service of the process (the app has one; tests one at a time).
    private static ClipboardService? s_current;

    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private StaWorker _sta;
    private int _retries;
    private uint _retrySequence;
    private readonly uint _dropEffectFormat;
    private ClipboardFiles? _current;
    private uint _sequence;
    private nint _window;

    /// <summary>Creates the service; call <see cref="StartAsync"/> to start listening.</summary>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public ClipboardService(ILoggerFactory? loggerFactory = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ClipboardService>();
        _sta = new StaWorker("clipboard", _logger);
        _dropEffectFormat = PInvoke.RegisterClipboardFormat("Preferred DropEffect");
    }

    /// <inheritdoc/>
    public event Action? Changed;

    /// <inheritdoc/>
    public ClipboardFiles? Current => Volatile.Read(ref _current);

    /// <summary>Creates the listener window and reads the clipboard once.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when listening.</returns>
    public Task StartAsync(CancellationToken ct) => Worker().InvokeAsync(StartOnSta, Timeout, ct);

    /// <inheritdoc/>
    public async Task<bool> SetFilesAsync(IReadOnlyList<string> paths, bool cut, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0 || paths.Any(string.IsNullOrEmpty))
        {
            return false; // QA m2: checked before any memory is allocated
        }

        try
        {
            return await Worker().InvokeAsync(() => Write(paths, cut), Timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Putting {Count} file(s) on the clipboard failed", paths.Count);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task EmptyAfterCutPasteAsync(ClipboardFiles pasted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pasted);
        try
        {
            await Worker().InvokeAsync(
                () =>
                {
                    // Explorer empties the clipboard after a cut is pasted; never when anything was put there meanwhile (the
                    // sequence number changes with every write, also one by the same app with the same files).
                    if (pasted.Cut && pasted.Sequence != 0 && Open())
                    {
                        try
                        {
                            // Checked with the clipboard open (locked): no other app can write between the check and the empty.
                            if (PInvoke.GetClipboardSequenceNumber() == pasted.Sequence)
                            {
                                PInvoke.EmptyClipboard();
                            }
                        }
                        finally
                        {
                            PInvoke.CloseClipboard();
                        }
                    }

                    return true;
                },
                Timeout,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Emptying the clipboard after a cut-paste failed");
        }
    }

    /// <summary>Stops listening and the STA.</summary>
    public void Dispose()
    {
        // The listener window and its registration go away with the STA thread (the OS removes both at thread exit).
        Interlocked.Exchange(ref _window, 0);
        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }

        lock (_gate)
        {
            _sta.Dispose();
        }
    }

    private static LRESULT ListenerProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == PInvoke.WM_TIMER && wParam.Value == RetryTimer)
        {
            PInvoke.KillTimer(hwnd, RetryTimer);
            s_current?.QueueRefresh(hwnd);
            return default;
        }

        if (msg == PInvoke.WM_CLIPBOARDUPDATE)
        {
            s_current?.QueueRefresh(hwnd);
            return default;
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static unsafe bool RegisterListenerClass()
    {
        fixed (char* className = ListenerClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = s_listenerProc,
                hInstance = PInvoke.GetModuleHandle((char*)null),
                lpszClassName = className,
            };
            return PInvoke.RegisterClassEx(in wc) != 0;
        }
    }

    private static unsafe nint CreateListener()
    {
        if (!s_classRegistered.Value)
        {
            throw new InvalidOperationException("The clipboard listener class could not be registered");
        }

        fixed (char* className = ListenerClass)
        {
            HWND hwnd = PInvoke.CreateWindowEx(0, className, default, 0, 0, 0, 0, 0, HWND.HWND_MESSAGE, default, PInvoke.GetModuleHandle((char*)null), null);
            if (hwnd.IsNull || !PInvoke.AddClipboardFormatListener(hwnd))
            {
                throw new InvalidOperationException("The clipboard listener could not be created");
            }

            return (nint)hwnd;
        }
    }

    // QA N1: the read runs as a work item with a limit (never inside the WndProc), so a hang is detected. A listener window of
    // an abandoned thread that wakes up later is not this service's listener any more: it is unregistered and ignored.
    private void QueueRefresh(HWND listener)
    {
        if ((nint)listener != Volatile.Read(ref _window))
        {
            PInvoke.RemoveClipboardFormatListener(listener);
            return;
        }

        _ = Worker().InvokeAsync(
            () =>
            {
                Refresh();
                return true;
            },
            ReadTimeout,
            CancellationToken.None).ContinueWith(t => _logger.LogWarning(t.Exception, "Reading the clipboard failed"), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    // QA m1: a worker stuck in a hung delayed-render read is replaced, with its own listener, so the clipboard keeps working.
    private StaWorker Worker()
    {
        lock (_gate)
        {
            if (_sta.IsHung)
            {
                _logger.LogWarning("The clipboard thread is hung: a new one takes over");
                _sta = new StaWorker("clipboard", _logger); // the hung thread is abandoned; the OS cleans it up at exit
                _ = _sta.InvokeAsync(StartOnSta, Timeout, CancellationToken.None)
                    .ContinueWith(t => _logger.LogError(t.Exception, "Restarting the clipboard listener failed"), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            return _sta;
        }
    }

    private bool StartOnSta()
    {
        s_current = this;
        _window = CreateListener();
        Refresh();
        return true;
    }

    // Another app may hold the clipboard for a moment (ADR-0011: 5 retries x 20 ms).
    private bool Open()
    {
        for (int i = 0; i < 5; i++)
        {
            if (PInvoke.OpenClipboard((HWND)_window))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        _logger.LogWarning("The clipboard is held by another application");
        return false;
    }

    // Reads CF_HDROP and the drop effect into Current, then raises Changed (clipboard STA).
    private unsafe void Refresh()
    {
        uint sequence = PInvoke.GetClipboardSequenceNumber();
        if (sequence == _sequence && _sequence != 0)
        {
            return;
        }

        ClipboardFiles? files = null;
        if (PInvoke.IsClipboardFormatAvailable(CfHDrop))
        {
            if (!Open())
            {
                // QA M1: another app holds the clipboard (clipboard history right after a change). Keep what we had, do not
                // remember this change as read, and look again shortly.
                if (sequence != _retrySequence)
                {
                    _retrySequence = sequence; // a new change gets a fresh retry budget (QA N2)
                    _retries = 0;
                }

                if (_retries++ < MaxRetries)
                {
                    PInvoke.SetTimer((HWND)_window, RetryTimer, RetryMilliseconds, null);
                    return;
                }

                // QA N2 (data safety): the clipboard changed but stayed unreadable: never keep the previous files (a paste
                // would move a set the user no longer has on the clipboard). Paste goes off until the next change.
                _logger.LogWarning("The clipboard stayed held by another application; Paste is off until it changes again");
                _sequence = sequence;
                Volatile.Write(ref _current, null);
                RaiseChanged();
                return;
            }

            try
            {
                var drop = (HDROP)PInvoke.GetClipboardData(CfHDrop).Value;
                if (drop.Value is not null)
                {
                    uint count = PInvoke.DragQueryFile(drop, uint.MaxValue, null, 0);
                    var paths = new List<string>((int)count);
                    char* buffer = stackalloc char[32_768];
                    for (uint i = 0; i < count; i++)
                    {
                        uint length = PInvoke.DragQueryFile(drop, i, buffer, 32_768);
                        if (length > 0)
                        {
                            paths.Add(new string(buffer, 0, (int)length));
                        }
                    }

                    uint effect = 0;
                    HANDLE effectHandle = PInvoke.GetClipboardData(_dropEffectFormat);
                    if (effectHandle.Value is not null)
                    {
                        var global = (HGLOBAL)effectHandle.Value;
                        if (PInvoke.GlobalSize(global) >= sizeof(uint) && PInvoke.GlobalLock(global) is var data && data is not null)
                        {
                            effect = *(uint*)data;
                            PInvoke.GlobalUnlock(global);
                        }
                    }

                    // Explorer sets the move bit only for a cut and pastes any effect with it as a move; the same here.
                    files = paths.Count == 0 ? null : new ClipboardFiles(paths, (effect & DropEffectMove) != 0, sequence);
                }
            }
            finally
            {
                PInvoke.CloseClipboard();
            }
        }

        _sequence = sequence;
        _retries = 0;
        Volatile.Write(ref _current, files);
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A clipboard listener failed");
        }
    }

    private unsafe bool Write(IReadOnlyList<string> paths, bool cut)
    {
        // DROPFILES header, then the wide paths, each NUL-terminated, then a final NUL.
        int chars = paths.Sum(p => p.Length + 1) + 1;
        int size = sizeof(DROPFILES) + (chars * sizeof(char));
        HGLOBAL drop = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE | GLOBAL_ALLOC_FLAGS.GMEM_ZEROINIT, (nuint)size);
        HGLOBAL effect = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, sizeof(uint));
        if (drop.Value is null || effect.Value is null)
        {
            Free(drop);
            Free(effect);
            return false;
        }

        var header = (DROPFILES*)PInvoke.GlobalLock(drop);
        uint* effectData = (uint*)PInvoke.GlobalLock(effect);
        if (header is null || effectData is null)
        {
            // Fail before the clipboard is opened and emptied: the user's clipboard stays as it was (QA m2: unlock first).
            if (header is not null)
            {
                PInvoke.GlobalUnlock(drop);
            }

            if (effectData is not null)
            {
                PInvoke.GlobalUnlock(effect);
            }

            Free(drop);
            Free(effect);
            return false;
        }

        header->pFiles = (uint)sizeof(DROPFILES);
        header->fWide = true;
        char* text = (char*)((byte*)header + sizeof(DROPFILES));
        foreach (string path in paths)
        {
            path.AsSpan().CopyTo(new Span<char>(text, path.Length));
            text += path.Length + 1;
        }

        PInvoke.GlobalUnlock(drop);
        *effectData = cut ? DropEffectMove : DropEffectCopy;
        PInvoke.GlobalUnlock(effect);

        if (!Open())
        {
            Free(drop);
            Free(effect);
            return false;
        }

        try
        {
            PInvoke.EmptyClipboard();

            // On success the clipboard owns the memory; on failure it is still ours to free.
            if (PInvoke.SetClipboardData(CfHDrop, (HANDLE)drop.Value).Value is null)
            {
                Free(drop);
                Free(effect);
                return false;
            }

            if (PInvoke.SetClipboardData(_dropEffectFormat, (HANDLE)effect.Value).Value is null)
            {
                Free(effect);
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }

        _logger.LogInformation("{Action} {Count} item(s) to the clipboard", cut ? "Cut" : "Copied", paths.Count);
        return true;
    }

    private static void Free(HGLOBAL memory)
    {
        unsafe
        {
            if (memory.Value is not null)
            {
                PInvoke.GlobalFree(memory);
            }
        }
    }
}
