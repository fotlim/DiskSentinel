; ============================================================
;  磁盘哨兵 DiskSentinel — Inno Setup 安装脚本
;  用 Inno Setup 6 编译：ISCC.exe installer.iss
; ============================================================

#define AppName        "磁盘哨兵"
#define AppNameEn      "DiskSentinel"
#define AppVersion     "1.0.0"
#define AppPublisher   "fotlim"
#define AppURL         "https://github.com/fotlim/DiskSentinel"
#define AppExeName     "DiskSentinel.exe"

[Setup]
AppId={{8F3A2C71-4D5B-4E8A-9C16-7B2D5E9F1A03}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
DefaultDirName={autopf}\{#AppNameEn}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=LICENSE
OutputDir=dist
OutputBaseFilename=DiskSentinel-{#AppVersion}-Setup
SetupIconFile=Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
; 让安装程序显示中文
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "installer\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.CreateDesktopIcon=创建桌面快捷方式
chinesesimplified.LaunchApp=立即运行 {#AppName}
chinesesimplified.RegisterBootTask=注册开机自动检测（推荐）
chinesesimplified.RegisterBootTaskDesc=每次开机后静默检测一次磁盘健康，记录后立即退出，不常驻后台。
chinesesimplified.UninstallKeepData=是否保留历史数据？
chinesesimplified.UninstallKeepDataDesc=历史记录保存在 %LOCALAPPDATA%\DiskSentinel 或程序目录的 Data 文件夹。选择「是」保留，方便以后重装继续积累趋势。
chinesesimplified.NeedDotNet=本程序需要 .NET 8 桌面运行时，是否现在下载？
chinesesimplified.NeedWinAppSdk=本程序需要 Windows App Runtime 1.8，是否现在下载？

english.CreateDesktopIcon=Create a desktop shortcut
english.LaunchApp=Launch {#AppName}
english.RegisterBootTask=Register boot-time check (recommended)
english.RegisterBootTaskDesc=Silently check disk health once after each logon, then exit immediately. Never runs in background.
english.UninstallKeepData=Keep history data?
english.UninstallKeepDataDesc=History is stored in %LOCALAPPDATA%\DiskSentinel or the app's Data folder. Choose Yes to keep it for future installs.
english.NeedDotNet=This app requires .NET 8 Desktop Runtime. Download now?
english.NeedWinAppSdk=This app requires Windows App Runtime 1.8. Download now?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "bootcheck";   Description: "{cm:RegisterBootTask}"; GroupDescription: "{cm:RegisterBootTaskDesc}"

[Files]
; 主程序全部文件
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 说明文档
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE";   DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";     Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; 注册开机检测任务（静默，不弹窗）
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/Create /TN ""DiskSentinel_BootCheck"" /TR ""\""{app}\{#AppExeName}\"" --bootcheck"" /SC ONLOGON /DELAY 0000:30 /RL HIGHEST /F"; \
  Flags: runhidden waituntilterminated; \
  Tasks: bootcheck; \
  StatusMsg: "{cm:RegisterBootTaskDesc}"

; 安装完成后可选启动
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载时移除计划任务
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""DiskSentinel_BootCheck"" /F"; \
  Flags: runhidden waituntilterminated; RunOnceId: "DelBootTask"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Data"

[Code]
{ 检查 .NET 8 桌面运行时 }
function IsDotNet8Installed(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetSubkeyNames(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('8.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

{ 检查 Windows App Runtime 1.8 }
function IsWinAppSdkInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetSubkeyNames(HKLM, 'SOFTWARE\Microsoft\WindowsRuntime\ActivatableClassId', Names) then
  begin
    { 该键存在不代表版本正确，这里只做粗检查；
      真正的启动失败由程序自己提示，避免误拦截安装 }
    Result := True;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrCode: Integer;
begin
  Result := True;

  if not IsDotNet8Installed() then
  begin
    if MsgBox(CustomMessage('NeedDotNet'), mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0/runtime', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDirLocal, DataDirPortable: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDirLocal := ExpandConstant('{localappdata}\DiskSentinel');
    DataDirPortable := ExpandConstant('{app}\Data');

    if DirExists(DataDirLocal) or DirExists(DataDirPortable) then
    begin
      if MsgBox(CustomMessage('UninstallKeepDataDesc'), mbConfirmation, MB_YESNO) = IDNO then
      begin
        DelTree(DataDirLocal, True, True, True);
        DelTree(DataDirPortable, True, True, True);
      end;
    end;
  end;
end;
