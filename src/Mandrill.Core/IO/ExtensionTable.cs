using System.Collections.Concurrent;

namespace Mandrill.Core.IO;

/// <summary>
/// Interns file extensions to small integer ids so <see cref="FileEntry"/> carries no extension string
/// (icons and type names are cached per id). Thread-safe; lookups allocate nothing for known extensions.
/// </summary>
public sealed class ExtensionTable
{
    /// <summary>Id for "no extension" (and for folders).</summary>
    public const int None = 0;

    private readonly ConcurrentDictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _spanLookup;
    private readonly List<string> _byId = [string.Empty];
    private readonly Lock _gate = new();

    /// <summary>Creates an empty table.</summary>
    public ExtensionTable()
    {
        _spanLookup = _ids.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>Process-wide table shared by all listers.</summary>
    public static ExtensionTable Shared { get; } = new();

    /// <summary>Number of interned extensions (excluding <see cref="None"/>).</summary>
    public int Count => _ids.Count;

    /// <summary>Id of the extension of <paramref name="fileName"/> (".TXT" and ".txt" share an id).</summary>
    /// <param name="fileName">File name.</param>
    /// <returns>The id, or <see cref="None"/> when there is no extension.</returns>
    public int IdOf(ReadOnlySpan<char> fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Length <= 1)
        {
            // "" or a lone trailing dot: no extension.
            return None;
        }

        if (_spanLookup.TryGetValue(ext, out int id))
        {
            return id;
        }

        lock (_gate)
        {
            if (_spanLookup.TryGetValue(ext, out id))
            {
                return id;
            }

            string key = ext.ToString().ToLowerInvariant();
            id = _byId.Count;
            _byId.Add(key);
            _ids[key] = id;
            return id;
        }
    }

    /// <summary>The extension for an id, lower-case with the leading dot (".txt"); "" for <see cref="None"/>.</summary>
    /// <param name="id">Extension id.</param>
    /// <returns>The extension.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The id was never issued.</exception>
    public string ExtensionOf(int id)
    {
        lock (_gate)
        {
            if ((uint)id >= (uint)_byId.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown extension id");
            }

            return _byId[id];
        }
    }
}
