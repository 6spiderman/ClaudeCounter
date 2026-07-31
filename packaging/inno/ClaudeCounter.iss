; ClaudeCounter installer - Inno Setup 6.3 or later (6.3 introduced the
; "arm64" and "x64compatible" architecture identifiers used below).
;
; Invoked once per architecture by the release workflow:
;
;   ISCC.exe /DAppVersion=1.0.0 /DArch=x64 ^
;            /DSourceDir=<abs path to dist\win-x64> ^
;            packaging\inno\ClaudeCounter.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\win-" + Arch
#endif

; PERMANENT. Changing this after v1.0.0 gives users two side-by-side installs
; and breaks winget's ProductCode match ({AppId}_is1). Never touch it.
; The doubled leading brace is Inno's escape: without it the compiler reads
; the GUID as an unknown {constant}.
#define AppId "{{46EDE506-3485-4A9F-93D6-EE4491756F22}"

; VersionInfoVersion goes into the Win32 resource and only accepts numbers, so
; a prerelease tag like 1.0.0-rc.1 has to be trimmed back to 1.0.0 there.
; AppVersion keeps the full string for the UI and the uninstall entry.
#define DashPos Pos("-", AppVersion)
#if DashPos > 0
  #define NumericVersion Copy(AppVersion, 1, DashPos - 1)
#else
  #define NumericVersion AppVersion
#endif

#define AppName "ClaudeCounter"
#define AppPublisher "6spiderman"
#define AppUrl "https://github.com/6spiderman/ClaudeCounter"

#if Arch == "arm64"
  #define ArchAllowed "arm64"
#else
  ; x64compatible also lets the x64 build install on arm64 under emulation,
  ; which is the sane fallback while the arm64 build is barely tested.
  #define ArchAllowed "x64compatible"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#NumericVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

; Per-user install: no admin prompt, no UAC, nothing outside the user profile.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

ArchitecturesAllowed={#ArchAllowed}

OutputDir=..\..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-{#Arch}-setup
SetupIconFile=..\..\assets\{#AppName}.ico
UninstallDisplayIcon={app}\{#AppName}.exe
UninstallDisplayName={#AppName}
LicenseFile=..\..\LICENSE

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Detect a running instance so an upgrade prompts the user to close it rather
; than failing on a locked executable. The mutex name must match the one
; Program.cs takes for single-instance enforcement.
AppMutex=Local\ClaudeCounter_SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\{#AppName}.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppName}.exe"
; Deliberately NO {userstartup} shortcut. The app owns its own
; HKCU\...\Run value (see Settings\AutostartManager.cs) and repairs it when the
; exe moves. A second autostart mechanism would launch it twice.

[Registry]
; The app creates this value itself; this entry exists only so uninstalling
; removes it. Without it every uninstall leaves a dead startup entry behind.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "{#AppName}"; \
  Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\{#AppName}.exe"; \
  Description: "Launch {#AppName}"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Logs and the encrypted session are disposable; settings are asked about in
; code below. Leaving a stale session.dat behind after uninstall would be worse
; than deleting it - it is credential material.
Type: filesandordirs; Name: "{localappdata}\{#AppName}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if DirExists(ExpandConstant('{userappdata}\{#AppName}')) then
    begin
      if SuppressibleMsgBox(
           'Also remove your ClaudeCounter settings?' + #13#10#13#10 +
           'Choose No if you plan to reinstall.',
           mbConfirmation, MB_YESNO, IDNO) = IDYES then
        DelTree(ExpandConstant('{userappdata}\{#AppName}'), True, True, True);
    end;
  end;
end;
