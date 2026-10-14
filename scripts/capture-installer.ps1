$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -Raw -LiteralPath (Join-Path $repo 'package.json') | ConvertFrom-Json).version
# 仅截图安装前页面。所有查找和退出操作限定在本次启动的安装进程及其子进程。
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Text; using System.Runtime.InteropServices;
public static class ShanyuInstallerCapture {
 [StructLayout(LayoutKind.Sequential)] public struct Rect {public int left,top,right,bottom;}
 public class Control {public IntPtr Handle; public string Text;}
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect rect);
 [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h,int attr,out Rect rect,int bytes);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int command);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder text,int count);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h,EnumProc proc,IntPtr l);
 public static List<Control> Controls(IntPtr parent) {
  var list=new List<Control>(); EnumChildWindows(parent,(h,l)=>{var text=new StringBuilder(512);GetWindowText(h,text,text.Capacity);list.Add(new Control{Handle=h,Text=text.ToString()});return true;},IntPtr.Zero);return list;
 }
}
"@
$setup = Start-Process -FilePath (Join-Path $repo "artifacts\QuickPhrase-Setup-$version-x64.exe") -ArgumentList '/NORESTART' -PassThru -WindowStyle Hidden
$childIds = @($setup.Id)
function OwnWindows {
  foreach ($processId in $childIds) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$processId)
    [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)
  }
}
try {
  $window = $null
  for ($attempt=0; $attempt -lt 50 -and -not $window; $attempt++) {
    $childIds = @($setup.Id) + @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($setup.Id)" | Select-Object -ExpandProperty ProcessId)
    $window = OwnWindows | Where-Object { $_.Current.Name -like '*闪语*' } | Select-Object -First 1
    if (-not $window) { Start-Sleep -Milliseconds 100 }
  }
  if (-not $window) { throw '没有找到本次安装程序的向导窗口。' }
  $handle = [IntPtr]$window.Current.NativeWindowHandle
  [ShanyuInstallerCapture]::ShowWindow($handle,5) | Out-Null
  Start-Sleep -Milliseconds 300
  $controls = [ShanyuInstallerCapture]::Controls($handle)
  if (-not ($controls | Where-Object { $_.Text -match '^下一步' })) { throw '安装向导没有显示中文下一步按钮。' }
  $expectedPath = Join-Path $env:LOCALAPPDATA 'Programs\QuickPhrase'
  $previous = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E9DBDCE4-4E86-4F88-A845-7E91B5D7726C}_is1' -ErrorAction SilentlyContinue
  if ($previous.'Inno Setup: App Path') { $expectedPath = $previous.'Inno Setup: App Path' }
  if (-not ($controls | Where-Object { $_.Text.TrimEnd('\') -ieq $expectedPath.TrimEnd('\') })) { throw '安装路径没有采用新默认目录或保留原安装目录。' }
  $rect = New-Object ShanyuInstallerCapture+Rect
  if ([ShanyuInstallerCapture]::DwmGetWindowAttribute($handle,9,[ref]$rect,16) -ne 0) { [ShanyuInstallerCapture]::GetWindowRect($handle,[ref]$rect) | Out-Null }
  $bitmap = [Drawing.Bitmap]::new($rect.right-$rect.left,$rect.bottom-$rect.top)
  $graphics = [Drawing.Graphics]::FromImage($bitmap)
  try {
    $graphics.CopyFromScreen($rect.left,$rect.top,0,0,$bitmap.Size)
    $bitmap.Save((Join-Path $repo 'docs\ui\runtime\installer.png'),[Drawing.Imaging.ImageFormat]::Png)
  } finally { $graphics.Dispose(); $bitmap.Dispose() }
  Write-Host '中文安装向导与当前用户默认安装路径验证通过。'
  $cancel = $controls | Where-Object { $_.Text -eq '取消' } | Select-Object -First 1
  [ShanyuInstallerCapture]::PostMessage($cancel.Handle,245,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
  Start-Sleep -Milliseconds 250
  foreach ($item in (OwnWindows)) {
    $yes = [ShanyuInstallerCapture]::Controls([IntPtr]$item.Current.NativeWindowHandle) | Where-Object { $_.Text -match '^是|^Yes' } | Select-Object -First 1
    if ($yes) { [ShanyuInstallerCapture]::PostMessage($yes.Handle,245,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null }
  }
  $setup.WaitForExit(2000) | Out-Null
} finally {
  foreach ($processId in $childIds) { Stop-Process -Id $processId -ErrorAction SilentlyContinue }
}
