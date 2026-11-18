namespace Mandrill.Presentation.Shell;

/// <summary>One row of the Keyboard shortcuts dialog (ui-spec §11).</summary>
/// <param name="Keys">The keys as Windows menus write them ("Ctrl+Shift+T"); not translated, like the menus' accelerator texts.</param>
/// <param name="TextKey">i18n key of what they do.</param>
public sealed record ShortcutInfo(string Keys, string TextKey);

/// <summary>A titled group of rows.</summary>
/// <param name="TitleKey">i18n key of the group title.</param>
/// <param name="Items">Rows in display order.</param>
public sealed record ShortcutGroup(string TitleKey, IReadOnlyList<ShortcutInfo> Items);

/// <summary>
/// Every keyboard shortcut that works, as Help shows it (F1, ui-spec §11): the one list to update when a key is added or
/// removed (PaneView.AddShortcuts, MainWindow.AddWindowShortcuts, the list and menu accelerators). Read-only, no
/// customisation; mouse gestures (Ctrl+wheel, middle-click) are not listed.
/// </summary>
public static class ShortcutCatalog
{
    /// <summary>The groups in display order.</summary>
    public static IReadOnlyList<ShortcutGroup> Groups { get; } =
    [
        new("dialog.shortcuts.group.navigation",
        [
            new("Enter", "context.open"),
            new("Alt+← / Backspace", "dialog.shortcuts.back"),
            new("Alt+→", "dialog.shortcuts.forward"),
            new("Alt+↑", "dialog.shortcuts.up"),
            new("F5", "dialog.shortcuts.refresh"),
            new("Ctrl+L / Alt+D", "dialog.shortcuts.pathBox"),
        ]),
        new("dialog.shortcuts.group.tabs",
        [
            new("Ctrl+T", "pane.tab.new"),
            new("Ctrl+W", "pane.tab.close"),
            new("Ctrl+Tab", "dialog.shortcuts.nextTab"),
            new("Ctrl+Shift+Tab", "dialog.shortcuts.previousTab"),
            new("Ctrl+Shift+T", "dialog.shortcuts.reopenTab"),
        ]),
        new("dialog.shortcuts.group.files",
        [
            new("Ctrl+A", "dialog.shortcuts.selectAll"),
            new("Ctrl+C", "context.copy"),
            new("Ctrl+X", "context.cut"),
            new("Ctrl+V", "context.paste"),
            new("Ctrl+Shift+C", "dialog.shortcuts.copyAsPath"),
            new("F2", "context.rename"),
            new("Del", "dialog.shortcuts.delete"),
            new("Shift+Del", "dialog.shortcuts.deletePermanently"),
            new("Ctrl+Shift+N", "context.newFolder"),
            new("Alt+Enter", "context.properties"),
            new("Shift+F10", "dialog.shortcuts.contextMenu"),
        ]),
        new("dialog.shortcuts.group.view",
        [
            new("Ctrl+B", "menu.view.toggleTree"),
        ]),
        new("dialog.shortcuts.group.search",
        [
            new("Ctrl+F", "dialog.shortcuts.search"),
            new("Ctrl+Shift+F", "dialog.shortcuts.filter"),
            new("Esc", "dialog.shortcuts.clearFilter"),
        ]),
        new("dialog.shortcuts.group.window",
        [
            new("Alt+1 … Alt+4", "dialog.shortcuts.activatePane"),
            new("Ctrl+Shift+=", "menu.view.addPane"),
            new("Ctrl+Shift+-", "menu.view.removePane"),
            new("Ctrl+N", "menu.file.newWindow"),
            new("F1", "dialog.shortcuts.title"),
            new("Alt+F4", "menu.file.exit"),
        ]),
    ];
}
