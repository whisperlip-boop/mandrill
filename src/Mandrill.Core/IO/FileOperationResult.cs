namespace Mandrill.Core.IO;

/// <summary>How a copy, move or delete ended (WP-3.2, AC-3.7–3.12).</summary>
public enum FileOperationOutcome
{
    /// <summary>Every item was processed.</summary>
    Completed,

    /// <summary>The user cancelled (progress Cancel, "No" in a confirmation, Skip in a conflict); not an error.</summary>
    Aborted,

    /// <summary>At least one item failed (see <see cref="FileOperationResult.Failures"/>; the shell showed its own error).</summary>
    Failed,
}

/// <summary>An item that failed in a file operation.</summary>
/// <param name="Path">The item's path.</param>
/// <param name="HResult">The failing HRESULT.</param>
public sealed record FileOperationFailure(string Path, int HResult);

/// <summary>The result of a copy, move or delete.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Failures">Items that failed (empty unless <see cref="FileOperationOutcome.Failed"/>).</param>
/// <param name="TouchedFolders">Folders whose contents changed (sources' parents and the destination; AC-3.10 re-scans them).</param>
public sealed record FileOperationResult(FileOperationOutcome Outcome, IReadOnlyList<FileOperationFailure> Failures, IReadOnlyList<string> TouchedFolders);
