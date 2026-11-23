[Setup]
AppId={{92C18ECB-4BA8-4D05-BAE6-CA3047E78C69}
AppName=Oshi Tuner
AppVersion={#AppVersion}
AppPublisher=Oshi Tuner
AppPublisherURL=https://github.com/benjaminkwilliams/oshituner
AppSupportURL=https://github.com/benjaminkwilliams/oshituner/issues
AppUpdatesURL=https://github.com/benjaminkwilliams/oshituner/releases/latest
DefaultDirName={localappdata}\Programs\Oshi Tuner
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\oshituner.exe
OutputBaseFilename=Oshi-Tuner-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "..\scripts\Guard-BrowserHost.ps1"; Flags: dontcopy noencryption
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#AppLicense}"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Run]
Filename: "{app}\oshituner.exe"; Parameters: "--browser-setup"; Description: "Set up Edge or Chrome integration"; Flags: postinstall skipifsilent unchecked

[UninstallRun]
Filename: "{app}\oshituner.exe"; Parameters: "--remove-browser-pairings"; Flags: runhidden; RunOnceId: "RemoveBrowserPairings"

[Icons]
Name: "{autoprograms}\Oshi Tuner"; Filename: "{app}\oshituner.exe"; WorkingDir: "{app}"

[Code]
var
  BrowserGuardActive: Boolean;

function RunBrowserGuard(const Mode: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\Guard-BrowserHost.ps1') + '" -Mode ' + Mode +
    ' -Executable "' + ExpandConstant('{app}\oshituner.exe') + '" -ExitDesktop',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure ResumeBrowsers;
begin
  if not BrowserGuardActive then
    Exit;
  BrowserGuardActive := not RunBrowserGuard('Resume');
  if BrowserGuardActive then
    Log('Could not resume the Oshi Tuner browser registrations. Restart setup to retry.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not FileExists(ExpandConstant('{app}\oshituner.exe')) then
    Exit;
  ExtractTemporaryFile('Guard-BrowserHost.ps1');
  BrowserGuardActive := True;
  if not RunBrowserGuard('Suspend') then
  begin
    ResumeBrowsers;
    Result := 'Could not pause the Oshi Tuner browser helpers. Exit Oshi Tuner from the system tray and exit the paired browsers, then try setup again.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    ResumeBrowsers;
end;

procedure DeinitializeSetup;
begin
  ResumeBrowsers;
end;
