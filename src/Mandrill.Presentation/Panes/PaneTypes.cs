using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.Platform;
using Mandrill.Core.Shell;
using Mandrill.Core.Watching;
using Mandrill.Presentation.Common;
using Microsoft.Extensions.Logging;

namespace Mandrill.Presentation.Panes;

/// <summary>Why a pane navigates; decides history handling and the missing-folder fallback (architecture §7).</summary>
public enum NavOrigin
{
    /// <summary>Typed into the path box: no fallback; an invalid or missing path keeps the previous folder (AC-1.18).</summary>
    Typed,

    /// <summary>Back (history moved at request time).</summary>
    Back,

    /// <summary>Forward (history moved at request time).</summary>
    Forward,

    /// <summary>Up to the parent.</summary>
    Up,

    /// <summary>A folder (or folder shortcut) opened from the list.</summary>
    Open,

    /// <summary>A tree node was selected.</summary>
    Tree,

    /// <summary>A favorite was restored (AC-1.31).</summary>
    Favorite,

    /// <summary>Session restore at launch (AC-1.33).</summary>
    Restore,

    /// <summary>Re-open the current folder (Retry after an error); history is unchanged.</summary>
    Refresh,

    /// <summary>The shown folder or its drive went away; replaces the current history entry.</summary>
    Fallback,

    /// <summary>A tab was activated: its history already points at the target, so a redirect replaces that entry.</summary>
    Tab,
}

/// <summary>How a navigation ended.</summary>
public enum NavigationOutcome
{
    /// <summary>The folder (or its nearest existing ancestor) is shown.</summary>
    Committed,

    /// <summary>The folder could not be shown.</summary>
    Failed,

    /// <summary>A newer navigation replaced this one before it finished (E-1.10).</summary>
    Superseded,
}

/// <summary>Result of a navigation.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Path">Folder shown (committed) or requested (failed).</param>
/// <param name="RedirectedFrom">Requested folder when an ancestor was shown instead (AC-1.31); else null.</param>
/// <param name="Error">Failure, if any.</param>
public sealed record NavigationResult(NavigationOutcome Outcome, string? Path = null, string? RedirectedFrom = null, FsError? Error = null);

/// <summary>Selection the view applies through ListViewBase (architecture §5): e.g. the new folder, or the folder left by Up.</summary>
/// <param name="Names">Entry names to select.</param>
/// <param name="Focus">Focus the first one and scroll it into view.</param>
/// <param name="BeginRename">Start inline rename on it (new folder, AC-1.20).</param>
public sealed record SelectionRequest(IReadOnlyList<string> Names, bool Focus, bool BeginRename = false);

/// <summary>A status-bar message as an i18n key plus named arguments; <see cref="Text"/> localizes it.</summary>
/// <param name="Key">Locale key.</param>
/// <param name="IsError">True for errors (red text).</param>
/// <param name="Args">Named placeholder values.</param>
public sealed record PaneStatus(string Key, bool IsError, params (string Name, object Value)[] Args)
{
    /// <summary>Localized text in the current language.</summary>
    public string Text => Localizer.Current.Format(Key, Args);
}

/// <summary>Services shared by all panes (created once at bootstrap).</summary>
public sealed class PaneContext
{
    /// <summary>Folder change notifications.</summary>
    public required IChangeFeed ChangeFeed { get; init; }

    /// <summary>Drive arrival/removal (E-1.4).</summary>
    public required IDriveMonitor Drives { get; init; }

    /// <summary>New folder (AC-1.20).</summary>
    public required IFileOperations FileOperations { get; init; }

    /// <summary>Opens files (AC-1.26/1.27).</summary>
    public required IShellLauncher ShellLauncher { get; init; }

    /// <summary>UI-thread marshaling.</summary>
    public required IUiDispatcher Dispatcher { get; init; }

    /// <summary>The user's home folder (resolved at bootstrap); the last resort when nothing else exists.</summary>
    public required string? HomeFolder { get; init; }

    /// <summary>Explorer's hidden-file setting, read for every scan (architecture §10). Must be cheap.</summary>
    public required Func<HiddenFilesSetting> HiddenFiles { get; init; }

    /// <summary>Logger factory.</summary>
    public required ILoggerFactory LoggerFactory { get; init; }

    /// <summary>
    /// Resolves shell type names for extension ids before a Type sort (WP-1.6 <c>TypeNameCache.EnsureResolvedAsync</c>);
    /// null sorts with whatever names are cached.
    /// </summary>
    public Func<IReadOnlyCollection<int>, CancellationToken, Task>? EnsureTypeNames { get; init; }

    /// <summary>
    /// The Details view's Type text of an entry, callable on any thread (export, ui-spec §10); null writes an empty type.
    /// </summary>
    public Func<FileEntry, string>? TypeName { get; init; }

    /// <summary>
    /// Shows the shell's "Open with" dialog for a file whose type has no associated app (WP-3.3, Explorer parity, §15 (d));
    /// true when the dialog ran. Null (P1, tests) keeps the "no associated app" message.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? OpenWith { get; init; }

    /// <summary>
    /// Grants the current user access to a denied folder through an elevated icacls (owner F14, Explorer's "Continue");
    /// null shows only the access-denied state.
    /// </summary>
    public IFolderAccess? FolderAccess { get; init; }

    /// <summary>Shell info tips for a single selection in the status bars (owner WP3); null shows none.</summary>
    public IInfoTipService? InfoTips { get; init; }
}
