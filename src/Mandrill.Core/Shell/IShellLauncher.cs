using Mandrill.Core.IO;

namespace Mandrill.Core.Shell;

/// <summary>Opens files through the shell (architecture §3, §7 "Open"). Implemented in WP-1.6 on the misc STA.</summary>
public interface IShellLauncher
{
    /// <summary>Opens <paramref name="path"/>: a .lnk to a folder resolves to <see cref="OpenOutcome.NavigateTo"/>, anything else is launched.</summary>
    /// <param name="path">Normalized file path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Launched, a folder to navigate to, or the failure.</returns>
    Task<OpenResult> OpenAsync(string path, CancellationToken ct);
}

/// <summary>What <see cref="IShellLauncher.OpenAsync"/> did.</summary>
public enum OpenOutcome
{
    /// <summary>The shell started the file's handler.</summary>
    Launched,

    /// <summary>The path is a shortcut to a folder; the pane should navigate to <see cref="OpenResult.Directory"/>.</summary>
    NavigateTo,

    /// <summary>Opening failed; see <see cref="OpenResult.Error"/>.</summary>
    Failed,
}

/// <summary>Result of <see cref="IShellLauncher.OpenAsync"/>: Launched | NavigateTo(dir) | Failed(FsError).</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Directory">Folder to navigate to for <see cref="OpenOutcome.NavigateTo"/>.</param>
/// <param name="Error">Failure for <see cref="OpenOutcome.Failed"/>.</param>
public sealed record OpenResult(OpenOutcome Outcome, string? Directory = null, FsError? Error = null)
{
    /// <summary>The file was launched.</summary>
    public static OpenResult Launched { get; } = new(OpenOutcome.Launched);

    /// <summary>Navigate to <paramref name="directory"/>.</summary>
    /// <param name="directory">Target folder.</param>
    /// <returns>The result.</returns>
    public static OpenResult NavigateTo(string directory) => new(OpenOutcome.NavigateTo, directory);

    /// <summary>Opening failed.</summary>
    /// <param name="error">The failure.</param>
    /// <returns>The result.</returns>
    public static OpenResult Failed(FsError error) => new(OpenOutcome.Failed, Error: error);
}
