using System.IO.Enumeration;
using Mandrill.Core.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Core.Index;

/// <summary>
/// Lists directories for the index with Win32 enumeration (AC-2.1). The enumerator copies each name span straight into
/// the <see cref="IndexEntryBuffer"/>: no string or object per entry (ADR-0008). Long paths go through
/// <see cref="PathUtil.ToIo"/> (E-2.5). Only "not found" makes a directory an empty, complete subtree; any other I/O
/// failure (not ready, sharing, device, network) leaves it incomplete, so search lists it live instead of trusting an
/// empty index (§8.4). Index thread only.
/// </summary>
public sealed class Win32IndexSource : IIndexSource
{
    private const int ErrorFileNotFound = unchecked((int)0x80070002);
    private const int ErrorPathNotFound = unchecked((int)0x80070003);

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly FsErrorLog _errors;

    /// <summary>Creates the source.</summary>
    /// <param name="logger">Logger for listing failures (once per path and kind per minute); null disables logging.</param>
    public Win32IndexSource(ILogger? logger = null)
    {
        _errors = new FsErrorLog(logger ?? NullLogger.Instance);
    }

    /// <summary>How a listing failure maps to an outcome: only "not found" is an empty, complete directory.</summary>
    /// <param name="ex">The exception thrown while listing.</param>
    /// <returns>The outcome.</returns>
    internal static ListOutcome Classify(Exception ex) => ex switch
    {
        UnauthorizedAccessException => ListOutcome.AccessDenied,
        DirectoryNotFoundException or FileNotFoundException => ListOutcome.NotFound,
        IOException { HResult: ErrorFileNotFound or ErrorPathNotFound } => ListOutcome.NotFound,
        _ => ListOutcome.Unavailable, // not ready, sharing, device, network, a file at the path, an odd name
    };

    /// <inheritdoc/>
    public ListOutcome List(string directory, IndexEntryBuffer buffer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(buffer);
        buffer.Clear();
        ct.ThrowIfCancellationRequested();
        try
        {
            using var enumerator = new Enumerator(PathUtil.ToIo(directory), buffer, ct);
            while (enumerator.MoveNext())
            {
                // Entries land in the buffer inside TransformEntry; nothing to collect here.
            }

            return ListOutcome.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            buffer.Clear();
            ListOutcome outcome = Classify(ex);
            if (outcome == ListOutcome.Unavailable)
            {
                _errors.Report("Index", FsErrors.FromException(ex, directory));
            }

            return outcome;
        }
    }

    private sealed class Enumerator(string directory, IndexEntryBuffer buffer, CancellationToken ct) : FileSystemEnumerator<int>(directory, Options)
    {
        protected override int TransformEntry(ref FileSystemEntry entry)
        {
            if ((buffer.Count & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested(); // a single huge folder stays cancellable
            }

            FileAttributes attributes = entry.Attributes;
            IndexFlags flags = IndexFlags.None;
            if (entry.IsDirectory)
            {
                flags |= IndexFlags.Directory;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                flags |= IndexFlags.Reparse;
            }

            if ((attributes & FileAttributes.Hidden) != 0)
            {
                flags |= IndexFlags.Hidden;
            }

            if ((attributes & FileAttributes.System) != 0)
            {
                flags |= IndexFlags.System;
            }

            buffer.Add(entry.FileName, entry.IsDirectory ? 0 : entry.Length, IndexEntryBuffer.ToUnixSeconds(entry.LastWriteTimeUtc), flags);
            return 0;
        }
    }
}
