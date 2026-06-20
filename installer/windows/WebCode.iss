#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#ifndef PublishDir
  #error PublishDir must be provided.
#endif

#ifndef OutputDir
  #define OutputDir "."
#endif

#ifndef MyAppInstallerFileName
  #define MyAppInstallerFileName "WebCode-Setup"
#endif

#ifndef MyAppSourceExe
  #define MyAppSourceExe "WebCodeCli.exe"
#endif

#define MyAppName "WebCode"
#define MyAppPublisher "lusile2024"
#define MyAppURL "https://github.com/lusile2024/WebCode"

[Setup]
AppId={{3D3D5C64-7824-4CC0-B6A4-27FFCB9AE4B1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename={#MyAppInstallerFileName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
SetupIconFile={#PublishDir}\wwwroot\favicon.ico
UninstallDisplayIcon={app}\{#MyAppSourceExe}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Dirs]
Name: "{app}\data"
Name: "{app}\logs"
Name: "{app}\workspaces"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppSourceExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppSourceExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppSourceExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
