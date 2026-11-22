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
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#AppLicense}"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Oshi Tuner"; Filename: "{app}\oshituner.exe"; WorkingDir: "{app}"
