; Compile through scripts/Publish-Release.ps1 -Installer.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef PackageName
  #error PackageName is required
#endif

[Setup]
AppId={{AC1CE159-8A7D-46EE-B071-C87991081620}
AppName=MagiDesk
AppVersion={#AppVersion}
AppPublisher=MagiDesk
DefaultDirName={localappdata}\Programs\MagiDesk
DefaultGroupName=MagiDesk
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.17763
#if Runtime == "win-arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir={#PackageDir}
OutputBaseFilename={#PackageName}
SetupIconFile=..\MagiDesk\Assets\app.ico
UninstallDisplayIcon={app}\MagiDesk.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex={code:RunningMutex}
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
DisableWelcomePage=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MagiDesk"; Filename: "{app}\MagiDesk.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\MagiDesk"; Filename: "{app}\MagiDesk.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\MagiDesk.exe"; Description: "启动 MagiDesk"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function RunningMutex(Param: String): String;
begin
  Result := 'MagiDesk.SingleInstance.' + GetUserNameString;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, InstalledExe: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Only remove this installation's Run value. Preserve portable copies. }
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MagiDesk', Command) then
    begin
      InstalledExe := ExpandConstant('{app}\MagiDesk.exe');
      if (CompareText(Trim(Command), '"' + InstalledExe + '"') = 0) or
         (CompareText(Trim(Command), InstalledExe) = 0) then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MagiDesk');
    end;
  end;
  { Never delete AppData configuration, desktop files, or mapped folders. }
end;
