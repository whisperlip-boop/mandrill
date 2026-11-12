using System.Diagnostics;
using Mandrill.Core.Index;
using Mandrill.Core.IO;

namespace Mandrill.Core.Search;

/// <summary>
/// One search (architecture §8.4 Resolve). The scope is answered from the index where it is complete (one name-range
/// scan plus the overlay), from child blocks where it is partial, and by listing from disk where the index does not reach
/// (no segment, not indexed yet, overlay-only, Dead, Excluded, a reparse point as the scope itself). Every folder's direct
/// children come from exactly one source, so there are no duplicates (AC-2.24). Reparse points and excluded folders below
/// the scope are never entered; hidden/system items that are not shown are neither found nor entered. Stops at cap + 1.
/// Walks and parent-chain lookups use explicit stacks, so deep trees cannot overflow the thread's stack. Single use,
/// worker thread.
/// </summary>
internal sealed class ScopedWalker
{
    private const int BatchSize = 2_000;
    private static readonly long BatchTicks = Stopwatch.Frequency / 10; // 100 ms

    private readonly SearchQuery _query;
    private readonly string _scope;
    private readonly NameMatcher _matcher;
    private readonly IReadOnlyList<DriveSegment> _segments;
    private readonly string[] _excluded;
    private readonly bool _scopeExcluded;
    private readonly IIndexSource _live;
    private readonly Action<IReadOnlyList<SearchHit>> _sink;
    private readonly Action<SearchProgress>? _progress;
    private readonly CancellationToken _ct;
    private readonly IndexEntryBuffer _buffer = new();
    private readonly List<string> _liveRoots = [];
    private List<SearchHit> _batch = [];
    private long _batchStart = Stopwatch.GetTimestamp();
    private int _found;
    private bool _capped;
    private bool _liveUsed;
    private int _accessDenied;
    private bool _partial;
    private SearchProgress _reported = new(false, 0, false);
    private int _candidates;

    // Index side: folder path and "shown" (visible, not excluded, every ancestor up to the scope shown) per item.
    private DriveSegment? _segment;
    private int _scopeItem = -1;
    private readonly Dictionary<int, (string Path, bool Shown)> _dirs = [];
    private readonly Dictionary<string, bool> _overlayDirShown = new(PathUtil.Comparer);
    private Dictionary<string, OverlayItem>? _overlayByPath;
    private readonly Stack<int> _chain = new();
    private readonly Stack<string> _pathChain = new();

    /// <summary>Creates the walker.</summary>
    /// <param name="query">The query (scope already normalized).</param>
    /// <param name="segments">Published segments (snapshot).</param>
    /// <param name="excluded">Exclude list (normalized paths).</param>
    /// <param name="live">Lister for folders the index does not cover.</param>
    /// <param name="sink">Hit batches.</param>
    /// <param name="progress">State flags when they change; null for none.</param>
    /// <param name="ct">Cancellation token.</param>
    public ScopedWalker(SearchQuery query, IReadOnlyList<DriveSegment> segments, IReadOnlyCollection<string> excluded, IIndexSource live, Action<IReadOnlyList<SearchHit>> sink, Action<SearchProgress>? progress, CancellationToken ct)
    {
        _query = query;
        _scope = query.Scope;
        _matcher = NameMatcher.Create(query.Text);
        _segments = segments;
        _live = live;
        _sink = sink;
        _progress = progress;
        _ct = ct;

        // A scope inside an excluded folder is searched live (E-2.11); only exclusions below the scope apply.
        _scopeExcluded = excluded.Any(e => PathUtil.IsSameOrUnder(_scope, e));
        _excluded = [.. excluded.Where(e => !PathUtil.IsSameOrUnder(_scope, e))];
    }

    /// <summary>Runs the search.</summary>
    /// <param name="elapsed">Time since the search started (for the outcome).</param>
    /// <returns>The outcome.</returns>
    public SearchOutcome Run(Stopwatch elapsed)
    {
        if (!_matcher.MatchesNothing)
        {
            Walk();
        }

        Flush();
        return new SearchOutcome(_found, _capped, _liveUsed, _accessDenied, _partial, elapsed.Elapsed);
    }

    private bool Stopped => _capped;

    private void Walk()
    {
        _segment = _scopeExcluded ? null : _segments.FirstOrDefault(s => PathUtil.IsSameOrUnder(_scope, s.Root));
        int item = _segment?.Lookup(_scope) ?? -1;
        // A reparse scope (junction, folder symlink) is recorded but never descended by the builder: list it live.
        if (_segment is null || item < 0 || (_segment.FlagsOf(item) & (IndexFlags.Dead | IndexFlags.Excluded | IndexFlags.Reparse)) != 0)
        {
            WalkLive(_scope);
            return;
        }

        _scopeItem = item;
        WalkIndexed(_segment, item);
        if (!Stopped)
        {
            EmitOverlay(_segment);
        }
    }

    // ---- index ----------------------------------------------------------------------------------------------------

    private void WalkIndexed(DriveSegment segment, int scopeItem)
    {
        var pending = new Stack<int>();
        pending.Push(scopeItem);
        while (pending.Count > 0 && !Stopped)
        {
            _ct.ThrowIfCancellationRequested();
            FlushIfDue();
            int dir = pending.Pop();
            int start = segment.ChildStart(dir);
            int count = segment.ChildCount(dir);
            int end = segment.SubtreeEnd(dir);
            if (end >= 0)
            {
                // Complete: every descendant in one name-range scan.
                if (start >= 0)
                {
                    ScanRange(segment, start, end);
                }
            }
            else if (start >= 0 && start + count <= segment.Count)
            {
                // Partial: the direct children from the child block, then each child folder on its own.
                ScanRange(segment, start, start + count);
                for (int child = start + count - 1; child >= start && !Stopped; child--)
                {
                    if (IsEnterable(segment, child))
                    {
                        pending.Push(child);
                    }
                }
            }
            else
            {
                WalkLive(Dir(segment, dir).Path); // never listed by the builder (or not published yet)
            }
        }
    }

    // The names of [first, end): one prefilter pass for the query; when its decomposed (NFD) form differs, a second pass
    // for names stored that way, skipping names the first pass already saw (one candidate per item).
    private void ScanRange(DriveSegment segment, int first, int end)
    {
        if (!segment.ScanMatches(first, end, _matcher.Literal, _matcher.Comparison, i => Candidate(segment, i)))
        {
            return;
        }

        if (_matcher.AlternateLiteral is { } decomposed)
        {
            segment.ScanMatches(first, end, decomposed, _matcher.Comparison, i =>
                segment.NameOf(i).IndexOf(_matcher.Literal, _matcher.Comparison) >= 0 || Candidate(segment, i));
        }
    }

    // A name-range candidate: visible, not excluded, its folder shown, and the full (glob) match.
    private bool Candidate(DriveSegment segment, int item)
    {
        if ((++_candidates & 1023) == 0)
        {
            _ct.ThrowIfCancellationRequested();
        }

        IndexFlags flags = segment.FlagsOf(item);
        if ((flags & IndexFlags.Excluded) != 0 || !Visible(flags))
        {
            return true;
        }

        ReadOnlySpan<char> name = segment.NameOf(item);
        if (_matcher.IsGlob && !_matcher.IsMatch(name))
        {
            return true;
        }

        var parent = Dir(segment, segment.ParentOf(item));
        if (!parent.Shown)
        {
            return true;
        }

        bool isDir = (flags & IndexFlags.Directory) != 0;
        if (isDir && _excluded.Length > 0 && IsExcludedPath(PathUtil.Combine(parent.Path, name.ToString())))
        {
            return true;
        }

        return Emit(name.ToString(), parent.Path, isDir ? 0 : segment.SizeOf(item), segment.MtimeOf(item), isDir);
    }

    private bool IsEnterable(DriveSegment segment, int item)
    {
        IndexFlags flags = segment.FlagsOf(item);
        return (flags & IndexFlags.Directory) != 0
            && (flags & (IndexFlags.Reparse | IndexFlags.Excluded | IndexFlags.Dead)) == 0
            && Visible(flags)
            && Dir(segment, item).Shown;
    }

    // Path and "shown" of a folder item at or under the scope; the parent chain is cached, one string per folder. Walks
    // up to the nearest known folder, then fills the chain downwards (no recursion: folder depth is unbounded).
    private (string Path, bool Shown) Dir(DriveSegment segment, int item)
    {
        if (item == _scopeItem)
        {
            return (_scope, true);
        }

        if (_dirs.TryGetValue(item, out var known))
        {
            return known;
        }

        _chain.Clear();
        (string Path, bool Shown) parent;
        int current = item;
        while (true)
        {
            _chain.Push(current);
            current = segment.ParentOf(current);
            if (current == _scopeItem)
            {
                parent = (_scope, true);
                break;
            }

            if (current < 0)
            {
                parent = (segment.Root, false); // not under the scope: never shown (cannot happen for scope candidates)
                break;
            }

            if (_dirs.TryGetValue(current, out parent))
            {
                break;
            }
        }

        while (_chain.Count > 0)
        {
            int folder = _chain.Pop();
            string path = PathUtil.Combine(parent.Path, segment.NameOf(folder).ToString());
            IndexFlags flags = segment.FlagsOf(folder);
            bool shown = parent.Shown
                && Visible(flags)
                && (flags & (IndexFlags.Excluded | IndexFlags.Dead | IndexFlags.Reparse)) == 0
                && !IsExcludedPath(path);
            parent = (path, shown);
            _dirs[folder] = parent;
        }

        return parent;
    }

    // Items added since the build (§8.3 overlay) under the scope, except below folders that were listed live (those
    // listings already include them).
    private void EmitOverlay(DriveSegment segment)
    {
        IReadOnlyList<OverlayItem> overlay = segment.OverlayItems();
        if (overlay.Count == 0)
        {
            return;
        }

        _overlayByPath = new Dictionary<string, OverlayItem>(PathUtil.Comparer);
        foreach (var o in overlay)
        {
            _overlayByPath[o.Path] = o;
        }

        foreach (var o in overlay)
        {
            _ct.ThrowIfCancellationRequested();
            if (Stopped)
            {
                return;
            }

            // Live roots are few (an overlay exists on built segments, where only unreadable folders are listed live).
            if (PathUtil.PathEquals(o.Path, _scope) || !PathUtil.IsSameOrUnder(o.Path, _scope) || _liveRoots.Any(r => PathUtil.IsSameOrUnder(o.Path, r)))
            {
                continue;
            }

            string name = PathUtil.LeafName(o.Path);
            bool isDir = (o.Flags & IndexFlags.Directory) != 0;
            if ((o.Flags & IndexFlags.Excluded) != 0 || !Visible(o.Flags) || !_matcher.IsMatch(name)
                || PathUtil.Parent(o.Path) is not { } parent || !OverlayDirShown(segment, parent)
                || (isDir && IsExcludedPath(o.Path)))
            {
                continue;
            }

            Emit(name, parent, isDir ? 0 : o.Size, o.Mtime, isDir);
        }
    }

    // Whether a folder of an overlay item is shown: indexed folders answer from Dir; overlay-only folders from their own
    // flags and their parent. Walks up to a known answer, then fills downwards (no recursion).
    private bool OverlayDirShown(DriveSegment segment, string dir)
    {
        _pathChain.Clear();
        bool shown;
        string? current = dir;
        while (true)
        {
            if (current is null || !PathUtil.IsSameOrUnder(current, _scope))
            {
                shown = false; // above the scope (cannot happen for items under it)
                break;
            }

            if (PathUtil.PathEquals(current, _scope))
            {
                shown = true;
                break;
            }

            if (_overlayDirShown.TryGetValue(current, out shown))
            {
                break;
            }

            int item = segment.Lookup(current);
            if (item >= 0)
            {
                shown = Dir(segment, item).Shown;
                _overlayDirShown[current] = shown;
                break;
            }

            _pathChain.Push(current);
            current = PathUtil.Parent(current);
        }

        while (_pathChain.Count > 0)
        {
            string folder = _pathChain.Pop();
            IndexFlags flags = _overlayByPath!.TryGetValue(folder, out var o) ? o.Flags : IndexFlags.Directory;
            shown = shown
                && Visible(flags)
                && (flags & (IndexFlags.Excluded | IndexFlags.Reparse)) == 0
                && !IsExcludedPath(folder);
            _overlayDirShown[folder] = shown;
        }

        return shown;
    }

    // ---- live -----------------------------------------------------------------------------------------------------

    // Lists a folder the index does not cover and everything below it (explicit stack: deep trees cannot overflow).
    private void WalkLive(string root)
    {
        _liveUsed = true;
        _liveRoots.Add(root);
        ReportProgress();
        var pending = new Stack<string>();
        pending.Push(root);
        var subdirs = new List<string>();
        while (pending.Count > 0 && !Stopped)
        {
            _ct.ThrowIfCancellationRequested();
            FlushIfDue();
            string dir = pending.Pop();
            switch (_live.List(dir, _buffer, _ct))
            {
                case ListOutcome.AccessDenied:
                    _accessDenied++; // reported with the next flush, not per folder
                    continue;
                case ListOutcome.NotFound:
                case ListOutcome.Unavailable:
                    _partial = true;
                    ReportProgress();
                    continue;
            }

            subdirs.Clear();
            for (int i = 0; i < _buffer.Count && !Stopped; i++)
            {
                IndexFlags flags = _buffer.FlagsAt(i);
                if (!Visible(flags))
                {
                    continue; // not shown, not entered
                }

                ReadOnlySpan<char> name = _buffer.NameAt(i);
                bool isDir = (flags & IndexFlags.Directory) != 0;
                string? path = null;
                if (isDir && _excluded.Length > 0)
                {
                    path = PathUtil.Combine(dir, name.ToString());
                    if (IsExcludedPath(path))
                    {
                        continue;
                    }
                }

                if (_matcher.IsMatch(name) && !Emit(name.ToString(), dir, isDir ? 0 : _buffer.SizeAt(i), _buffer.MtimeAt(i), isDir))
                {
                    break;
                }

                if (isDir && (flags & IndexFlags.Reparse) == 0)
                {
                    subdirs.Add(path ?? PathUtil.Combine(dir, name.ToString()));
                }
            }

            for (int i = subdirs.Count - 1; i >= 0; i--)
            {
                pending.Push(subdirs[i]);
            }
        }
    }

    // ---- shared ---------------------------------------------------------------------------------------------------

    private bool Visible(IndexFlags flags)
        => (flags & IndexFlags.Hidden) == 0 || (_query.ShowHidden && ((flags & IndexFlags.System) == 0 || _query.ShowSystem));

    private bool IsExcludedPath(string path)
    {
        foreach (string e in _excluded)
        {
            if (PathUtil.IsSameOrUnder(path, e))
            {
                return true;
            }
        }

        return false;
    }

    // Delivers a hit; false (and Capped) when the cap is already full: the walk stops at cap + 1 (AC-2.18).
    private bool Emit(string name, string directory, long size, uint mtime, bool isDirectory)
    {
        if (_found >= _query.Cap)
        {
            _capped = true;
            return false;
        }

        _batch.Add(new SearchHit(name, directory, size, DateTime.UnixEpoch.AddSeconds(mtime), isDirectory));
        _found++;
        if (_batch.Count >= BatchSize)
        {
            Flush();
        }
        else
        {
            FlushIfDue();
        }

        return true;
    }

    // Hits found during a long stretch without new ones (a slow live listing) still arrive within ~100 ms; so does a
    // grown access-denied count.
    private void FlushIfDue()
    {
        if (Stopwatch.GetTimestamp() - _batchStart >= BatchTicks && (_batch.Count > 0 || _reported.AccessDenied != _accessDenied))
        {
            Flush();
        }
    }

    private void Flush()
    {
        if (_batch.Count > 0)
        {
            var batch = _batch;
            _batch = [];
            _sink(batch);
        }

        ReportProgress();
        _batchStart = Stopwatch.GetTimestamp();
    }

    // Sends the state flags when they changed since the last report (G1: the strip shows during a long live listing).
    private void ReportProgress()
    {
        var now = new SearchProgress(_liveUsed, _accessDenied, _partial);
        if (_progress is not null && now != _reported)
        {
            _reported = now;
            _progress(now);
        }
    }
}
