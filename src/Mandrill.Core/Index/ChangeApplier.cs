using Mandrill.Core.IO;

namespace Mandrill.Core.Index;

/// <summary>
/// Applies dirty paths to a segment (architecture §8.3): each path is stat'ed, never followed; present → upsert, and a
/// directory the segment did not know (or knew as dead) has its subtree listed into the overlay; absent → delete. Events
/// are hints and the stat is the truth, so duplicates do not matter; parents are applied before children, so a moved-in
/// folder is listed whole even when one of its children has its own event. Paths under an ignored or excluded folder are
/// dropped. Known limit: a base folder deleted and recreated inside one 2 s batch keeps its old children live until the
/// next verify. Index thread only.
/// </summary>
internal sealed class ChangeApplier(IIndexSource source, Func<string, PathStat> stat, WaitHandle? resume = null)
{
    // Like the builder's checkpoint (§8.3): a search pauses a long overlay listing of a moved-in tree.
    private const int PauseCheckEvery = 64;

    private readonly IndexEntryBuffer _buffer = new();
    private int _sincePauseCheck;

    /// <summary>Applies <paramref name="paths"/> to <paramref name="segment"/>.</summary>
    /// <param name="segment">The published segment of the drive.</param>
    /// <param name="paths">Dirty paths (raw watcher paths; normalized here).</param>
    /// <param name="ignored">Normalized folders whose changes never reach the index (the data folder).</param>
    /// <param name="exclude">Normalized excluded folders: recorded, not descended (AC-2.10).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="unknown">Receives the paths (as given) whose stat failed, for a retry; null drops them.</param>
    /// <returns>True when the segment changed.</returns>
    public bool Apply(DriveSegment segment, IEnumerable<string> paths, IReadOnlyCollection<string> ignored, IReadOnlyCollection<string> exclude, CancellationToken ct, List<string>? unknown = null)
    {
        bool changed = false;
        foreach (string raw in paths.OrderBy(p => p.Length))
        {
            PauseCheck(ct);
            if (!PathUtil.TryNormalize(raw, out string path, out _) || PathUtil.IsDriveRoot(path) || !PathUtil.IsSameOrUnder(path, segment.Root)
                || Under(path, ignored) || UnderStrictly(path, exclude))
            {
                continue;
            }

            PathStat found = stat(path);
            switch (found.State)
            {
                case PathState.Missing:
                    changed |= segment.Delete(path);
                    break;
                case PathState.Present:
                    Upsert(segment, path, found, exclude, ct);
                    changed = true;
                    break;
                default:
                    unknown?.Add(raw); // sharing violation while written, an access hiccup: try again next tick
                    break;
            }
        }

        return changed;
    }

    /// <summary>True when <paramref name="path"/> is a live item of the segment: in the base and not dead, or in the overlay.</summary>
    internal static bool IsKnown(DriveSegment segment, string path)
    {
        int item = segment.Lookup(path);
        if (item >= 0)
        {
            return item == 0 || (segment.FlagsOf(item) & IndexFlags.Dead) == 0;
        }

        return segment.OverlayContains(path);
    }

    private static bool Under(string path, IReadOnlyCollection<string> roots)
    {
        foreach (string root in roots)
        {
            if (PathUtil.IsSameOrUnder(path, root))
            {
                return true;
            }
        }

        return false;
    }

    // An excluded folder itself is still recorded (with its mark); only what lies below it is dropped.
    private static bool UnderStrictly(string path, IReadOnlyCollection<string> roots)
    {
        foreach (string root in roots)
        {
            if (PathUtil.IsSameOrUnder(path, root) && !PathUtil.PathEquals(path, root))
            {
                return true;
            }
        }

        return false;
    }

    // Same rule as the builder: only a non-reparse directory on the list is marked.
    private static IndexFlags MarkExcluded(string path, IndexFlags flags, IReadOnlyCollection<string> exclude)
        => (flags & IndexFlags.Directory) != 0 && (flags & IndexFlags.Reparse) == 0 && exclude.Any(e => PathUtil.PathEquals(e, path))
            ? flags | IndexFlags.Excluded
            : flags;

    private static bool Descends(IndexFlags flags)
        => (flags & IndexFlags.Directory) != 0 && (flags & (IndexFlags.Reparse | IndexFlags.Excluded)) == 0;

    private void Upsert(DriveSegment segment, string path, PathStat found, IReadOnlyCollection<string> exclude, CancellationToken ct)
    {
        // Decided before the upsert: a folder the segment did not hold (moved in, created, recreated) is listed whole.
        bool wasKnown = IsKnown(segment, path);
        IndexFlags flags = MarkExcluded(path, found.Flags, exclude);
        segment.Upsert(path, found.Size, found.Mtime, flags);
        if (!wasKnown && Descends(flags))
        {
            ScanIntoOverlay(segment, path, exclude, ct);
        }
    }

    // Explicit stack, so a deep new tree stays off the call stack. Listing failures leave that folder as it is.
    private void ScanIntoOverlay(DriveSegment segment, string directory, IReadOnlyCollection<string> exclude, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(directory);
        while (stack.Count > 0)
        {
            PauseCheck(ct);
            string dir = stack.Pop();
            if (source.List(dir, _buffer, ct) != ListOutcome.Ok)
            {
                continue;
            }

            for (int i = 0; i < _buffer.Count; i++)
            {
                string child = PathUtil.Combine(dir, _buffer.NameAt(i).ToString());
                IndexFlags flags = MarkExcluded(child, _buffer.FlagsAt(i), exclude);
                segment.Upsert(child, _buffer.SizeAt(i), _buffer.MtimeAt(i), flags);
                if (Descends(flags))
                {
                    stack.Push(child);
                }
            }
        }
    }

    private void PauseCheck(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (resume is null || ++_sincePauseCheck < PauseCheckEvery)
        {
            return;
        }

        _sincePauseCheck = 0;
        WaitHandle.WaitAny([resume, ct.WaitHandle]);
        ct.ThrowIfCancellationRequested();
    }
}
