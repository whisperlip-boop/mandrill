# Mandrill <img src="assets/mandrill.png" alt="Mandrill icon" height="32">

A fast multi-pane file explorer for Windows 11. Browse up to four folders side by side, save a whole workspace as a
favorite, and find files by name across your local drives from Mandrill's own index.

## Features

- **Up to four panes** in eight layouts. Each pane has its own folder tree, file list, toolbar, path box with history,
  and back/forward/up. Add or remove a pane with one click.
- **Tabs in every pane**: up to 10 per pane, each with its own folder, view, sort and colour. Ctrl+T opens a tab,
  Ctrl+W or a middle-click closes one, Ctrl+Tab / Ctrl+Shift+Tab switch to the next or previous tab, and
  Ctrl+Shift+T reopens a closed tab (the last 10 per pane, in this session).
- **Quick filter**: narrow a pane's list by name while you type. Open it with Ctrl+Shift+F, the filter button at the
  right end of the tab row, or **View → Filter**. The filter bar shows "shown / total", and "No items match the
  filter." when nothing matches. **Settings… → Filter bar**: **Show when needed** (default) or **Always show**.
- **Several windows**: open another Mandrill window with **File → New window** (Ctrl+N). Settings and favorites stay in
  sync between windows.
- **Views**: Icons (several sizes), List and Details with sortable columns. Hidden files on request.
  Rubber band selection in every view: drag from an empty area, or from a row away from an item's icon and name.
  Shift adds to the selection, Ctrl toggles, Esc cancels and restores it, and the list scrolls at its edge.
  In Icons view, file names wrap to up to four lines.
- **Status bar**: the selection's count and size, in the panes and in the search window. For one selected item, the
  Windows info tip follows (the text File Explorer shows for it, including what installed shell extensions add;
  online-only cloud files are not downloaded for it). For two or more items, the drive's free disk space.
- **Favorites**: save the current layout and every pane's folder as a named favorite and restore it with one click.
  Mandrill also reopens as you left it.
- **Index search**: local fixed drives are indexed in the background and kept up to date. Each pane has a search
  window that finds files and folders by name (`*` and `?` wildcards) under that pane's folder. While indexing is
  still running, unindexed folders are scanned directly. You can exclude folders from the index in Settings.
- **Export file list**: **File → Export file list…** saves the active pane's list as CSV (Name, Full path, Date
  modified, Type, Size in bytes; UTF-8 with BOM) or as a text file (names only).
- **Windows integration**: the native Windows context menu (with a built-in fallback), copy, move, delete and rename
  with the standard Windows progress and conflict dialogs and undo, clipboard and drag and drop with File Explorer,
  and the usual keyboard shortcuts (see [Keyboard shortcuts](#keyboard-shortcuts)).
- **Light, dark and high contrast**: follows the Windows theme, or pick light or dark in Settings.
- **UI languages**: five languages, see [Supported languages](#supported-languages).
- **Private**: no telemetry and no administrator rights. The only network access is the manual "Check for updates"
  button in About, which asks GitHub for the latest release.

## Keyboard shortcuts

**File → Keyboard shortcuts…** (F1) shows this list in the app.

| Group | Keys | Action |
|---|---|---|
| Navigation | Enter | Open |
| | Alt+← / Backspace | Back |
| | Alt+→ | Forward |
| | Alt+↑ | Up one level |
| | F5 | Refresh |
| | Ctrl+L / Alt+D | Go to the path box |
| Tabs | Ctrl+T | New tab |
| | Ctrl+W | Close tab |
| | Ctrl+Tab | Next tab |
| | Ctrl+Shift+Tab | Previous tab |
| | Ctrl+Shift+T | Reopen closed tab |
| File operations | Ctrl+A | Select all |
| | Ctrl+C | Copy |
| | Ctrl+X | Cut |
| | Ctrl+V | Paste |
| | Ctrl+Shift+C | Copy as path |
| | F2 | Rename |
| | Del | Delete (to the Recycle Bin) |
| | Shift+Del | Delete permanently |
| | Ctrl+Shift+N | New folder |
| | Alt+Enter | Properties |
| | Shift+F10 | Context menu |
| View | Ctrl+B | Toggle tree |
| Search and filter | Ctrl+F | Search |
| | Ctrl+Shift+F | Quick filter |
| | Esc | Clear the filter (in the filter box) |
| Window and panes | Alt+1 … Alt+4 | Go to pane 1–4 |
| | Ctrl+Shift+= | Add pane |
| | Ctrl+Shift+- | Remove pane |
| | Ctrl+N | New window |
| | F1 | Keyboard shortcuts |
| | Alt+F4 | Exit |

## Supported languages

| Language | Native name |
|---|---|
| English | English |
| Japanese | 日本語 |
| Korean | 한국어 |
| Chinese (Simplified) | 简体中文 |
| Chinese (Traditional) | 繁體中文 |

Change the language with **File → Language…**. The default, **Auto (Windows language)**, follows the Windows display
language. A new language applies after Mandrill restarts.

## Install

1. Download `Mandrill-<version>-setup-x64.exe` from [Releases](https://github.com/whisperlip-boop/mandrill/releases).
2. Run it. The installer is not code-signed, so Windows SmartScreen may warn about an unrecognized app. Choose
   **More info** → **Run anyway** if you trust the download.

Setup installs for the current user only, needs no administrator rights, and adds Mandrill to the Start menu (a
desktop shortcut is optional). Uninstall from Windows Settings → Apps.

Requirements: Windows 11, x64. Everything Mandrill needs is included; no separate .NET install.

## Your data

Settings, favorites, the search index and logs are stored in `%APPDATA%\mandrill\`. Uninstalling Mandrill leaves this
folder in place; delete it yourself to remove all data.

## Build from source

Requirements: Windows 11 x64, [.NET 10 SDK](https://dotnet.microsoft.com/download) (10.0.403 or later). Visual Studio
is not required. The installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
dotnet build Mandrill.sln -c Release
dotnet format Mandrill.sln --verify-no-changes
dotnet publish src/Mandrill.App -c Release -r win-x64 --self-contained -o publish/
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /DAppVersion=1.0.4 installer\Mandrill.iss
```

`publish\Mandrill.exe` runs without installing. The installer is written to `dist\`. If Inno Setup is installed for
all users, `ISCC.exe` is in `C:\Program Files (x86)\Inno Setup 6\`.

## License

MIT, see [LICENSE](LICENSE).

Mandrill uses these components, each under the MIT license: Windows App SDK / WinUI 3, CommunityToolkit.WinUI.Controls.Sizers,
CommunityToolkit.Mvvm and Microsoft.Extensions.Logging.
