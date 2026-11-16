; Mandrill per-user installer (Inno Setup 6). Build after `dotnet publish ... -o publish/`:
;   ISCC.exe /DAppVersion=1.0.1 installer\Mandrill.iss  ->  dist\Mandrill-1.0.1-setup-x64.exe
; Uninstall removes only installed files; user data in %APPDATA%\mandrill\ stays.

#ifndef AppVersion
  #error Pass the version: ISCC.exe /DAppVersion=x.y.z installer\Mandrill.iss
#endif

[Setup]
; Never change AppId: it links upgrades and the uninstaller entry.
AppId={{B57DF464-34D8-4499-B9B6-AC2EEA67431A}
AppName=Mandrill
AppVersion={#AppVersion}
; Plain name in Windows Apps (the default adds a localized "version x.y.z").
UninstallDisplayName=Mandrill
AppPublisher=bskim
AppPublisherURL=https://github.com/whisperlip-boop/mandrill
DefaultDirName={localappdata}\Programs\Mandrill
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
SetupIconFile=..\src\Mandrill.App\Assets\mandrill.ico
UninstallDisplayIcon={app}\Mandrill.exe
OutputDir=..\dist
OutputBaseFilename=Mandrill-{#AppVersion}-setup-x64
WizardStyle=modern
SolidCompression=yes
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Mandrill"; Filename: "{app}\Mandrill.exe"
Name: "{autodesktop}\Mandrill"; Filename: "{app}\Mandrill.exe"; Tasks: desktopicon
