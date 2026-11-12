using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Mandrill.Core.Platform;

/// <summary>Drive type as reported by GetDriveTypeW.</summary>
public enum DriveKind
{
    /// <summary>Unknown type.</summary>
    Unknown,

    /// <summary>Removable media (USB stick, card reader).</summary>
    Removable,

    /// <summary>Fixed disk.</summary>
    Fixed,

    /// <summary>Network drive.</summary>
    Network,

    /// <summary>Optical drive.</summary>
    CdRom,

    /// <summary>RAM disk.</summary>
    Ram,
}

/// <summary>One drive letter.</summary>
/// <param name="Root">Root path, e.g. "C:\".</param>
/// <param name="Kind">Drive type.</param>
public sealed record DriveDescriptor(string Root, DriveKind Kind);

/// <summary>Volume information for a drive root (may block on slow media).</summary>
/// <param name="Label">Volume label ("" when none).</param>
/// <param name="FileSystem">File system name, e.g. "NTFS".</param>
public sealed record VolumeInfo(string Label, string FileSystem);

/// <summary>Drive enumeration and volume information.</summary>
public static class Drives
{
    /// <summary>
    /// Current drive letters with their types (GetLogicalDrives + GetDriveTypeW). Touches no media, so it is cheap and
    /// safe to poll (drive monitor, architecture §4). Drives with no root directory (type 1) are omitted.
    /// </summary>
    /// <returns>Drives in letter order.</returns>
    public static IReadOnlyList<DriveDescriptor> GetDrives()
    {
        uint mask = PInvoke.GetLogicalDrives();
        var drives = new List<DriveDescriptor>();
        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0)
            {
                continue;
            }

            string root = $"{(char)('A' + i)}:\\";
            var kind = KindOf(PInvoke.GetDriveType(root));
            if (kind is not null)
            {
                drives.Add(new DriveDescriptor(root, kind.Value));
            }
        }

        return drives;
    }

    /// <summary>Volume label and file system. Worker thread only: this reads the media and can block on removable or network drives.</summary>
    /// <param name="root">Drive root, e.g. "C:\".</param>
    /// <returns>The information, or null when the drive is not ready or inaccessible.</returns>
    public static VolumeInfo? TryGetVolumeInfo(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Span<char> label = stackalloc char[261];
        Span<char> fileSystem = stackalloc char[261];
        if (!PInvoke.GetVolumeInformation(root, label, fileSystem))
        {
            return null;
        }

        return new VolumeInfo(UntilNul(label), UntilNul(fileSystem));
    }

    /// <summary>
    /// The drive's name as Explorer shows it, e.g. "Label (E:)" (an autorun.inf label wins over the volume label). STA
    /// thread only: a Shell call that reads the media.
    /// </summary>
    /// <param name="root">Drive root, e.g. "E:\".</param>
    /// <returns>The name, or null when the Shell has none.</returns>
    public static string? TryGetShellDisplayName(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var info = default(SHFILEINFOW);
        return PInvoke.SHGetFileInfo(root, 0, ref info, SHGFI_FLAGS.SHGFI_DISPLAYNAME) == 0 ? null : info.szDisplayName.ToString();
    }

    /// <summary>
    /// Volume serial number of a drive (the index checks it on load, architecture §8.3). Worker thread only: this reads
    /// the media and can block on removable or network drives.
    /// </summary>
    /// <param name="root">Drive root with its trailing backslash, e.g. "C:\" (GetVolumeInformationW requires it).</param>
    /// <returns>The serial, or null when the drive is not ready or inaccessible.</returns>
    public static uint? TryGetVolumeSerial(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return PInvoke.GetVolumeInformation(root, default, out uint serial, out _, out _, default) ? serial : null;
    }

    /// <summary>
    /// Total size and free space of a drive (This PC list, ADR-0015): GetDiskFreeSpaceExW, free = what the user may still
    /// write. Worker thread only: it reads the media; a not-ready drive fails fast (no "insert a disk" box, AppBootstrap).
    /// </summary>
    /// <param name="root">Drive root, e.g. "C:\".</param>
    /// <returns>Total and free bytes, or null when the drive is not ready or inaccessible.</returns>
    public static (long Total, long Free)? TryGetSpace(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return PInvoke.GetDiskFreeSpaceEx(root, out ulong free, out ulong total, out _) ? ((long)total, (long)free) : null;
    }

    private static string UntilNul(ReadOnlySpan<char> buffer)
    {
        int end = buffer.IndexOf('\0');
        return (end < 0 ? buffer : buffer[..end]).ToString();
    }

    private static DriveKind? KindOf(uint driveType) => driveType switch
    {
        1 => null, // DRIVE_NO_ROOT_DIR
        2 => DriveKind.Removable,
        3 => DriveKind.Fixed,
        4 => DriveKind.Network,
        5 => DriveKind.CdRom,
        6 => DriveKind.Ram,
        _ => DriveKind.Unknown,
    };
}
