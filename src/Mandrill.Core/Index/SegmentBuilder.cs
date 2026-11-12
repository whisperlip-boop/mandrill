using System.Diagnostics;
using Mandrill.Core.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Core.Index;

/// <summary>Options of one index build.</summary>
public sealed class IndexBuildOptions
{
    /// <summary>Folders recorded with <see cref="IndexFlags.Excluded"/> and not descended (AC-2.10). Normalized paths.</summary>
    public IReadOnlyCollection<string> Exclude { get; init; } = [];

    /// <summary>Entries between two throttle checkpoints (§8.3: 2,000).</summary>
    public int CheckpointEvery { get; init; } = 2_000;

    /// <summary>Sleep at each checkpoint (§8.3: 1 ms; zero in tests).</summary>
    public TimeSpan ThrottleSleep { get; init; } = TimeSpan.FromMilliseconds(1);

    /// <summary>When set and not signaled, the build waits at the next checkpoint (a search is running, §8.3).</summary>
    public WaitHandle? Resume { get; init; }

    /// <summary>Called at every checkpoint with the published item count (status, partial saves). Index thread.</summary>
    public Action<DriveSegment>? Checkpoint { get; init; }
}

/// <summary>Result of one build.</summary>
/// <param name="Items">Items in the segment.</param>
/// <param name="Directories">Directories listed.</param>
/// <param name="AccessDenied">Directories skipped because access was denied (AC-2.12).</param>
/// <param name="Unavailable">Directories that could not be listed (not ready, I/O error); their subtrees stay incomplete.</param>
/// <param name="Complete">True when the whole drive was indexed.</param>
/// <param name="Elapsed">Build time.</param>
public sealed record IndexBuildResult(int Items, int Directories, int AccessDenied, int Unavailable, bool Complete, TimeSpan Elapsed);

/// <summary>
/// Builds a <see cref="DriveSegment"/> in "children-block DFS" order (architecture §8.1): a directory's children are
/// appended as one block sorted OrdinalIgnoreCase, then each child directory is visited in order, so every subtree is
/// one contiguous range. An explicit stack keeps deep trees off the call stack. Reparse points are recorded and never
/// descended (AC-2.2, E-2.4); excluded folders are recorded and not descended; access-denied folders are counted and
/// logged once in aggregate (AC-2.12). A cancelled build leaves a consistent partial segment (unfinished subtrees keep
/// SubtreeEnd = -1). Runs on the index thread; readers see each block once it is published.
/// </summary>
public sealed class SegmentBuilder
{
    private readonly IIndexSource _source;
    private readonly IndexBuildOptions _options;
    private readonly ILogger _logger;
    private readonly HashSet<string> _exclude;

    /// <summary>Creates a builder.</summary>
    /// <param name="source">Directory source.</param>
    /// <param name="options">Build options; null for defaults.</param>
    /// <param name="logger">Logger; null disables logging.</param>
    public SegmentBuilder(IIndexSource source, IndexBuildOptions? options = null, ILogger? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = options ?? new IndexBuildOptions();
        _logger = logger ?? NullLogger.Instance;
        _exclude = new HashSet<string>(_options.Exclude, PathUtil.Comparer);
    }

    /// <summary>Builds the whole drive into <paramref name="segment"/>, which must contain only its root item.</summary>
    /// <param name="segment">A new segment (<c>new DriveSegment(root, serial)</c>); readable while it fills.</param>
    /// <param name="ct">Cancellation token; a cancelled build leaves a consistent partial segment.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentException">The segment already has items.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public IndexBuildResult Build(DriveSegment segment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Count != 1)
        {
            throw new ArgumentException("The segment must contain only its root.", nameof(segment));
        }

        var sw = Stopwatch.StartNew();
        var buffer = new IndexEntryBuffer();
        WaitHandle[]? resumeOrCancel = _options.Resume is { } resume ? [resume, ct.WaitHandle] : null;
        int unavailable = 0;
        var stack = new Stack<Frame>();
        int directories = 0, denied = 0, sinceCheckpoint = 0;
        bool complete = true;

        // Lists a directory and appends its child block; returns false when its subtree stays incomplete.
        bool Visit(int item, string path)
        {
            ListOutcome outcome = _source.List(path, buffer, ct);
            switch (outcome)
            {
                case ListOutcome.Unavailable:
                    unavailable++;
                    return false;
                case ListOutcome.AccessDenied:
                    denied++;
                    _logger.LogDebug("Index: access denied to {Path}", path);
                    break;
            }

            directories++;
            buffer.Sort();
            int start = segment.Pending;
            for (int i = 0; i < buffer.Count; i++)
            {
                IndexFlags flags = buffer.FlagsAt(i);
                if ((flags & IndexFlags.Directory) != 0 && (flags & IndexFlags.Reparse) == 0 && _exclude.Count > 0
                    && _exclude.Contains(PathUtil.Combine(path, buffer.NameAt(i).ToString())))
                {
                    flags |= IndexFlags.Excluded;
                }

                segment.AddItem(buffer.NameAt(i), item, buffer.SizeAt(i), buffer.MtimeAt(i), flags);
            }

            segment.SetChildBlock(item, start, buffer.Count);
            segment.Publish();
            sinceCheckpoint += buffer.Count + 1;
            if (sinceCheckpoint >= _options.CheckpointEvery)
            {
                sinceCheckpoint = 0;
                Checkpoint(segment, resumeOrCancel, ct);
            }

            return true;
        }

        if (Visit(0, segment.Root))
        {
            stack.Push(new Frame(0, segment.Root, segment.ChildStart(0), Complete: true));
        }
        else
        {
            complete = false;
        }

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            Frame frame = stack.Pop();
            int end = segment.ChildStart(frame.Item) + segment.ChildCount(frame.Item);
            int next = frame.Next;
            bool frameComplete = frame.Complete;
            bool descended = false;
            while (next < end)
            {
                int child = next++;
                IndexFlags flags = segment.FlagsOf(child);
                if ((flags & IndexFlags.Directory) == 0)
                {
                    continue;
                }

                if ((flags & (IndexFlags.Reparse | IndexFlags.Excluded)) != 0)
                {
                    // Recorded, never descended: an empty, complete subtree.
                    segment.SetChildBlock(child, segment.Pending, 0);
                    segment.SetSubtreeEnd(child, segment.Pending);
                    continue;
                }

                string childPath = PathUtil.Combine(frame.Path, segment.NameOf(child).ToString());
                if (!Visit(child, childPath))
                {
                    frameComplete = false; // its own SubtreeEnd stays -1
                    continue;
                }

                // Descend: remember where this directory continues, then process the child first.
                stack.Push(frame with { Next = next, Complete = frameComplete });
                stack.Push(new Frame(child, childPath, segment.ChildStart(child), Complete: true));
                descended = true;
                break;
            }

            if (descended)
            {
                continue;
            }

            // All children done: the subtree ends here, if every child subtree is complete.
            if (frameComplete)
            {
                segment.SetSubtreeEnd(frame.Item, segment.Pending);
            }
            else if (stack.Count > 0)
            {
                Frame parent = stack.Pop();
                stack.Push(parent with { Complete = false });
            }
            else
            {
                complete = false;
            }
        }

        segment.Publish();
        complete &= segment.IsComplete;
        if (denied > 0)
        {
            _logger.LogWarning("Index {Root}: {Count} folders skipped (access denied)", segment.Root, denied);
        }

        if (unavailable > 0)
        {
            _logger.LogWarning("Index {Root}: {Count} folders could not be listed; they stay incomplete (searched live)", segment.Root, unavailable);
        }

        var result = new IndexBuildResult(segment.Count, directories, denied, unavailable, complete, sw.Elapsed);
        _logger.LogInformation(
            "Index {Root}: {Items} items, {Directories} folders in {Ms} ms ({Bytes} bytes, complete {Complete})",
            segment.Root, result.Items, result.Directories, (long)sw.Elapsed.TotalMilliseconds, segment.ByteSize, result.Complete);
        return result;
    }

    private void Checkpoint(DriveSegment segment, WaitHandle[]? resumeOrCancel, CancellationToken ct)
    {
        _options.Checkpoint?.Invoke(segment);
        if (_options.ThrottleSleep > TimeSpan.Zero)
        {
            Thread.Sleep(_options.ThrottleSleep);
        }

        if (resumeOrCancel is not null)
        {
            WaitHandle.WaitAny(resumeOrCancel);
        }

        ct.ThrowIfCancellationRequested();
    }

    // A directory being walked: its item, path, the next child to look at, and whether all subtrees so far are complete.
    private readonly record struct Frame(int Item, string Path, int Next, bool Complete);
}
