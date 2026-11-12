using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Core.Shell;

/// <summary>
/// Owner of one named <see cref="StaWorker"/> ("icons", "misc"; architecture §4). When the worker hangs, the next call
/// abandons it (logged; the thread leaks until exit) and starts a fresh one, so one stuck shell extension costs one
/// fallback result instead of the whole feature.
/// </summary>
public sealed class ShellSta : IDisposable
{
    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private StaWorker _worker;
    private int _replacements;
    private bool _disposed;

    /// <summary>Creates the owner and starts its first worker.</summary>
    /// <param name="name">Worker name ("icons", "misc").</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public ShellSta(string name, ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<StaWorker>();
        _worker = new StaWorker(name, _logger);
    }

    /// <summary>
    /// Raised (on the calling thread that noticed the hang, never the STA) after a hung worker was replaced. Work that
    /// failed only because the worker was stuck can be retried now.
    /// </summary>
    public event Action? Replaced;

    /// <summary>Worker name.</summary>
    public string Name { get; }

    /// <summary>Number of hung workers replaced so far (diagnostics, tests).</summary>
    public int Replacements => Volatile.Read(ref _replacements);

    /// <summary>Managed thread id of the current worker (diagnostics, tests).</summary>
    public int CurrentThreadId
    {
        get
        {
            lock (_gate)
            {
                return _worker.ThreadId;
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> on the current worker (see <see cref="StaWorker.InvokeAsync{T}"/>).</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="work">Work to run on the STA thread.</param>
    /// <param name="timeout">Maximum run time; on overrun the caller gets <see cref="TimeoutException"/> and the worker is replaced.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The work's result.</returns>
    /// <exception cref="TimeoutException">The work overran <paramref name="timeout"/>.</exception>
    /// <exception cref="ObjectDisposedException">This owner was disposed.</exception>
    public async Task<T> InvokeAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken ct)
    {
        StaWorker worker;
        bool replaced = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker.IsHung)
            {
                ReplaceLocked();
                replaced = true;
            }

            worker = _worker;
        }

        if (replaced)
        {
            RaiseReplaced();
        }

        try
        {
            return await worker.InvokeAsync(work, timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            bool replacedNow = false;
            lock (_gate)
            {
                if (!_disposed && ReferenceEquals(_worker, worker) && worker.IsHung)
                {
                    ReplaceLocked();
                    replacedNow = true;
                }
            }

            if (replacedNow)
            {
                RaiseReplaced();
            }

            throw;
        }
    }

    /// <summary>Stops the current worker.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _worker.Dispose();
        }
    }

    private void RaiseReplaced()
    {
        try
        {
            Replaced?.Invoke();
        }
        catch (Exception ex)
        {
            // A subscriber's bug must not turn the caller's TimeoutException into something else.
            _logger.LogError(ex, "STA worker {Name}: Replaced handler failed", Name);
        }
    }

    private void ReplaceLocked()
    {
        var hung = _worker;
        _worker = new StaWorker(Name, _logger);
        _replacements++;
        _logger.LogWarning("STA worker {Name}: hung thread {ThreadId} abandoned (leaks until exit); replacement #{Count} started", Name, hung.ThreadId, _replacements);
        hung.Dispose();
    }
}
