; SpartaClips.iss — Inno Setup 6 script for the one-click Windows installer (SpartaClipsSetup.exe).
;
; Built by .github/workflows/release.yml on every push to main (and locally by installer\build.ps1) from a
; SELF-CONTAINED publish, so users need nothing preinstalled: no .NET, no admin rights.
;   - per-user install (%LocalAppData%\Programs\Sparta Clips) -> no UAC prompt, autostart + hotkeys just work
;   - closes a running Sparta Clips before upgrading (it lives in the tray, so users may not know it's open)
;   - installs Microsoft's WebView2 Runtime only if the PC lacks it (built into Windows 11 / current Windows 10)
;   - uninstall keeps the user's clips and settings
;   - UPGRADES: an existing install (same AppId) is detected → no options page, files replaced in place, clips/settings
;     kept, and the user's own shortcut/autostart choices left alone; an older installer won't silently downgrade
;   - IN-APP UPDATES: the app runs this with /SILENT /relaunch=window|tray → wait for the app to exit, update, reopen it
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
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; Tasks: desktopicon; Check: not IsUpgrade

[Registry]
; Same value the app's own "Start with Windows" setting writes (AppConfig.SetAutostart), so the in-app toggle agrees.
; Fresh installs only — on an upgrade the in-app toggle owns this value (re-adding it would undo a user's "off").
; The first install's uninsdeletevalue stays in the (appended) uninstall log, so uninstall still removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SpartaClips"; ValueData: """{app}\{#AppExe}"" --tray"; Tasks: autostart; Flags: uninsdeletevalue; Check: not IsUpgrade

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent
; In-app update: reopen the app the way it was (window or tray) and let it say "updated".
Filename: "{app}\{#AppExe}"; Parameters: "{code:RelaunchParams}"; Flags: nowait; Check: IsRelaunch

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  WebView2Key = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2UserKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';   { Microsoft's tiny WebView2 bootstrapper }

  UninstKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F3C2A1E-5B7D-4E9A-9C2F-8D1B4A7E3C55}_is1';   { = AppId + _is1 }

var
  DownloadPage: TDownloadWizardPage;
  InstalledVersion: String;   { '' = not installed yet }

function IsUpgrade: Boolean;
begin
  Result := InstalledVersion <> '';
end;

{ /relaunch=window|tray — passed by the app's own updater }
function IsRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|}') <> '';
end;

function RelaunchParams(Param: String): String;
begin
  if CompareText(ExpandConstant('{param:relaunch|}'), 'tray') = 0 then Result := '--tray --updated' else Result := '--updated';
end;

function NextVerPart(var S: String): Integer;
var p: Integer;
begin
  p := Pos('.', S);
  if p = 0 then begin Result := StrToIntDef(S, 0); S := ''; end
  else begin Result := StrToIntDef(Copy(S, 1, p - 1), 0); Delete(S, 1, p); end;
end;

{ -1 / 0 / 1 for dotted versions (0.1.9 < 0.1.10) }
function CompareVersions(A, B: String): Integer;
var i, x, y: Integer;
begin
  Result := 0;
  for i := 1 to 4 do
  begin
    x := NextVerPart(A); y := NextVerPart(B);
    if x <> y then begin if x > y then Result := 1 else Result := -1; Exit; end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  InstalledVersion := '';
  if not RegQueryStringValue(HKCU, UninstKey, 'DisplayVersion', InstalledVersion) then
    RegQueryStringValue(HKLM, UninstKey, 'DisplayVersion', InstalledVersion);
  if IsUpgrade then Log('Existing install found: ' + InstalledVersion + ' -> {#AppVersion}');
  if IsUpgrade and (CompareVersions(InstalledVersion, '{#AppVersion}') > 0) then
  begin
    if WizardSilent then
    begin
      Log('A newer version is installed - not downgrading.');
      Result := False;
    end
    else
      Result := MsgBox('{#AppName} ' + InstalledVersion + ' is already installed, which is newer than this installer ({#AppVersion}).' + #13#10#13#10 +
                       'Install the older version anyway?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;
end;

{ Upgrades skip the options page: the shortcut/autostart choices were made on first install (and autostart is managed
  in the app's settings since). }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpSelectTasks) and IsUpgrade;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and IsUpgrade then
  begin
    WizardForm.FinishedHeadingLabel.Caption := 'Update complete';
    WizardForm.FinishedLabel.Caption := '{#AppName} has been updated from ' + InstalledVersion + ' to {#AppVersion}.' + #13#10#13#10 +
                                        'Your clips and settings were kept.';
  end;
end;

function AppIsRunning: Boolean;
var rc: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq {#AppExe}" /NH | find /I "{#AppExe}" >nul', '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0);
end;

function HasWebView2: Boolean;
var v: String;
begin
  Result := (RegQueryStringValue(HKLM, WebView2Key, 'pv', v) and (v <> '') and (v <> '0.0.0.0'))
         or (RegQueryStringValue(HKCU, WebView2UserKey, 'pv', v) and (v <> '') and (v <> '0.0.0.0'));
end;

{ The app lives in the tray, so it is often running without a visible window — stop it before files are replaced. }
procedure StopRunningApp;
var rc, i: Integer;
begin
  { An in-app update: the app quits by itself (finishing any clip it's saving) — give it a moment before forcing it. }
  if IsRelaunch then
    for i := 1 to 30 do
    begin
      if not AppIsRunning then Break;
      Sleep(500);
    end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(700);
end;

procedure InitializeWizard;
begin
  if IsUpgrade then WizardForm.Caption := 'Updating {#AppName} to {#AppVersion}';
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
