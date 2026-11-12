using System.Diagnostics;
using Mandrill.Core.Index;
using Mandrill.Core.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Core.Search;

/// <summary>
/// Filename search (architecture §3 Core.Search, §8.4): each search runs on its own long-running worker over the index
/// snapshot (<see cref="ISearchIndex.Segments"/>), holding <see cref="ISearchIndex.PauseForSearch"/> so the builder does
/// not compete for the disk, and lists from disk where the index does not reach. Searches are independent (E-2.12).
/// </summary>
public sealed class SearchService : ISearchService
{
    private readonly ISearchIndex _index;
    private readonly Func<IReadOnlyCollection<string>> _exclude;
    private readonly Func<ILogger, IIndexSource> _liveSource;
    private readonly ILogger _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="index">The index (an empty one before the index service runs: everything is listed live).</param>
    /// <param name="exclude">Current exclude list (settings), read once per search; normalized folder paths.</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public SearchService(ISearchIndex index, Func<IReadOnlyCollection<string>> exclude, ILoggerFactory? loggerFactory = null)
        : this(index, exclude, logger => new Win32IndexSource(logger), loggerFactory)
    {
    }

    /// <summary>Test seam: the lister for folders the index does not cover.</summary>
    internal SearchService(ISearchIndex index, Func<IReadOnlyCollection<string>> exclude, Func<ILogger, IIndexSource> liveSource, ILoggerFactory? loggerFactory)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _exclude = exclude ?? throw new ArgumentNullException(nameof(exclude));
        _liveSource = liveSource ?? throw new ArgumentNullException(nameof(liveSource));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SearchService>();
    }

    /// <inheritdoc/>
    public Task<SearchOutcome> SearchAsync(SearchQuery query, Action<IReadOnlyList<SearchHit>> sink, Action<SearchProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(sink);
        if (!PathUtil.TryNormalize(query.Scope, out string scope, out _))
        {
            throw new ArgumentException("The scope is not a valid folder path.", nameof(query));
        }

        if (string.IsNullOrEmpty(query.Text))
        {
            throw new ArgumentException("The query text is empty.", nameof(query));
        }

        if (query.Cap <= 0)
        {
            throw new ArgumentException("The cap must be positive.", nameof(query));
        }

        var normalized = query with { Scope = scope };
        return Task.Factory.StartNew(() => Run(normalized, sink, progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private SearchOutcome Run(SearchQuery query, Action<IReadOnlyList<SearchHit>> sink, Action<SearchProgress>? progress, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        using IDisposable pause = _index.PauseForSearch();
        try
        {
            IReadOnlyCollection<string> exclude = Normalized(_exclude());
            var walker = new ScopedWalker(query, _index.Segments, exclude, _liveSource(_logger), sink, progress, ct);
            SearchOutcome outcome = walker.Run(elapsed);
            _logger.LogInformation(
                "Search done: {Found} found, capped {Capped}, live {Live}, access denied {Denied}, partial {Partial}, {Ms} ms",
                outcome.Found, outcome.Capped, outcome.LiveUsed, outcome.AccessDenied, outcome.Partial, (long)outcome.Elapsed.TotalMilliseconds);
            _logger.LogDebug("Search {Scope} for '{Text}'", query.Scope, query.Text);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Search cancelled after {Ms} ms", elapsed.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search in {Scope} failed", query.Scope);
            throw;
        }
    }

    // Settings hold what the user typed; compare like the index does (normalized, invalid entries dropped).
    private static string[] Normalized(IReadOnlyCollection<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return [];
        }

        var result = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            if (PathUtil.TryNormalize(path, out string normalized, out _))
            {
                result.Add(normalized);
            }
        }

        return [.. result];
    }
}
