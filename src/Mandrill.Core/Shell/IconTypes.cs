using Mandrill.Core.IO;

namespace Mandrill.Core.Shell;

/// <summary>
/// System image list to draw from; values are the nominal pixel sizes. SHIL_SMALL/LARGE/EXTRALARGE scale with the
/// process DPI, so the delivered image can be larger than nominal; the App scales down.
/// </summary>
public enum IconSize
{
    /// <summary>SHIL_SMALL (16 px at 100 %).</summary>
    Small = 16,

    /// <summary>SHIL_LARGE (32 px at 100 %).</summary>
    Medium = 32,

    /// <summary>SHIL_EXTRALARGE (48 px at 100 %).</summary>
    Large = 48,

    /// <summary>SHIL_JUMBO (always 256 px).</summary>
    Jumbo = 256,
}

/// <summary>Icon size helpers.</summary>
public static class IconSizes
{
    /// <summary>The smallest image list whose nominal size is at least <paramref name="pixels"/> (Jumbo above 48).</summary>
    /// <param name="pixels">Requested display size in pixels.</param>
    /// <returns>The image list to request.</returns>
    public static IconSize ForPixels(int pixels) => pixels switch
    {
        <= 16 => IconSize.Small,
        <= 32 => IconSize.Medium,
        <= 48 => IconSize.Large,
        _ => IconSize.Jumbo,
    };
}

/// <summary>
/// Cache key of one icon image: by type (<see cref="ExtId"/>) for ordinary items, per path (<see cref="UniquePath"/>)
/// for items whose icon or overlay depends on the item itself. Paths compare with <see cref="PathUtil.Comparer"/>.
/// </summary>
/// <param name="ExtId">Extension id (<see cref="ExtensionTable"/>), or <see cref="FolderExtId"/> for folders.</param>
/// <param name="UniquePath">Full path for per-item icons; null for by-type icons.</param>
/// <param name="Size">Image list.</param>
/// <param name="Thumbnail">
/// For a thumbnail key, the file's last write time and size, so an edited image gets a new key; null for icon keys.
/// </param>
public readonly record struct IconKey(int ExtId, string? UniquePath, IconSize Size, (long LastWriteUtcTicks, long Size)? Thumbnail = null)
{
    /// <summary><see cref="ExtId"/> used for folders (0 is "file without extension").</summary>
    public const int FolderExtId = -1;

    /// <summary><see cref="ExtId"/> of the stock removable-drive icon (tree; no media access).</summary>
    public const int RemovableDriveExtId = -2;

    /// <summary><see cref="ExtId"/> of the stock CD/DVD drive icon.</summary>
    public const int CdRomDriveExtId = -3;

    /// <summary><see cref="ExtId"/> of the stock network drive icon.</summary>
    public const int NetworkDriveExtId = -4;

    /// <summary><see cref="ExtId"/> of the stock RAM disk icon.</summary>
    public const int RamDriveExtId = -5;

    /// <summary><see cref="ExtId"/> of the stock generic drive icon (unknown drive kind).</summary>
    public const int OtherDriveExtId = -6;

    /// <summary><see cref="ExtId"/> of the stock "This PC" icon (tree node, ADR-0015).</summary>
    public const int ThisPcExtId = -7;

    /// <summary><see cref="ExtId"/> of the stock warning icon (SIID_WARNING; folder-access prompt, as in Explorer).</summary>
    public const int WarningExtId = -8;

    /// <summary><see cref="ExtId"/> of the stock UAC shield (SIID_SHIELD; a button that asks for elevation).</summary>
    public const int ShieldExtId = -9;

    /// <summary>The by-type key of the same item (its generic icon: the placeholder while a per-item icon loads).</summary>
    public IconKey Generic => this with { UniquePath = null, Thumbnail = null };

    /// <summary>Equality with the path-identity rule.</summary>
    /// <param name="other">Other key.</param>
    /// <returns>True when equal.</returns>
    public bool Equals(IconKey other)
        => ExtId == other.ExtId && Size == other.Size && Thumbnail == other.Thumbnail && PathUtil.Comparer.Equals(UniquePath, other.UniquePath);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(ExtId, Size, Thumbnail, UniquePath is null ? 0 : PathUtil.Comparer.GetHashCode(UniquePath));
}

/// <summary>A rendered icon: premultiplied BGRA, top-down rows, no UI types.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Bgra">Pixels, 4 bytes each.</param>
/// <param name="OverlayIndex">Shell overlay index composited into the pixels (0 = none).</param>
public sealed record IconImage(int Width, int Height, byte[] Bgra, int OverlayIndex = 0);

/// <summary>
/// Icons and type names for list rows and tree nodes (AC-1.14). Never blocks; results arrive through events off the UI
/// thread.
/// </summary>
public interface IIconService
{
    /// <summary>
    /// Looks up the icon of <paramref name="entry"/>. A miss queues the icon (most recent request first) and returns
    /// false; the row shows a placeholder until <see cref="IconsReady"/> reports the key.
    /// </summary>
    /// <param name="entry">Listed entry.</param>
    /// <param name="dir">Folder that contains the entry.</param>
    /// <param name="size">Image list.</param>
    /// <param name="key">Key to match against <see cref="IconsReady"/>.</param>
    /// <param name="image">The image; null with a true result means "failed, keep the placeholder".</param>
    /// <returns>True when resolved (successfully or not).</returns>
    bool TryGet(FileEntry entry, string dir, IconSize size, out IconKey key, out IconImage? image);

    /// <summary>
    /// Looks up a key from this service (<see cref="KeyForFolder"/>, or <see cref="IconKey.Generic"/> of a key the entry
    /// lookup returned); same rules as the entry lookup.
    /// </summary>
    /// <param name="key">Key.</param>
    /// <param name="image">The image; null with a true result means "failed, keep the placeholder".</param>
    /// <returns>True when resolved (successfully or not).</returns>
    bool TryGet(IconKey key, out IconImage? image);

    /// <summary>
    /// Key of a folder shown by path (folder tree, no I/O): a drive root gets its drive icon (per path on fixed drives,
    /// the stock icon of the drive kind elsewhere, so no media is touched); another folder its own icon when
    /// <paramref name="perItem"/> (Quick access: special folder icons) on a fixed drive, else the generic folder icon.
    /// </summary>
    /// <param name="path">Normalized folder path.</param>
    /// <param name="perItem">Whether the folder may have its own icon (desktop.ini).</param>
    /// <param name="size">Image list.</param>
    /// <returns>The key.</returns>
    IconKey KeyForFolder(string path, bool perItem, IconSize size);

    /// <summary>Moves queued keys for realized rows to the front of the queue.</summary>
    /// <param name="visible">Keys of the rows on screen.</param>
    void SetPriority(IReadOnlyCollection<IconKey> visible);

    /// <summary>
    /// Drops a queued lookup the caller no longer waits for (its row scrolled away); a lookup already running finishes.
    /// A dropped key is reported in <see cref="IconsReady"/> so other waiters for it request it again.
    /// </summary>
    /// <param name="key">Key.</param>
    void Cancel(IconKey key);

    /// <summary>Shell type name of <paramref name="entry"/>; "" while pending (<see cref="TypeNamesReady"/> follows).</summary>
    /// <param name="entry">Listed entry.</param>
    /// <returns>The type name or "".</returns>
    string TypeName(FileEntry entry);

    /// <summary>Raised off the UI thread with newly resolved keys, batched about every 30 ms.</summary>
    event Action<IReadOnlyList<IconKey>>? IconsReady;

    /// <summary>Raised off the UI thread when queued type names were resolved.</summary>
    event Action? TypeNamesReady;
}
