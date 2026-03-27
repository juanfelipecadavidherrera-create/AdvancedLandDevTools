; ══════════════════════════════════════════════════════════════════
;  Advanced Land Development Tools – Inno Setup Installer
;  Civil 3D 2026 (.NET 8) Productivity Plugin
; ══════════════════════════════════════════════════════════════════
;
;  HOW TO BUILD THE INSTALLER:
;  1. Install Inno Setup 6 from https://jrsoftware.org/isdl.php
;  2. Build the project in Visual Studio (Release x64).
;     The post-build target creates:
;       Publish\AdvancedLandDevTools.bundle\
;           PackageContents.xml
;           Contents\AdvancedLandDevTools.dll
;  3. Open this .iss file in Inno Setup Compiler.
;  4. Click Build → Compile.
;  5. Output: Installer\Output\AdvancedLandDevTools_Setup.exe
;
;  The installer copies the .bundle folder to:
;    %APPDATA%\Autodesk\ApplicationPlugins\
;  which Civil 3D 2026 scans on every startup.
; ══════════════════════════════════════════════════════════════════

#define MyAppName        "Advanced Land Development Tools"
#define MyAppVersion     "1.0.0"
#define MyAppPublisher   "Advanced Land Dev Tools"
#define MyAppURL         ""
#define MyBundleName     "AdvancedLandDevTools.bundle"

[Setup]
; App identity
AppId={{CA5BEAD8-A09C-4C3D-A782-5C195BF26BB7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Install location — Autodesk ApplicationPlugins under user's AppData
DefaultDirName={userappdata}\Autodesk\ApplicationPlugins\{#MyBundleName}
DirExistsWarning=no
DisableProgramGroupPage=yes

; Output
OutputDir=Output
OutputBaseFilename=AdvancedLandDevTools_v{#MyAppVersion}_Setup
SetupIconFile=

; Compression
Compression=lzma2
SolidCompression=yes

; UI
WizardStyle=modern
WizardSizePercent=100

; Privileges — per-user install (no admin required)
PrivilegesRequired=lowest

; Uninstall
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\Contents\AdvancedLandDevTools.dll

; Minimum Windows version (Windows 10+)
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] for Autodesk Civil 3D 2026.%n%nThe plugin adds a dedicated Ribbon tab with the following tools:%n%n  • Bulk Surface Profile Creator (BULKSUR)%n  • Align Deploy (ALIGNDEPLOY)%n  • Pipe Magic (PIPEMAGIC)%n  • Invert Pull Up (INVERTPULLUP)%n%nCivil 3D 2026 must be installed on this computer.
FinishedLabel=Installation complete. Start Civil 3D 2026 and the "Advanced Land Dev Tools" ribbon tab will appear automatically.%n%nNo NETLOAD required.

[Files]
; PackageContents.xml goes into the .bundle root
Source: "..\Publish\AdvancedLandDevTools.bundle\PackageContents.xml"; DestDir: "{app}"; Flags: ignoreversion

; DLL + .NET 8 runtime files go into Contents subfolder
Source: "..\Publish\AdvancedLandDevTools.bundle\Contents\AdvancedLandDevTools.dll"; DestDir: "{app}\Contents"; Flags: ignoreversion
Source: "..\Publish\AdvancedLandDevTools.bundle\Contents\AdvancedLandDevTools.runtimeconfig.json"; DestDir: "{app}\Contents"; Flags: ignoreversion
Source: "..\Publish\AdvancedLandDevTools.bundle\Contents\AdvancedLandDevTools.deps.json"; DestDir: "{app}\Contents"; Flags: ignoreversion skipifsourcedoesntexist

[Code]
// ── Pre-install check: warn if Civil 3D 2026 is running ──────────
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  // Check if acad.exe is running
  if FindWindowByClassName('Afx:400000:8:10011:0:0') <> 0 then
  begin
    if MsgBox('Civil 3D appears to be running.' + #13#10 +
              'It is recommended to close Civil 3D before installing.' + #13#10#13#10 +
              'Continue anyway?',
              mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
    end;
  end;
end;

// ── Post-uninstall: clean up the bundle folder ───────────────────
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Remove the Contents folder and the bundle folder itself
    DelTree(ExpandConstant('{app}\Contents'), True, True, True);
    DelTree(ExpandConstant('{app}'), True, True, True);
  end;
end;
