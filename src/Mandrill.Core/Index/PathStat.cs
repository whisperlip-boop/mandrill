using Mandrill.Core.IO;

namespace Mandrill.Core.Index;

/// <summary>What a stat found at a path (change tracking, architecture §8.3).</summary>
public enum PathState
{
    /// <summary>Nothing exists at the path.</summary>
    Missing,

    /// <summary>A file or directory exists.</summary>
    Present,

    /// <summary>The stat failed (sharing violation, access denied, drive not ready): retried, then left as it is.</summary>
    Unknown,
}

/// <summary>Result of a stat without following links.</summary>
/// <param name="State">What was found.</param>
/// <param name="Size">File size in bytes (0 for a directory).</param>
/// <param name="Mtime">Last write time, Unix seconds.</param>
/// <param name="Flags">Directory, reparse, hidden and system flags.</param>
public readonly record struct PathStat(PathState State, long Size, uint Mtime, IndexFlags Flags)
{
    /// <summary>Nothing at the path.</summary>
    public static PathStat Missing => new(PathState.Missing, 0, 0, IndexFlags.None);

    /// <summary>The stat failed.</summary>
    public static PathStat Unknown => new(PathState.Unknown, 0, 0, IndexFlags.None);

    /// <summary>
    /// Stats <paramref name="path"/> with one GetFileAttributesEx (through <see cref="FileInfo"/>); a reparse point reports
    /// itself, not its target.
    /// </summary>
    /// <param name="path">Normalized full path.</param>
    /// <returns>The stat.</returns>
    public static PathStat Win32(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            var info = new FileInfo(PathUtil.ToIo(path));
            FileAttributes attributes = info.Attributes; // throws when nothing is there (file or directory alike)
            IndexFlags flags = IndexFlags.None;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                flags |= IndexFlags.Directory;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                flags |= IndexFlags.Reparse;
            }

            if ((attributes & FileAttributes.Hidden) != 0)
            {
                flags |= IndexFlags.Hidden;
            }

            if ((attributes & FileAttributes.System) != 0)
            {
                flags |= IndexFlags.System;
            }

            long size = (flags & IndexFlags.Directory) != 0 ? 0 : info.Length;
            return new PathStat(PathState.Present, size, IndexEntryBuffer.ToUnixSeconds(info.LastWriteTimeUtc), flags);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Unknown;
        }
    }
}
