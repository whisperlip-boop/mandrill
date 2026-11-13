# Mandrill

A fast multi-pane file explorer for Windows 11. Browse up to four folders side by side, save a whole workspace as a
favorite, and find files by name across your local drives from Mandrill's own index.

## Features

- **Up to four panes** in eight layouts. Each pane has its own folder tree, file list, toolbar, path box with history,
  and back/forward/up. Add or remove a pane with one click.
- **Tabs in every pane**: up to 10 per pane, each with its own folder, view, sort and colour.
- **Views**: Icons (several sizes), List and Details with sortable columns. Hidden files on request.
- **Favorites**: save the current layout and every pane's folder as a named favorite and restore it with one click.
  Mandrill also reopens as you left it.
- **Index search**: local fixed drives are indexed in the background and kept up to date. Each pane has a search
  window that finds files and folders by name (`*` and `?` wildcards) under that pane's folder. While indexing is
  still running, unindexed folders are scanned directly. You can exclude folders from the index in Settings.
- **Windows integration**: the native Windows context menu (with a built-in fallback), copy, move, delete and rename
  with the standard Windows progress and conflict dialogs and undo, clipboard and drag and drop with File Explorer,
  and the usual keyboard shortcuts.
- **Light, dark and high contrast**: follows the Windows theme, or pick light or dark in Settings.
- **UI languages**: English, Korean, Japanese, Chinese (Simplified) and Chinese (Traditional). Mandrill follows the
  Windows display language, or pick one with **File → Language…** (applies after a restart).
- **Private**: no telemetry and no administrator rights. The only network access is the manual "Check for updates"
  button in About, which asks GitHub for the latest release.

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
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /DAppVersion=1.0.0 installer\Mandrill.iss
```

`publish\Mandrill.exe` runs without installing. The installer is written to `dist\`. If Inno Setup is installed for
all users, `ISCC.exe` is in `C:\Program Files (x86)\Inno Setup 6\`.

## License

MIT, see [LICENSE](LICENSE).

Mandrill uses these components, each under the MIT license: Windows App SDK / WinUI 3, CommunityToolkit.WinUI.Controls.Sizers,
CommunityToolkit.Mvvm, Microsoft.Extensions.Logging and NReco.Logging.File.
