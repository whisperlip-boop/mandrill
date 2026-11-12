using System.Runtime.InteropServices;
using Mandrill.Core.IO;
using Mandrill.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Mandrill.Core.Shell;

/// <summary>
/// <see cref="IFileOperations"/> through the shell's IFileOperation (WP-3.2, architecture §7 "File operations", AC-3.7–3.12):
/// native progress, conflict and error dialogs owned by the main window, and an undo record. Every operation runs on its own
/// short-lived STA with no hang limit at all (infinite StaWorker timeout: the shell's dialogs are user UI), so operations run
/// in parallel like Explorer's and a long copy never delays a delete. A progress sink records per-item failures and the final
/// names of new items.
/// </summary>
public sealed class ShellFileOperations : IFileOperations
{
    private const int ErrorCancelled = unchecked((int)0x8007_04C7);
    private const int CopyEngineUserCancelled = unchecked((int)0x8027_0000);
    private const int ErrorAlreadyExists = 183;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private static readonly TimeSpan UserLimit = Timeout.InfiniteTimeSpan; // a large copy with its dialogs is user-driven

    private readonly ILogger _logger;
    private readonly Func<bool> _confirmRecycle;
    private readonly bool _silent;
    private nint _owner;

    /// <summary>Creates the service.</summary>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    /// <param name="confirmRecycle">The user's Recycle Bin confirmation setting; null reads Explorer's (SSF_NOCONFIRMRECYCLE).</param>
    /// <param name="silent">
    /// Tests only: no shell UI (progress, confirmation, error dialogs), no undo record in the user's Explorer history and no
    /// Recycle Bin: every delete is then permanent (RECYCLEONDELETE is cleared too), so silent mode never touches the bin.
    /// </param>
    public ShellFileOperations(ILoggerFactory? loggerFactory = null, Func<bool>? confirmRecycle = null, bool silent = false)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ShellFileOperations>();
        _confirmRecycle = confirmRecycle ?? ExplorerSettings.ReadConfirmRecycle;
        _silent = silent;
    }

    /// <summary>Raised (on a worker thread) after every copy, move or delete; the app re-scans panes on its touched folders.</summary>
    public event Action<FileOperationResult>? Completed;

    /// <summary>The main window's handle: owner of the progress, confirmation and error dialogs (0 until the window exists).</summary>
    public nint Owner
    {
        get => Volatile.Read(ref _owner);
        set => Volatile.Write(ref _owner, value);
    }

    /// <inheritdoc/>
    public Task<FsResult<string>> CreateUniqueFolderAsync(string parent, string baseName, CancellationToken ct)
        => NewItemAsync(parent, baseName, FileAttributeDirectory, ct);

    /// <inheritdoc/>
    public Task<FsResult<string>> CreateUniqueFileAsync(string parent, string baseName, CancellationToken ct)
        => NewItemAsync(parent, baseName, FileAttributeNormal, ct);

    /// <inheritdoc/>
    public async Task<FsResult<string>> RenameAsync(string path, string newName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(newName);
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

        // AC-3.11: a taken name keeps the inline editor open with a message (the shell would offer "name (2)" instead).
        if (!PathUtil.PathEquals(target, path) && await Task.Run(() => Exists(target), ct).ConfigureAwait(false))
        {
            return Fail("Rename", new FsError(FsErrorKind.AlreadyExists, target, ErrorAlreadyExists));
        }

        var result = await RunAsync(
            "Rename",
            [parent],
            UndoFlags,
            (operation, items) =>
            {
                if (items.Get(path) is not { } item)
                {
                    return 0;
                }

                operation.RenameItem(item, name, null);
                return 1;
            },
            ct).ConfigureAwait(false);
        Finish(result.Operation); // QA m3: other panes on the folder re-list at once too
        return result.Operation.Outcome switch
        {
            FileOperationOutcome.Completed => FsResult<string>.Success(result.Sink.LastNewPath ?? target),
            FileOperationOutcome.Aborted => FsResult<string>.Failure(new FsError(FsErrorKind.Other, path, 1223)),
            _ => FsResult<string>.Failure(ErrorOf(result.Operation, path)),
        };
    }

    /// <inheritdoc/>
    public Task<FileOperationResult> CopyAsync(IReadOnlyList<string> sources, string destination, CancellationToken ct) => CopyAsync(sources, destination, 0, ct);

    /// <inheritdoc/>
    public async Task<FileOperationResult> CopyAsync(IReadOnlyList<string> sources, string destination, nint owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);
        // Explorer: an item copied into its own folder becomes "name - Copy" (RENAMEONCOLLISION); items from other folders keep
        // the shell's conflict dialog, so a mixed set runs as two operations (QA M2, reviewer m5).
        var own = sources.Where(s => PathUtil.Parent(s) is { } parent && PathUtil.PathEquals(parent, destination)).ToArray();
        var others = sources.Except(own).ToArray();
        FileOperationResult? first = null;
        if (own.Length > 0)
        {
            first = (await RunAsync("Copy", Touched(own, destination), () => UndoFlags() | (uint)FILEOPERATION_FLAGS.FOF_RENAMEONCOLLISION, Transfer(own, destination, copy: true), ct, owner).ConfigureAwait(false)).Operation;
        }

        // A cancel in the first part cancels the whole paste, as in Explorer (qa-wp32).
        if (others.Length > 0 && first is not { Outcome: FileOperationOutcome.Aborted })
        {
            var second = (await RunAsync("Copy", Touched(others, destination), UndoFlags, Transfer(others, destination, copy: true), ct, owner).ConfigureAwait(false)).Operation;
            first = first is null ? second : Merge(first, second);
        }

        return Finish(first ?? new FileOperationResult(FileOperationOutcome.Completed, [], Touched(sources, destination)));
    }

    /// <inheritdoc/>
    public Task<FileOperationResult> MoveAsync(IReadOnlyList<string> sources, string destination, CancellationToken ct) => MoveAsync(sources, destination, 0, ct);

    /// <inheritdoc/>
    public async Task<FileOperationResult> MoveAsync(IReadOnlyList<string> sources, string destination, nint owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);
        var result = await RunAsync(
            "Move",
            Touched(sources, destination),
            UndoFlags,
            Transfer(sources, destination, copy: false),
            ct,
            owner).ConfigureAwait(false);
        return Finish(result.Operation);
    }

    /// <inheritdoc/>
    public Task<FileOperationResult> DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken ct) => DeleteAsync(paths, permanent, 0, ct);

    /// <inheritdoc/>
    public async Task<FileOperationResult> DeleteAsync(IReadOnlyList<string> paths, bool permanent, nint owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // AC-3.8: Recycle Bin with undo, confirmation as the user set it for Explorer (read on the operation's STA when it starts,
        // so a change in Explorer applies at once); Shift+Del: the native permanent-delete warning. WANTNUKEWARNING on recycle
        // deletes too (Explorer): an item the Recycle Bin cannot take (network share, a drive without a bin, too large) still
        // asks before it is deleted for good, even with the confirmation turned off.
        uint Flags() => permanent
            ? (uint)FILEOPERATION_FLAGS.FOF_WANTNUKEWARNING
            : UndoFlags() | (uint)(FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE | FILEOPERATION_FLAGS.FOF_WANTNUKEWARNING)
                | (_confirmRecycle() ? 0u : (uint)FILEOPERATION_FLAGS.FOF_NOCONFIRMATION);
        var result = await RunAsync(
            permanent ? "Delete permanently" : "Delete",
            Touched(paths, null),
            Flags,
            (operation, items) =>
            {
                int queued = 0;
                foreach (string path in paths)
                {
                    if (items.Get(path) is { } item)
                    {
                        operation.DeleteItem(item, null);
                        queued++;
                    }
                }

                return queued;
            },
            ct,
            owner).ConfigureAwait(false);
        return Finish(result.Operation);
    }

    private static uint UndoFlags()
        => (uint)(FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR | FILEOPERATION_FLAGS.FOFX_ADDUNDORECORD);

    private static bool Exists(string path) => File.Exists(PathUtil.ToIo(path)) || Directory.Exists(PathUtil.ToIo(path));

    private static IReadOnlyList<string> Touched(IReadOnlyList<string> sources, string? destination)
    {
        var folders = new List<string>();
        void Add(string? folder)
        {
            if (folder is not null && !folders.Any(f => PathUtil.PathEquals(f, folder)))
            {
                folders.Add(folder);
            }
        }

        foreach (string source in sources)
        {
            Add(PathUtil.Parent(Canonical(source)));
        }

        Add(destination is null ? null : Canonical(destination));
        return folders;
    }

    // The long, normalized form of a path the shell reports (8.3 segments, a \\?\ prefix), so it matches a pane's path (QA m1).
    private static string Canonical(string path)
    {
        string plain = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        return FolderAccessGrant.Canonicalize(plain, FolderAccessGrant.ToLongPath) ?? plain;
    }

    private static FileOperationResult Merge(FileOperationResult a, FileOperationResult b)
    {
        var outcome = a.Outcome == FileOperationOutcome.Failed || b.Outcome == FileOperationOutcome.Failed ? FileOperationOutcome.Failed
            : a.Outcome == FileOperationOutcome.Aborted || b.Outcome == FileOperationOutcome.Aborted ? FileOperationOutcome.Aborted
            : FileOperationOutcome.Completed;
        var touched = a.TouchedFolders.ToList();
        touched.AddRange(b.TouchedFolders.Where(f => !touched.Any(t => PathUtil.PathEquals(t, f))));
        return new FileOperationResult(outcome, [.. a.Failures, .. b.Failures], touched);
    }

    // Queues a copy or move of every source that the shell can resolve; a missing one is a failure of its own (QA M1).
    private static Func<IFileOperation, Items, int> Transfer(IReadOnlyList<string> sources, string destination, bool copy)
        => (operation, items) =>
        {
            if (items.Get(destination) is not { } target)
            {
                return 0;
            }

            int queued = 0;
            foreach (string source in sources)
            {
                if (items.Get(source) is { } item)
                {
                    if (copy)
                    {
                        operation.CopyItem(item, target, null, null);
                    }
                    else
                    {
                        operation.MoveItem(item, target, null, null);
                    }

                    queued++;
                }
            }

            return queued;
        };

    private static bool IsCancelled(int hresult) => hresult is ErrorCancelled or CopyEngineUserCancelled;

    private static FsError ErrorOf(FileOperationResult result, string path)
    {
        int hr = result.Failures.Count > 0 ? result.Failures[0].HResult : unchecked((int)0x8000_4005);
        int win32 = (hr & 0xFFFF_0000) == 0x8007_0000 ? hr & 0xFFFF : 0;
        var kind = win32 != 0 ? FsErrors.KindOf(win32) : CopyEngineKind(hr);
        return new FsError(kind, path, win32 == 0 ? hr : win32);
    }

    // COPYENGINE_E_* (sherrors.h) the copy engine reports instead of Win32 codes; 0x80270027 (locked file on rename) and
    // 0x8027001E (path too deep) were seen by QA. Unknown codes stay Other.
    private static FsErrorKind CopyEngineKind(int hresult) => unchecked((uint)hresult) switch
    {
        0x8027_001D or 0x8027_001E => FsErrorKind.PathTooLong,      // PATH_TOO_DEEP_SRC/DEST
        0x8027_0021 or 0x8027_0022 => FsErrorKind.AccessDenied,     // ACCESS_DENIED_SRC/DEST
        >= 0x8027_0023 and <= 0x8027_0026 => FsErrorKind.AlreadyExists, // ALREADY_EXISTS_NORMAL/READONLY/SYSTEM/FOLDER
        0x8027_0027 or 0x8027_0030 or 0x8027_0031 => FsErrorKind.InUse, // locked file / SHARING_VIOLATION_SRC/DEST
        _ => FsErrorKind.Other,
    };

    // Shell items of one operation: each created item is released with it; an item the shell cannot resolve (missing,
    // denied) becomes a failure with its own path and the others still run (QA M1).
    private sealed class Items(List<FileOperationFailure> failures)
    {
        public List<object> Created { get; } = [];

        public unsafe IShellItem? Get(string path)
        {
            Guid iid = typeof(IShellItem).GUID;
            object result;
            HRESULT hr;
            fixed (char* p = path)
            {
                hr = PInvoke.SHCreateItemFromParsingName(p, null, &iid, out result);
            }

            if (hr.Failed)
            {
                failures.Add(new FileOperationFailure(path, hr.Value));
                return null;
            }

            Created.Add(result);
            return (IShellItem)result;
        }
    }

    private static void ReleaseAll(List<object> created)
    {
        foreach (object com in created)
        {
            if (Marshal.IsComObject(com))
            {
                Marshal.FinalReleaseComObject(com);
            }
        }
    }

    private FsResult<string> Fail(string operation, FsError error)
    {
        _logger.LogWarning("{Operation} failed: {Kind} {Path} ({Code})", operation, error.Kind, error.Path, error.Win32Code);
        return FsResult<string>.Failure(error);
    }

    private FileOperationResult Finish(FileOperationResult result)
    {
        try
        {
            Completed?.Invoke(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A file operation listener failed");
        }

        return result;
    }

    private async Task<FsResult<string>> NewItemAsync(string parent, string baseName, uint attributes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(baseName);
        string? name = PathUtil.CleanName(baseName);
        if (name is null)
        {
            return Fail("New item", new FsError(FsErrorKind.InvalidPath, PathUtil.Combine(parent, baseName), 123));
        }

        // RENAMEONCOLLISION: the shell picks "New folder (2)" / "New Text Document (2).txt"; the sink reports the final name.
        var result = await RunAsync(
            "New item",
            [parent],
            () => UndoFlags() | (uint)FILEOPERATION_FLAGS.FOF_RENAMEONCOLLISION,
            (operation, items) =>
            {
                if (items.Get(parent) is not { } folder)
                {
                    return 0;
                }

                operation.NewItem(folder, attributes, name, null, null);
                return 1;
            },
            ct).ConfigureAwait(false);
        Finish(result.Operation); // QA m3: other panes on the folder re-list at once too
        if (result.Operation.Outcome == FileOperationOutcome.Completed && result.Sink.LastNewPath is { } path)
        {
            return FsResult<string>.Success(path);
        }

        return FsResult<string>.Failure(ErrorOf(result.Operation, PathUtil.Combine(parent, name)));
    }

    // One operation on its own STA: build it, queue the items, perform, and read the outcome from the sink.
    private async Task<(FileOperationResult Operation, ProgressSink Sink)> RunAsync(
        string what,
        IReadOnlyList<string> touched,
        Func<uint> operationFlags,
        Func<IFileOperation, Items, int> queue,
        CancellationToken ct,
        nint ownerWindow = 0)
    {
        using var sta = new StaWorker("fileop", _logger);
        var sink = new ProgressSink();
        nint owner = ownerWindow != 0 ? ownerWindow : Owner; // §7: the window the command came from, else the main window

        var operation = await sta.InvokeAsync(
            () =>
            {
                var items = new Items(sink.Failures);
                object? instance = null;
                bool aborted = false;
                try
                {
                    instance = new FileOperation();
                    var op = (IFileOperation)instance;
                    if (owner != 0)
                    {
                        op.SetOwnerWindow((HWND)owner);
                    }

                    uint flags = operationFlags();
                    if (_silent)
                    {
                        flags |= (uint)(FILEOPERATION_FLAGS.FOF_SILENT | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION | FILEOPERATION_FLAGS.FOF_NOERRORUI);
                        flags &= ~(uint)(FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOFX_ADDUNDORECORD | FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE);
                    }

                    op.SetOperationFlags((FILEOPERATION_FLAGS)flags);
                    op.Advise(sink, out uint cookie);
                    try
                    {
                        // Nothing to do when no item could be resolved (their failures are already recorded).
                        if (queue(op, items) > 0)
                        {
                            try
                            {
                                op.PerformOperations();
                            }
                            catch (COMException ex) when (IsCancelled(ex.HResult))
                            {
                                aborted = true;
                            }
                            catch (COMException) when (sink.Failures.Count > 0)
                            {
                                // Per-item failures are already recorded (by the sink or for unresolved items).
                            }

                            op.GetAnyOperationsAborted(out BOOL anyAborted);
                            aborted |= anyAborted;
                        }
                    }
                    finally
                    {
                        op.Unadvise(cookie);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failure the sink did not see (a missing source before PerformOperations, an engine error).
                    if (sink.Failures.Count == 0)
                    {
                        sink.Failures.Add(new FileOperationFailure(touched.Count > 0 ? touched[0] : string.Empty, ex.HResult));
                    }
                }
                finally
                {
                    ReleaseAll(items.Created);
                    if (instance is not null && Marshal.IsComObject(instance))
                    {
                        Marshal.FinalReleaseComObject(instance);
                    }
                }

                var outcome = sink.Failures.Count > 0 ? FileOperationOutcome.Failed : aborted ? FileOperationOutcome.Aborted : FileOperationOutcome.Completed;
                return new FileOperationResult(outcome, [.. sink.Failures], touched);
            },
            UserLimit,
            ct).ConfigureAwait(false);

        if (operation.Outcome == FileOperationOutcome.Failed)
        {
            foreach (var failure in operation.Failures)
            {
                _logger.LogWarning("{What} failed for {Path}: 0x{Hr:X8}", what, failure.Path, failure.HResult);
            }
        }
        else
        {
            _logger.LogInformation("{What}: {Outcome} in {Folders}", what, operation.Outcome, string.Join(", ", touched));
        }

        return (operation, sink);
    }

    // Receives the engine's per-item results on the operation's STA (the engine calls it there).
    [ComVisible(true)]
    private sealed class ProgressSink : IFileOperationProgressSink
    {
        public List<FileOperationFailure> Failures { get; } = [];

        public string? LastNewPath { get; private set; }

        public void StartOperations()
        {
        }

        public void FinishOperations(HRESULT hrResult)
        {
        }

        public void PreRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName)
        {
        }

        public void PostRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName, HRESULT hrRename, IShellItem psiNewlyCreated)
            => Record(psiItem, hrRename, psiNewlyCreated);

        public void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
        }

        public void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrMove, IShellItem psiNewlyCreated)
            => Record(psiItem, hrMove, null);

        public void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
        }

        public void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrCopy, IShellItem psiNewlyCreated)
            => Record(psiItem, hrCopy, null);

        public void PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
        }

        public void PostDeleteItem(uint dwFlags, IShellItem psiItem, HRESULT hrDelete, IShellItem psiNewlyCreated)
            => Record(psiItem, hrDelete, null);

        public void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
        }

        public void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, PCWSTR pszNewName, PCWSTR pszTemplateName, uint dwFileAttributes, HRESULT hrNew, IShellItem psiNewItem)
            => Record(psiDestinationFolder, hrNew, psiNewItem);

        public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar)
        {
        }

        public void ResetTimer()
        {
        }

        public void PauseTimer()
        {
        }

        public void ResumeTimer()
        {
        }

        private static string? PathOf(IShellItem? item)
        {
            if (item is null)
            {
                return null;
            }

            try
            {
                item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out PWSTR name);
                try
                {
                    return name.ToString();
                }
                finally
                {
                    unsafe
                    {
                        PInvoke.CoTaskMemFree(name.Value);
                    }
                }
            }
            catch (Exception)
            {
                return null; // a virtual item has no file-system path
            }
        }

        private void Record(IShellItem item, HRESULT hr, IShellItem? created)
        {
            if (hr.Failed && !IsCancelled(hr.Value))
            {
                Failures.Add(new FileOperationFailure(PathOf(item) is { } failed ? Canonical(failed) : string.Empty, hr.Value));
            }
            else if (hr.Succeeded && PathOf(created) is { } path)
            {
                LastNewPath = Canonical(path);
            }
        }
    }
}
