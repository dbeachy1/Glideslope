#define AppId "Glideslope.Desktop"
#define AppName "Glideslope"
#define AppPublisher "Glideslope"
#define AppVersion GetEnv("GLIDESLOPE_APP_VERSION")
#define PublishDir GetEnv("GLIDESLOPE_PUBLISH_DIR")

#if AppVersion == ""
  #error "GLIDESLOPE_APP_VERSION must come from Glideslope.App.csproj."
#endif
#if PublishDir == ""
  #error "GLIDESLOPE_PUBLISH_DIR must name an explicit frozen publish output."
#endif

; Start Glideslope.App.exe directly for shortcuts, post-install launch, and sign-in. The publish keeps
; native libraries beside the executable, so no wrapper is needed.

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Glideslope
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputBaseFilename=Glideslope-Setup-{#AppVersion}-x64
SetupIconFile={#PublishDir}\Assets\glideslope-icon.ico
UninstallDisplayIcon={app}\Assets\glideslope-icon.ico
CloseApplications=no
UsePreviousAppDir=no
WizardStyle=modern

[Tasks]
; Offered on a first install only. On an upgrade the choice already lives in
; Windows (the Run value the app's Settings checkbox reads and writes), so the task is hidden and
; CurStepChanged leaves the registration alone; see IsUpgrade.
Name: "startatsignin"; Description: "Start Glideslope when I sign in"; GroupDescription: "Startup options:"; Flags: checkedonce; Check: not IsUpgrade
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[InstallDelete]
; An upgrade removes the retired wrapper and its native-library extraction cache.
Type: files; Name: "{app}\glideslope-launcher.cmd"
Type: filesandordirs; Name: "{localappdata}\Glideslope\cache\bundle"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; Marks {app} as the installed copy. With it beside Glideslope.App.exe the app
; may register and reconcile start at sign-in; a build run from its output folder has none.
Source: "glideslope-installed.marker"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Glideslope"; Filename: "{app}\Glideslope.App.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Glideslope"; Filename: "{app}\Glideslope.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Glideslope.App.exe"; Description: "Launch Glideslope"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The app removes only the start-at-sign-in entries it owns (the Run value, and any retired
; Startup-folder .cmd entry under either name).
Filename: "{app}\Glideslope.App.exe"; Parameters: "--startup-registration disable"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; RunOnceId: "GlideslopeStartupRegistration"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Glideslope\cache\bundle"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupApprovedRunKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  RunValueName = 'Glideslope';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1';

var
  Upgrading: Boolean;

// Preserve the existing Windows startup registration on upgrades. Inno may remember an unchecked task
// from the first install, so read the current value before rewriting the uninstall key.
function InitializeSetup(): Boolean;
begin
  Upgrading := RegValueExists(HKCU, UninstallKey, 'UninstallString');
  if Upgrading then
    Log('Upgrade of an installed Glideslope: start at sign-in keeps its current state.')
  else
    Log('First install of Glideslope: start at sign-in follows the Startup options task.');
  Result := True;
end;

function IsUpgrade(): Boolean;
begin
  Result := Upgrading;
end;

function RunStartupRegistration(const Action: String): Boolean;
var
  ResultCode: Integer;
begin
  // Run the GUI executable directly, so no console window appears and its exit
  // code comes straight back (0 done, 1 refused or failed, 2 invalid or not an installed copy).
  Result := Exec(ExpandConstant('{app}\Glideslope.App.exe'), '--startup-registration ' + Action,
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    Log(Format('Glideslope startup registration "%s" failed with exit code %d.', [Action, ResultCode]));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if Upgrading then
      Log('Left the start-at-sign-in registration as it was (upgrade).')
    else if WizardIsTaskSelected('startatsignin') then
    begin
      if not RunStartupRegistration('enable') then
        SuppressibleMsgBox('Glideslope was installed, but its sign-in startup entry could not be updated. Open Settings to check the current startup state.', mbError, MB_OK, IDOK);
    end
    else if not RunStartupRegistration('disable') then
      SuppressibleMsgBox('Glideslope was installed, but its sign-in startup entry could not be updated. Open Settings to check the current startup state.', mbError, MB_OK, IDOK);
  end;
end;

// Safety net for uninstall. If the app's own "--startup-registration disable"
// in [UninstallRun] could not run, a Run value pointing at the removed exe would be left behind. The
// value is deleted only when its data is exactly the quoted installed exe with --autostart, the shape
// the app writes, so a value anyone else wrote under the same name is kept.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
  Expected: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Expected := '"' + ExpandConstant('{app}\Glideslope.App.exe') + '" --autostart';
    if RegQueryStringValue(HKCU, RunKey, RunValueName, Data) then
    begin
      if CompareText(Data, Expected) = 0 then
      begin
        if RegDeleteValue(HKCU, RunKey, RunValueName) then
        begin
          RegDeleteValue(HKCU, StartupApprovedRunKey, RunValueName);
          Log('Removed the Glideslope Run value left for the removed exe.');
        end
        else
          Log('Could not remove the Glideslope Run value left for the removed exe.');
      end
      else
        Log('Kept a Glideslope Run value that does not start this installation.');
    end;
  end;
end;
