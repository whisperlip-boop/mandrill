using Mandrill.Presentation.Search;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;

namespace Mandrill.App.Platform;

/// <summary>Text to the clipboard through the WinRT clipboard (search "Copy path", WP-2.2b). UI thread.</summary>
/// <param name="logger">Logger for clipboard failures (another app holding the clipboard).</param>
public sealed class WinUiClipboard(ILogger logger) : IClipboardText
{
    /// <inheritdoc/>
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush(); // keep the text after the app exits, like Explorer's "Copy as path"
        }
        catch (Exception ex)
        {
            // Usually the clipboard held by another app for a moment (CsWinRT does not map every HRESULT to COMException):
            // nothing to recover, and a menu click must never crash the app; the user can copy again.
            logger.LogWarning(ex, "Copying to the clipboard failed");
        }
    }
}
