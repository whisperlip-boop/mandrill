namespace Mandrill.Core.IO;

/// <summary>One directory entry as listed (immutable; shared between panes and caches).</summary>
/// <param name="Name">File or folder name exactly as on disk (may end with a space or dot).</param>
/// <param name="Attributes">File attributes.</param>
/// <param name="Size">Size in bytes; 0 for folders.</param>
/// <param name="LastWriteUtcTicks">Last write time, UTC ticks.</param>
/// <param name="ExtId">Interned extension id (<see cref="ExtensionTable"/>); 0 for folders and names without an extension.</param>
public sealed record FileEntry(string Name, FileAttributes Attributes, long Size, long LastWriteUtcTicks, int ExtId)
{
    /// <summary>True for folders (including junctions and directory symlinks).</summary>
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    /// <summary>True when the Hidden attribute is set.</summary>
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    /// <summary>True when the System attribute is set.</summary>
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;

    /// <summary>True for junctions, symlinks and other reparse points (shown with a link overlay; E-1.7).</summary>
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Explorer visibility rule (AC-1.24): non-hidden entries are always shown; hidden ones only with "show hidden";
    /// hidden + system ("protected operating system files") additionally need "show protected system files".
    /// </summary>
    /// <param name="showHidden">Explorer "Show hidden files".</param>
    /// <param name="showProtectedSystem">Explorer "Show protected operating system files".</param>
    /// <returns>True when the entry is visible.</returns>
    public bool IsVisible(bool showHidden, bool showProtectedSystem)
        => !IsHidden || (showHidden && (!IsSystem || showProtectedSystem));
}
