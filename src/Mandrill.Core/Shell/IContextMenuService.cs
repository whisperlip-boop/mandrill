namespace Mandrill.Core.Shell;

/// <summary>
/// The native shell context menu (architecture §7 "Native context menu", AC-3.1–3.4) on its own STA with a hidden owner
/// window. A menu that takes longer than the build limit, cannot take the foreground or fails to build reports
/// <see cref="ContextMenuOutcome.Fallback"/>, and the UI shows its own menu instead.
/// </summary>
public interface IContextMenuService
{
    /// <summary>Builds, shows and runs the native menu for <paramref name="request"/>.</summary>
    /// <param name="request">Items or folder background, owner window and position.</param>
    /// <param name="ct">Cancels the wait (an open menu is closed with <see cref="Cancel"/>).</param>
    /// <returns>What the user chose, or why the fallback menu is needed.</returns>
    Task<ContextMenuResult> ShowAsync(ContextMenuRequest request, CancellationToken ct);

    /// <summary>Runs one shell verb on the items without showing a menu (Del = "delete", Shift+Del adds Shift).</summary>
    /// <param name="target">The items (a background target is not allowed).</param>
    /// <param name="verb">Canonical verb, e.g. "delete" or "properties".</param>
    /// <param name="owner">Owner window for the shell's dialogs (the main window).</param>
    /// <param name="shift">Invoke as with Shift held (permanent delete).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="ContextMenuOutcome.Invoked"/> or <see cref="ContextMenuOutcome.Failed"/>.</returns>
    Task<ContextMenuResult> InvokeVerbAsync(ContextMenuTarget target, string verb, nint owner, bool shift, CancellationToken ct);

    /// <summary>Shows the shell's Properties sheet of a file, a folder or a drive root (the fallback menu's Properties).</summary>
    /// <param name="path">The item.</param>
    /// <param name="owner">Owner window of the sheet (the main window).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the sheet was started.</returns>
    Task<bool> ShowPropertiesAsync(string path, nint owner, CancellationToken ct);

    /// <summary>Closes an open native menu (navigation, window closing). Safe from any thread.</summary>
    void Cancel();

    /// <summary>Builds and drops the menus of <paramref name="targets"/> so the first real right-click is fast (§9 pre-warm).</summary>
    /// <param name="targets">Paths that always exist (the data folder and its log).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when every target was built (failures are logged, never thrown).</returns>
    Task PrewarmAsync(IReadOnlyList<ContextMenuTarget> targets, CancellationToken ct);
}

/// <summary>What a context menu is for: items of one folder, or the folder's background when <see cref="Names"/> is empty.</summary>
/// <param name="Folder">The folder (the items' parent, or the background's folder).</param>
/// <param name="Names">Item names in <paramref name="Folder"/>; empty for the background.</param>
public sealed record ContextMenuTarget(string Folder, IReadOnlyList<string> Names)
{
    /// <summary>
    /// <see cref="Folder"/> for the Shell namespace root (the Desktop folder, WP6b): its only item is This PC
    /// (<see cref="IO.PathUtil.ThisPc"/>), the tree's This PC node.
    /// </summary>
    public const string DesktopRoot = "";

    /// <summary>True for the folder background menu.</summary>
    public bool IsBackground => Names.Count == 0;
}

/// <summary>A context menu request from the UI.</summary>
/// <param name="Target">Items or background.</param>
/// <param name="Owner">The main window's handle (dialogs of invoked verbs are owned by it; the point is in its client area).</param>
/// <param name="ClientX">X in the owner's client area, physical pixels.</param>
/// <param name="ClientY">Y in the owner's client area, physical pixels.</param>
/// <param name="Extended">Shift was held: extended verbs (CMF_EXTENDEDVERBS) and Shift passed to the chosen verb.</param>
/// <param name="Commands">
/// Mandrill's own items (architecture §7, ids ≥0x8000), appended after a separator at the end of the native menu in this
/// order; choosing one returns <see cref="ContextMenuOutcome.Command"/> with its index. Empty for none.
/// </param>
/// <param name="CanRename">
/// The caller has an inline editor (CMF_CANRENAME, the shell's Rename item); false for search results (WP-3.5, lead Q1).
/// </param>
/// <param name="OwnTerminal">
/// <paramref name="Commands"/> include Mandrill's Open in Terminal: the shell's own (Windows Terminal's) item is removed.
/// </param>
public sealed record ContextMenuRequest(ContextMenuTarget Target, nint Owner, int ClientX, int ClientY, bool Extended, IReadOnlyList<string>? Commands = null, bool CanRename = true, bool OwnTerminal = false);

/// <summary>How a context menu request ended.</summary>
public enum ContextMenuOutcome
{
    /// <summary>The user chose a native verb and the shell ran it (the panes refresh through the watcher).</summary>
    Invoked,

    /// <summary>The user chose Rename: the UI starts its inline editor.</summary>
    Rename,

    /// <summary>
    /// The user chose the shell's New folder on a background with <see cref="ContextMenuRequest.CanRename"/>: the UI creates
    /// the folder and starts its inline editor (outside Explorer, Windows' own top-level item creates it in the parent folder).
    /// </summary>
    NewFolder,

    /// <summary>
    /// The user chose Open (or Explore) on a single item: the UI opens it like a double-click, so a folder navigates the
    /// pane instead of opening an Explorer window (QA N1).
    /// </summary>
    Open,

    /// <summary>The user chose one of Mandrill's own items; <see cref="ContextMenuResult.Command"/> is its index.</summary>
    Command,

    /// <summary>The menu closed without a choice (Esc, click outside, <see cref="IContextMenuService.Cancel"/>).</summary>
    Dismissed,

    /// <summary>No native menu: show the fallback menu (<see cref="ContextMenuResult.Reason"/> says why).</summary>
    Fallback,

    /// <summary>The chosen verb or <see cref="IContextMenuService.InvokeVerbAsync"/> failed (logged).</summary>
    Failed,
}

/// <summary>Why the native menu was not shown.</summary>
public enum FallbackReason
{
    /// <summary>Not a fallback.</summary>
    None,

    /// <summary>Building took longer than the limit (AC-3.3, 2 s); the late menu is discarded.</summary>
    Timeout,

    /// <summary>The menu window could not take the foreground (S4: it would ignore the keyboard and outside clicks).</summary>
    NotForeground,

    /// <summary>The shell could not build the menu (missing item, shell extension failure).</summary>
    BuildFailed,

    /// <summary>Items of several folders (search results, §7 "mixed parents"): no shell folder has one menu for them.</summary>
    MixedParents,
}

/// <summary>The result of a context menu request.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Verb">The canonical verb chosen or invoked, when the handler has one.</param>
/// <param name="Reason">Why the fallback menu is needed.</param>
/// <param name="HResult">The failing HRESULT for <see cref="ContextMenuOutcome.Failed"/> and build failures.</param>
/// <param name="Command">Index into <see cref="ContextMenuRequest.Commands"/> for <see cref="ContextMenuOutcome.Command"/>, else -1.</param>
public sealed record ContextMenuResult(ContextMenuOutcome Outcome, string? Verb = null, FallbackReason Reason = FallbackReason.None, int HResult = 0, int Command = -1)
{
    /// <summary>The menu closed without a choice.</summary>
    public static ContextMenuResult Dismissed { get; } = new(ContextMenuOutcome.Dismissed);

    /// <summary>Show the fallback menu.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="hresult">The failing HRESULT, if any.</param>
    /// <returns>The result.</returns>
    public static ContextMenuResult Fallback(FallbackReason reason, int hresult = 0) => new(ContextMenuOutcome.Fallback, null, reason, hresult);
}
