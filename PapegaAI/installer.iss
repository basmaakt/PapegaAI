; PapegaAI-installer — bouw met: ISCC.exe installer.iss
; Vereist dat `dotnet publish -o dist` eerst is gedraaid (zie README).

#define MyAppName "PapegaAI"
#define MyAppVersion "1.0"
#define MyAppExeName "PapegaAI.exe"

[Setup]
AppId={{B3F6D2E8-7C41-4A9B-9D25-1E8F0A6C4D77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Bas
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
; Per-user installatie: geen adminrechten of UAC-prompt nodig.
PrivilegesRequired=lowest
OutputDir=installer-output
OutputBaseFilename=PapegaAI-setup
SetupIconFile=papegaai.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "autostart"; Description: "{#MyAppName} automatisch starten bij inloggen"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#MyAppName}"; \
    ValueData: """{app}\{#MyAppExeName}"" run --hidden"; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} nu starten"; \
    Flags: nowait postinstall skipifsilent

[Code]
// Draaiende PapegaAI afsluiten vóór installeren/verwijderen, anders zijn de
// bestanden vergrendeld.
function KillApp(): Integer;
var R: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}', '',
    SW_HIDE, ewWaitUntilTerminated, R);
  Result := R;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  KillApp();
  Result := True;
end;

// Bij verwijderen: vragen of de gedownloade modellen, instellingen en
// geschiedenis ook weg mogen (kan 2+ GB zijn; hersteld door opnieuw te
// downloaden). Bij een stille uninstall blijven ze staan.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent then
    begin
      if MsgBox('Ook de gedownloade taalmodellen, instellingen en dictatiegeschiedenis verwijderen?'
        + #13#10#13#10 + '(map: ' + ExpandConstant('{localappdata}\{#MyAppName}') + ')',
        mbConfirmation, MB_YESNO) = IDYES then
        DelTree(ExpandConstant('{localappdata}\{#MyAppName}'), True, True, True);
    end;
  end;
end;
