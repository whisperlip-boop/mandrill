using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Mandrill.Core.Shell;

/// <summary>
/// Info tips through IQueryInfo (IShellItem.BindToHandler with BHID_SFUIObject), on an own "infotip" STA so a slow
/// handler (a DICOM extension parsing an 8 MB file) holds up neither icons nor opening; a hung handler costs one
/// timeout and a fresh worker (<see cref="ShellSta"/>). Failures are logged without the tip text.
/// </summary>
public sealed class ShellInfoTips : IInfoTipService, IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    // Offline | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: an online-only cloud placeholder.
    private const FileAttributes CloudPlaceholder = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    private readonly ShellSta _sta;
    private readonly ILogger _logger;

    /// <summary>Creates the service and its STA.</summary>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public ShellInfoTips(ILoggerFactory? loggerFactory = null)
    {
        _sta = new ShellSta("infotip", loggerFactory);
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ShellInfoTips>();
    }

    /// <inheritdoc/>
    public async Task<string?> GetInfoTipAsync(string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return await _sta.InvokeAsync(() => Read(path), ReadTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Info tip for {Path} timed out; the STA is replaced", path); // a hung shell extension
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Expected per click (a vanished search hit, an item without IQueryInfo), so Debug. HRESULT only: an exception
            // message from a third-party handler could quote the file's content.
            _logger.LogDebug("Info tip for {Path} failed: {Kind} (0x{HResult:X8})", path, ex.GetType().Name, ex.HResult);
            return null;
        }
    }

    /// <summary>Stops the STA.</summary>
    public void Dispose() => _sta.Dispose();

    private static unsafe string? Read(string path)
    {
        // A content-parsing handler (the DICOM one reads the file) would download an online-only file; attributes do not.
        if ((File.GetAttributes(path) & CloudPlaceholder) != 0)
        {
            return null;
        }

        Guid iid = typeof(IShellItem).GUID;
        object item;
        fixed (char* p = path)
        {
            PInvoke.SHCreateItemFromParsingName(p, null, &iid, out item).ThrowOnFailure();
        }

        IQueryInfo? handler = null;
        try
        {
            ((IShellItem)item).BindToHandler(null, PInvoke.BHID_SFUIObject, out handler);
            handler.GetInfoTip(QITIPF_FLAGS.QITIPF_DEFAULT, out PWSTR tip); // the probe found Explorer's text with the default
            try
            {
                return tip.Value is null ? null : tip.ToString();
            }
            finally
            {
                PInvoke.CoTaskMemFree(tip.Value);
            }
        }
        finally
        {
            if (handler is not null)
            {
                Marshal.ReleaseComObject(handler);
            }

            Marshal.ReleaseComObject(item);
        }
    }
}
