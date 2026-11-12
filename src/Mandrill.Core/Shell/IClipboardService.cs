namespace Mandrill.Core.Shell;

/// <summary>Files on the clipboard: their paths and whether they were cut (Preferred DropEffect = move).</summary>
/// <param name="Paths">Full paths (CF_HDROP).</param>
/// <param name="Cut">Cut (move on paste) rather than copied.</param>
/// <param name="Sequence">The clipboard's sequence number when these files were read (0 when unknown).</param>
public sealed record ClipboardFiles(IReadOnlyList<string> Paths, bool Cut, uint Sequence = 0);

/// <summary>
/// The system clipboard as Explorer uses it for files (WP-3.3, ADR-0011, AC-3.13–3.15): CF_HDROP + "Preferred DropEffect",
/// so copy/cut/paste work in both directions with Explorer. Its state follows every clipboard change.
/// </summary>
public interface IClipboardService
{
    /// <summary>Raised after the clipboard changed (on a worker thread); <see cref="Current"/> is already updated.</summary>
    event Action? Changed;

    /// <summary>The files on the clipboard now, or null when it holds no files (Paste disabled, AC-3.15).</summary>
    ClipboardFiles? Current { get; }

    /// <summary>Puts files on the clipboard (Ctrl+C / Ctrl+X).</summary>
    /// <param name="paths">Full paths.</param>
    /// <param name="cut">Cut (move on paste).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>False when another app held the clipboard (logged).</returns>
    Task<bool> SetFilesAsync(IReadOnlyList<string> paths, bool cut, CancellationToken ct);

    /// <summary>Empties the clipboard after a successful cut-paste, unless it changed since <paramref name="pasted"/> was read.</summary>
    /// <param name="pasted">The files that were pasted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when done.</returns>
    Task EmptyAfterCutPasteAsync(ClipboardFiles pasted, CancellationToken ct);
}
