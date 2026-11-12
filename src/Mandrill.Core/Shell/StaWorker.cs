using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mandrill.Core.Shell;

/// <summary>
/// A dedicated STA thread (OleInitialize) with a message-only window and a GetMessage pump; runs work items in order
/// (architecture §4, ADR-0006). A work item that overruns its timeout marks the worker hung: its owner abandons it
/// (<see cref="ShellSta"/>) and creates a fresh one.
/// </summary>
public sealed class StaWorker : IDisposable
{
    private const string WindowClass = "Mandrill.StaWorker";
    private const uint WorkMessage = PInvoke.WM_APP;
    private const uint QuitMessage = PInvoke.WM_APP + 1;

    // Kept in a static field so the marshaled callback outlives every window of the class.
    private static readonly WNDPROC s_windowProc = WindowProc;
    private static readonly Lazy<bool> s_classRegistered = new(RegisterWindowClass);

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private nint _hwnd;
    private int _posted;
    private volatile bool _disposed;
    private volatile bool _hung;
    private volatile bool _faulted;
    private volatile WorkItem? _running;

    /// <summary>Starts the thread; work can be queued immediately (it runs once the window exists).</summary>
    /// <param name="name">Thread name suffix, e.g. "icons" or "misc".</param>
    /// <param name="logger">Logger; null disables logging.</param>
    public StaWorker(string name, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _logger = logger ?? NullLogger.Instance;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "Mandrill STA " + name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Worker name ("icons", "misc").</summary>
    public string Name { get; }

    /// <summary>True once a work item overran its timeout; the worker accepts no more work and should be replaced.</summary>
    public bool IsHung => _hung;

    /// <summary>Managed id of the worker thread (diagnostics, tests).</summary>
    public int ThreadId => _thread.ManagedThreadId;

    /// <summary>
    /// Runs <paramref name="work"/> on the STA thread. The timeout counts from the moment the item starts running.
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="work">Work to run; exceptions it throws fault the returned task.</param>
    /// <param name="timeout">Maximum run time of this item before the worker is declared hung.</param>
    /// <param name="ct">Cancels the wait (and the item, if it has not started yet).</param>
    /// <returns>The work's result.</returns>
    /// <exception cref="TimeoutException">The item overran <paramref name="timeout"/>, or the worker is already hung.</exception>
    /// <exception cref="ObjectDisposedException">The worker was disposed.</exception>
    public Task<T> InvokeAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            // Infinite (opt-in): user-driven work such as a file operation with its own dialogs never marks the worker hung.
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        }
        if (_disposed)
        {
            return Task.FromException<T>(new ObjectDisposedException(nameof(StaWorker)));
        }

        if (_hung)
        {
            return Task.FromException<T>(new TimeoutException($"STA worker '{Name}' is hung"));
        }

        if (_faulted)
        {
            return Task.FromException<T>(new InvalidOperationException($"STA worker '{Name}' could not start"));
        }

        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }

        var item = new WorkItem<T>(work, timeout, ct);
        _queue.Enqueue(item);
        // Enqueue is a full fence, so either this read sees the window or the thread's first drain sees the item.
        nint hwnd = Volatile.Read(ref _hwnd);
        if (hwnd != 0 && Interlocked.Exchange(ref _posted, 1) == 0)
        {
            PInvoke.PostMessage((HWND)hwnd, WorkMessage, default, default);
        }

        if (_disposed || _hung || _faulted)
        {
            // Raced with Dispose, a hang or a failed start: nobody may drain the queue any more.
            FailQueued(
                _hung ? new TimeoutException($"STA worker '{Name}' is hung")
                : _faulted ? new InvalidOperationException($"STA worker '{Name}' could not start")
                : new ObjectDisposedException(nameof(StaWorker)));
        }

        return item.Task;
    }

    /// <summary>Stops the pump after the running item (a hung item is abandoned, never waited for).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Store-load fence: pairs with the thread's Interlocked.Exchange(_hwnd) before it reads _disposed, so either the
        // quit message is posted here or the pump sees _disposed and never blocks in GetMessage.
        Interlocked.MemoryBarrier();
        nint hwnd = Volatile.Read(ref _hwnd);
        if (hwnd != 0)
        {
            PInvoke.PostMessage((HWND)hwnd, QuitMessage, default, default);
        }

        FailQueued(new ObjectDisposedException(nameof(StaWorker)));
    }

    private static LRESULT WindowProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == QuitMessage)
        {
            // Only reached when a nested modal loop (inside a work item) dispatched it: WM_QUIT ends that loop and then
            // makes the outer GetMessage return 0, so a disposed worker still exits.
            PInvoke.PostQuitMessage(0);
            return default;
        }

        // Work messages are handled by the pump itself; one dispatched by a nested modal loop is ignored here, because
        // the outer drain keeps dequeuing until the queue is empty.
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static unsafe bool RegisterWindowClass()
    {
        fixed (char* className = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = s_windowProc,
                hInstance = ModuleInstance(),
                lpszClassName = className,
            };

            // 0 with ERROR_CLASS_ALREADY_EXISTS cannot happen within one process (Lazy), so 0 is a real failure.
            return PInvoke.RegisterClassEx(in wc) != 0;
        }
    }

    private void ThreadMain()
    {
        var ole = PInvoke.OleInitialize();
        if (ole.Failed)
        {
            _logger.LogWarning("STA worker {Name}: OleInitialize failed (0x{Hr:X8})", Name, ole.Value);
        }

        HWND hwnd = default;
        try
        {
            hwnd = CreateMessageWindow();
            if (hwnd.IsNull)
            {
                _faulted = true;
                _logger.LogError("STA worker {Name}: message window could not be created", Name);
                FailQueued(new InvalidOperationException($"STA worker '{Name}' could not start"));
                return;
            }

            Interlocked.Exchange(ref _hwnd, (nint)hwnd);
            Drain();
            Pump(hwnd);
        }
        catch (Exception ex)
        {
            // Work exceptions are caught per item; anything here is a pump failure.
            _faulted = true;
            _logger.LogError(ex, "STA worker {Name}: message loop failed", Name);
            FailQueued(new InvalidOperationException($"STA worker '{Name}' stopped", ex));
        }
        finally
        {
            if (!hwnd.IsNull)
            {
                PInvoke.DestroyWindow(hwnd);
            }

            if (ole.Succeeded)
            {
                PInvoke.OleUninitialize();
            }

            _logger.LogDebug("STA worker {Name}: stopped", Name);
        }
    }

    private static unsafe HWND CreateMessageWindow()
    {
        if (!s_classRegistered.Value)
        {
            return default;
        }

        fixed (char* className = WindowClass)
        {
            return PInvoke.CreateWindowEx(
                0,
                className,
                default,
                0,
                0,
                0,
                0,
                0,
                HWND.HWND_MESSAGE,
                default,
                ModuleInstance(),
                null);
        }
    }

    private static unsafe HINSTANCE ModuleInstance() => new(PInvoke.GetModuleHandle(default(PCWSTR)).Value);

    private void Pump(HWND hwnd)
    {
        while (!_disposed)
        {
            var got = PInvoke.GetMessage(out MSG msg, default, 0, 0);
            if (got.Value is 0 or -1)
            {
                // WM_QUIT (a component posted it) or an invalid-window error: either way the loop cannot continue.
                return;
            }

            if (msg.hwnd == hwnd && msg.message == QuitMessage)
            {
                return;
            }

            if (msg.hwnd == hwnd && msg.message == WorkMessage)
            {
                Drain();
                continue;
            }

            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
    }

    private void Drain()
    {
        while (true)
        {
            while (!_disposed && !_hung && _queue.TryDequeue(out var item))
            {
                Run(item);
            }

            // Reset after the queue looked empty, then re-check: an item enqueued in between saw _posted == 1 and did
            // not post, so this thread must pick it up.
            Interlocked.Exchange(ref _posted, 0);
            if (_disposed || _hung || _queue.IsEmpty)
            {
                return;
            }
        }
    }

    private void Run(WorkItem item)
    {
        if (item.IsCompleted)
        {
            return; // cancelled while queued
        }

        _running = item;
        using var timer = new Timer(_ => OnTimeout(item), null, item.Timeout, Timeout.InfiniteTimeSpan);
        try
        {
            item.Run();
        }
        finally
        {
            _running = null;
        }
    }

    private void OnTimeout(WorkItem item)
    {
        if (!ReferenceEquals(_running, item) || !item.TryTimeOut())
        {
            return; // finished in time (including a finish racing with this callback)
        }

        _hung = true;
        _logger.LogWarning("STA worker {Name} hung: a work item ran longer than {Timeout} ms; the thread is abandoned", Name, (long)item.Timeout.TotalMilliseconds);
        item.Fail(new TimeoutException($"STA worker '{Name}' did not finish within {item.Timeout}"));
        FailQueued(new TimeoutException($"STA worker '{Name}' is hung"));
    }

    private void FailQueued(Exception error)
    {
        while (_queue.TryDequeue(out var item))
        {
            item.Fail(error);
        }
    }

    private abstract class WorkItem
    {
        private const int Running = 0;
        private const int Finished = 1;
        private const int TimedOut = 2;
        private int _state;

        protected WorkItem(TimeSpan timeout) => Timeout = timeout;

        public TimeSpan Timeout { get; }

        public abstract bool IsCompleted { get; }

        // Running -> Finished (by the STA) or Running -> TimedOut (by the timer): exactly one side wins.
        public bool TryTimeOut() => Interlocked.CompareExchange(ref _state, TimedOut, Running) == Running;

        public abstract void Run();

        public abstract void Fail(Exception error);

        protected bool TryFinish() => Interlocked.CompareExchange(ref _state, Finished, Running) == Running;
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly Func<T> _work;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public WorkItem(Func<T> work, TimeSpan timeout, CancellationToken ct)
            : base(timeout)
        {
            _work = work;
            _registration = ct.Register(() => _tcs.TrySetCanceled(ct));
        }

        public Task<T> Task => _tcs.Task;

        public override bool IsCompleted => _tcs.Task.IsCompleted;

        public override void Run()
        {
            try
            {
                T result = _work();
                if (TryFinish())
                {
                    _tcs.TrySetResult(result);
                }
            }
            catch (Exception ex)
            {
                if (TryFinish())
                {
                    _tcs.TrySetException(ex);
                }
            }
            finally
            {
                // Long-lived tokens (service lifetime) must not accumulate one registration per item.
                _registration.Dispose();
            }
        }

        public override void Fail(Exception error)
        {
            _tcs.TrySetException(error);
            _registration.Dispose();
        }
    }
}
