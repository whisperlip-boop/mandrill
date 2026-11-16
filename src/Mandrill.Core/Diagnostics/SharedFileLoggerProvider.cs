using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Mandrill.Core.Diagnostics;

/// <summary>
/// Appends the log lines of every Mandrill process to one shared file (architecture §18 D1). <see cref="ILogger.Log"/> only
/// enqueues; a background thread writes each batch under a named mutex, opening the file per batch, and rolls it at the limit.
/// </summary>
public sealed class SharedFileLoggerProvider : ILoggerProvider
{
    private const int MaxQueuedLines = 10_000;
    private const int MaxBatchChars = 256 * 1024;
    private const int MutexWaitMs = 2_000;
    private const int MinBackoffMs = 100;
    private const int MaxBackoffMs = 2_000;
    private const int DrainLimitMs = 1_000;

    private readonly string _path;
    private readonly string _tag;
    private readonly long _sizeLimit;
    private readonly Mutex _mutex;
    private readonly Queue<string> _queue = new(); // also the lock for _dropped, _disposed and _drainDeadline
    private readonly Thread _writer;
    private int _dropped;
    private volatile bool _disposed;
    private long _drainDeadline;

    /// <summary>Creates the provider and starts its writer thread.</summary>
    /// <param name="path">The shared log file; its directory must exist.</param>
    /// <param name="instance">This process's instance number (the line tag is <c>pid#instance</c>).</param>
    public SharedFileLoggerProvider(string path, int instance)
        : this(path, instance, MandrillLogging.FileSizeLimitBytes)
    {
    }

    internal SharedFileLoggerProvider(string path, int instance, long sizeLimit)
    {
        _path = Path.GetFullPath(path);
        _tag = string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}#{instance}");
        _sizeLimit = sizeLimit;
        _mutex = new Mutex(false, MutexName(_path));
        _writer = new Thread(Run) { Name = "Mandrill log writer", IsBackground = true };
        _writer.Start();
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Stops accepting lines and writes the queued ones, waiting at most one second.</summary>
    public void Dispose()
    {
        lock (_queue)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _drainDeadline = Environment.TickCount64 + DrainLimitMs;
            Monitor.PulseAll(_queue);
        }

        _writer.Join(DrainLimitMs);
    }

    // One name per log file across processes; the hash keeps MANDRILL_DATA_DIR runs apart. Mutex names are case-sensitive.
    internal static string MutexName(string path)
    {
        ulong hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        return @"Local\Mandrill.Log." + hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>Opens the log for one append with the writer's sharing (readers and other writers stay possible).</summary>
    internal static FileStream Open(string path)
        => new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);

    private string RolledPath(int n)
        => Path.Combine(Path.GetDirectoryName(_path)!, string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(_path)}.{n}{Path.GetExtension(_path)}"));

    private void Enqueue(LogLevel level, string category, string message, Exception? exception)
    {
        string line = MandrillLogging.Format(level, _tag, category, message, exception) + Environment.NewLine;
        lock (_queue)
        {
            if (_disposed)
            {
                return;
            }

            if (_queue.Count >= MaxQueuedLines)
            {
                _queue.Dequeue();
                _dropped++;
            }

            _queue.Enqueue(line);
            Monitor.Pulse(_queue);
        }
    }

    private void Run()
    {
        var batch = new StringBuilder();
        int backoff = MinBackoffMs;
        try
        {
            while (true)
            {
                bool stopping = Take(batch);
                if (batch.Length == 0)
                {
                    return; // disposed and drained
                }

                if (TryAppend(batch.ToString()))
                {
                    batch.Clear();
                    backoff = MinBackoffMs;
                    continue;
                }

                // Keep the batch and retry; a stopping provider gives up at the drain limit.
                long wait = stopping ? Math.Min(backoff, Volatile.Read(ref _drainDeadline) - Environment.TickCount64) : backoff;
                if (wait <= 0)
                {
                    return;
                }

                Thread.Sleep((int)wait);
                backoff = Math.Min(backoff * 2, MaxBackoffMs);
            }
        }
        finally
        {
            _mutex.Dispose();
        }
    }

    // Moves queued lines into the batch (waits while there is nothing to write); returns true once disposed.
    private bool Take(StringBuilder batch)
    {
        lock (_queue)
        {
            while (batch.Length == 0 && _queue.Count == 0 && !_disposed)
            {
                Monitor.Wait(_queue);
            }

            if (_dropped > 0 && batch.Length < MaxBatchChars)
            {
                string note = string.Create(CultureInfo.InvariantCulture, $"{_dropped} log lines dropped");
                batch.Append(MandrillLogging.Format(LogLevel.Warning, _tag, typeof(SharedFileLoggerProvider).FullName!, note, null)).Append(Environment.NewLine);
                _dropped = 0;
            }

            while (_queue.Count > 0 && batch.Length < MaxBatchChars)
            {
                batch.Append(_queue.Dequeue());
            }

            return _disposed;
        }
    }

    // Under the mutex the size check, the roll and the append are atomic across processes; lines never interleave.
    private bool TryAppend(string batch)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(batch);
        bool owned;
        try
        {
            owned = _mutex.WaitOne(MutexWaitMs);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // a killed instance held it: we own it now; only its last batch may be cut
        }

        if (!owned)
        {
            return false;
        }

        try
        {
            var stream = Open(_path);
            try
            {
                if (stream.Length > 0 && stream.Length + bytes.Length > _sizeLimit)
                {
                    stream.Dispose();
                    Roll();
                    stream = Open(_path);
                }

                stream.Write(bytes);
            }
            finally
            {
                stream.Dispose();
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    // mandrill.1.log -> .2 (replacing it), mandrill.log -> .1; the next Open creates a new mandrill.log.
    private void Roll()
    {
        try
        {
            for (int n = MandrillLogging.MaxRollingFiles - 1; n > 0; n--)
            {
                string from = n == 1 ? _path : RolledPath(n - 1);
                if (File.Exists(from))
                {
                    File.Move(from, RolledPath(n), overwrite: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A viewer holds a file without delete sharing: append over the limit and roll at a later batch (nothing is lost).
        }
    }

    private sealed class FileLogger(SharedFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && !owner._disposed;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Enqueue(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
