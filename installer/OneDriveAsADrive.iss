; OneDriveAsADrive Setup - the grown-up installer.
; Wraps the exe + install.ps1 so winget and double-clickers get the FULL experience
; (WebClient config, background task, mapped drives), not just a lonely exe.
; Compiled in CI by ISCC with /DAppVersion=x.y.z - see .github/workflows/release.yml.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Fixed AppId so upgrades replace instead of stacking like Peter's parking tickets.
AppId={{8F1D3C0A-4B7E-4C3D-9A26-0D9E5B1C7A41}
AppName=OneDriveAsADrive
AppVersion={#AppVersion}
AppPublisher=Avatorsinc
AppPublisherURL=https://github.com/Avatorsinc/OneDriveAsADrive
AppSupportURL=https://github.com/Avatorsinc/OneDriveAsADrive/issues
AppUpdatesURL=https://github.com/Avatorsinc/OneDriveAsADrive/releases
DefaultDirName={autopf}\OneDriveAsADrive
DisableProgramGroupPage=yes
DisableDirPage=yes
; Admin needed for the HKLM WebClient tweak; the per-user bits run as the original user below.
PrivilegesRequired=admin
OutputBaseFilename=OneDriveAsADrive-Setup
OutputDir=Output
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=OneDriveAsADrive
UninstallDisplayIcon={app}\OneDriveAsADrive.exe

[Files]
Source: "..\publish\OneDriveAsADrive.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\install.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall-local.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\config.example.json"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; The machine-wide WebDAV client settings install.ps1 would need admin for anyway.
; Deliberately NOT removed on uninstall - other WebDAV tools may rely on them by then
; (same policy as the manual uninstall docs).
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\WebClient\Parameters"; ValueType: dword; ValueName: "BasicAuthLevel"; ValueData: 2; Flags: noerror
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\WebClient\Parameters"; ValueType: dword; ValueName: "FileSizeLimitInBytes"; ValueData: $ffffffff; Flags: noerror

[Run]
; WebClient service on autostart (admin context). Non-zero exit codes are shrugged off -
; "start" fails if it's already running, and that's fine. Very Peter of it.
Filename: "{sys}\sc.exe"; Parameters: "config WebClient start= auto"; Flags: runhidden; StatusMsg: "Configuring Windows WebDAV client..."
Filename: "{sys}\sc.exe"; Parameters: "start WebClient"; Flags: runhidden; StatusMsg: "Starting Windows WebDAV client..."
; The per-user setup (task, server, secret, drive mapping) MUST run as the ORIGINAL user:
; drive letters live per logon session, so mapping them from the elevated context would
; give the drives to an admin session nobody is looking at. Classic wrong-living-room move.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install.ps1"" -LocalExe ""{app}\OneDriveAsADrive.exe""{code:SilentArg}"; Flags: runasoriginaluser runhidden waituntilterminated; StatusMsg: "Registering background service and mapping drives..."

[Code]
// When Setup runs silently (winget /VERYSILENT, IT push), tell install.ps1 to skip the
// interactive sign-in - otherwise a Microsoft sign-in popup hangs the unattended install
// waiting for a click that never comes. Interactive double-click installs still sign in.
function SilentArg(Param: String): String;
begin
  if WizardSilent then Result := ' -Silent' else Result := '';
end;

[UninstallRun]
; Runs elevated (Inno has no runasoriginaluser here). The elevated session can't SEE the
; user's live net-use mappings, so the script also strips the persistent-mapping keys in
; HKCU:\Network - live drives die with the server and are gone for good at next logon.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall-local.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "PerUserCleanup"
