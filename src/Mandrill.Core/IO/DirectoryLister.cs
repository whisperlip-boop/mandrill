using System.IO.Enumeration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Core.IO;

/// <summary>Outcome of <see cref="IDirectoryLister.List"/>.</summary>
/// <param name="SkippedEntries">Entries skipped while listing. Always 0 today (kept for the contract): a failed directory
/// read cannot be resumed, so it is reported as <paramref name="Error"/> instead of being counted.</param>
/// <param name="Error">Why listing failed; batches already delivered are then partial. Null on success.</param>
public sealed record ListResult(int SkippedEntries, FsError? Error);

/// <summary>Directory enumeration for panes and the tree. Worker threads only.</summary>
public interface IDirectoryLister
{
    /// <summary>Lists <paramref name="dir"/> in batches of at most 256 entries. Never follows into subfolders.</summary>
    /// <param name="dir">Normalized folder path.</param>
    /// <param name="dirsOnly">True to return folders only (tree).</param>
    /// <param name="onBatch">Receives each batch on the calling thread.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Skipped count and error.</returns>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    ListResult List(string dir, bool dirsOnly, Action<FileEntry[]> onBatch, CancellationToken ct);

    /// <summary>Looks up specific names in <paramref name="dir"/> (partial refresh after change events).</summary>
    /// <param name="dir">Normalized folder path.</param>
    /// <param name="names">Names to look up.</param>
    /// <returns>Entries that exist, and names that do not.</returns>
    (IReadOnlyList<FileEntry> Present, IReadOnlyList<string> Missing) StatNames(string dir, IReadOnlyCollection<string> names);

    /// <summary>The path itself if it is an existing folder, else its nearest existing ancestor; null when even the root is gone.</summary>
    /// <param name="path">Normalized path.</param>
    /// <returns>An existing folder or null.</returns>
    string? NearestExistingDirectory(string path);
}

/// <summary>
/// <see cref="IDirectoryLister"/> on <see cref="FileSystemEnumerator{TResult}"/>: one native call per batch of entries
/// yields name, attributes, size and time, with no per-entry stat. Reparse points are reported, never traversed
/// (E-1.7), and paths go through <see cref="PathUtil.ToIo"/> (E-1.6, E-1.11).
/// </summary>
public sealed class DirectoryLister : IDirectoryLister
{
    /// <summary>Maximum entries per <c>onBatch</c> call.</summary>
    public const int BatchSize = 256;

    private readonly ExtensionTable _extensions;
    private readonly FsErrorLog _errors;
    private readonly ILogger _logger;

    /// <summary>Creates a lister.</summary>
    /// <param name="extensions">Extension table (normally <see cref="ExtensionTable.Shared"/>).</param>
    /// <param name="logger">Logger; errors are throttled per path.</param>
    public DirectoryLister(ExtensionTable extensions, ILogger<DirectoryLister>? logger = null)
    {
        _extensions = extensions ?? throw new ArgumentNullException(nameof(extensions));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _errors = new FsErrorLog(_logger);
    }

    /// <inheritdoc />
    public ListResult List(string dir, bool dirsOnly, Action<FileEntry[]> onBatch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(onBatch);
        ct.ThrowIfCancellationRequested();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        var batch = new List<FileEntry>(BatchSize);
        try
        {
            using var enumerator = new EntryEnumerator(PathUtil.ToIo(dir), options, _extensions, dirsOnly);
            while (enumerator.MoveNext())
            {
                batch.Add(enumerator.Current);
                if (batch.Count == BatchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    onBatch(batch.ToArray());
                    batch.Clear();
                }
            }

            ct.ThrowIfCancellationRequested();
            if (batch.Count > 0)
            {
                onBatch(batch.ToArray());
            }

            return new ListResult(0, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var error = ClassifyListError(ex, dir);
            _errors.Report("List", error);
            return new ListResult(0, error);
        }
    }

    /// <inheritdoc />
    public (IReadOnlyList<FileEntry> Present, IReadOnlyList<string> Missing) StatNames(string dir, IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(names);
        var present = new List<FileEntry>(names.Count);
        var missing = new List<string>();
        foreach (string name in names)
        {
            var entry = PathUtil.IsValidSegment(name) ? Stat(PathUtil.Combine(dir, name), name) : null;
            if (entry is null)
            {
                missing.Add(name);
            }
            else
            {
                present.Add(entry);
            }
        }

        return (present, missing);
    }

    /// <inheritdoc />
    public string? NearestExistingDirectory(string path)
    {
        for (string? p = path; p is not null; p = PathUtil.Parent(p))
        {
            if (Directory.Exists(PathUtil.ToIo(p)))
            {
                return p;
            }
        }

        return null;
    }

    private FileEntry? Stat(string path, string name)
    {
        try
        {
            // One stat: FileInfo.Attributes is -1 when nothing (file or folder) exists at the path.
            var info = new FileInfo(PathUtil.ToIo(path));
            var attributes = info.Attributes;
            if ((int)attributes == -1)
            {
                return null;
            }

            bool isDir = (attributes & FileAttributes.Directory) != 0;
            return new FileEntry(name, attributes, isDir ? 0 : info.Length, info.LastWriteTimeUtc.Ticks, isDir ? ExtensionTable.None : _extensions.IdOf(name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _errors.Report("Stat", FsErrors.FromException(ex, path));
            return null;
        }
    }

    // Distinguishes "not a folder" (a file at that path) from "not found", which Win32 reports alike for enumeration.
    private static FsError ClassifyListError(Exception ex, string dir)
    {
        var error = FsErrors.FromException(ex, dir);
        if (error.Kind is (FsErrorKind.NotFound or FsErrorKind.Other or FsErrorKind.InvalidPath) && File.Exists(PathUtil.ToIo(dir)))
        {
            return new FsError(FsErrorKind.NotADirectory, dir, 267);
        }

        return error;
    }

    private sealed class EntryEnumerator : FileSystemEnumerator<FileEntry>
    {
        private readonly ExtensionTable _extensions;
        private readonly bool _dirsOnly;

        public EntryEnumerator(string directory, EnumerationOptions options, ExtensionTable extensions, bool dirsOnly)
            : base(directory, options)
        {
            _extensions = extensions;
            _dirsOnly = dirsOnly;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry) => !_dirsOnly || entry.IsDirectory;

        protected override FileEntry TransformEntry(ref FileSystemEntry entry)
        {
            bool isDir = entry.IsDirectory;
            return new FileEntry(
                entry.FileName.ToString(),
                entry.Attributes,
                isDir ? 0 : entry.Length,
                entry.LastWriteTimeUtc.UtcTicks,
                isDir ? ExtensionTable.None : _extensions.IdOf(entry.FileName));
        }

        // Non-recursive enumeration calls this only for the folder open and for a failed directory read, and neither
        // can be resumed: returning true would end the listing silently and report a partial folder as complete.
        // Throwing turns the failure into an FsError (e.g. NotReady when a USB drive is pulled).
        protected override bool ContinueOnError(int error) => false;
    }
}
