using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mandrill.Core.Platform;

/// <summary>Explorer's hidden-file settings (AC-1.24).</summary>
/// <param name="ShowHidden">"Show hidden files, folders and drives".</param>
/// <param name="ShowProtectedSystem">Inverse of "Hide protected operating system files".</param>
public sealed record HiddenFilesSetting(bool ShowHidden, bool ShowProtectedSystem);

/// <summary>Reads Explorer folder options.</summary>
public static class ExplorerSettings
{
    /// <summary>
    /// Current hidden-file settings via SHGetSetSettings (SSF_SHOWALLOBJECTS | SSF_SHOWSUPERHIDDEN). Read on every scan
    /// request so a change in Explorer is picked up on refresh. Reads the user's settings only; never writes them.
    /// </summary>
    /// <returns>The settings.</returns>
    public static HiddenFilesSetting ReadHiddenFiles()
    {
        var state = default(SHELLSTATEA);
        PInvoke.SHGetSetSettings(ref state, SSF_MASK.SSF_SHOWALLOBJECTS | SSF_MASK.SSF_SHOWSUPERHIDDEN, false);
        return new HiddenFilesSetting(state.fShowAllObjects, state.fShowSuperHidden);
    }

    /// <summary>
    /// Whether the user wants a confirmation before moving items to the Recycle Bin (Recycle Bin properties, "Display delete
    /// confirmation dialog"; SSF_NOCONFIRMRECYCLE). Explorer follows it; so does Mandrill's Del (WP-3.2).
    /// </summary>
    /// <returns>True when the confirmation is on.</returns>
    public static bool ReadConfirmRecycle()
    {
        var state = default(SHELLSTATEA);
        PInvoke.SHGetSetSettings(ref state, SSF_MASK.SSF_NOCONFIRMRECYCLE, false);
        return !state.fNoConfirmRecycle;
    }

    /// <summary>
    /// The Windows icon-title font, which Explorer uses for its items (SPI_GETICONTITLELOGFONT at 96 DPI), e.g.
    /// ("Segoe UI", 12) or ("맑은 고딕", 12). No disk access.
    /// </summary>
    /// <returns>Face name and size in DIPs, or null when Windows reports none.</returns>
    public static unsafe (string Face, double Size)? ReadIconTitleFont()
    {
        var font = default(LOGFONTW);
        if (!PInvoke.SystemParametersInfoForDpi((uint)SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETICONTITLELOGFONT, (uint)sizeof(LOGFONTW), &font, 0, 96))
        {
            return null;
        }

        // A negative height is the em height in pixels (the usual form); a positive one is the cell height, close enough.
        string face = font.lfFaceName.ToString();
        return face.Length == 0 || font.lfHeight == 0 ? null : (face, Math.Abs(font.lfHeight));
    }
}
