# Mandrill

A fast multi-pane file explorer for Windows 11: up to four panes in eight layouts, favorites that restore a whole
workspace, and (planned) an Everything-style index search. Built with C# / .NET 10 and WinUI 3, unpackaged and
self-contained. Korean and English UI; follows the Windows light/dark theme.

Status: early development (0.1.0).

## Requirements

- Windows 11 (x64)
- .NET 10 SDK (10.0.403 or later). Visual Studio is not required.

## Build, test, publish

```powershell
dotnet build Mandrill.sln -c Release
dotnet test Mandrill.sln --no-build -c Release
dotnet format Mandrill.sln --verify-no-changes
dotnet publish src/Mandrill.App -c Release -r win-x64 --self-contained -o publish/
```

Run `publish\Mandrill.exe`. Settings and logs are stored in `%APPDATA%\mandrill\`.

For testing, `MANDRILL_LANG=ko|en` overrides the UI language and `MANDRILL_DEBUG=1` enables debug logging.

## License

MIT, see [LICENSE](LICENSE).
