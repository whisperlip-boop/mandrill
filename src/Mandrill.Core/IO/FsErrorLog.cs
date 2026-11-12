using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Mandrill.Core.IO;

/// <summary>Logs file-system errors at most once per path and kind per interval (architecture §10: once per minute).</summary>
public sealed class FsErrorLog
{
    private const int MaxTracked = 4096;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;
    private readonly Func<long> _now;
    private readonly ConcurrentDictionary<(string Path, FsErrorKind Kind), long> _last = new(PathKindComparer.Instance);

    /// <summary>Creates the throttle.</summary>
    /// <param name="logger">Target logger.</param>
    /// <param name="interval">Minimum time between two entries for the same path and kind (default 1 minute).</param>
    /// <param name="nowTicks">Clock in <see cref="TimeSpan"/> ticks (tests); defaults to Environment.TickCount64.</param>
    public FsErrorLog(ILogger logger, TimeSpan? interval = null, Func<long>? nowTicks = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval ?? TimeSpan.FromMinutes(1);
        _now = nowTicks ?? (() => Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
    }

    /// <summary>Number of path/kind pairs currently remembered (diagnostics, tests).</summary>
    internal int TrackedCount => _last.Count;

    /// <summary>Logs <paramref name="error"/> unless the same path and kind were logged within the interval.</summary>
    /// <param name="operation">What was attempted (e.g. "List").</param>
    /// <param name="error">The error.</param>
    /// <returns>True when an entry was written.</returns>
    public bool Report(string operation, FsError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        long now = _now();
        var key = (error.Path, error.Kind);
        bool write = true;
        _last.AddOrUpdate(
            key,
            now,
            (_, previous) =>
            {
                if (now - previous < _interval.Ticks)
                {
                    write = false;
                    return previous;
                }

                write = true;
                return now;
            });
        if (_last.Count > MaxTracked)
        {
            // Bulk walkers (e.g. the Phase 2 index) can hit thousands of paths; forget entries past their interval.
            foreach (var item in _last)
            {
                if (now - item.Value >= _interval.Ticks)
                {
                    _last.TryRemove(item);
                }
            }
        }

        if (write)
        {
            _logger.LogWarning("{Operation} failed: {Kind} (Win32 {Code}) for {Path}", operation, error.Kind, error.Win32Code, error.Path);
        }

        return write;
    }

    // Path part compared with the path-equality rule (PathUtil.Comparer), kind compared exactly.
    private sealed class PathKindComparer : IEqualityComparer<(string Path, FsErrorKind Kind)>
    {
        public static readonly PathKindComparer Instance = new();

        public bool Equals((string Path, FsErrorKind Kind) x, (string Path, FsErrorKind Kind) y)
            => x.Kind == y.Kind && PathUtil.Comparer.Equals(x.Path, y.Path);

        public int GetHashCode((string Path, FsErrorKind Kind) obj)
            => HashCode.Combine(PathUtil.Comparer.GetHashCode(obj.Path), obj.Kind);
    }
}
