using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mandrill.Core.Platform;

/// <summary>
/// Makes one top-level window owned by another (architecture §3 WP-2.2b: the search popup stays above the main window
/// and minimizes with it). WinUI windows have no owner property, so this sets GWLP_HWNDPARENT; the App passes the window
/// handles (no WinUI types here).
/// </summary>
public static class WindowOwner
{
    /// <summary>Sets the owner of <paramref name="owned"/> to <paramref name="owner"/> (0 removes the owner).</summary>
    /// <param name="owned">Handle of the window that gets an owner.</param>
    /// <param name="owner">Handle of the owner window, or 0.</param>
    /// <returns>False when Windows refused (e.g. an invalid handle); the window then simply has no owner.</returns>
    /// <exception cref="ArgumentException"><paramref name="owned"/> is 0.</exception>
    public static bool Set(nint owned, nint owner)
    {
        if (owned == 0)
        {
            throw new ArgumentException("A window handle is required.", nameof(owned));
        }

        // SetWindowLongPtr returns the previous value, which is 0 for a window that had no owner: the last error tells.
        Marshal.SetLastPInvokeError(0);
        nint previous = PInvoke.SetWindowLongPtr(new HWND(owned), WINDOW_LONG_PTR_INDEX.GWLP_HWNDPARENT, owner);
        return previous != 0 || Marshal.GetLastPInvokeError() == 0;
    }
}
