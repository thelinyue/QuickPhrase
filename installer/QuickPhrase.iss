; 闪语（QuickPhrase）按当前用户安装的 Inno Setup 脚本（纯 WPF 自包含安装包，无 WebView2 运行时依赖）。
#define AppVersion "1.0.0"
#define AppId "{E9DBDCE4-4E86-4F88-A845-7E91B5D7726C}"
#define AppExeName "QuickPhrase.exe"
#define OutputBase "QuickPhrase-Setup-1.0.0"

[Setup]
AppId={{#AppId}}
AppName=闪语
AppVerName=闪语 1.0.0
AppVersion={#AppVersion}
AppPublisher=QuickPhrase Contributors
DefaultDirName={localappdata}\Programs\QuickPhrase
DefaultGroupName=闪语
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\release\1.0.0\installers
OutputBaseFilename={#OutputBase}
SetupIconFile=..\assets\quickphrase.ico
UninstallDisplayName=闪语
Uninstallable=yes
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=no
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\artifacts\release\1.0.0\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\闪语"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"

[UninstallDelete]
Type: files; Name: "{autodesktop}\闪语.lnk"

[Code]
const
  RunKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValueName = 'QuickPhrase';
  ShellLinkClassId = '{00021401-0000-0000-C000-000000000046}';

type
  { Shell Link COM 接口定义。安装器直接写入 .lnk，不依赖 PowerShell 或外部辅助程序。 }
  IShellLinkW = interface(IUnknown)
    '{000214F9-0000-0000-C000-000000000046}'
    procedure Dummy;
    procedure Dummy2;
    procedure Dummy3;
    function GetDescription(pszName: String; cchMaxName: Integer): HResult;
    function SetDescription(pszName: String): HResult;
    function GetWorkingDirectory(pszDir: String; cchMaxPath: Integer): HResult;
    function SetWorkingDirectory(pszDir: String): HResult;
    function GetArguments(pszArgs: String; cchMaxPath: Integer): HResult;
    function SetArguments(pszArgs: String): HResult;
    function GetHotkey(var pwHotkey: Word): HResult;
    function SetHotkey(wHotkey: Word): HResult;
    function GetShowCmd(out piShowCmd: Integer): HResult;
    function SetShowCmd(iShowCmd: Integer): HResult;
    function GetIconLocation(pszIconPath: String; cchIconPath: Integer;
      out piIcon: Integer): HResult;
    function SetIconLocation(pszIconPath: String; iIcon: Integer): HResult;
    function SetRelativePath(pszPathRel: String; dwReserved: DWORD): HResult;
    function Resolve(Wnd: HWND; fFlags: DWORD): HResult;
    function SetPath(pszFile: String): HResult;
  end;

  IPersist = interface(IUnknown)
    '{0000010C-0000-0000-C000-000000000046}'
    function GetClassID(var classID: TGUID): HResult;
  end;

  IPersistFile = interface(IPersist)
    '{0000010B-0000-0000-C000-000000000046}'
    function IsDirty: HResult;
    function Load(pszFileName: String; dwMode: Longint): HResult;
    function Save(pszFileName: String; fRemember: BOOL): HResult;
    function SaveCompleted(pszFileName: String): HResult;
    function GetCurFile(out pszFileName: String): HResult;
  end;

var
  DesktopShortcutCheckBox: TNewCheckBox;
  DesktopShortcutHandled: Boolean;

procedure InitializeWizard;
begin
  { 完成页复选框默认勾选；桌面快捷方式只有在用户确认完成时才写入。 }
  DesktopShortcutCheckBox := TNewCheckBox.Create(WizardForm);
  DesktopShortcutCheckBox.Parent := WizardForm;
  DesktopShortcutCheckBox.Caption := '创建桌面快捷方式(&D)';
  DesktopShortcutCheckBox.Checked := True;
  DesktopShortcutCheckBox.Left := WizardForm.FinishedLabel.Left;
  DesktopShortcutCheckBox.Width := WizardForm.FinishedLabel.Width;
  DesktopShortcutCheckBox.Height := ScaleY(17);
  DesktopShortcutCheckBox.Top := WizardForm.FinishedLabel.Top +
    WizardForm.FinishedLabel.Height + ScaleY(12);
  DesktopShortcutCheckBox.TabOrder := 0;
  DesktopShortcutCheckBox.Visible := False;
  DesktopShortcutHandled := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  DesktopShortcutCheckBox.Visible := CurPageID = wpFinished;
end;

procedure CreateDesktopShortcut;
var
  ShellLinkObject: IUnknown;
  ShellLink: IShellLinkW;
  PersistFile: IPersistFile;
  ShortcutPath: String;
  TargetPath: String;
  WorkingDirectory: String;
begin
  ShortcutPath := ExpandConstant('{autodesktop}\闪语.lnk');
  TargetPath := ExpandConstant('{app}\{#AppExeName}');
  WorkingDirectory := ExpandConstant('{app}');

  if not FileExists(TargetPath) then
  begin
    MsgBox('桌面快捷方式创建失败：安装目录中未找到主程序，但安装已完成。',
      mbError, MB_OK);
    Exit;
  end;

  try
    { 直接使用当前安装路径，并通过 Save(..., True) 覆盖旧快捷方式。 }
    ShellLinkObject := CreateComObject(StringToGuid(ShellLinkClassId));
    ShellLink := IShellLinkW(ShellLinkObject);
    OleCheck(ShellLink.SetPath(TargetPath));
    OleCheck(ShellLink.SetWorkingDirectory(WorkingDirectory));
    OleCheck(ShellLink.SetDescription('闪语'));
    OleCheck(ShellLink.SetIconLocation(TargetPath, 0));
    OleCheck(ShellLink.SetShowCmd(SW_SHOWNORMAL));

    PersistFile := IPersistFile(ShellLinkObject);
    OleCheck(PersistFile.Save(ShortcutPath, True));
  except
    { 快捷方式是附加便利功能，任何失败都不能阻断安装主流程。 }
    MsgBox('桌面快捷方式创建失败，但安装已完成。您仍可从安装目录启动闪语。',
      mbError, MB_OK);
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpFinished) and (not WizardSilent) and
    (not DesktopShortcutHandled) then
  begin
    DesktopShortcutHandled := True;
    if DesktopShortcutCheckBox.Checked then
      CreateDesktopShortcut;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { 静默安装没有完成页，按交互安装的默认勾选行为创建快捷方式。 }
  if (CurStep = ssPostInstall) and WizardSilent and
    (not DesktopShortcutHandled) then
  begin
    DesktopShortcutHandled := True;
    CreateDesktopShortcut;
  end;
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
begin
  if Step = usUninstall then
    RegDeleteValue(HKCU, RunKeyPath, RunValueName);
end;


