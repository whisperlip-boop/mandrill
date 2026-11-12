using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace Mandrill.Core.State;

/// <summary>
/// Loads and saves one JSON document with a top-level "version" field.
/// Saves are atomic: &lt;file&gt;.tmp.&lt;pid&gt; → flush to disk → replace. Saves through one instance are serialized;
/// use exactly one store per file. Call from worker threads only.
/// </summary>
/// <typeparam name="T">Document type (source-generated metadata required).</typeparam>
public sealed class JsonStore<T>
    where T : class
{
    private const string VersionProperty = "version";
    private const int ReplaceAttempts = 5;
    private const int ReplaceRetryDelayMs = 20;
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(1);
    private readonly string _path;
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly Func<T> _createDefault;
    private readonly int _currentVersion;
    private readonly ILogger _logger;
    private readonly Func<JsonObject, int, JsonObject>? _migrate;

    // Overlapping saves would share the temp file name; one writer at a time.
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>Creates a store for one file.</summary>
    /// <param name="path">Target file.</param>
    /// <param name="typeInfo">Source-generated type metadata.</param>
    /// <param name="createDefault">Factory for the default document (missing or corrupt file).</param>
    /// <param name="currentVersion">Schema version written on save.</param>
    /// <param name="logger">Logger for I/O and recovery events.</param>
    /// <param name="migrate">Upgrades an older document (node, fromVersion) to the current schema.</param>
    public JsonStore(string path, JsonTypeInfo<T> typeInfo, Func<T> createDefault, int currentVersion, ILogger logger, Func<JsonObject, int, JsonObject>? migrate = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(createDefault);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(currentVersion, 1);
        _path = path;
        _typeInfo = typeInfo;
        _createDefault = createDefault;
        _currentVersion = currentVersion;
        _logger = logger;
        _migrate = migrate;
    }

    /// <summary>Path of the backing file.</summary>
    public string FilePath => _path;

    /// <summary>Loads the document. Missing → defaults; corrupt → renamed to .bak, logged, defaults.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The loaded or default document.</returns>
    public async Task<T> LoadAsync(CancellationToken ct)
    {
        DeleteStaleTempFiles();
        if (!File.Exists(_path))
        {
            _logger.LogInformation("{File} not found; using defaults", _path);
            return _createDefault();
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not corrupt: keep the file so a later run can still read it.
            _logger.LogError(ex, "Cannot read {File}; using defaults", _path);
            return _createDefault();
        }

        try
        {
            return Parse(bytes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            BackUpCorrupt(ex);
            return _createDefault();
        }
    }

    /// <summary>Writes the document atomically with the current "version".</summary>
    /// <param name="value">Document to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the file has been replaced.</returns>
    /// <exception cref="IOException">The file cannot be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the data directory is denied.</exception>
    public async Task SaveAsync(T value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        var node = JsonSerializer.SerializeToNode(value, _typeInfo) as JsonObject
            ?? throw new InvalidOperationException($"{typeof(T).Name} must serialize to a JSON object");

        // The version leads the document so humans and migrations see it first.
        var ordered = new JsonObject { [VersionProperty] = _currentVersion };
        foreach (var (key, child) in node.ToList())
        {
            if (key != VersionProperty)
            {
                node.Remove(key);
                ordered[key] = child;
            }
        }

        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmp = $"{_path}.tmp.{Environment.ProcessId}";
        await _saveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                await using (writer.ConfigureAwait(false))
                {
                    ordered.WriteTo(writer, _typeInfo.Options);
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                }

                stream.Flush(flushToDisk: true);
            }

            await ReplaceWithRetryAsync(tmp, ct).ConfigureAwait(false);
            _logger.LogDebug("Saved {File}", _path);
        }
        catch (OperationCanceledException)
        {
            // Cancellation (e.g. shutdown) is expected; the previous file stays intact.
            TryDelete(tmp);
            _logger.LogDebug("Saving {File} cancelled", _path);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            _logger.LogError(ex, "Saving {File} failed", _path);
            throw;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // Antivirus/indexer scanners briefly hold a just-written target, which makes the replace fail with
    // access denied or a sharing violation; a few short retries cover that without hiding a real lock.
    private async Task ReplaceWithRetryAsync(string tmp, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, _path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < ReplaceAttempts && ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Replacing {File} failed (attempt {Attempt}); retrying", _path, attempt);
                await Task.Delay(ReplaceRetryDelayMs * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    // Temp files left by a crashed earlier process (other pid, older than a minute) are useless: they never replaced the target.
    private void DeleteStaleTempFiles()
    {
        string? dir = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }

        string own = $"{Path.GetFileName(_path)}.tmp.{Environment.ProcessId}";
        try
        {
            foreach (string stale in Directory.EnumerateFiles(dir, Path.GetFileName(_path) + ".tmp.*"))
            {
                // Another running instance (E-1.12) may be mid-save; only old leftovers are stale.
                if (!string.Equals(Path.GetFileName(stale), own, StringComparison.OrdinalIgnoreCase)
                    && File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow - StaleTempAge)
                {
                    TryDelete(stale);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not scan {Directory} for stale temporary files", dir);
        }
    }

    private T Parse(byte[] bytes)
    {
        ReadOnlySpan<byte> json = bytes;
        json = json.StartsWith(Encoding.UTF8.Preamble) ? json[Encoding.UTF8.Preamble.Length..] : json; // a UTF-8 BOM (Notepad, PowerShell 7 -Encoding utf8BOM) is rejected by the reader; UTF-16 is not supported
        if (JsonNode.Parse(json) is not JsonObject node)
        {
            throw new JsonException("Root is not a JSON object");
        }

        int version = node[VersionProperty]?.GetValue<int>() ?? throw new JsonException("Missing \"version\"");
        if (version < 1)
        {
            throw new JsonException($"Invalid version {version}");
        }

        if (version > _currentVersion)
        {
            _logger.LogWarning("{File} has newer version {Version} (> {Current}); unknown fields are ignored", _path, version, _currentVersion);
        }
        else if (version < _currentVersion)
        {
            if (_migrate is null)
            {
                throw new JsonException($"No migration from version {version}");
            }

            node = _migrate(node, version);
            _logger.LogInformation("{File} migrated from version {From} to {To}", _path, version, _currentVersion);
        }

        node.Remove(VersionProperty);
        return node.Deserialize(_typeInfo) ?? throw new JsonException("Document is null");
    }

    private void BackUpCorrupt(Exception ex)
    {
        string backup = _path + ".bak";
        try
        {
            File.Move(_path, backup, overwrite: true);
            _logger.LogWarning(ex, "{File} is corrupt; moved to {Backup} and using defaults", _path, backup);
        }
        catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(moveEx, "{File} is corrupt and could not be backed up; using defaults", _path);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete temporary file {File}", path);
        }
    }
}
