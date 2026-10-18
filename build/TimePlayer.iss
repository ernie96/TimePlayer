; Inno Setup script for TimePlayer. Built by build\package.ps1, which passes the version
; and the staged program folder:  ISCC.exe /DAppVersion=1.0.0 /DSourceDir=... TimePlayer.iss
;
; Installs per user (no administrator prompt) into %LOCALAPPDATA%\Programs\TimePlayer, so
; TimePlayer can update its bundled yt-dlp in place.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\release\staging\TimePlayer"
#endif
#ifndef OutputDir
  #define OutputDir "..\release"
#endif

[Setup]
AppId={{6F2B8C4E-3D1A-4B7E-9A55-2E7C1D9F0B31}
AppName=TimePlayer
AppVersion={#AppVersion}
AppVerName=TimePlayer {#AppVersion}
AppPublisher=TimePlayer Contributors
DefaultDirName={localappdata}\Programs\TimePlayer
DefaultGroupName=TimePlayer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=TimePlayer-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=TimePlayer
UninstallDisplayIcon={app}\TimePlayer.exe
InfoBeforeFile={#SourceDir}\README.txt
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Everything except the portable marker: an installed copy keeps its data in %LOCALAPPDATA%.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "portable.txt"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TimePlayer"; Filename: "{app}\TimePlayer.exe"
Name: "{group}\TimePlayer guide"; Filename: "{app}\README.txt"
Name: "{autodesktop}\TimePlayer"; Filename: "{app}\TimePlayer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TimePlayer.exe"; Description: "{cm:LaunchProgram,TimePlayer}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; yt-dlp updates itself and writes these next to it.
Type: files; Name: "{app}\bin\yt-dlp-version.json"
Type: files; Name: "{app}\bin\yt-dlp.*.tmp"
