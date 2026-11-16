using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mandrill.Core.IO;
using Microsoft.Extensions.Logging;

namespace Mandrill.Core.Index;

/// <summary>What is known about one drive's saved index (architecture §8.3 manifest.json).</summary>
/// <param name="File">Index file name in the index folder (<see cref="IndexFile.FileNameFor"/>).</param>
/// <param name="VolumeSerial">Volume serial the file was built from.</param>
/// <param name="Complete">True when the whole drive was indexed when saved.</param>
/// <param name="BuiltAtUtc">When the last full build finished (null while the first build runs).</param>
/// <param name="VerifiedAtUtc">When the last verify pass finished (null before the first one).</param>
/// <param name="Items">Items in the file.</param>
public sealed record DriveIndexInfo(string File, uint VolumeSerial, bool Complete, DateTime? BuiltAtUtc, DateTime? VerifiedAtUtc, int Items);

/// <summary>The index folder's manifest: one entry per drive letter ("C"). Written last, after the drive files.</summary>
/// <param name="Drives">Drive letter → saved index.</param>
public sealed record IndexManifest(IReadOnlyDictionary<string, DriveIndexInfo> Drives)
{
    /// <summary>Current manifest version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>A manifest with no drives (first launch, or a corrupt manifest).</summary>
    /// <returns>The empty manifest.</returns>
    public static IndexManifest Empty() => new(new Dictionary<string, DriveIndexInfo>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The same manifest with case-insensitive drive keys ("c" finds "C"); call after loading.</summary>
    /// <returns>The normalized manifest.</returns>
    public IndexManifest Normalized()
        => new(new Dictionary<string, DriveIndexInfo>(Drives ?? new Dictionary<string, DriveIndexInfo>(), StringComparer.OrdinalIgnoreCase));

    /// <summary>File name of the manifest in the index folder.</summary>
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Reads <c>&lt;index folder&gt;\manifest.json</c> synchronously (the index thread never blocks on async work). The format
    /// is the state-file one (a leading "version", camelCase). Missing, unreadable, corrupt or another version → an
    /// empty manifest, logged; the drives are then rebuilt. Unlike the settings store, a corrupt manifest is not kept as .bak:
    /// it holds nothing a rebuild cannot recreate.
    /// </summary>
    /// <param name="indexDirectory">The index folder.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>The manifest with case-insensitive drive keys.</returns>
    public static IndexManifest Read(string indexDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(indexDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        string path = Path.Combine(indexDirectory, FileName);
        try
        {
            if (!File.Exists(PathUtil.ToIo(path)))
            {
                return Empty();
            }

            ReadOnlySpan<byte> json = ReadShared(PathUtil.ToIo(path));
            json = json.StartsWith(Encoding.UTF8.Preamble) ? json[Encoding.UTF8.Preamble.Length..] : json; // a hand-edited copy may carry a BOM
            if (JsonNode.Parse(json) is not JsonObject node
                || node["version"]?.GetValue<int>() != CurrentVersion)
            {
                logger.LogWarning("Index manifest {File} has another version or shape; the index is rebuilt", path);
                return Empty();
            }

            node.Remove("version");
            return (node.Deserialize(IndexJsonContext.Default.IndexManifest) ?? Empty()).Normalized();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            logger.LogWarning(ex, "Index manifest {File} could not be read; the index is rebuilt", path);
            return Empty();
        }
    }

    /// <summary>Writes the manifest atomically (temp file, flush to disk, move), after the drive files.</summary>
    /// <param name="indexDirectory">The index folder (created if missing).</param>
    /// <param name="manifest">The manifest.</param>
    /// <exception cref="IOException">The write failed (the old manifest is untouched).</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    public static void Write(string indexDirectory, IndexManifest manifest)
    {
        ArgumentException.ThrowIfNullOrEmpty(indexDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        var document = new JsonObject { ["version"] = CurrentVersion };
        if (JsonSerializer.SerializeToNode(manifest, IndexJsonContext.Default.IndexManifest) is JsonObject body)
        {
            foreach (string key in body.Select(p => p.Key).ToList())
            {
                JsonNode? value = body[key];
                body.Remove(key);
                document[key] = value;
            }
        }

        string path = Path.Combine(indexDirectory, FileName);
        var (temp, target) = PathUtil.ToIoPair(path + ".tmp", path);
        Directory.CreateDirectory(PathUtil.ToIo(indexDirectory));
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(document.ToJsonString(Indented)));
                stream.Flush(flushToDisk: true);
            }

            IndexFile.ReplaceFile(temp, target);
        }
        catch
        {
            TryDeleteTemp(temp); // never leave a half-written .tmp behind
            throw;
        }
    }

    // Delete sharing: the owner instance can replace the manifest while a reader reads it (§18 D2, IndexFile.ReplaceFile).
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void TryDeleteTemp(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed at the next start (stale *.tmp cleanup).
        }
    }
}

/// <summary>Source-generated JSON metadata for the index manifest (camelCase).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IndexManifest))]
internal sealed partial class IndexJsonContext : JsonSerializerContext;
