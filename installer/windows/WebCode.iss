#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#ifndef PublishDir
  #error PublishDir must be provided.
#endif

#ifndef TtsBundleDir
  #error TtsBundleDir must be provided.
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
Name: "{code:GetReplyTtsInstallRoot}\cache"
Name: "{code:GetReplyTtsInstallRoot}\logs"
Name: "{code:GetReplyTtsInstallRoot}\service"
Name: "{code:GetReplyTtsInstallRoot}\temp"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist ignoreversion
Source: "{#TtsBundleDir}\*"; DestDir: "{code:GetReplyTtsInstallRoot}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppSourceExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppSourceExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppSourceExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DRIVE_FIXED = 3;

var
  ReplyTtsDirPage: TInputDirWizardPage;

function GetDriveType(lpRootPathName: string): Integer;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function NormalizePathSeparators(Value: string): string;
begin
  Result := Trim(Value);
  if Result <> '' then
    StringChangeEx(Result, '/', '\', True);
end;

function NormalizeDriveRoot(Value: string): string;
var
  Candidate: string;
begin
  Candidate := NormalizePathSeparators(Value);
  Result := ExtractFileDrive(Candidate);

  if (Result = '') and (Length(Candidate) >= 2) and (Candidate[2] = ':') then
    Result := Copy(Candidate, 1, 2);

  if Result <> '' then
    Result := Uppercase(Result) + '\';
end;

function NormalizeReplyTtsInstallRoot(Value: string): string;
begin
  Result := NormalizePathSeparators(Value);
  while (Length(Result) > 3) and (Result[Length(Result)] = '\') do
    Delete(Result, Length(Result), 1);
end;

function GetSystemDriveRoot: string;
begin
  Result := NormalizeDriveRoot(ExpandConstant('{sys}'));
  if Result = '' then
    Result := 'C:\';
end;

function IsSystemDrivePath(Value: string): Boolean;
begin
  Result := NormalizeDriveRoot(Value) = GetSystemDriveRoot;
end;

function CanWriteToDrive(DriveRoot: string): Boolean;
var
  ProbeDir: string;
  ProbeFile: string;
begin
  Result := False;
  ProbeDir := AddBackslash(NormalizeDriveRoot(DriveRoot)) +
    '.webcode-kokoro-probe-' + GetDateTimeString('yyyymmddhhnnsszzz', #0, #0);
  ProbeFile := AddBackslash(ProbeDir) + 'probe.tmp';

  if not ForceDirectories(ProbeDir) then
    Exit;

  if not SaveStringToFile(ProbeFile, 'probe', False) then begin
    RemoveDir(ProbeDir);
    Exit;
  end;

  Result := True;
  DeleteFile(ProbeFile);
  RemoveDir(ProbeDir);
end;

function IsWritableFixedNonSystemDriveRoot(Value: string): Boolean;
var
  DriveRoot: string;
begin
  DriveRoot := NormalizeDriveRoot(Value);
  Result :=
    (DriveRoot <> '') and
    DirExists(DriveRoot) and
    (GetDriveType(DriveRoot) = DRIVE_FIXED) and
    (not IsSystemDrivePath(DriveRoot)) and
    CanWriteToDrive(DriveRoot);
end;

function FindExistingReplyTtsInstallRoot: string;
var
  DriveIndex: Integer;
  DriveRoot: string;
  CandidateRoot: string;
begin
  Result := '';

  for DriveIndex := Ord('A') to Ord('Z') do begin
    DriveRoot := Chr(DriveIndex) + ':\';
    if not IsWritableFixedNonSystemDriveRoot(DriveRoot) then
      Continue;

    CandidateRoot := NormalizeReplyTtsInstallRoot(DriveRoot + 'WebCodeData\Kokoro');
    if DirExists(CandidateRoot) then begin
      Result := CandidateRoot;
      Exit;
    end;
  end;
end;

function GetFirstWritableNonSystemDriveRoot: string;
var
  DriveIndex: Integer;
  DriveRoot: string;
begin
  Result := '';

  for DriveIndex := Ord('A') to Ord('Z') do begin
    DriveRoot := Chr(DriveIndex) + ':\';
    if IsWritableFixedNonSystemDriveRoot(DriveRoot) then begin
      Result := DriveRoot;
      Exit;
    end;
  end;
end;

function GetDefaultReplyTtsInstallRoot: string;
var
  PreviousRoot: string;
  ExistingRoot: string;
  DriveRoot: string;
begin
  PreviousRoot := NormalizeReplyTtsInstallRoot(GetPreviousData('ReplyTtsInstallRoot', ''));
  if (PreviousRoot <> '') and IsWritableFixedNonSystemDriveRoot(PreviousRoot) then begin
    Result := PreviousRoot;
    Exit;
  end;

  ExistingRoot := FindExistingReplyTtsInstallRoot;
  if ExistingRoot <> '' then begin
    Result := ExistingRoot;
    Exit;
  end;

  DriveRoot := GetFirstWritableNonSystemDriveRoot;
  if DriveRoot <> '' then begin
    Result := NormalizeReplyTtsInstallRoot(DriveRoot + 'WebCodeData\Kokoro');
    Exit;
  end;

  Result := '';
end;

function InitializeSetup(): Boolean;
begin
  Result := GetFirstWritableNonSystemDriveRoot <> '';
  if not Result then
    MsgBox(
      'WebCode cannot install the bundled Reply TTS payload because this Windows machine has no writable non-system fixed drive. ' +
      'Attach or map a writable data drive, then run Setup again.',
      mbCriticalError,
      MB_OK);
end;

procedure InitializeWizard;
begin
  ReplyTtsDirPage := CreateInputDirPage(
    wpSelectDir,
    'Reply TTS Storage',
    'Where should the bundled Reply TTS payload be installed?',
    'Setup installs the Kokoro/sherpa-onnx model, ffmpeg, Python runtime, and dependencies to a writable non-system drive.',
    False,
    SetupMessage(msgNewFolderName));

  ReplyTtsDirPage.Add('Reply TTS storage root:');
  ReplyTtsDirPage.Values[0] := GetDefaultReplyTtsInstallRoot;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  CandidateRoot: string;
  DriveRoot: string;
begin
  Result := True;

  if (ReplyTtsDirPage <> nil) and (CurPageID = ReplyTtsDirPage.ID) then begin
    CandidateRoot := NormalizeReplyTtsInstallRoot(ReplyTtsDirPage.Values[0]);
    DriveRoot := NormalizeDriveRoot(CandidateRoot);

    if CandidateRoot = '' then begin
      MsgBox('Choose a Reply TTS storage root on a writable non-system drive.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if DriveRoot = '' then begin
      MsgBox('Reply TTS storage root must be an absolute Windows path such as E:\WebCodeData\Kokoro.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if CandidateRoot = DriveRoot then
      CandidateRoot := NormalizeReplyTtsInstallRoot(DriveRoot + 'WebCodeData\Kokoro');

    if IsSystemDrivePath(CandidateRoot) then begin
      MsgBox('Reply TTS storage root must be on a non-system drive. Do not use the Windows system drive.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if GetDriveType(DriveRoot) <> DRIVE_FIXED then begin
      MsgBox('Reply TTS storage root must be on a fixed local drive.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if not DirExists(DriveRoot) then begin
      MsgBox('The selected Reply TTS drive is not available.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if not CanWriteToDrive(DriveRoot) then begin
      MsgBox('The selected Reply TTS drive is not writable.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    ReplyTtsDirPage.Values[0] := CandidateRoot;
  end;
end;

function GetReplyTtsInstallRoot(Param: string): string;
begin
  if ReplyTtsDirPage <> nil then
    Result := NormalizeReplyTtsInstallRoot(ReplyTtsDirPage.Values[0])
  else
    Result := GetDefaultReplyTtsInstallRoot;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'ReplyTtsInstallRoot', GetReplyTtsInstallRoot(''));
end;
