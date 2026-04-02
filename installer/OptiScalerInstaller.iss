#define MyAppId "OptiScalerInstaller"
#define MyAppName "OptiScaler Installer"
#ifndef AppVersion
  #error AppVersion define is required.
#endif
#ifndef PublishDir
  #error PublishDir define is required.
#endif
#ifndef OutputDir
  #error OutputDir define is required.
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename define is required.
#endif
#ifndef AppExeName
  #error AppExeName define is required.
#endif

[Setup]
AppId={{9B3D4E08-7915-4D94-A4F0-B9330AAB7D4A}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher=fungk
AppPublisherURL=https://github.com/fungk/OptiScaler-Installer
AppSupportURL=https://github.com/fungk/OptiScaler-Installer/issues
DefaultDirName={localappdata}\Programs\OptiScaler Installer
DefaultGroupName=OptiScaler Installer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\src\OptiScalerInstaller.App\app.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\OptiScaler Installer"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall OptiScaler Installer"; Filename: "{uninstallexe}"
Name: "{autodesktop}\OptiScaler Installer"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch OptiScaler Installer"; Flags: nowait postinstall skipifsilent
