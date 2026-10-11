; 闪语 Rust/Tauri 安装器。AppId 在各版本保持一致，数据由应用保存在用户目录。
#ifndef AppVersion
  #define AppVersion "0.1.1"
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
  #define StartupValue "Shanyu"
#endif

[Setup]
AppId={#AppId}
AppName=闪语
AppVersion={#AppVersion}
AppVerName=闪语 {#AppVersion}
AppPublisher=闪语开源项目
DefaultDirName={localappdata}\Programs\Shanyu
DefaultGroupName=闪语
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; 吸附服务调用 GetDpiForWindow，需要 Windows 10 1607 或更高版本。
MinVersion=10.0.14393
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#ReleaseRoot}
OutputBaseFilename=Shanyu-Setup-{#AppVersion}-x64
SetupIconFile=..\src-tauri\icons\icon.ico
UninstallDisplayIcon={app}\shanyu.exe
UninstallDisplayName=闪语
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
Source: "{#SourceDir}\shanyu.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#WebViewBootstrap}"; Flags: dontcopy

[Icons]
Name: "{group}\闪语"; Filename: "{app}\shanyu.exe"
Name: "{group}\卸载闪语"; Filename: "{uninstallexe}"
Name: "{autodesktop}\闪语"; Filename: "{app}\shanyu.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\shanyu.exe"; Description: "打开闪语(&L)"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#StartupValue}"; Flags: uninsdeletevalue

[Code]
const
  WebViewKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

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
  if WebViewInstalled then Exit;
  { 官方引导程序支持当前用户安装；运行失败时停止安装并保留具体退出码。 }
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := '无法启动 WebView2 安装程序。系统错误：' + SysErrorMessage(ExitCode)
  else if not WebViewInstalled then
    Result := 'WebView2 运行时安装失败（退出码 ' + IntToStr(ExitCode) + '）。请检查网络连接，或先安装 Microsoft Edge WebView2 运行时后重试。';
end;

{ 卸载不包含数据目录清理。数据库、图片、设置和日志始终保留。 }
