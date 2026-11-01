#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
AppId={{76CF9AAE-A91A-4261-9D38-DA67204D55F1}
AppName=ChatGPT Skin Studio
AppVersion=1.1.4
AppVerName=ChatGPT Skin Studio V1.1.4
AppPublisher=Skin Studio
DefaultDirName={localappdata}\Programs\ChatGptSkinStudio
DefaultGroupName=ChatGPT Skin Studio
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.18362
OutputDir={#OutputDir}
OutputBaseFilename=ChatGptSkinStudio-1.1.4-Windows-x64-Setup
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\ChatGptSkinStudio.exe
VersionInfoVersion=1.1.4.0
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=ChatGptSkinStudio.exe,SkinRenderer.exe
RestartApplications=no
AppMutex=Local\ChatGptSkinStudio-1
UninstallDisplayName=ChatGPT Skin Studio V1.1.4

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,zh-CN.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ChatGPT Skin Studio"; Filename: "{app}\ChatGptSkinStudio.exe"
Name: "{group}\卸载 ChatGPT Skin Studio"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ChatGPT Skin Studio"; Filename: "{app}\ChatGptSkinStudio.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "ChatGptSkinStudio"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\ChatGptSkinStudio.exe"; Description: "打开 ChatGPT Skin Studio"; Flags: nowait postinstall skipifsilent

[Code]
function CloseManager(): Boolean;
var
  ResultCode: Integer;
  Count: Integer;
begin
  Result := True;
  if FileExists(ExpandConstant('{app}\ChatGptSkinStudio.exe')) then
    Exec(ExpandConstant('{app}\ChatGptSkinStudio.exe'), '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Count := 0;
  while CheckForMutexes('Local\ChatGptSkinStudio-1') and (Count < 60) do
  begin
    Sleep(500);
    Count := Count + 1;
  end;
  Result := not CheckForMutexes('Local\ChatGptSkinStudio-1');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not CloseManager() then
    Result := '请从管理器或托盘菜单退出 ChatGPT Skin Studio 后再继续。官方 ChatGPT 客户端可以保持运行。';
end;

function InitializeUninstall(): Boolean;
begin
  Result := CloseManager();
  if not Result then
    MsgBox('请先退出 ChatGPT Skin Studio，再卸载。官方 ChatGPT 客户端可以保持运行。', mbInformation, MB_OK);
end;
