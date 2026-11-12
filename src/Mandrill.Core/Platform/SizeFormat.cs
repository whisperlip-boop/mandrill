using Windows.Win32;

namespace Mandrill.Core.Platform;

/// <summary>
/// Size text exactly as Explorer shows it (AC-1.23), from the shell's own formatters, so locale separators and units match.
/// Pure (no I/O) and UI-thread-safe.
/// </summary>
public static class SizeFormat
{
    /// <summary>Details "Size" column: whole kilobytes, rounded up ("0 KB", "1 KB", "1,234 KB"); StrFormatKBSizeW.</summary>
    /// <param name="bytes">Size in bytes (negative values are treated as 0).</param>
    /// <returns>The text.</returns>
    public static string Kb(long bytes)
    {
        Span<char> buffer = stackalloc char[64];
        var text = PInvoke.StrFormatKBSize(Math.Max(0, bytes), buffer);
        return text.ToString() ?? string.Empty;
    }

    /// <summary>Status bar size: bytes/KB/MB/GB with 3 significant digits ("1.20 MB"); StrFormatByteSizeW.</summary>
    /// <param name="bytes">Size in bytes (negative values are treated as 0).</param>
    /// <returns>The text.</returns>
    public static string Bytes(long bytes)
    {
        Span<char> buffer = stackalloc char[64];
        var text = PInvoke.StrFormatByteSize(Math.Max(0, bytes), buffer);
        return text.ToString() ?? string.Empty;
    }
}
