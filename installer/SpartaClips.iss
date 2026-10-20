; SpartaClips.iss — Inno Setup 6 script for the one-click Windows installer (SpartaClipsSetup.exe).
;
; Built by .github/workflows/release.yml on every push to main (and locally by installer\build.ps1) from a
; SELF-CONTAINED publish, so users need nothing preinstalled: no .NET, no admin rights.
;   - per-user install (%LocalAppData%\Programs\Sparta Clips) -> no UAC prompt, autostart + hotkeys just work
;   - closes a running Sparta Clips before upgrading (it lives in the tray, so users may not know it's open)
;   - installs Microsoft's WebView2 Runtime only if the PC lacks it (built into Windows 11 / current Windows 10)
;   - uninstall keeps the user's clips and settings
; Compile:  ISCC.exe /DAppVersion=1.2.3 /DSourceDir=..\artifacts\publish SpartaClips.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#define AppName "Sparta Clips"
#define AppExe  "SpartaClips.exe"

[Setup]
; AppId must NEVER change — it is how upgrades find the existing install.
AppId={{6F3C2A1E-5B7D-4E9A-9C2F-8D1B4A7E3C55}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Kayliox
AppPublisherURL=https://github.com/kayliox/sparta-clips
AppSupportURL=https://github.com/kayliox/sparta-clips/issues
AppUpdatesURL=https://github.com/kayliox/sparta-clips/releases/latest
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts
OutputBaseFilename=SpartaClipsSetup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "autostart";   Description: "&Start Sparta Clips when Windows starts (sits quietly in the tray, ready to clip)"; GroupDescription: "Startup:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value the app's own "Start with Windows" setting writes (AppConfig.SetAutostart), so the in-app toggle agrees.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SpartaClips"; ValueData: """{app}\{#AppExe}"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  WebView2Key = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2UserKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';   { Microsoft's tiny WebView2 bootstrapper }

var
  DownloadPage: TDownloadWizardPage;

function HasWebView2: Boolean;
var v: String;
begin
  Result := (RegQueryStringValue(HKLM, WebView2Key, 'pv', v) and (v <> '') and (v <> '0.0.0.0'))
         or (RegQueryStringValue(HKCU, WebView2UserKey, 'pv', v) and (v <> '') and (v <> '0.0.0.0'));
end;

{ The app lives in the tray, so it is often running without a visible window — stop it before files are replaced. }
procedure StopRunningApp;
var rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(700);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Getting Microsoft WebView2 (needed for the app window)...', nil);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var rc: Integer;
begin
  Result := '';
  StopRunningApp;
  if not HasWebView2 then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(WebView2Url, 'MicrosoftEdgeWebview2Setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, rc);
      except
        { Not fatal: most PCs already have WebView2, and the app shows a clear message if it's missing. }
        Log('WebView2 download/install failed: ' + GetExceptionMessage);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

function InitializeUninstall: Boolean;
begin
  StopRunningApp;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var rc: Integer;
begin
  { Users who enabled "Start elevated (for anti-cheat games)" have an admin logon task. Remove it via the app's own
    helper verb — one UAC prompt, and only for those users (schtasks /Query needs no admin). }
  if CurUninstallStep = usUninstall then
    if Exec(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN "SpartaClips"', '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0) then
      ShellExec('runas', ExpandConstant('{app}\{#AppExe}'), '--uninstall-elevated-task', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;
