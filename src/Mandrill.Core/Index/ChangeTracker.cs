using System.Diagnostics;
using Mandrill.Core.IO;
using Mandrill.Core.Watching;

namespace Mandrill.Core.Index;

/// <summary>
/// Change intake (architecture §8.3): watcher callbacks on pool threads add dirty paths per drive and overflow marks; the
/// index thread takes them. Thread-safe; never blocks beyond a short lock.
/// </summary>
internal sealed class ChangeTracker
{
    /// <summary>Paths held per drive before the drive counts as overflowed (a long first build on a busy drive).</summary>
    internal const int MaxPathsPerDrive = 100_000;

    /// <summary>Ticks a path whose stat failed is retried before it is dropped (a file still open for writing).</summary>
    internal const int MaxRetries = 3;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, HashSet<string>> _dirty = new(PathUtil.Comparer);
    private readonly HashSet<string> _overflow = new(PathUtil.Comparer);
    private readonly Dictionary<string, int> _retries = new(PathUtil.Comparer);
    private readonly Dictionary<string, long> _firstSeen = new(PathUtil.Comparer); // root → Stopwatch timestamp

    /// <summary>Adds the paths of <paramref name="changes"/> (and the old path of a rename) to the drive's dirty set.</summary>
    public void Add(string root, IReadOnlyList<RawChange> changes)
    {
        lock (_gate)
        {
            if (_overflow.Contains(root))
            {
                return; // a verify covers everything anyway
            }

            HashSet<string> paths = DirtySet(root);
            foreach (RawChange change in changes)
            {
                paths.Add(change.FullPath);
                _retries.Remove(change.FullPath); // a new event is new evidence: the retry count starts over
                if (change.OldFullPath is { } old)
                {
                    paths.Add(old);
                }
            }

            if (paths.Count > MaxPathsPerDrive)
            {
                _dirty.Remove(root);
                _firstSeen.Remove(root);
                _overflow.Add(root);
            }
        }
    }

    /// <summary>The watcher lost events for the drive: its dirty paths are dropped in favour of a verify.</summary>
    public void Overflow(string root)
    {
        lock (_gate)
        {
            _dirty.Remove(root);
            _overflow.Add(root);
            ForgetRetries(root);
        }
    }

    /// <summary>
    /// After an apply: paths whose stat failed go back for the next tick, at most <see cref="MaxRetries"/> times; the
    /// others are settled.
    /// </summary>
    /// <param name="root">Drive root.</param>
    /// <param name="unknown">Paths whose stat failed.</param>
    /// <param name="settled">Paths that were applied.</param>
    /// <param name="firstSeen">Stamp of the applied batch (from <see cref="Take(string, out long)"/>), kept for the retried
    /// paths so the oldest-change latency counts from their first event; 0 when unknown.</param>
    /// <returns>How many paths were dropped after their last retry.</returns>
    public int Retry(string root, IReadOnlyCollection<string> unknown, IEnumerable<string> settled, long firstSeen = 0)
    {
        lock (_gate)
        {
            foreach (string path in settled)
            {
                _retries.Remove(path);
            }

            if (_overflow.Contains(root))
            {
                return 0; // the verify covers them
            }

            int dropped = 0;
            bool requeued = false;
            foreach (string path in unknown)
            {
                int tries = _retries.GetValueOrDefault(path) + 1;
                if (tries > MaxRetries)
                {
                    _retries.Remove(path);
                    dropped++;
                    continue;
                }

                _retries[path] = tries;
                DirtySet(root).Add(path);
                requeued = true;
            }

            // Only a requeued path carries the old stamp; changes added during the apply keep their own.
            if (requeued && firstSeen != 0 && _firstSeen.TryGetValue(root, out long stamp) && firstSeen < stamp)
            {
                _firstSeen[root] = firstSeen;
            }

            return dropped;
        }
    }

    /// <summary>Takes the dirty paths of the drive (empty when there are none).</summary>
    public List<string> Take(string root) => Take(root, out _);

    /// <summary>Takes the dirty paths of the drive and when the oldest of them was first recorded.</summary>
    /// <param name="root">Drive root.</param>
    /// <param name="firstSeen"><see cref="Stopwatch.GetTimestamp"/> of the oldest pending change; 0 when there are none.</param>
    /// <returns>The paths.</returns>
    public List<string> Take(string root, out long firstSeen)
    {
        lock (_gate)
        {
            _firstSeen.Remove(root, out firstSeen);
            if (!_dirty.Remove(root, out HashSet<string>? paths))
            {
                return [];
            }

            return [.. paths];
        }
    }

    /// <summary>Drives with dirty paths.</summary>
    public List<string> DirtyRoots()
    {
        lock (_gate)
        {
            return [.. _dirty.Keys];
        }
    }

    /// <summary>Takes the overflowed drives.</summary>
    public List<string> TakeOverflows()
    {
        lock (_gate)
        {
            List<string> roots = [.. _overflow];
            _overflow.Clear();
            return roots;
        }
    }

    /// <summary>Forgets a drive (removed or no longer indexed).</summary>
    public void Forget(string root)
    {
        lock (_gate)
        {
            _dirty.Remove(root);
            _overflow.Remove(root);
            ForgetRetries(root);
        }
    }

    // Called under _gate. The first path of a batch stamps the drive's oldest pending change.
    private HashSet<string> DirtySet(string root)
    {
        if (!_dirty.TryGetValue(root, out HashSet<string>? paths))
        {
            paths = new HashSet<string>(PathUtil.Comparer);
            _dirty[root] = paths;
            _firstSeen[root] = Stopwatch.GetTimestamp();
        }

        return paths;
    }

    // Called under _gate.
    private void ForgetRetries(string root)
    {
        _firstSeen.Remove(root);
        foreach (string path in _retries.Keys.Where(p => PathUtil.IsSameOrUnder(p, root)).ToList())
        {
            _retries.Remove(path);
        }
    }
}
