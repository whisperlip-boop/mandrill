using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;

namespace Mandrill.Core.IO;

/// <summary>
/// File operations of the panes (architecture §7): new folder/file, rename, copy, move, delete. The app runs them through the
/// shell's IFileOperation (<c>Core.Shell.ShellFileOperations</c>, WP-3.2: native progress, conflict dialogs, undo record).
/// </summary>
public interface IFileOperations
{
    /// <summary>Creates "<paramref name="baseName"/>", or "<paramref name="baseName"/> (2)", "(3)", … if taken.</summary>
    /// <param name="parent">Normalized parent folder.</param>
    /// <param name="baseName">Localized base name ("New folder").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new folder's path, or the error.</returns>
    Task<FsResult<string>> CreateUniqueFolderAsync(string parent, string baseName, CancellationToken ct);

    /// <summary>Renames a file or folder within its folder.</summary>
    /// <param name="path">Normalized path of the item.</param>
    /// <param name="newName">New name (one segment).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new path, or the error.</returns>
    Task<FsResult<string>> RenameAsync(string path, string newName, CancellationToken ct);

    /// <summary>Creates an empty file "<paramref name="baseName"/>", or "name (2).ext", … if taken.</summary>
    /// <param name="parent">Normalized parent folder.</param>
    /// <param name="baseName">Localized file name with extension ("New Text Document.txt").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new file's path, or the error.</returns>
    Task<FsResult<string>> CreateUniqueFileAsync(string parent, string baseName, CancellationToken ct);

    /// <summary>Copies items into a folder (conflicts are resolved by the shell's dialog).</summary>
    /// <param name="sources">Normalized paths of the items.</param>
    /// <param name="destination">Normalized destination folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<FileOperationResult> CopyAsync(IReadOnlyList<string> sources, string destination, CancellationToken ct);

    /// <summary>Moves items into a folder (conflicts are resolved by the shell's dialog).</summary>
    /// <param name="sources">Normalized paths of the items.</param>
    /// <param name="destination">Normalized destination folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<FileOperationResult> MoveAsync(IReadOnlyList<string> sources, string destination, CancellationToken ct);

    /// <summary>Deletes items: to the Recycle Bin, or permanently after the shell's confirmation (AC-3.8).</summary>
    /// <param name="paths">Normalized paths of the items.</param>
    /// <param name="permanent">Shift+Del: skip the Recycle Bin.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<FileOperationResult> DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken ct);

    /// <summary>
    /// <see cref="CopyAsync(IReadOnlyList{string}, string, CancellationToken)"/> with the shell's dialogs owned by
    /// <paramref name="owner"/> (architecture §7: the window the command came from, e.g. the search popup); 0 = the default.
    /// </summary>
    /// <param name="sources">Normalized paths of the items.</param>
    /// <param name="destination">Normalized destination folder.</param>
    /// <param name="owner">Owner window of the dialogs; 0 for the default owner.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<FileOperationResult> CopyAsync(IReadOnlyList<string> sources, string destination, nint owner, CancellationToken ct)
        => CopyAsync(sources, destination, ct);

    /// <summary><see cref="MoveAsync(IReadOnlyList{string}, string, CancellationToken)"/> with the dialogs owned by <paramref name="owner"/>.</summary>
    /// <param name="sources">Normalized paths of the items.</param>
    /// <param name="destination">Normalized destination folder.</param>
    /// <param name="owner">Owner window of the dialogs; 0 for the default owner.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<FileOperationResult> MoveAsync(IReadOnlyList<string> sources, string destination, nint owner, CancellationToken ct)
        => MoveAsync(sources, destination, ct);

    /// <summary><see cref="DeleteAsync(IReadOnlyList{string}, bool, CancellationToken)"/> with the dialogs owned by <paramref name="owner"/>.</summary>
    /// <param name="paths">Normalized paths of the items.</param>
    /// <param name="permanent">Shift+Del: skip the Recycle Bin.</param>
    /// <param name="owner">Owner window of the dialogs; 0 for the default owner.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<FileOperationResult> DeleteAsync(IReadOnlyList<string> paths, bool permanent, nint owner, CancellationToken ct)
        => DeleteAsync(paths, permanent, ct);
}

/// <summary>
/// Phase 1 <see cref="IFileOperations"/> on System.IO/Win32 (thread pool): new folder/file and rename only; copy, move and
/// delete are not supported here (the app uses ShellFileOperations).
/// </summary>
public sealed class DirectFileOperations : IFileOperations
{
    /// <summary>Upper bound for the "(n)" suffix search.</summary>
    public const int MaxSuffix = 9999;

    private const int ErrorAlreadyExists = 183;
    private readonly ILogger _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="logger">Logger for completed and failed operations.</param>
    public DirectFileOperations(ILogger<DirectFileOperations>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public Task<FsResult<string>> CreateUniqueFolderAsync(string parent, string baseName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(baseName);
        return Task.Run(() => CreateUniqueFolder(parent, baseName, ct), ct);
    }

    /// <inheritdoc />
    public Task<FsResult<string>> RenameAsync(string path, string newName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(newName);
        return Task.Run(() => Rename(path, newName, ct), ct);
    }

    /// <inheritdoc />
    public Task<FsResult<string>> CreateUniqueFileAsync(string parent, string baseName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(baseName);
        return Task.Run(() => CreateUniqueFile(parent, baseName, ct), ct);
    }

    /// <inheritdoc />
    public Task<FileOperationResult> CopyAsync(IReadOnlyList<string> sources, string destination, CancellationToken ct) => Unsupported(sources);

    /// <inheritdoc />
    public Task<FileOperationResult> MoveAsync(IReadOnlyList<string> sources, string destination, CancellationToken ct) => Unsupported(sources);

    /// <inheritdoc />
    public Task<FileOperationResult> DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken ct) => Unsupported(paths);

    private static Task<FileOperationResult> Unsupported(IReadOnlyList<string> paths)
        => Task.FromResult(new FileOperationResult(FileOperationOutcome.Failed, [.. paths.Select(p => new FileOperationFailure(p, unchecked((int)0x8000_4001)))], []));

    private FsResult<string> CreateUniqueFile(string parent, string baseName, CancellationToken ct)
    {
        string? name = PathUtil.CleanName(baseName);
        if (name is null)
        {
            return Fail("CreateFile", new FsError(FsErrorKind.InvalidPath, PathUtil.Combine(parent, baseName), 123));
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        for (int n = 1; n <= MaxSuffix; n++)
        {
            ct.ThrowIfCancellationRequested();
            string candidate = n == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}){extension}");
            string path = PathUtil.Combine(parent, candidate);
            if (Directory.Exists(PathUtil.ToIo(path)))
            {
                continue; // a folder of that name: CreateNew would fail with access denied, not "exists"
            }

            try
            {
                using (new FileStream(PathUtil.ToIo(path), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                _logger.LogInformation("Created file {Path}", path);
                return FsResult<string>.Success(path);
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x8007_0050))
            {
                // ERROR_FILE_EXISTS: try the next suffix
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail("CreateFile", FsErrors.FromException(ex, path));
            }
        }

        return Fail("CreateFile", new FsError(FsErrorKind.AlreadyExists, PathUtil.Combine(parent, name), 80));
    }

    private FsResult<string> CreateUniqueFolder(string parent, string baseName, CancellationToken ct)
    {
        string? name = PathUtil.CleanName(baseName);
        if (name is null)
        {
            return Fail("CreateFolder", new FsError(FsErrorKind.InvalidPath, PathUtil.Combine(parent, baseName), 123));
        }

        for (int n = 1; n <= MaxSuffix; n++)
        {
            ct.ThrowIfCancellationRequested();
            string candidate = n == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{name} ({n})");
            string path = PathUtil.Combine(parent, candidate);

            // CreateDirectoryW fails when the name exists (file or folder), so check and create are one atomic step.
            if (PInvoke.CreateDirectory(PathUtil.ToIo(path), null))
            {
                _logger.LogInformation("Created folder {Path}", path);
                return FsResult<string>.Success(path);
            }

            int code = Marshal.GetLastPInvokeError();
            if (code != ErrorAlreadyExists)
            {
                return Fail("CreateFolder", new FsError(FsErrors.KindOf(code), path, code));
            }
        }

        return Fail("CreateFolder", new FsError(FsErrorKind.AlreadyExists, PathUtil.Combine(parent, name), ErrorAlreadyExists));
    }

    private FsResult<string> Rename(string path, string newName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? name = PathUtil.CleanName(newName);
        string? parent = PathUtil.Parent(path);
        if (name is null || parent is null)
        {
            return Fail("Rename", new FsError(FsErrorKind.InvalidPath, path, 123));
        }

        string target = PathUtil.Combine(parent, name);
        if (string.Equals(target, path, StringComparison.Ordinal))
        {
            return FsResult<string>.Success(path);
        }

        bool caseOnly = PathUtil.PathEquals(target, path);
        var (source, destination) = PathUtil.ToIoPair(path, target);
        try
        {
            var attributes = File.GetAttributes(source);
            if (!caseOnly && (File.Exists(destination) || Directory.Exists(destination)))
            {
                return Fail("Rename", new FsError(FsErrorKind.AlreadyExists, target, ErrorAlreadyExists));
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination, overwrite: false);
            }

            _logger.LogInformation("Renamed {Path} to {Name}", path, name);
            return FsResult<string>.Success(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail("Rename", FsErrors.FromException(ex, path));
        }
    }

    private FsResult<string> Fail(string operation, FsError error)
    {
        _logger.LogWarning("{Operation} failed: {Kind} (Win32 {Code}) for {Path}", operation, error.Kind, error.Win32Code, error.Path);
        return FsResult<string>.Failure(error);
    }
}
