using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mandrill.Core.Shell;

/// <summary>System image-list index and shell overlay index of one item (overlay 0 = none, else 1-based).</summary>
/// <param name="Index">Index into the system image lists (same for every size).</param>
/// <param name="Overlay">Shell overlay index from SHGFI_OVERLAYINDEX.</param>
internal readonly record struct IconLookup(int Index, int Overlay);

/// <summary>
/// Raw shell icon and type-name calls (architecture §9). Every method must run on an STA thread with OLE initialized
/// (the "icons" <see cref="ShellSta"/>); none of them throws for an unknown item, they return null instead.
/// </summary>
internal static class ShellIconReader
{
    /// <summary>The by-type lookup's stand-in for a folder ("folder" + FILE_ATTRIBUTE_DIRECTORY; no disk access).</summary>
    private const string FolderName = "folder";

    /// <summary>System icon index for a type (SHGFI_USEFILEATTRIBUTES: no disk access, no overlay).</summary>
    /// <param name="isDirectory">True for a folder.</param>
    /// <param name="extension">Extension with the dot (".txt"), or "" for none.</param>
    /// <returns>The lookup, or null when the shell has none.</returns>
    public static IconLookup? LookupByType(bool isDirectory, string extension)
    {
        var info = default(SHFILEINFOW);
        nuint ok = PInvoke.SHGetFileInfo(
            isDirectory ? FolderName : "file" + extension,
            isDirectory ? FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY : FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            ref info,
            SHGFI_FLAGS.SHGFI_USEFILEATTRIBUTES | SHGFI_FLAGS.SHGFI_SYSICONINDEX);
        return ok == 0 ? null : new IconLookup(info.iIcon & 0x00FFFFFF, 0);
    }

    /// <summary>
    /// Path-based icon index plus the shell overlay index (shortcut arrow on .lnk files, junctions and symlinks). Reads
    /// the item, so it may be slow or hang inside a shell extension; callers run it under a timeout.
    /// </summary>
    /// <param name="path">Plain path shorter than 260 characters.</param>
    /// <returns>The lookup, or null when the shell cannot resolve the path.</returns>
    public static IconLookup? LookupByPath(string path)
    {
        var info = default(SHFILEINFOW);
        // SHGFI_OVERLAYINDEX is documented to need SHGFI_ICON; the small icon is the cheapest one to create and drop.
        nuint ok = PInvoke.SHGetFileInfo(
            path,
            0,
            ref info,
            SHGFI_FLAGS.SHGFI_SYSICONINDEX | SHGFI_FLAGS.SHGFI_OVERLAYINDEX | SHGFI_FLAGS.SHGFI_ICON | SHGFI_FLAGS.SHGFI_SMALLICON);
        if (!info.hIcon.IsNull)
        {
            PInvoke.DestroyIcon(info.hIcon);
        }

        return ok == 0 ? null : new IconLookup(info.iIcon & 0x00FFFFFF, (int)((uint)info.iIcon >> 24));
    }

    /// <summary>System icon index of a stock shell icon (drive kinds in the folder tree); no disk or media access.</summary>
    /// <param name="id">Stock icon.</param>
    /// <returns>The lookup, or null when the shell has none.</returns>
    public static unsafe IconLookup? LookupStock(SHSTOCKICONID id)
    {
        var info = new SHSTOCKICONINFO { cbSize = (uint)sizeof(SHSTOCKICONINFO) };
        HRESULT hr = PInvoke.SHGetStockIconInfo(id, SHGSI_FLAGS.SHGSI_SYSICONINDEX, ref info);
        return hr.Succeeded ? new IconLookup(info.iSysImageIndex, 0) : null;
    }

    /// <summary>Shell type name ("Text Document", "File folder") for a type; no disk access.</summary>
    /// <param name="isDirectory">True for a folder.</param>
    /// <param name="extension">Extension with the dot, or "" for none.</param>
    /// <returns>The type name, or null when the shell has none.</returns>
    public static string? TypeName(bool isDirectory, string extension)
    {
        var info = default(SHFILEINFOW);
        nuint ok = PInvoke.SHGetFileInfo(
            isDirectory ? FolderName : "file" + extension,
            isDirectory ? FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY : FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            ref info,
            SHGFI_FLAGS.SHGFI_USEFILEATTRIBUTES | SHGFI_FLAGS.SHGFI_TYPENAME);
        return ok == 0 ? null : info.szTypeName.ToString();
    }

    /// <summary>
    /// Draws image <paramref name="lookup"/> from the system image list of <paramref name="size"/> as premultiplied
    /// BGRA, with the shell overlay (if any) composited over it.
    /// </summary>
    /// <param name="lookup">Icon and overlay index.</param>
    /// <param name="size">Image list to use.</param>
    /// <returns>The image, or null when the list or icon is unavailable.</returns>
    public static IconImage? Render(IconLookup lookup, IconSize size)
    {
        var list = GetImageList(size);
        if (list is null)
        {
            return null;
        }

        try
        {
            IconImage? image;
            try
            {
                image = DrawIcon(list, lookup.Index);
            }
            catch (COMException)
            {
                return null; // the generated IImageList methods throw on failure; callers expect null
            }

            if (image is null || lookup.Overlay <= 0)
            {
                return image;
            }

            IconImage? overlay;
            try
            {
                list.GetOverlayImage(lookup.Overlay, out int overlayIndex);
                overlay = overlayIndex >= 0 ? DrawIcon(list, overlayIndex) : null;
            }
            catch (COMException)
            {
                overlay = null; // an overlay the list does not know: the base icon is still right
            }

            if (overlay is null || overlay.Width != image.Width || overlay.Height != image.Height)
            {
                return image;
            }

            CompositeOver(image.Bgra, overlay.Bgra);
            return image with { OverlayIndex = lookup.Overlay };
        }
        finally
        {
            Marshal.ReleaseComObject(list);
        }
    }

    /// <summary>
    /// True when the type has a thumbnail handler through its association (extension, ProgID, SystemFileAssociations,
    /// perceived type): IThumbnailProvider, or the older IExtractImage (MicroDicom's .dcm). Registry only, loads no handler.
    /// </summary>
    /// <param name="extension">Extension with the dot (".dcm").</param>
    /// <returns>True when Explorer would ask a handler for a thumbnail.</returns>
    public static bool HasThumbnailHandler(string extension)
    {
        foreach (string iid in (ReadOnlySpan<string>)["{e357fccd-a995-4576-b01f-234630154e96}", "{BB2E617C-0920-11d1-9A0B-00C04FC2D6C1}"])
        {
            uint length = 0;
            // Without a buffer the call only measures: S_FALSE when the handler exists, ERROR_NO_ASSOCIATION when not.
            if (PInvoke.AssocQueryString(ASSOCF.ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR.ASSOCSTR_SHELLEXTENSION, extension, iid, null, ref length).Succeeded)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The shell thumbnail of a file (owner #7), fitted into <paramref name="pixels"/> square with its aspect ratio, as
    /// premultiplied BGRA. Thumbnail only: a type without a thumbnail handler gives null, and the caller shows the icon.
    /// Reads the file (thumbnail cache or decoder), so callers run it under a timeout.
    /// </summary>
    /// <param name="path">Plain path shorter than 260 characters.</param>
    /// <param name="pixels">Longest side wanted.</param>
    /// <param name="logger">Gets the reason (Debug) when the shell or a thumbnail handler throws.</param>
    /// <returns>The image, or null when there is no thumbnail.</returns>
    public static unsafe IconImage? Thumbnail(string path, int pixels, ILogger logger)
    {
        Guid iid = typeof(IShellItemImageFactory).GUID;
        object item;
        fixed (char* p = path)
        {
            if (PInvoke.SHCreateItemFromParsingName(p, null, &iid, out item).Failed)
            {
                return null;
            }
        }

        HBITMAP bitmap = default;
        try
        {
            ((IShellItemImageFactory)item).GetImage(new SIZE { cx = pixels, cy = pixels }, SIIGBF.SIIGBF_THUMBNAILONLY, &bitmap);
            BITMAP info = default;
            if (bitmap.IsNull || PInvoke.GetObject(bitmap, sizeof(BITMAP), &info) == 0 || ReadBitmap(bitmap, info.bmWidth, Math.Abs(info.bmHeight)) is not { } bgra)
            {
                return null;
            }

            return new IconImage(info.bmWidth, Math.Abs(info.bmHeight), ToPremultiplied(bgra));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No thumbnail handler, extraction failed, or a handler threw something else (cast, I/O): the type icon shows.
            logger.LogDebug("Thumbnail {Path}: {Error} (0x{HResult:X8})", path, ex.Message, ex.HResult);
            return null;
        }
        finally
        {
            if (!bitmap.IsNull)
            {
                PInvoke.DeleteObject(bitmap);
            }

            Marshal.ReleaseComObject(item);
        }
    }

    private static unsafe IImageList? GetImageList(IconSize size)
    {
        int shil = (int)(size switch
        {
            IconSize.Small => PInvoke.SHIL_SMALL,
            IconSize.Medium => PInvoke.SHIL_LARGE,
            IconSize.Large => PInvoke.SHIL_EXTRALARGE,
            _ => PInvoke.SHIL_JUMBO,
        });
        var iid = typeof(IImageList).GUID;
        return PInvoke.SHGetImageList(shil, &iid, out object list).Succeeded ? list as IImageList : null;
    }

    private static unsafe IconImage? DrawIcon(IImageList list, int index)
    {
        HICON icon = default;
        list.GetIcon(index, (uint)IMAGE_LIST_DRAW_STYLE.ILD_TRANSPARENT, &icon);
        if (icon.IsNull)
        {
            return null;
        }

        ICONINFO info = default;
        try
        {
            if (!PInvoke.GetIconInfo(icon, &info) || info.hbmColor.IsNull)
            {
                return null; // monochrome icons (no colour bitmap) fall back to the placeholder
            }

            list.GetIconSize(out int width, out int height);
            byte[]? bgra = ReadBitmap(info.hbmColor, width, height);
            if (bgra is null)
            {
                return null;
            }

            if (!HasAlpha(bgra))
            {
                // Legacy icon without an alpha channel: transparency comes from the AND mask (white = transparent).
                byte[]? mask = info.hbmMask.IsNull ? null : ReadBitmap(info.hbmMask, width, height);
                for (int i = 0; i < bgra.Length; i += 4)
                {
                    bool transparent = mask is not null && mask[i] != 0;
                    bgra[i + 3] = transparent ? (byte)0 : (byte)255;
                }
            }

            Premultiply(bgra);
            return new IconImage(width, height, bgra);
        }
        finally
        {
            if (!info.hbmColor.IsNull)
            {
                PInvoke.DeleteObject(info.hbmColor);
            }

            if (!info.hbmMask.IsNull)
            {
                PInvoke.DeleteObject(info.hbmMask);
            }

            PInvoke.DestroyIcon(icon);
        }
    }

    // Top-down 32 bpp copy; GetDIBits converts the monochrome mask too (0 → black, 1 → white).
    private static unsafe byte[]? ReadBitmap(HBITMAP bitmap, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var header = new BITMAPINFO();
        header.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        header.bmiHeader.biWidth = width;
        header.bmiHeader.biHeight = -height;
        header.bmiHeader.biPlanes = 1;
        header.bmiHeader.biBitCount = 32;
        header.bmiHeader.biCompression = 0; // BI_RGB
        var pixels = new byte[width * height * 4];
        var dc = PInvoke.GetDC(default);
        try
        {
            fixed (byte* p = pixels)
            {
                int lines = PInvoke.GetDIBits(dc, bitmap, 0, (uint)height, p, &header, DIB_USAGE.DIB_RGB_COLORS);
                return lines == height ? pixels : null;
            }
        }
        finally
        {
            PInvoke.ReleaseDC(default, dc);
        }
    }

    private static bool HasAlpha(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    // A thumbnail bitmap: an opaque photo comes as RGB with alpha 0; a transparent one is premultiplied already, unless a
    // colour exceeds its alpha somewhere (then it is straight alpha).
    internal static byte[] ToPremultiplied(byte[] bgra)
    {
        if (!HasAlpha(bgra))
        {
            for (int i = 3; i < bgra.Length; i += 4)
            {
                bgra[i] = 255;
            }

            return bgra;
        }

        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a)
            {
                Premultiply(bgra);
                break;
            }
        }

        return bgra;
    }

    private static void Premultiply(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255)
            {
                continue;
            }

            bgra[i] = (byte)((bgra[i] * a + 127) / 255);
            bgra[i + 1] = (byte)((bgra[i + 1] * a + 127) / 255);
            bgra[i + 2] = (byte)((bgra[i + 2] * a + 127) / 255);
        }
    }

    // Porter-Duff "over" on premultiplied pixels: dst = src + dst × (1 − srcAlpha).
    internal static void CompositeOver(byte[] dst, byte[] src)
    {
        for (int i = 0; i < dst.Length; i += 4)
        {
            int inv = 255 - src[i + 3];
            for (int c = 0; c < 4; c++)
            {
                dst[i + c] = (byte)Math.Min(255, src[i + c] + ((dst[i + c] * inv) + 127) / 255);
            }
        }
    }
}
