#ifdef UpdateTest
  #define Profile "ShuShuShai-UpdateTest"
  #define AppTitle "舒舒筛（更新验收）"
#else
  #define Profile "ShuShuShai"
  #define AppTitle "舒舒筛"
#endif
#define InstallKey "Software\ShuShuShai\Installations\" + Profile
#define AppExe "舒舒筛.exe"

[Setup]
AppId={#Profile}.Windows.x64
AppName={#AppTitle}
AppVersion={#AppVersion}
AppPublisher=舒舒筛
AppVerName={#AppTitle} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName={#AppTitle}
DefaultDirName={localappdata}\Programs\{#Profile}
DefaultGroupName={#AppTitle}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
AppMutex=Local\{#Profile}-Installed
SetupMutex=Local\{#Profile}-Setup
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\ShuShuShai.App\Assets\ShuShuShai.ico
OutputDir={#Destination}
OutputBaseFilename=ShuShuShai-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no

[Languages]
Name: "zhcn"; MessagesFile: "{#ChineseMessages}"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#InnoLicense}"; DestDir: "{app}\licenses"; DestName: "Inno-Setup.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppTitle}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{userdesktop}\{#AppTitle}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "{#InstallKey}"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "{#InstallKey}"; ValueType: string; ValueName: "InstalledVersion"; ValueData: "{#AppVersion}"
Root: HKCU; Subkey: "{#InstallKey}"; ValueType: string; ValueName: "UninstallerPath"; ValueData: "{uninstallexe}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "安装完成后启动{#AppTitle}"; Flags: postinstall nowait skipifsilent

[Code]
function OpenProcess(DesiredAccess: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function WaitForAppExit(): Boolean;
var
  RequestedPID: String;
  PID: Integer;
  ProcessHandle: THandle;
begin
  Result := True;
  RequestedPID := ExpandConstant('{param:WAITFORPID|}');
  if RequestedPID = '' then Exit;

  Result := False;
  PID := StrToIntDef(RequestedPID, 0);
  if PID > 0 then begin
    ProcessHandle := OpenProcess($00100000, False, PID);
    if ProcessHandle = 0 then
      Result := DLLGetLastError = 87
    else begin
      try
        Result := WaitForSingleObject(ProcessHandle, 30000) = 0;
      finally
        CloseHandle(ProcessHandle);
      end;
    end;
  end;
  if not Result then
    SuppressibleMsgBox('未能确认舒舒筛已正常退出。请关闭软件后重试。', mbError, MB_OK, IDOK);
end;

function InitializeUninstall(): Boolean;
begin
  Result := WaitForAppExit();
end;

function InitializeSetup(): Boolean;
begin
  Result := WaitForAppExit();
#ifdef UpdateTest
  if Result and (ExpandConstant('{param:QACANCEL|}') = '1') then begin
    Log('QA: Cancelled before installation.');
    Result := False;
  end;
#endif
end;

function HasContents(const Directory: String): Boolean;
var
  Item: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Directory) + '*', Item) then
    try
      repeat
        if (Item.Name <> '.') and (Item.Name <> '..') then begin
          Result := True;
          Exit;
        end;
      until not FindNext(Item);
    finally
      FindClose(Item);
    end;
end;

function DirectoryProblem(): String;
var
  Previous, Target, DataDirectory: String;
begin
  Result := '';
  Target := AddBackslash(WizardDirValue);
  DataDirectory := AddBackslash(ExpandConstant('{localappdata}\{#Profile}'));
  if (Pos(LowerCase(Target), LowerCase(DataDirectory)) = 1) or
     (Pos(LowerCase(DataDirectory), LowerCase(Target)) = 1) then
    Result := '请选择独立的程序安装目录。选片数据目录应与程序目录分开。'
  else if RegQueryStringValue(HKCU, '{#InstallKey}', 'InstallPath', Previous) and
          FileExists(AddBackslash(Previous) + '{#AppExe}') then begin
    if CompareText(AddBackslash(Previous), Target) <> 0 then
      Result := '升级沿用现有安装目录。若要更换位置，请先卸载现有版本。';
  end else if HasContents(Target) then
    Result := '请选择一个空目录安装舒舒筛。';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Problem: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;
  Problem := DirectoryProblem();
  if Problem <> '' then begin
    SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := DirectoryProblem();
end;
