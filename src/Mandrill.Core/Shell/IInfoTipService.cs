namespace Mandrill.Core.Shell;

/// <summary>
/// The Shell info tip of an item, the text Explorer's status bar shows for a single selection (owner WP3). The text can
/// carry personal data (a DICOM handler's patient fields): callers must not log, cache or store it.
/// </summary>
public interface IInfoTipService
{
    /// <summary>Reads the info tip of <paramref name="path"/> off the calling thread.</summary>
    /// <param name="path">Full path of a file or folder.</param>
    /// <param name="ct">Cancels the wait (and the read, if it has not started).</param>
    /// <returns>The tip as the handler returns it (may be multi-line), or null when there is none or reading failed.</returns>
    Task<string?> GetInfoTipAsync(string path, CancellationToken ct);
}
