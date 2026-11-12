using System.Collections.Concurrent;
using Mandrill.Core.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Core.Shell;

/// <summary>
/// Shell type names ("Text Document", "File folder") per extension id, resolved with SHGetFileInfo(SHGFI_TYPENAME) on the
/// icons STA (AC-1.14). <see cref="Get(int)"/> never blocks and fits <c>EntrySorter</c>'s type-name parameter; a Type
/// sort awaits <see cref="EnsureResolvedAsync"/> first so pending names do not sort as "". A lookup that throws or times
/// out shows the extension text and is retried after <see cref="IconService.FailureCooldown"/> (or at once after the STA
/// was replaced), at most <see cref="IconService.MaxAttempts"/> times; then the extension text is final.
/// </summary>
public sealed class TypeNameCache : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly ShellSta _sta;
    private readonly ExtensionTable _extensions;
    private readonly TimeProvider _time;
    private readonly Func<bool, string, string?> _lookup;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, string> _names = new();
    private readonly ConcurrentDictionary<int, Failure> _failures = new();
    private readonly Lock _gate = new();
    private readonly HashSet<int> _pending = [];
    private readonly CancellationTokenSource _disposed = new();
    private bool _pumping;

    /// <summary>Creates the cache.</summary>
    /// <param name="sta">The "icons" STA.</param>
    /// <param name="extensions">Extension table the ids come from (default <see cref="ExtensionTable.Shared"/>).</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public TypeNameCache(ShellSta sta, ExtensionTable? extensions = null, ILoggerFactory? loggerFactory = null)
        : this(sta, extensions, loggerFactory, TimeProvider.System)
    {
    }

    /// <summary>Test seam: clock, and the STA-side lookup (isDirectory, extension) with its timeout.</summary>
    internal TypeNameCache(
        ShellSta sta,
        ExtensionTable? extensions,
        ILoggerFactory? loggerFactory,
        TimeProvider timeProvider,
        Func<bool, string, string?>? lookup = null,
        TimeSpan? timeout = null)
    {
        _sta = sta ?? throw new ArgumentNullException(nameof(sta));
        _extensions = extensions ?? ExtensionTable.Shared;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TypeNameCache>();
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _lookup = lookup ?? ShellIconReader.TypeName;
        _timeout = timeout ?? DefaultTimeout;
        _sta.Replaced += OnStaReplaced;
    }

    /// <summary>Raised on a pool thread after queued names were resolved.</summary>
    public event Action? TypeNamesReady;

    /// <summary>
    /// Type name for an extension id (<see cref="IconKey.FolderExtId"/> for folders); "" while pending, the extension text
    /// while a failed lookup waits for its retry.
    /// </summary>
    /// <param name="extId">Extension id.</param>
    /// <returns>The name, "" (queued), or the extension text (failed).</returns>
    public string Get(int extId)
    {
        if (_names.TryGetValue(extId, out string? name))
        {
            return name;
        }

        if (_failures.TryGetValue(extId, out var failure))
        {
            if (_time.GetUtcNow() >= failure.RetryAt)
            {
                Queue(extId);
            }

            return ExtensionText(extId);
        }

        Queue(extId);
        return string.Empty;
    }

    /// <summary>Type name of an entry; "" while pending.</summary>
    /// <param name="entry">Listed entry.</param>
    /// <returns>The name, or "" (queued).</returns>
    public string Get(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Get(entry.IsDirectory ? IconKey.FolderExtId : entry.ExtId);
    }

    /// <summary>
    /// Resolves every id in <paramref name="extIds"/> that is neither cached nor waiting for a retry (one STA call).
    /// </summary>
    /// <param name="extIds">Extension ids.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the lookups finished (failed ones read as the extension text).</returns>
    public async Task EnsureResolvedAsync(IEnumerable<int> extIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(extIds);
        var now = _time.GetUtcNow();
        int[] missing = extIds
            .Where(id => !_names.ContainsKey(id) && !(_failures.TryGetValue(id, out var f) && now < f.RetryAt))
            .Distinct()
            .ToArray();
        if (missing.Length > 0)
        {
            await ResolveAsync(missing, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Stops the background queue (the STA is owned by the caller).</summary>
    public void Dispose()
    {
        _sta.Replaced -= OnStaReplaced;
        // Cancel only: a pump may still read the token, and this source has no timer to release.
        _disposed.Cancel();
    }

    // Failures may have been caused by the stuck worker: make them due now (attempt counts are kept).
    private void OnStaReplaced()
    {
        foreach (var (id, failure) in _failures)
        {
            _failures.TryUpdate(id, failure with { RetryAt = DateTimeOffset.MinValue }, failure);
        }
    }

    private void Queue(int extId)
    {
        lock (_gate)
        {
            if (!_pending.Add(extId) || _pumping)
            {
                return;
            }

            _pumping = true;
        }

        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        try
        {
            await PumpLoopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fire-and-forget: an escaping exception would be unobserved and leave the queue stalled with _pumping set.
            _logger.LogError(ex, "Type-name queue stopped unexpectedly");
            lock (_gate)
            {
                _pumping = false;
            }
        }
    }

    private async Task PumpLoopAsync()
    {
        while (true)
        {
            int[] batch;
            lock (_gate)
            {
                if (_pending.Count == 0 || _disposed.IsCancellationRequested)
                {
                    _pumping = false;
                    return;
                }

                batch = [.. _pending];
                _pending.Clear();
            }

            try
            {
                await ResolveAsync(batch, _disposed.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _pumping = false;
                }

                return;
            }
            catch (Exception ex)
            {
                // One bad batch must not stop the queue (its ids are queued again by the next Get).
                _logger.LogError(ex, "Type-name queue failed for {Count} extensions", batch.Length);
            }

            try
            {
                TypeNamesReady?.Invoke();
            }
            catch (Exception ex)
            {
                // A subscriber's bug must not stop the queue.
                _logger.LogError(ex, "TypeNamesReady handler failed");
            }
        }
    }

    private async Task ResolveAsync(int[] ids, CancellationToken ct)
    {
        var requests = ids.Select(id => (Id: id, Ext: ExtensionText(id))).ToArray();
        string?[] names;
        try
        {
            names = await _sta.InvokeAsync(
                () => requests.Select(r => _lookup(r.Id == IconKey.FolderExtId, r.Ext)).ToArray(),
                _timeout,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Timeout (STA replaced), COM failure or a disposed STA: extension text for now, retried after the cooldown.
            _logger.LogWarning("Type names for {Count} extensions not resolved: {Error}", requests.Length, ex.Message);
            foreach (var r in requests)
            {
                RecordFailure(r.Id, r.Ext);
            }

            return;
        }

        for (int i = 0; i < requests.Length; i++)
        {
            // A shell that has no name for the type is final: the extension text sorts and displays deterministically.
            _names[requests[i].Id] = names[i] ?? requests[i].Ext;
            _failures.TryRemove(requests[i].Id, out _);
        }
    }

    private void RecordFailure(int id, string fallback)
    {
        var failure = _failures.AddOrUpdate(
            id,
            _ => new Failure(1, _time.GetUtcNow() + IconService.FailureCooldown),
            (_, f) => new Failure(f.Attempts + 1, _time.GetUtcNow() + IconService.FailureCooldown));
        if (failure.Attempts >= IconService.MaxAttempts)
        {
            _names[id] = fallback;
            _failures.TryRemove(id, out _);
            _logger.LogWarning("Type name for {Extension}: {Attempts} failed lookups, extension text kept for this session", fallback, failure.Attempts);
        }
    }

    private string ExtensionText(int id)
    {
        if (id == IconKey.FolderExtId)
        {
            return string.Empty;
        }

        try
        {
            return _extensions.ExtensionOf(id);
        }
        catch (ArgumentOutOfRangeException)
        {
            _logger.LogWarning("Type name requested for unknown extension id {Id}", id);
            return string.Empty;
        }
    }

    private sealed record Failure(int Attempts, DateTimeOffset RetryAt);
}
