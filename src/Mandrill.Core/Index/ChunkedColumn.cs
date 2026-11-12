using System.Runtime.CompilerServices;

namespace Mandrill.Core.Index;

/// <summary>
/// One numeric column of a segment: fixed-size chunks that never move (architecture §8.1), so growth never copies data
/// and readers below the published count need no lock. The chunk table is replaced (not resized in place) when it
/// grows, and published with a volatile write. Single writer.
/// </summary>
/// <typeparam name="T">Unmanaged element type.</typeparam>
internal sealed class ChunkedColumn<T>
    where T : unmanaged
{
    /// <summary>Items per chunk (2^16).</summary>
    public const int ChunkShift = 16;

    /// <summary>Items per chunk.</summary>
    public const int ChunkSize = 1 << ChunkShift;

    private const int ChunkMask = ChunkSize - 1;

    private T[][] _chunks = [];
    private int _chunkCount;

    /// <summary>Number of allocated chunks.</summary>
    public int ChunkCount => Volatile.Read(ref _chunkCount);

    /// <summary>Allocated bytes (memory accounting).</summary>
    public long ByteSize => (long)ChunkCount * ChunkSize * Unsafe.SizeOf<T>();

    /// <summary>Element <paramref name="index"/> (must be below the allocated capacity).</summary>
    /// <param name="index">Item index.</param>
    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Volatile.Read(ref _chunks)[index >> ChunkShift][index & ChunkMask];
    }

    /// <summary>The chunk at <paramref name="chunk"/> (persistence).</summary>
    /// <param name="chunk">Chunk index.</param>
    /// <returns>The chunk array.</returns>
    public T[] ChunkAt(int chunk) => Volatile.Read(ref _chunks)[chunk];

    /// <summary>Makes room for <paramref name="count"/> items (writer only).</summary>
    /// <param name="count">Item count.</param>
    public void EnsureCapacity(int count)
    {
        while ((long)_chunkCount * ChunkSize < count)
        {
            AddChunk(new T[ChunkSize]);
        }
    }

    /// <summary>Appends a chunk (writer only; also used by the loader).</summary>
    /// <param name="chunk">A chunk of exactly <see cref="ChunkSize"/> items.</param>
    public void AddChunk(T[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length != ChunkSize)
        {
            throw new ArgumentException($"A chunk holds {ChunkSize} items.", nameof(chunk));
        }

        T[][] table = _chunks;
        if (_chunkCount == table.Length)
        {
            var bigger = new T[Math.Max(16, table.Length * 2)][];
            Array.Copy(table, bigger, _chunkCount);
            bigger[_chunkCount] = chunk;
            Volatile.Write(ref _chunks, bigger);
        }
        else
        {
            table[_chunkCount] = chunk;
        }

        Volatile.Write(ref _chunkCount, _chunkCount + 1);
    }
}
