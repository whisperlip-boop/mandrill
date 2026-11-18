using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Microsoft.Extensions.Logging;

namespace Mandrill.Presentation.Panes;

// File → Export file list (ui-spec §10): what is shown, written by Core off the UI thread.
public sealed partial class PaneViewModel
{
    private static readonly string[] ExportHeaderKeys =
        ["export.column.name", "export.column.path", "export.column.modified", "export.column.type", "export.column.size"];

    /// <summary>
    /// Whether the pane shows a folder whose list can be exported: not This PC, not an error, and listed completely (a
    /// folder still loading would be exported in part).
    /// </summary>
    public bool CanExportList => !IsThisPc && CurrentPath is not null && Items.Current is { IsFinal: true };

    /// <summary>
    /// Writes the shown list to <paramref name="target"/>: the selection when two or more items are selected, else every
    /// shown item (sort order, quick filter and hidden-files setting as shown; no subfolders). The status bar reports the
    /// count or the reason it failed.
    /// </summary>
    /// <param name="target">The file chosen in the save dialog.</param>
    /// <param name="format">CSV or TXT.</param>
    /// <returns>Completes when written (or failed).</returns>
    public async Task ExportListAsync(string target, FileListFormat format)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (!CanExportList)
        {
            return;
        }

        string folder = CurrentPath!;
        IReadOnlyList<FileEntry> entries = Items.Selection.Count >= 2 ? Items.Selection.SelectedEntries() : Items.Current!.Visible;
        string[] headers = [.. ExportHeaderKeys.Select(k => Localizer.Current[k])];
        Func<FileEntry, string> typeName = _context.TypeName ?? (_ => string.Empty);
        FsError? error;
        try
        {
            if (format == FileListFormat.Csv && _context.EnsureTypeNames is { } ensure)
            {
                // A pending type name would be written as "" (architecture §5); a failure still exports with the cached ones.
                try
                {
                    await ensure([.. entries.Where(e => !e.IsDirectory).Select(e => e.ExtId).Distinct()], _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Resolving type names before an export failed");
                }
            }

            error = await FileListExport.ExportAsync(target, entries, folder, format, headers, typeName, _logger, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // pane disposed
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exporting the file list of {Folder} failed", folder);
            error = new FsError(FsErrorKind.Other, target, 0);
        }

        RunOnUi(() =>
        {
            if (_disposed)
            {
                return;
            }

            Status = error is null
                ? new PaneStatus("export.done", false, ("count", entries.Count), ("path", target))
                : error.Kind == FsErrorKind.Other
                    ? new PaneStatus("export.failed", true, ("path", target)) // "Cannot read" would be wrong for a write
                    : ErrorStatus(error);
        });
    }
}
