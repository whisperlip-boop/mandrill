using Microsoft.Extensions.Logging;

namespace Mandrill.Core.State;

/// <summary>
/// <see cref="JsonStore{T}"/> for one state document plus normalization after load (invalid values → defaults, logged).
/// Call from worker threads only.
/// </summary>
/// <typeparam name="T">Settings, session or favorites document.</typeparam>
public sealed class StateStore<T>
    where T : class, IStateDocument<T>
{
    private readonly JsonStore<T> _store;
    private readonly ILogger _logger;

    /// <summary>Creates a store for one file (for example <c>AppPaths.SessionFile</c>).</summary>
    /// <param name="path">Target file.</param>
    /// <param name="logger">Logger for I/O, recovery and normalization events.</param>
    public StateStore(string path, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _store = new JsonStore<T>(path, T.TypeInfo, T.CreateDefault, T.CurrentVersion, logger, T.Migrate);
    }

    /// <summary>Path of the backing file.</summary>
    public string FilePath => _store.FilePath;

    /// <summary>Loads and normalizes the document. Missing → defaults; corrupt → .bak + defaults.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A normalized document.</returns>
    public async Task<T> LoadAsync(CancellationToken ct) => Normalize(await _store.LoadAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Reads and normalizes the document again while the app runs (see <see cref="JsonStore{T}.TryReloadAsync"/>): no
    /// defaults, no .bak, no temp cleanup.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="missingIsExpected">True for a check without a save behind it: a missing file is logged at Debug.</param>
    /// <returns>A normalized document, or null when it cannot be read or parsed (keep the current values).</returns>
    public async Task<T?> TryReloadAsync(CancellationToken ct, bool missingIsExpected = false)
        => await _store.TryReloadAsync(ct, missingIsExpected).ConfigureAwait(false) is { } loaded ? Normalize(loaded) : null;

    /// <summary>Writes the document atomically (see <see cref="JsonStore{T}.SaveAsync"/>).</summary>
    /// <param name="value">Document to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the file has been replaced.</returns>
    /// <exception cref="IOException">The file cannot be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the data directory is denied.</exception>
    public Task SaveAsync(T value, CancellationToken ct) => _store.SaveAsync(value, ct);

    private T Normalize(T loaded)
    {
        var fixes = new List<string>();
        T normalized = loaded.Normalize(fixes);
        if (fixes.Count > 0)
        {
            _logger.LogWarning("{File}: replaced {Count} invalid value(s) with defaults: {Fixes}", FilePath, fixes.Count, string.Join("; ", fixes));
        }

        return normalized;
    }
}
