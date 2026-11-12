namespace Mandrill.Core.Index;

/// <summary>
/// All item names of a segment, UTF-16, each followed by '\0', in chunks of 1M chars that never move (architecture §8.1).
/// A name never crosses a chunk boundary; the rest of a chunk stays '\0', so a search over a char range can never match
/// across two names. An offset is <c>chunk &lt;&lt; 20 | position</c>. Single writer; readers below the published count
/// need no lock.
/// </summary>
internal sealed class NameBlob
{
    /// <summary>log2 of the chunk size in chars.</summary>
    public const int ChunkShift = 20;

    /// <summary>Chars per chunk.</summary>
    public const int ChunkChars = 1 << ChunkShift;

    private const int ChunkMask = ChunkChars - 1;

    private char[][] _chunks = [];
    private int[] _used = [];
    private int _chunkCount;

    /// <summary>Number of allocated chunks.</summary>
    public int ChunkCount => Volatile.Read(ref _chunkCount);

    /// <summary>Allocated bytes (memory accounting).</summary>
    public long ByteSize => (long)ChunkCount * ChunkChars * sizeof(char);

    /// <summary>Offset just past the last name (the end of the used chars).</summary>
    public int End => _chunkCount == 0 ? 0 : ((_chunkCount - 1) << ChunkShift) | _used[_chunkCount - 1];

    /// <summary>Appends a name and its terminator (writer only).</summary>
    /// <param name="name">Name (1–255 chars on Windows).</param>
    /// <returns>Its offset.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> does not fit a chunk.</exception>
    /// <exception cref="InvalidOperationException">The blob is full (2^31 chars).</exception>
    public int Append(ReadOnlySpan<char> name)
    {
        if (name.Length + 1 > ChunkChars)
        {
            throw new ArgumentException("Name longer than a name chunk.", nameof(name));
        }

        if (_chunkCount == 0 || _used[_chunkCount - 1] + name.Length + 1 > ChunkChars)
        {
            if (_chunkCount == int.MaxValue >> ChunkShift)
            {
                throw new InvalidOperationException("The name blob is full.");
            }

            AddChunk(new char[ChunkChars], 0);
        }

        int chunk = _chunkCount - 1;
        int position = _used[chunk];
        name.CopyTo(_chunks[chunk].AsSpan(position));
        _used[chunk] = position + name.Length + 1; // the terminator is the array's own '\0'
        return (chunk << ChunkShift) | position;
    }

    /// <summary>The name at <paramref name="offset"/>.</summary>
    /// <param name="offset">Offset from <see cref="Append"/>.</param>
    /// <returns>The name without its terminator.</returns>
    public ReadOnlySpan<char> Get(int offset)
    {
        ReadOnlySpan<char> rest = Volatile.Read(ref _chunks)[offset >> ChunkShift].AsSpan(offset & ChunkMask);
        int end = rest.IndexOf('\0');
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>The used part of chunk <paramref name="chunk"/> (search and persistence).</summary>
    /// <param name="chunk">Chunk index.</param>
    /// <returns>The chars written so far, including terminators.</returns>
    public ReadOnlySpan<char> UsedChunk(int chunk) => Volatile.Read(ref _chunks)[chunk].AsSpan(0, Volatile.Read(ref _used)[chunk]);

    /// <summary>Appends a loaded chunk (loader).</summary>
    /// <param name="chunk">A chunk of exactly <see cref="ChunkChars"/> chars.</param>
    /// <param name="used">Chars in use.</param>
    public void AddChunk(char[] chunk, int used)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length != ChunkChars || used < 0 || used > ChunkChars)
        {
            throw new ArgumentException("Invalid name chunk.", nameof(chunk));
        }

        if (_chunkCount == _chunks.Length)
        {
            int size = Math.Max(4, _chunks.Length * 2);
            var chunks = new char[size][];
            var usedTable = new int[size];
            Array.Copy(_chunks, chunks, _chunkCount);
            Array.Copy(_used, usedTable, _chunkCount);
            chunks[_chunkCount] = chunk;
            usedTable[_chunkCount] = used;
            Volatile.Write(ref _used, usedTable);
            Volatile.Write(ref _chunks, chunks);
        }
        else
        {
            _chunks[_chunkCount] = chunk;
            _used[_chunkCount] = used;
        }

        Volatile.Write(ref _chunkCount, _chunkCount + 1);
    }
}
