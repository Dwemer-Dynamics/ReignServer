#ifndef PackageRoot
  #error PackageRoot must be provided by the canonical release builder.
#endif
#ifndef ReleaseVersion
  #error ReleaseVersion must be provided by the canonical release builder.
#endif
#ifndef ReleaseNumericVersion
  #error ReleaseNumericVersion must be provided by the canonical release builder.
#endif
[Setup]
AppId={{BD4814E0-2C63-45C1-9353-A2D8A5386930}
AppName=Reign
AppVersion={#ReleaseVersion}
VersionInfoVersion={#ReleaseNumericVersion}
AppPublisher=Dwemer Dynamics
AppPublisherURL=https://github.com/Dwemer-Dynamics/ReignServer
DefaultDirName={localappdata}\Programs\ReignSetup
DefaultGroupName=Reign
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir={#PackageRoot}
OutputBaseFilename=ReignSetup-{#ReleaseVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Reign (preserves player data)
UninstallFilesDir={app}
InfoBeforeFile={#PackageRoot}\INSTALL.txt
[Files]
Source: "{#PackageRoot}\bootstrap\*.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageRoot}\package.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageRoot}\INSTALL.txt"; DestDir: "{app}"; Flags: ignoreversion
[Icons]
Name: "{group}\Start ReignServer"; Filename: "{sys}\cmd.exe"; Parameters: "/c title ReignServer && powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""{app}\Start-ReignServer.ps1"""; WorkingDir: "{app}"
Name: "{group}\Reign installation guide"; Filename: "{app}\INSTALL.txt"
Name: "{group}\Uninstall Reign"; Filename: "{uninstallexe}"
[Code]
var
  Folders: TInputDirWizardPage;
  Sources: TInputFileWizardPage;

procedure InitializeWizard;
begin
  Folders := CreateInputDirPage(wpWelcome, 'Choose installation folders',
    'Select Bannerlord and the drive with enough space for Reign.',
    'Keep the program and player data in separate folders. Setup verifies the game version and available space before installing.', False, '');
  Folders.Add('Steam Bannerlord v1.4.8 folder:');
  Folders.Add('ReignServer program folder:');
  Folders.Add('Reign player data and portraits:');
  Folders.Add('Folder containing the five Reign payload ZIP files:');
  Folders.Values[0] := ExpandConstant('{commonpf32}\Steam\steamapps\common\Mount & Blade II Bannerlord');
  Folders.Values[1] := ExpandConstant('{localappdata}\Programs\ReignServer');
  Folders.Values[2] := ExpandConstant('{localappdata}\Bannerlord Reign');
  Folders.Values[3] := ExpandConstant('{src}');
  Sources := CreateInputFilePage(Folders.ID, 'Private downloads (optional)',
    'Offline payload files work without a download link.',
    'If your distributor supplied a download-sources.json file, select it here. Setup downloads only missing payloads and verifies their hashes.');
  Sources.Add('Private download file (optional):', 'JSON files|*.json', '.json');
end;

function Quoted(Value: String): String;
var Tail: Integer;
begin
  if Pos('"', Value) > 0 then RaiseException('A folder cannot contain a quotation mark.');
  Result := '"' + Value;
  Tail := Length(Value);
  while Tail > 0 do begin
    if Value[Tail] <> '\' then Break;
    Result := Result + '\';
    Tail := Tail - 1;
  end;
  Result := Result + '"';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer; Arguments: String;
begin
  if CurStep = ssPostInstall then begin
    WizardForm.StatusLabel.Caption := 'Verifying payloads and installing Reign. Large portrait files can take several minutes.';
    Arguments := '-NoProfile -ExecutionPolicy Bypass -File ' + Quoted(ExpandConstant('{app}\Install-Reign.ps1')) +
      ' -Manifest ' + Quoted(ExpandConstant('{app}\package.json')) +
      ' -BannerlordRoot ' + Quoted(Folders.Values[0]) + ' -ProgramRoot ' + Quoted(Folders.Values[1]) +
      ' -DataRoot ' + Quoted(Folders.Values[2]) + ' -PayloadDirectory ' + Quoted(Folders.Values[3]);
    if Sources.Values[0] <> '' then Arguments := Arguments + ' -SourcesFile ' + Quoted(Sources.Values[0]);
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments, '', SW_SHOW, ewWaitUntilTerminated, Code) or (Code <> 0) then
      RaiseException('Reign setup did not complete. Review the setup journal in your chosen data folder, correct the reported problem, and run setup again.');
  end;
end;

function InitializeUninstall(): Boolean;
var Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File ' + Quoted(ExpandConstant('{app}\Uninstall-Reign.ps1')),
    '', SW_SHOW, ewWaitUntilTerminated, Code) and (Code = 0);
end;
