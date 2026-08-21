; 闪语（QuickPhrase）按当前用户安装的 Inno Setup 脚本（纯 WPF 自包含安装包，无 WebView2 运行时依赖）。
#ifndef AppVersion
  #define AppVersion "0.0.1"
#endif
#ifndef ReleaseRoot
  #define ReleaseRoot "..\artifacts\release\0.0.1"
#endif
#ifndef OutputBase
  #define OutputBase "QuickPhrase-Setup-0.0.1"
#endif
#define AppId "{E9DBDCE4-4E86-4F88-A845-7E91B5D7726C}"
#define AppExeName "QuickPhrase.exe"

[Setup]
AppId={{#AppId}}
AppName=闪语
AppVerName=闪语 {#AppVersion}
AppVersion={#AppVersion}
AppPublisher=QuickPhrase Contributors
DefaultDirName={localappdata}\Programs\QuickPhrase
DefaultGroupName=闪语
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#ReleaseRoot}\installers
OutputBaseFilename={#OutputBase}
SetupIconFile=..\assets\quickphrase.ico
UninstallDisplayIcon={app}\{#AppExeName},0
UninstallDisplayName=闪语
Uninstallable=yes
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=no
CloseApplications=yes
RestartApplications=no

[Tasks]
; 使用 Inno Setup 原生任务创建快捷方式，确保勾选后由安装器可靠写入当前用户桌面。
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务："

[Files]
Source: "{#ReleaseRoot}\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 显式引用 EXE 的第一个图标组，让 Windows 按当前 DPI 从内嵌 ICO 中选择合适尺寸。
Name: "{group}\闪语"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; IconIndex: 0
Name: "{autodesktop}\闪语"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; IconIndex: 0; Tasks: desktopicon

[Run]
; 仅交互安装显示完成页启动项；静默安装绝不自动启动应用。
Filename: "{app}\{#AppExeName}"; Description: "打开闪语(&L)"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValueName = 'QuickPhrase';

var
  DeleteLocalDataRequested: Boolean;

function IsExpectedUserDataRoot(const Candidate: String): Boolean;
var
  ExpectedRoot: String;
begin
  { 仅允许删除固定的当前用户闪语数据根目录，避免任何变量异常扩大删除范围。 }
  ExpectedRoot := RemoveBackslashUnlessRoot(ExpandConstant('{localappdata}\QuickPhrase'));
  Result := RemoveBackslashUnlessRoot(Candidate) = ExpectedRoot;
end;

function ConfirmUninstallAndGetDataCleanupChoice: Boolean;
var
  Form: TSetupForm;
  Description: TNewStaticText;
  CleanupDataCheckBox: TNewCheckBox;
  UninstallButton: TNewButton;
  CancelButton: TNewButton;
begin
  Form := CreateCustomForm(ScaleX(420), ScaleY(140), False, True);
  try
    Form.Caption := '卸载闪语';

    Description := TNewStaticText.Create(Form);
    Description.AutoSize := False;
    Description.Left := ScaleX(12);
    Description.Top := ScaleY(12);
    Description.Width := Form.ClientWidth - ScaleX(24);
    Description.WordWrap := True;
    Description.Caption := '即将卸载闪语。默认保留本地话术、设置和日志，以便重新安装后继续使用。';
    Description.Parent := Form;
    Description.AdjustHeight;

    CleanupDataCheckBox := TNewCheckBox.Create(Form);
    CleanupDataCheckBox.Left := Description.Left;
    CleanupDataCheckBox.Top := Description.Top + Description.Height + ScaleY(12);
    CleanupDataCheckBox.Width := Description.Width;
    CleanupDataCheckBox.Height := ScaleY(17);
    CleanupDataCheckBox.Caption := '删除本地数据和日志（不可恢复）';
    CleanupDataCheckBox.Checked := False;
    CleanupDataCheckBox.Parent := Form;

    UninstallButton := TNewButton.Create(Form);
    UninstallButton.Caption := '卸载';
    UninstallButton.Width := ScaleX(84);
    UninstallButton.Height := ScaleY(23);
    UninstallButton.Left := Form.ClientWidth - UninstallButton.Width - ScaleX(12);
    UninstallButton.Top := Form.ClientHeight - UninstallButton.Height - ScaleY(12);
    UninstallButton.ModalResult := mrOk;
    UninstallButton.Default := True;
    UninstallButton.Parent := Form;

    CancelButton := TNewButton.Create(Form);
    CancelButton.Caption := '取消';
    CancelButton.Width := UninstallButton.Width;
    CancelButton.Height := UninstallButton.Height;
    CancelButton.Left := UninstallButton.Left - CancelButton.Width - ScaleX(8);
    CancelButton.Top := UninstallButton.Top;
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;
    CancelButton.Parent := Form;

    Result := Form.ShowModal = mrOk;
    if Result then
      DeleteLocalDataRequested := CleanupDataCheckBox.Checked;
  finally
    Form.Free;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  { 静默卸载没有交互入口，始终保留数据，绝不因默认值触发不可逆删除。 }
  DeleteLocalDataRequested := False;
  if UninstallSilent then
  begin
    Result := True;
    Exit;
  end;

  Result := ConfirmUninstallAndGetDataCleanupChoice;
end;

procedure DeleteLocalDataIfRequested;
var
  DataRoot: String;
begin
  if not DeleteLocalDataRequested then
    Exit;

  DataRoot := ExpandConstant('{localappdata}\QuickPhrase');
  if not IsExpectedUserDataRoot(DataRoot) then
  begin
    MsgBox('本地数据目录校验失败，未执行清理。安装程序已继续卸载。', mbError, MB_OK);
    Exit;
  end;

  if DirExists(DataRoot) and not DelTree(DataRoot, True, True, True) then
    MsgBox('本地数据和日志清理失败。请关闭占用文件的程序后，手动删除：' + DataRoot, mbError, MB_OK);
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
begin
  if Step = usUninstall then
  begin
    RegDeleteValue(HKCU, RunKeyPath, RunValueName);
    DeleteLocalDataIfRequested;
  end;
end;

