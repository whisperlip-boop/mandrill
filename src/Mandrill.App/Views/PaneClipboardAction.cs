namespace Mandrill.App.Views;

/// <summary>A clipboard key in a pane's file list (WP-3.3, 3.6).</summary>
public enum PaneClipboardAction
{
    /// <summary>Ctrl+C.</summary>
    Copy,

    /// <summary>Ctrl+X.</summary>
    Cut,

    /// <summary>Ctrl+V.</summary>
    Paste,

    /// <summary>Ctrl+Shift+C: the selected paths as text, each in quotes (Explorer's "Copy as path", WP-3.6).</summary>
    CopyAsPath,
}
