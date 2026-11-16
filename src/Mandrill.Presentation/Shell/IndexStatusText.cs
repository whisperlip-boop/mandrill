using System.Globalization;
using Mandrill.Core.Index;
using Mandrill.Core.Localization;

namespace Mandrill.Presentation.Shell;

/// <summary>Status-bar text and tooltip of the index indicator (AC-2.9, ui-spec §1 "Right: index state").</summary>
/// <param name="Text">The indicator text.</param>
/// <param name="Detail">Tooltip: last update, the drive being scanned or the error, a save warning; null when there is none.</param>
public sealed record IndexStatusText(string Text, string? Detail)
{
    /// <summary>
    /// Formats <paramref name="status"/> (empty text for <see cref="IndexState.Standby"/>): a localized text per state (with the item count or the percentage) and, as detail,
    /// the last update in local time plus <see cref="IndexStatus.Message"/>, which is unlocalized (drive root or error).
    /// </summary>
    /// <param name="status">The index status.</param>
    /// <param name="localizer">Localizer; null for <see cref="Localizer.Current"/>.</param>
    /// <param name="zone">Time zone of the last update; null for the local zone.</param>
    /// <returns>The text and the detail.</returns>
    public static IndexStatusText From(IndexStatus status, Localizer? localizer = null, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.State == IndexState.Standby)
        {
            return new IndexStatusText(string.Empty, null); // another instance owns the index: no indicator (§18 D2)
        }

        Localizer l = localizer ?? Localizer.Current;
        // Passed as "count" to Plural: it overrides Plural's raw count with the grouped form (Format keeps the last value
        // given for a name).
        string count = status.Items.ToString("N0", CultureInfo.CurrentCulture);
        string text = status.State switch
        {
            IndexState.Loading => l["indexStatus.loading"],
            IndexState.Scanning when status.Percent is { } percent => l.Format("indexStatus.scanningPercent", ("percent", percent)),
            IndexState.Scanning => l.Plural("indexStatus.scanningItems", status.Items, ("count", count)),
            IndexState.Paused => l["indexStatus.paused"],
            IndexState.Ready => l.Plural("indexStatus.ready", status.Items, ("count", count)),
            _ => l["indexStatus.error"],
        };

        var detail = new List<string>();
        if (status.LastUpdatedUtc is { } updated)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(updated, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local);
            detail.Add(l.Format("indexStatus.lastUpdated", ("time", local.ToString("g", CultureInfo.CurrentCulture))));
        }

        if (!string.IsNullOrEmpty(status.Message))
        {
            detail.Add(status.Message);
        }

        if (status.SaveFailed)
        {
            // E-2.14: the warning is shown, not only logged.
            text = l.Format("indexStatus.notSaved", ("state", text));
            detail.Add(l["indexStatus.notSavedDetail"]);
        }

        return new IndexStatusText(text, detail.Count == 0 ? null : string.Join(Environment.NewLine, detail));
    }
}
