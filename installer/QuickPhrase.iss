; 闪语 Rust/Tauri 安装器。AppId 在各版本保持一致，数据由应用保存在用户目录。
#ifndef AppVersion
  #define AppVersion "0.2.1"
#endif
#ifndef FileVersion
  #define FileVersion AppVersion
#endif
#ifndef AppId
  #define AppId "{{E9DBDCE4-4E86-4F88-A845-7E91B5D7726C}"
#endif
#ifndef ReleaseRoot
  #define ReleaseRoot "..\artifacts"
#endif
#ifndef SourceDir
  #define SourceDir "..\src-tauri\target\release"
#endif
#ifndef WebViewBootstrap
  #define WebViewBootstrap "..\.build\deps\MicrosoftEdgeWebview2Setup.exe"
#endif
#ifndef ChineseLanguage
  #define ChineseLanguage "Languages\ChineseSimplified.isl"
#endif
#ifndef StartupValue
  #define StartupValue "QuickPhrase"
#endif
#ifndef LegacyStartupValue
  #define LegacyStartupValue "Shanyu"
#endif
#ifndef AppGroup
  #define AppGroup "闪语"
#endif
#ifndef WindowSizeDirectory
  #define WindowSizeDirectory "{localappdata}\com.quickphrase.desktop"
#endif

[Setup]
AppId={#AppId}
AppName=闪语 (QuickPhrase)
AppVersion={#AppVersion}
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#FileVersion}
VersionInfoProductTextVersion={#AppVersion}
AppVerName=闪语 (QuickPhrase) {#AppVersion}
AppPublisher=QuickPhrase 开源项目
DefaultDirName={localappdata}\Programs\QuickPhrase
; 同一 AppId 的升级沿用用户原来的目录，避免改名后另装一份。
UsePreviousAppDir=yes
DefaultGroupName={#AppGroup}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; 吸附服务调用 GetDpiForWindow，需要 Windows 10 1607 或更高版本。
MinVersion=10.0.14393
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#ReleaseRoot}
OutputBaseFilename=QuickPhrase-Setup-{#AppVersion}-x64
SetupIconFile=..\src-tauri\icons\icon.ico
UninstallDisplayIcon={app}\QuickPhrase.exe
UninstallDisplayName=闪语 (QuickPhrase)
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#ChineseLanguage}"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#SourceDir}\QuickPhrase.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#WebViewBootstrap}"; Flags: dontcopy

[Icons]
Name: "{group}\闪语"; Filename: "{app}\QuickPhrase.exe"; AppUserModelID: "com.quickphrase.desktop"
Name: "{group}\卸载闪语"; Filename: "{uninstallexe}"
Name: "{autodesktop}\闪语"; Filename: "{app}\QuickPhrase.exe"; Tasks: desktopicon; AppUserModelID: "com.quickphrase.desktop"

[Run]
Filename: "{app}\QuickPhrase.exe"; Description: "打开闪语(&L)"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#StartupValue}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#LegacyStartupValue}"; Flags: uninsdeletevalue

[InstallDelete]
; 只清理已注册旧安装的程序本体；用户数据库和外部附件不在安装目录中。
Type: files; Name: "{app}\shanyu.exe"; Check: IsUpgrade

[Code]
type
  TWindowRect = record Left, Top, Right, Bottom: Integer; end;
var
  PreviousWindowSize: String;

function SizeFindWindowEx(Parent, After: HWND; ClassName, Title: String): HWND;
  external 'FindWindowExW@user32.dll stdcall';
function SizeGetProcessId(Window: HWND; var ProcessId: LongWord): LongWord;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function SizeOpenProcess(Access: LongWord; Inherit: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function SizeQueryImage(Process: THandle; Flags: LongWord; Image: String; var Length: LongWord): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function SizeCloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function SizeGetClientRect(Window: HWND; var Rect: TWindowRect): Boolean;
  external 'GetClientRect@user32.dll stdcall';
function SizeGetDpi(Window: HWND): LongWord;
  external 'GetDpiForWindow@user32.dll stdcall';
function SizeIsIconic(Window: HWND): Boolean;
  external 'IsIconic@user32.dll stdcall';
function SizeIsZoomed(Window: HWND): Boolean;
  external 'IsZoomed@user32.dll stdcall';
function SizeDpiContext(Context: THandle): THandle;
  external 'SetThreadDpiAwarenessContext@user32.dll stdcall';

{ 旧版尚未保存尺寸：在安装器关闭旧进程之前只读客户区，且核对进程确实来自本次安装目录。
  新版已有尺寸文件时不迁移，不覆盖运行中客户端保存的最新结果，也不读取话术或聊天内容。 }
procedure CapturePreviousWindowSize;
var Window: HWND; ProcessId, Length, Dpi: LongWord; Process, OldDpi: THandle;
    Image, SizePath: String; Rect: TWindowRect; Width, Height: Integer;
begin
  SizePath := ExpandConstant('{#WindowSizeDirectory}\window-size.json');
  if FileExists(SizePath) then Exit;
  Window := SizeFindWindowEx(0, 0, 'Tauri Window', '闪语');
  while Window <> 0 do begin
    SizeGetProcessId(Window, ProcessId);
    Process := SizeOpenProcess($1000, False, ProcessId);
    if Process <> 0 then begin
      try
        Length := 32768; SetLength(Image, Length);
        if SizeQueryImage(Process, 0, Image, Length) then begin
          SetLength(Image, Length);
          if ((CompareText(Image, ExpandConstant('{app}\QuickPhrase.exe')) = 0) or
              (CompareText(Image, ExpandConstant('{app}\shanyu.exe')) = 0)) and
              not SizeIsIconic(Window) and not SizeIsZoomed(Window) then begin
            OldDpi := SizeDpiContext(-4);
            try
              Dpi := SizeGetDpi(Window);
              if (Dpi > 0) and SizeGetClientRect(Window, Rect) then begin
                Width := ((Rect.Right - Rect.Left) * 96 + Integer(Dpi) div 2) div Integer(Dpi);
                Height := ((Rect.Bottom - Rect.Top) * 96 + Integer(Dpi) div 2) div Integer(Dpi);
                if (Width > 0) and (Height > 0) then begin
                  PreviousWindowSize := Format('{"width":%d,"height":%d}', [Width, Height]);
                  Log('已记录升级前窗口大小。');
                  Exit;
                end;
              end;
            finally
              if OldDpi <> 0 then SizeDpiContext(OldDpi);
            end;
          end;
        end;
      finally
        SizeCloseHandle(Process);
      end;
    end;
    Window := SizeFindWindowEx(0, Window, 'Tauri Window', '闪语');
  end;
end;

const
  WebViewKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function IsUpgrade: Boolean;
var PreviousPath: String;
begin
  Result := RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + ExpandConstant('{#AppId}') + '_is1',
    'Inno Setup: App Path', PreviousPath) and
    (CompareText(RemoveBackslashUnlessRoot(PreviousPath), RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) = 0);
end;

procedure RegisterExtraCloseApplicationsResources;
begin
  { 改名升级也必须检查旧程序是否正在运行，不能遗漏旧二进制的文件占用。 }
  if IsUpgrade then
    RegisterExtraCloseApplicationsResource(True, ExpandConstant('{app}\shanyu.exe'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var LegacyCommand, SizePath, SizeTemp: String;
begin
  if CurStep = ssPostInstall then begin
    SizePath := ExpandConstant('{#WindowSizeDirectory}\window-size.json');
    if (PreviousWindowSize <> '') and not FileExists(SizePath) then begin
      ForceDirectories(ExtractFileDir(SizePath));
      SizeTemp := SizePath + '.upgrade';
      if SaveStringToFile(SizeTemp, PreviousWindowSize, False) and RenameFile(SizeTemp, SizePath) then
        Log('已保留升级前窗口大小。')
      else begin
        DeleteFile(SizeTemp);
        Log('无法保存升级前窗口大小，首次打开可能使用默认大小。');
      end;
    end;
    { 静默升级未启动应用时也更新入口；仅接管指向本次安装目录的旧启动项。 }
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#LegacyStartupValue}', LegacyCommand) and
      (CompareText(LegacyCommand, '"' + ExpandConstant('{app}\shanyu.exe') + '" --autostart') = 0) then begin
      if RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupValue}',
        '"' + ExpandConstant('{app}\QuickPhrase.exe') + '" --autostart') then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#LegacyStartupValue}')
      else
        Log('开机启动入口更新失败，请在闪语设置中重新启用开机启动。');
    end;
  end;
end;

function WebViewInstalled: Boolean;
var Version: String;
begin
  Result := (RegQueryStringValue(HKCU, WebViewKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM32, WebViewKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM64, WebViewKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ExitCode: Integer;
begin
  Result := '';
  CapturePreviousWindowSize;
  if WebViewInstalled then Exit;
  { 官方引导程序支持当前用户安装；运行失败时停止安装并保留具体退出码。 }
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := '无法启动 WebView2 安装程序。系统错误：' + SysErrorMessage(ExitCode)
  else if not WebViewInstalled then
    Result := 'WebView2 运行时安装失败（退出码 ' + IntToStr(ExitCode) + '）。请检查网络连接，或先安装 Microsoft Edge WebView2 运行时后重试。';
end;

{ 卸载不包含数据目录清理。数据库、图片、设置和日志始终保留。 }
