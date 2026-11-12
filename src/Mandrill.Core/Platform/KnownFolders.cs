using Mandrill.Core.IO;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Mandrill.Core.Platform;

/// <summary>Known folders used by the tree and as fallback locations.</summary>
public enum KnownFolder
{
    /// <summary>Desktop.</summary>
    Desktop,

    /// <summary>Documents.</summary>
    Documents,

    /// <summary>Downloads.</summary>
    Downloads,

    /// <summary>Pictures.</summary>
    Pictures,

    /// <summary>The user profile ("home folder").</summary>
    Profile,
}

/// <summary>Resolves known folders through SHGetKnownFolderPath, so redirected (e.g. OneDrive) locations are honoured (AC-1.9).</summary>
public static class KnownFolders
{
    /// <summary>Quick-access tree roots in ui-spec order (AC-1.6).</summary>
    public static IReadOnlyList<KnownFolder> QuickAccess { get; } = [KnownFolder.Desktop, KnownFolder.Documents, KnownFolder.Downloads, KnownFolder.Pictures];

    /// <summary>The folder's current path (no existence check; pure registry/shell lookup, no media access).</summary>
    /// <param name="folder">Known folder.</param>
    /// <returns>The normalized path, or null when the shell has none.</returns>
    public static string? GetPath(KnownFolder folder)
    {
        var id = folder switch
        {
            KnownFolder.Desktop => PInvoke.FOLDERID_Desktop,
            KnownFolder.Documents => PInvoke.FOLDERID_Documents,
            KnownFolder.Downloads => PInvoke.FOLDERID_Downloads,
            KnownFolder.Pictures => PInvoke.FOLDERID_Pictures,
            KnownFolder.Profile => PInvoke.FOLDERID_Profile,
            _ => throw new ArgumentOutOfRangeException(nameof(folder), folder, null),
        };

        return Resolve(id);
    }

    // Pointer overload: the safe-handle overload's ToString() is the handle type name, not the path.
    private static unsafe string? Resolve(Guid id)
    {
        PWSTR raw = default;
        try
        {
            if (PInvoke.SHGetKnownFolderPath(&id, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, default, &raw).Failed || raw.Value is null)
            {
                return null;
            }

            return PathUtil.TryNormalize(raw.ToString(), out string path, out _) ? path : null;
        }
        finally
        {
            // The shell allocates the string even on some failures; CoTaskMemFree(null) is a no-op.
            PInvoke.CoTaskMemFree(raw.Value);
        }
    }

    /// <summary>
    /// Quick-access entries whose folder exists; missing ones are left out without error (AC-1.9).
    /// Worker thread only (checks the disk).
    /// </summary>
    /// <returns>Existing entries in tree order.</returns>
    public static IReadOnlyList<(KnownFolder Folder, string Path)> ExistingQuickAccess()
    {
        var result = new List<(KnownFolder, string)>(QuickAccess.Count);
        foreach (var folder in QuickAccess)
        {
            string? path = GetPath(folder);
            if (path is not null && Directory.Exists(PathUtil.ToIo(path)))
            {
                result.Add((folder, path));
            }
        }

        return result;
    }

    /// <summary>The home folder used when a pane has nowhere else to go (E-1.4): the user profile.</summary>
    /// <returns>The profile path, or null when unavailable.</returns>
    public static string? Home() => GetPath(KnownFolder.Profile);

    /// <summary>
    /// The home folder, or the system drive root when the profile folder is unknown, so a pane always has somewhere to
    /// go (first run, drive removed, missing folder without an ancestor).
    /// </summary>
    /// <param name="usedFallback">True when the system drive root was used.</param>
    /// <returns>The folder, or null only when even the system drive cannot be determined.</returns>
    public static string? HomeOrSystemRoot(out bool usedFallback)
    {
        string? home = Home();
        usedFallback = home is null;
        return home ?? Path.GetPathRoot(Environment.SystemDirectory);
    }
}
