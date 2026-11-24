using System.Globalization;
using Mandrill.Core.Localization;
using Mandrill.Core.Platform;

namespace Mandrill.Presentation.Common;

/// <summary>
/// The status bar's selection summary, one format for the panes and the search popup: "3 selected (1.2 MB)", with the
/// drive's free space inside the parentheses when it is known (owner WP4): "3 selected (1.2 MB, Free disk space: 123 GB)".
/// </summary>
public static class SelectionText
{
    /// <summary>"N selected (size[, free space])" for a selection, "" when nothing is selected.</summary>
    /// <param name="count">Selected items.</param>
    /// <param name="bytes">Their total size (folders count 0).</param>
    /// <param name="freeBytes">The drive's free space for the user, or null to leave it out.</param>
    /// <param name="localizer">Localizer; null for <see cref="Localizer.Current"/>.</param>
    /// <returns>The summary.</returns>
    public static string Summary(int count, long bytes, long? freeBytes = null, Localizer? localizer = null)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        Localizer l = localizer ?? Localizer.Current;
        string size = freeBytes is { } free
            ? l.Format("status.freeSpace", ("size", SizeFormat.Bytes(bytes)), ("free", SizeFormat.Bytes(free)))
            : SizeFormat.Bytes(bytes);
        return l.Plural("status.selected", count, ("count", count.ToString("N0", CultureInfo.CurrentCulture)), ("size", size));
    }
}
