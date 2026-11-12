using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mandrill.Core.IO;

namespace Mandrill.Core.Index;

/// <summary>Why a saved drive index could not be used (it is then discarded and rebuilt, AC-2.6).</summary>
public enum IndexLoadFailure
{
    /// <summary>No file.</summary>
    Missing,

    /// <summary>Not an index file (magic).</summary>
    NotAnIndex,

    /// <summary>Written by another format version.</summary>
    UnsupportedFormat,

    /// <summary>The drive was replaced or reformatted (volume serial differs).</summary>
    SerialMismatch,

    /// <summary>The file ends early or a section is malformed.</summary>
    Truncated,

    /// <summary>The checksum does not match (corruption).</summary>
    HashMismatch,

    /// <summary>The file could not be read (locked, I/O error).</summary>
    Unreadable,
}

/// <summary>Result of <see cref="IndexFile.Load"/>.</summary>
/// <param name="Segment">The loaded segment; null on failure.</param>
/// <param name="Failure">Why it failed; null on success.</param>
public sealed record IndexLoadResult(DriveSegment? Segment, IndexLoadFailure? Failure);

/// <summary>
/// One drive's index on disk (architecture §8.3, AC-2.5/2.6): header ("MNDX", format, flags, volume serial, counts), raw
/// column sections (<c>tag[4]</c>, <c>i64 length</c>, chunk bytes as in memory), the name chunks, the overlay, and an
/// XxHash64 trailer over everything before it. Written to "&lt;file&gt;.tmp", flushed to disk and moved over the old
/// file, so a crash leaves either the old or the new file. Partial (in-progress) segments save and load as incomplete.
/// Header numbers are little-endian; the column sections are the in-memory bytes (little-endian on win-x64, the only
/// target). Loading bounds every count before allocating and range-checks every loaded item, so a damaged file is
/// reported, never thrown. Index thread only.
/// </summary>
public static class IndexFile
{
    /// <summary>Current file format.</summary>
    public const ushort Format = 1;

    private const ushort FlagComplete = 1;
    private static ReadOnlySpan<byte> Magic => "MNDX"u8;

    /// <summary>File name for a drive: "C_1A2B3C4D.idx".</summary>
    /// <param name="root">Drive root, e.g. "C:\".</param>
    /// <param name="volumeSerial">Volume serial.</param>
    /// <returns>The file name.</returns>
    public static string FileNameFor(string root, uint volumeSerial)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        return $"{char.ToUpperInvariant(root[0])}_{volumeSerial:X8}.idx";
    }

    /// <summary>Writes <paramref name="segment"/> atomically to <paramref name="path"/>.</summary>
    /// <param name="segment">Segment (its published items and overlay are written).</param>
    /// <param name="path">Target file.</param>
    /// <exception cref="IOException">The write failed (the old file, if any, is untouched).</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    public static void Save(DriveSegment segment, string path)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentException.ThrowIfNullOrEmpty(path);
        string temp = path + ".tmp";
        var (tempIo, pathIo) = PathUtil.ToIoPair(temp, path);
        int count = segment.Count;
        IReadOnlyList<OverlayItem> overlay = segment.OverlayItems();
        try
        {
            WriteFile(segment, tempIo, count, overlay);
            File.Move(tempIo, pathIo, overwrite: true);
        }
        catch
        {
            TryDelete(tempIo); // never leave a half-written .tmp behind
            throw;
        }
    }

    private static void WriteFile(DriveSegment segment, string tempIo, int count, IReadOnlyList<OverlayItem> overlay)
    {
        using (var stream = new FileStream(tempIo, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var writer = new HashingWriter(stream);
            Span<byte> header = stackalloc byte[24];
            Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Format);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], segment.IsComplete ? FlagComplete : (ushort)0);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], segment.VolumeSerial);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..], count);
            BinaryPrimitives.WriteInt32LittleEndian(header[16..], segment.Names.ChunkCount);
            BinaryPrimitives.WriteInt32LittleEndian(header[20..], overlay.Count);
            writer.Write(header);
            writer.WriteString(segment.Root);

            WriteColumn(writer, "NOFF"u8, segment.NameOffsets, count);
            WriteColumn(writer, "PRNT"u8, segment.Parents, count);
            WriteColumn(writer, "CHST"u8, segment.ChildStarts, count);
            WriteColumn(writer, "CHCT"u8, segment.ChildCounts, count);
            WriteColumn(writer, "SBND"u8, segment.SubtreeEnds, count);
            WriteColumn(writer, "SIZE"u8, segment.Sizes, count);
            WriteColumn(writer, "MTIM"u8, segment.Mtimes, count);
            WriteColumn(writer, "FLAG"u8, segment.FlagColumn, count);

            writer.Write("NAME"u8);
            for (int chunk = 0; chunk < segment.Names.ChunkCount; chunk++)
            {
                ReadOnlySpan<char> used = segment.Names.UsedChunk(chunk);
                writer.WriteInt32(used.Length);
                writer.Write(MemoryMarshal.AsBytes(used));
            }

            writer.Write("OVLY"u8);
            foreach (OverlayItem item in overlay)
            {
                writer.WriteString(item.Path);
                writer.WriteInt64(item.Size);
                writer.WriteInt32((int)item.Mtime);
                writer.Write([(byte)item.Flags]);
            }

            Span<byte> trailer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(trailer, writer.Hash());
            stream.Write(trailer);
            stream.Flush(flushToDisk: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next save overwrites it.
        }
    }

    /// <summary>
    /// Reads a drive index. Every failure is reported, never thrown: the caller discards the file, logs the reason and
    /// rebuilds (AC-2.6). Loading streams the sections straight into new chunks; no second copy of the file is made.
    /// </summary>
    /// <param name="path">Index file.</param>
    /// <param name="expectedSerial">Current volume serial of the drive; null skips the check (tests).</param>
    /// <returns>The segment or the failure.</returns>
    public static IndexLoadResult Load(string path, uint? expectedSerial)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            using var stream = new FileStream(PathUtil.ToIo(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var reader = new HashingReader(stream);
            Span<byte> header = stackalloc byte[24];
            reader.Read(header);
            if (!header[..4].SequenceEqual(Magic))
            {
                return Fail(IndexLoadFailure.NotAnIndex);
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != Format)
            {
                return Fail(IndexLoadFailure.UnsupportedFormat);
            }

            uint serial = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            if (expectedSerial is { } expected && serial != expected)
            {
                return Fail(IndexLoadFailure.SerialMismatch);
            }

            int count = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            int nameChunks = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
            int overlayCount = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
            string root = reader.ReadString();

            // Bound every count by what the file can hold before allocating anything for it.
            long length = stream.Length;
            const int ColumnBytesPerItem = (5 * sizeof(int)) + sizeof(long) + sizeof(uint) + sizeof(byte);
            const int MinOverlayBytes = sizeof(int) + sizeof(long) + sizeof(int) + 1;
            if (count < 1 || (long)count * ColumnBytesPerItem > length
                || nameChunks < 0 || nameChunks > int.MaxValue >> NameBlob.ChunkShift || nameChunks > ((long)count * 256 / NameBlob.ChunkChars) + 1
                || overlayCount < 0 || (long)overlayCount * MinOverlayBytes > length
                || !PathUtil.IsDriveRoot(root))
            {
                return Fail(IndexLoadFailure.Truncated);
            }

            DriveSegment segment = DriveSegment.CreateForLoad(root, serial);
            ReadColumn(reader, "NOFF"u8, segment.NameOffsets, count);
            ReadColumn(reader, "PRNT"u8, segment.Parents, count);
            ReadColumn(reader, "CHST"u8, segment.ChildStarts, count);
            ReadColumn(reader, "CHCT"u8, segment.ChildCounts, count);
            ReadColumn(reader, "SBND"u8, segment.SubtreeEnds, count);
            ReadColumn(reader, "SIZE"u8, segment.Sizes, count);
            ReadColumn(reader, "MTIM"u8, segment.Mtimes, count);
            ReadColumn(reader, "FLAG"u8, segment.FlagColumn, count);

            ExpectTag(reader, "NAME"u8);
            for (int chunk = 0; chunk < nameChunks; chunk++)
            {
                int used = reader.ReadInt32();
                if (used < 0 || used > NameBlob.ChunkChars)
                {
                    throw new InvalidDataException("Bad name chunk length.");
                }

                var chars = new char[NameBlob.ChunkChars];
                reader.Read(MemoryMarshal.AsBytes(chars.AsSpan(0, used)));
                segment.Names.AddChunk(chars, used);
            }

            ExpectTag(reader, "OVLY"u8);
            var overlay = new List<OverlayItem>(overlayCount);
            Span<byte> flag = stackalloc byte[1];
            for (int i = 0; i < overlayCount; i++)
            {
                string itemPath = reader.ReadString();
                long size = reader.ReadInt64();
                uint mtime = (uint)reader.ReadInt32();
                reader.Read(flag);
                overlay.Add(new OverlayItem(itemPath, size, mtime, (IndexFlags)flag[0]));
            }

            ulong computed = reader.Hash();
            Span<byte> trailer = stackalloc byte[8];
            stream.ReadExactly(trailer);
            if (BinaryPrimitives.ReadUInt64LittleEndian(trailer) != computed || stream.Position != stream.Length)
            {
                return Fail(IndexLoadFailure.HashMismatch);
            }

            if (!Validate(segment, count, out int dead))
            {
                return Fail(IndexLoadFailure.Truncated);
            }

            segment.CompleteLoad(count, overlay, dead);
            return new IndexLoadResult(segment, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Fail(IndexLoadFailure.Missing);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentException)
        {
            return Fail(IndexLoadFailure.Truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(IndexLoadFailure.Unreadable);
        }
    }

    private static IndexLoadResult Fail(IndexLoadFailure failure) => new(null, failure);

    // One pass over the loaded columns: every reference stays inside the segment, so a file that passes the checksum but
    // is inconsistent (a bug in a future writer, an edited file) cannot crash a query later. Also counts dead items.
    private static bool Validate(DriveSegment segment, int count, out int dead)
    {
        dead = 0;
        NameBlob names = segment.Names;
        int chunks = names.ChunkCount;
        for (int i = 0; i < count; i++)
        {
            int parent = segment.Parents[i];
            int start = segment.ChildStarts[i];
            int children = segment.ChildCounts[i];
            int end = segment.SubtreeEnds[i];
            int offset = segment.NameOffsets[i];
            int chunk = offset >> NameBlob.ChunkShift;
            if ((i == 0 ? parent != -1 : parent < 0 || parent >= i)
                || (start < 0 ? children != 0 : start > count || children < 0 || (long)start + children > count)
                || end < -1 || end > count
                || offset < 0 || chunk >= chunks || (offset & (NameBlob.ChunkChars - 1)) >= names.UsedChunk(chunk).Length)
            {
                return false;
            }

            if (((IndexFlags)segment.FlagColumn[i] & IndexFlags.Dead) != 0)
            {
                dead++;
            }
        }

        return true;
    }

    private static void WriteColumn<T>(HashingWriter writer, ReadOnlySpan<byte> tag, ChunkedColumn<T> column, int count)
        where T : unmanaged
    {
        writer.Write(tag);
        int itemSize = Unsafe.SizeOf<T>();
        writer.WriteInt64((long)count * itemSize);
        for (int chunk = 0, remaining = count; remaining > 0; chunk++)
        {
            int n = Math.Min(remaining, ChunkedColumn<T>.ChunkSize);
            writer.Write(MemoryMarshal.AsBytes(column.ChunkAt(chunk).AsSpan(0, n)));
            remaining -= n;
        }
    }

    private static void ReadColumn<T>(HashingReader reader, ReadOnlySpan<byte> tag, ChunkedColumn<T> column, int count)
        where T : unmanaged
    {
        ExpectTag(reader, tag);
        if (reader.ReadInt64() != (long)count * Unsafe.SizeOf<T>())
        {
            throw new InvalidDataException("Column length does not match the item count.");
        }

        for (int remaining = count; remaining > 0;)
        {
            var chunk = new T[ChunkedColumn<T>.ChunkSize];
            int n = Math.Min(remaining, chunk.Length);
            reader.Read(MemoryMarshal.AsBytes(chunk.AsSpan(0, n)));
            column.AddChunk(chunk);
            remaining -= n;
        }
    }

    private static void ExpectTag(HashingReader reader, ReadOnlySpan<byte> tag)
    {
        Span<byte> actual = stackalloc byte[4];
        reader.Read(actual);
        if (!actual.SequenceEqual(tag))
        {
            throw new InvalidDataException("Unexpected section.");
        }
    }

    // Writes to the stream and feeds the same bytes to the checksum.
    private sealed class HashingWriter(Stream stream)
    {
        private readonly XxHash64 _hash = new();

        public void Write(ReadOnlySpan<byte> bytes)
        {
            stream.Write(bytes);
            _hash.Append(bytes);
        }

        public void WriteInt32(int value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, value);
            Write(b);
        }

        public void WriteInt64(long value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(b, value);
            Write(b);
        }

        public void WriteString(string value)
        {
            WriteInt32(value.Length);
            Write(MemoryMarshal.AsBytes(value.AsSpan()));
        }

        public ulong Hash() => _hash.GetCurrentHashAsUInt64();
    }

    // Reads exactly the requested bytes and feeds them to the checksum.
    private sealed class HashingReader(Stream stream)
    {
        private const int MaxString = 32_767 + 4; // longest Windows path plus a little

        private readonly XxHash64 _hash = new();

        public void Read(Span<byte> bytes)
        {
            stream.ReadExactly(bytes);
            _hash.Append(bytes);
        }

        public int ReadInt32()
        {
            Span<byte> b = stackalloc byte[4];
            Read(b);
            return BinaryPrimitives.ReadInt32LittleEndian(b);
        }

        public long ReadInt64()
        {
            Span<byte> b = stackalloc byte[8];
            Read(b);
            return BinaryPrimitives.ReadInt64LittleEndian(b);
        }

        public string ReadString()
        {
            int length = ReadInt32();
            if (length < 0 || length > MaxString)
            {
                throw new InvalidDataException("Bad string length.");
            }

            var chars = new char[length];
            Read(MemoryMarshal.AsBytes(chars.AsSpan()));
            return new string(chars);
        }

        public ulong Hash() => _hash.GetCurrentHashAsUInt64();
    }
}
