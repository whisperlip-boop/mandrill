using Mandrill.Core.IO;
using Mandrill.Core.Platform;

namespace Mandrill.Core.Index;

/// <summary>Why a folder cannot go on the exclude list (AC-2.10); <see cref="None"/> when it can.</summary>
public enum ExcludeFolderProblem
{
    /// <summary>The folder can be excluded.</summary>
    None,

    /// <summary>The folder is on a removable, network or optical drive: those are never indexed (AC-2.1).</summary>
    NotFixedDrive,

    /// <summary>No folder exists at the path.</summary>
    NotFound,
}

/// <summary>Disk checks for the exclude-list editor (AC-2.10). Blocking I/O: call on a worker thread.</summary>
public static class ExcludeFolderCheck
{
    /// <summary>
    /// Checks <paramref name="path"/>: its drive must be fixed (decided from the drive type, without touching removable
    /// media) and a folder must exist there.
    /// </summary>
    /// <param name="path">Normalized absolute drive-letter path.</param>
    /// <returns>The problem, or <see cref="ExcludeFolderProblem.None"/>.</returns>
    public static ExcludeFolderProblem Check(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string root = char.ToUpperInvariant(path[0]) + @":\";
        DriveKind? kind = Drives.GetDrives().FirstOrDefault(d => PathUtil.PathEquals(d.Root, root))?.Kind;
        if (kind != DriveKind.Fixed)
        {
            return kind is null ? ExcludeFolderProblem.NotFound : ExcludeFolderProblem.NotFixedDrive;
        }

        // Directory.Exists never throws: an unreadable or invalid path is "not found".
        return Directory.Exists(PathUtil.ToIo(path)) ? ExcludeFolderProblem.None : ExcludeFolderProblem.NotFound;
    }
}
