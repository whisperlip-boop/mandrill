using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Mandrill.Core.IO;

/// <summary>File type written by <see cref="FileListExport"/>.</summary>
public enum FileListFormat
{
    /// <summary>Header row plus Name, Full path, Date modified, Type, Size in bytes (RFC 4180).</summary>
    Csv,

    /// <summary>One name per line.</summary>
    Txt,
}

/// <summary>
/// File → Export file list (ui-spec §10): the given entries of one folder as CSV or TXT, UTF-8 with BOM and CRLF line ends
/// (Excel reads non-ASCII names correctly only with the BOM), written atomically: a temp file next to the target, then a
/// replace. The date is local time as <c>yyyy-MM-dd HH:mm:ss</c> (locale-independent); folders have an empty size.
/// </summary>
public static class FileListExport
{
    /// <summary>Number of CSV columns (and header texts).</summary>
    public const int CsvColumns = 5;

    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>The file's bytes.</summary>
    /// <param name="entries">Entries in the order to write.</param>
    /// <param name="folder">The folder they are in (for the full path).</param>
    /// <param name="format">CSV or TXT.</param>
    /// <param name="headers">The <see cref="CsvColumns"/> CSV header texts (localized); ignored for TXT.</param>
    /// <param name="typeName">Type column text of an entry.</param>
    /// <returns>UTF-8 with BOM.</returns>
    /// <exception cref="ArgumentException">CSV with other than <see cref="CsvColumns"/> headers.</exception>
    public static byte[] Build(IReadOnlyList<FileEntry> entries, string folder, FileListFormat format, IReadOnlyList<string> headers, Func<FileEntry, string> typeName)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(typeName);
        var text = new StringBuilder();
        if (format == FileListFormat.Txt)
        {
            foreach (FileEntry e in entries)
            {
                text.Append(e.Name).Append("\r\n");
            }
        }
        else
        {
            if (headers.Count != CsvColumns)
            {
                throw new ArgumentException($"CSV needs {CsvColumns} headers.", nameof(headers));
            }

            AppendRow(text, headers);
            foreach (FileEntry e in entries)
            {
                AppendRow(text, [
                    e.Name,
                    PathUtil.Combine(folder, e.Name),
                    e.LastWriteUtcTicks > 0 ? new DateTime(e.LastWriteUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString(DateFormat, CultureInfo.InvariantCulture) : string.Empty,
                    typeName(e),
                    e.IsDirectory ? string.Empty : e.Size.ToString(CultureInfo.InvariantCulture),
                ]);
            }
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>Builds and writes the file on a worker thread, atomically; logs the outcome.</summary>
    /// <param name="target">File chosen in the save dialog (replaced when it exists).</param>
    /// <param name="entries">Entries in the order to write.</param>
    /// <param name="folder">The folder they are in.</param>
    /// <param name="format">CSV or TXT.</param>
    /// <param name="headers">CSV header texts.</param>
    /// <param name="typeName">Type column text of an entry; called on the worker thread.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="ct">Cancels before the write starts.</param>
    /// <returns>Null on success, else the error.</returns>
    public static Task<FsError?> ExportAsync(string target, IReadOnlyList<FileEntry> entries, string folder, FileListFormat format, IReadOnlyList<string> headers, Func<FileEntry, string> typeName, ILogger logger, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(logger);
        return Task.Run(() => Export(target, entries, folder, format, headers, typeName, logger), ct);
    }

    private static FsError? Export(string target, IReadOnlyList<FileEntry> entries, string folder, FileListFormat format, IReadOnlyList<string> headers, Func<FileEntry, string> typeName, ILogger logger)
    {
        string tmp = $"{target}.tmp.{Environment.ProcessId}";
        try
        {
            byte[] bytes = Build(entries, folder, format, headers, typeName);
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tmp, target, overwrite: true);
            logger.LogInformation("Exported {Count} entries of {Folder} as {Format} to {File}", entries.Count, folder, format, target);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            logger.LogError(ex, "Exporting the file list of {Folder} to {File} failed", folder, target);
            try
            {
                File.Delete(tmp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(cleanup, "Removing the temp file {File} failed", tmp);
            }

            return FsErrors.FromException(ex, target);
        }
    }

    // RFC 4180: a field with a quote, comma, CR or LF is quoted, its quotes doubled; rows end with CRLF.
    private static void AppendRow(StringBuilder text, IReadOnlyList<string> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            string field = fields[i];
            if (field.AsSpan().IndexOfAny("\",\r\n") >= 0)
            {
                text.Append('"').Append(field.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                text.Append(field);
            }
        }

        text.Append("\r\n");
    }
}
