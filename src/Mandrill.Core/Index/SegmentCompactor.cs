using Mandrill.Core.IO;
using Microsoft.Extensions.Logging;

namespace Mandrill.Core.Index;

/// <summary>
/// Rebuilds a segment in memory from its base items minus the dead ones plus the overlay (architecture §8.3 compaction),
/// through the normal <see cref="SegmentBuilder"/>, so the result has the same children-block DFS layout as a fresh
/// build. Subtrees that were incomplete stay incomplete. Peak memory is about twice that drive for the duration. The
/// caller swaps the result in. Index thread.
/// </summary>
public static class SegmentCompactor
{
    /// <summary>Builds the compacted copy of <paramref name="segment"/>.</summary>
    /// <param name="segment">The segment to compact (unchanged).</param>
    /// <param name="options">Build options (exclude list; no throttle needed, no I/O happens).</param>
    /// <param name="logger">Logger; null disables logging.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new segment and its build result.</returns>
    public static (DriveSegment Segment, IndexBuildResult Result) Compact(DriveSegment segment, IndexBuildOptions? options, ILogger? logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var compacted = new DriveSegment(segment.Root, segment.VolumeSerial);
        var builder = new SegmentBuilder(new MergedSource(segment), options ?? new IndexBuildOptions { ThrottleSleep = TimeSpan.Zero }, logger);
        IndexBuildResult result = builder.Build(compacted, ct);
        return (compacted, result);
    }

    // Lists a directory from the old segment (live base items) plus its overlay children.
    private sealed class MergedSource(DriveSegment segment) : IIndexSource
    {
        public ListOutcome List(string directory, IndexEntryBuffer buffer, CancellationToken ct)
        {
            buffer.Clear();
            ct.ThrowIfCancellationRequested();
            int item = segment.Lookup(directory);
            if (item >= 0)
            {
                int start = segment.ChildStart(item);
                if (start < 0)
                {
                    return ListOutcome.Unavailable; // never listed: keep it incomplete
                }

                for (int child = start; child < start + segment.ChildCount(item); child++)
                {
                    IndexFlags flags = segment.FlagsOf(child);
                    if ((flags & IndexFlags.Dead) == 0)
                    {
                        buffer.Add(segment.NameOf(child), segment.SizeOf(child), segment.MtimeOf(child), flags);
                    }
                }
            }

            foreach (OverlayItem added in segment.OverlayChildren(directory))
            {
                // A name can be in both only if a change was applied while its directory was still being built: the base
                // item wins, so a child block never holds the same name twice.
                int existing = segment.Lookup(added.Path);
                if (existing < 0 || (segment.FlagsOf(existing) & IndexFlags.Dead) != 0)
                {
                    buffer.Add(PathUtil.LeafName(added.Path), added.Size, added.Mtime, added.Flags);
                }
            }

            return ListOutcome.Ok;
        }
    }
}
