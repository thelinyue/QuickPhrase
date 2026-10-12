param([ValidateSet('focus','inspect','move','minimize','restore','key','menu')][string]$Action = 'inspect', [long]$Handle = 0, [int]$X = 0, [int]$Y = 0, [int]$Width = 0, [int]$Height = 0, [int]$Key = 0, [switch]$Ctrl, [switch]$Alt, [switch]$Shift, [int]$Repeats = 1, [int]$HoldMilliseconds = 0, [string]$Keys, [string]$ClipboardExpected, [string]$ClipboardImageExpected, [string]$ClipboardImageSnapshot, [string]$ClipboardFileExpected, [int]$ProcessId = 0, [string]$Title, [string]$WindowClass, [string]$Screenshot, [int]$MenuIndex = -1)
$ErrorActionPreference = 'Stop'
# 仅用于本机验收：原生鼠标和按键确保焦点流程经过真实 Windows 输入队列。
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class ShanyuQaWindows {
 public delegate bool WindowCallback(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(WindowCallback callback,IntPtr l);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,System.Text.StringBuilder text,int count);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h,System.Text.StringBuilder text,int count);
 [DllImport("user32.dll",EntryPoint="GetWindowLongW")] public static extern int GetWindowLong(IntPtr h,int index);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr menu);
 [DllImport("user32.dll")] public static extern bool GetMenuItemRect(IntPtr window,IntPtr menu,uint item,out Rect rect);
 public static IntPtr Find(uint processId,string title,string className) {
  IntPtr found=IntPtr.Zero;
  EnumWindows((h,l)=>{uint id;GetWindowThreadProcessId(h,out id);if(id!=processId)return true;
   var text=new System.Text.StringBuilder(256);var kind=new System.Text.StringBuilder(256);GetWindowText(h,text,256);GetClassName(h,kind,256);
   if((String.IsNullOrEmpty(title)||text.ToString()==title)&&(String.IsNullOrEmpty(className)||kind.ToString()==className)){
    if(className=="#32768"&&!IsWindowVisible(h))return true;
    found=h;return false;}return true;},IntPtr.Zero);
  return found;
 }
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
 [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
 [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h,uint attribute,out Rect r,int size);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
 [StructLayout(LayoutKind.Explicit,Size=40)] struct TestInput {
  [FieldOffset(0)] public uint type;
  [FieldOffset(8)] public ushort key;
  [FieldOffset(10)] public ushort scan;
  [FieldOffset(12)] public uint flags;
  [FieldOffset(24)] public UIntPtr extra;
 }
 [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,TestInput[] inputs,int size);
 // 验收宿主与客户端均为 x64；SendInput 走与产品相同的系统输入队列。
 public static void keybd_event(byte key,byte scan,uint flags,UIntPtr extra) {
  var input=new TestInput{type=1,key=key,scan=scan,flags=flags,extra=extra};
  if(SendInput(1,new[]{input},40)!=1)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法提交验收按键");
 }
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int command);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int height,bool repaint);
 [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h,uint flags);
 [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
 [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint thread);
 [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
 [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr handle,int index,System.Text.StringBuilder name,int length,out int needed);
 public static string DesktopName(IntPtr handle) {int needed;var name=new System.Text.StringBuilder(256);GetUserObjectInformation(handle,2,name,512,out needed);return name.ToString();}
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int left,top,right,bottom; }
 [StructLayout(LayoutKind.Sequential)] public struct GuiThreadInfo { public int size; public uint flags; public IntPtr active,focus,capture,menuOwner,moveSize,caret; public Rect caretRect; }
 [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public int size; public Rect monitor,work; public uint flags; }
}
'@
$nativeHandle = [IntPtr]$Handle
if ($ProcessId) {
  if (-not $Title -and -not $WindowClass) { throw '查找验收窗口时需要标题或窗口类。' }
  $nativeHandle = [ShanyuQaWindows]::Find([uint32]$ProcessId,$Title,$WindowClass)
  if ($nativeHandle -eq [IntPtr]::Zero) { throw '没有找到指定进程的验收窗口。' }
}
if ($Action -eq 'focus') {
  [ShanyuQaWindows]::ShowWindow($nativeHandle,9) | Out-Null
  # 前台锁或其他置顶窗口会挡住验收点击，临时抬高本次指定窗口，激活后恢复原置顶状态。
  $wasTopmost = ([ShanyuQaWindows]::GetWindowLong($nativeHandle,-20) -band 8) -ne 0
  [ShanyuQaWindows]::SetWindowPos($nativeHandle,[IntPtr](-1),0,0,0,0,19) | Out-Null
  [ShanyuQaWindows]::SetForegroundWindow($nativeHandle) | Out-Null
  $rect = New-Object ShanyuQaWindows+Rect
  [ShanyuQaWindows]::GetWindowRect($nativeHandle,[ref]$rect) | Out-Null
  $focusX = if ($PSBoundParameters.ContainsKey('X')) { $X } else { 30 }
  $focusY = if ($PSBoundParameters.ContainsKey('Y')) { $Y } else { [Math]::Min(80,[Math]::Max(10,($rect.bottom-$rect.top)/2)) }
  [ShanyuQaWindows]::SetCursorPos($rect.left + $focusX,$rect.top + $focusY) | Out-Null
  [ShanyuQaWindows]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
  [ShanyuQaWindows]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 80
  if (-not $wasTopmost) { [ShanyuQaWindows]::SetWindowPos($nativeHandle,[IntPtr](-2),0,0,0,0,19) | Out-Null }
}
if ($Action -eq 'move') { [ShanyuQaWindows]::MoveWindow($nativeHandle,$X,$Y,$Width,$Height,$true) | Out-Null }
if ($Action -eq 'minimize') { [ShanyuQaWindows]::ShowWindow($nativeHandle,6) | Out-Null }
if ($Action -eq 'restore') { [ShanyuQaWindows]::ShowWindow($nativeHandle,9) | Out-Null }
if ($Action -eq 'menu') {
  # 按原生菜单项的真实矩形点击，避免鼠标初始位置改变键盘选中项后误选其他操作。
  $menuHandle = [ShanyuQaWindows]::SendMessage($nativeHandle,481,[IntPtr]::Zero,[IntPtr]::Zero)
  if ($MenuIndex -lt 0 -or $MenuIndex -ge [ShanyuQaWindows]::GetMenuItemCount($menuHandle)) { throw '验收菜单项序号无效。' }
  $menuRect = New-Object ShanyuQaWindows+Rect
  if (-not [ShanyuQaWindows]::GetMenuItemRect([IntPtr]::Zero,$menuHandle,[uint32]$MenuIndex,[ref]$menuRect)) { throw '无法读取原生菜单项位置。' }
  [ShanyuQaWindows]::SetCursorPos(($menuRect.left+$menuRect.right)/2,($menuRect.top+$menuRect.bottom)/2) | Out-Null
  [ShanyuQaWindows]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
  [ShanyuQaWindows]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 80
}
if ($Action -eq 'key') {
  # 只有隔离 Debug 客户端识别此标记，产品回放与发送事件仍被原生监听忽略。
  $testMarker = [UIntPtr]::new([uint32]0x53595141)
  if ($Ctrl) { [ShanyuQaWindows]::keybd_event(17,0,0,$testMarker) }
  if ($Alt) { [ShanyuQaWindows]::keybd_event(18,0,0,$testMarker) }
  if ($Shift) { [ShanyuQaWindows]::keybd_event(16,0,0,$testMarker) }
  Start-Sleep -Milliseconds 40
  $keyList = if ($Keys) { $Keys.Split(',') | ForEach-Object { [int]$_ } } else { @($Key) }
  foreach ($testKey in $keyList) {
    for ($repeat = 0; $repeat -lt $Repeats; $repeat++) { [ShanyuQaWindows]::keybd_event([byte]$testKey,0,0,$testMarker); Start-Sleep -Milliseconds 20 }
    if ($HoldMilliseconds) { Start-Sleep -Milliseconds $HoldMilliseconds }
    [ShanyuQaWindows]::keybd_event([byte]$testKey,0,2,$testMarker)
    Start-Sleep -Milliseconds 40
  }
  if ($Shift) { [ShanyuQaWindows]::keybd_event(16,0,2,$testMarker) }
  if ($Alt) { [ShanyuQaWindows]::keybd_event(18,0,2,$testMarker) }
  if ($Ctrl) { [ShanyuQaWindows]::keybd_event(17,0,2,$testMarker) }
}
$foreground = [ShanyuQaWindows]::GetForegroundWindow()
$owner = [uint32]0
[ShanyuQaWindows]::GetWindowThreadProcessId($foreground,[ref]$owner) | Out-Null
$rect = New-Object ShanyuQaWindows+Rect
[ShanyuQaWindows]::GetWindowRect($nativeHandle,[ref]$rect) | Out-Null
# 跟随服务使用可见边框；原矩形仍保留给鼠标位置与 MoveWindow，避免混淆不可见边框。
$frame = New-Object ShanyuQaWindows+Rect
if ([ShanyuQaWindows]::DwmGetWindowAttribute($nativeHandle,9,[ref]$frame,16) -ne 0) { $frame = $rect }
$extendedStyle = [ShanyuQaWindows]::GetWindowLong($nativeHandle,-20)
$monitorInfo = New-Object ShanyuQaWindows+MonitorInfo
$monitorInfo.size = [Runtime.InteropServices.Marshal]::SizeOf($monitorInfo)
[ShanyuQaWindows]::GetMonitorInfo([ShanyuQaWindows]::MonitorFromWindow($nativeHandle,2),[ref]$monitorInfo) | Out-Null
$result = @{ handle=$nativeHandle.ToInt64(); topmost=($extendedStyle -band 8) -ne 0; toolWindow=($extendedStyle -band 128) -ne 0; appWindow=($extendedStyle -band 262144) -ne 0; noActivate=($extendedStyle -band 134217728) -ne 0; foreground=$foreground.ToInt64(); pid=$owner; visible=[ShanyuQaWindows]::IsWindowVisible($nativeHandle); rect=$rect; frame=$frame; workArea=$monitorInfo.work; clipboardSequence=[ShanyuQaWindows]::GetClipboardSequenceNumber() }
$result.desktop = [ShanyuQaWindows]::DesktopName([ShanyuQaWindows]::GetThreadDesktop([ShanyuQaWindows]::GetCurrentThreadId()))
$result.enterDown = [ShanyuQaWindows]::GetAsyncKeyState(13) -lt 0
# 只读指定窗口线程的菜单状态，区分 WebView 导航与 Windows 系统菜单抢走方向键。
$guiInfo = New-Object ShanyuQaWindows+GuiThreadInfo
$guiInfo.size = [Runtime.InteropServices.Marshal]::SizeOf($guiInfo)
$guiProcess = [uint32]0
$guiWindow = if ($nativeHandle -ne [IntPtr]::Zero) { $nativeHandle } else { $foreground }
$guiThread = [ShanyuQaWindows]::GetWindowThreadProcessId($guiWindow,[ref]$guiProcess)
if ([ShanyuQaWindows]::GetGUIThreadInfo($guiThread,[ref]$guiInfo)) {
  $result.menuMode = ($guiInfo.flags -band 28) -ne 0
  $result.menuOwner = $guiInfo.menuOwner.ToInt64()
}
$inputDesktop = [ShanyuQaWindows]::OpenInputDesktop(0,$false,1)
try { $result.inputDesktop = [ShanyuQaWindows]::DesktopName($inputDesktop) } finally { [ShanyuQaWindows]::CloseDesktop($inputDesktop) | Out-Null }
if ($PSBoundParameters.ContainsKey('ClipboardExpected')) {
  Add-Type -AssemblyName System.Windows.Forms
  $result.clipboardMatches = [Windows.Forms.Clipboard]::ContainsText() -and [Windows.Forms.Clipboard]::GetText() -ceq $ClipboardExpected
}
if ($PSBoundParameters.ContainsKey('ClipboardImageSnapshot')) {
  # 在独立进程记录系统提供的测试图片；WinForms 跨进程转换后可能不再具有源 PNG 的透明度。
  Add-Type -AssemblyName System.Windows.Forms,System.Drawing
  if (-not [Windows.Forms.Clipboard]::ContainsImage()) { throw '测试剪贴板中没有图片，无法记录投递前的内容。' }
  $image = [Drawing.Bitmap]::new([Windows.Forms.Clipboard]::GetImage())
  try { $image.Save($ClipboardImageSnapshot, [Drawing.Imaging.ImageFormat]::Png); $result.clipboardImageRecorded = $true }
  finally { $image.Dispose() }
}
if ($PSBoundParameters.ContainsKey('ClipboardImageExpected')) {
  Add-Type -AssemblyName System.Windows.Forms,System.Drawing
  $result.clipboardImageMatches = $false
  if ([Windows.Forms.Clipboard]::ContainsImage()) {
    $actual = [Drawing.Bitmap]::new([Windows.Forms.Clipboard]::GetImage())
    $expected = [Drawing.Bitmap]::new($ClipboardImageExpected)
    try { $result.clipboardImageMatches = $actual.Width -eq $expected.Width -and $actual.Height -eq $expected.Height -and $actual.GetPixel(128,128).ToArgb() -eq $expected.GetPixel(128,128).ToArgb() }
    finally { $actual.Dispose(); $expected.Dispose() }
  }
}
if ($PSBoundParameters.ContainsKey('ClipboardFileExpected')) {
  Add-Type -AssemblyName System.Windows.Forms
  $result.clipboardFileMatches = [Windows.Forms.Clipboard]::ContainsFileDropList() -and [Windows.Forms.Clipboard]::GetFileDropList().Contains($ClipboardFileExpected)
}
if ($Screenshot) {
  Add-Type -AssemblyName System.Drawing
  $bitmap = [Drawing.Bitmap]::new($rect.right-$rect.left,$rect.bottom-$rect.top)
  $graphics = [Drawing.Graphics]::FromImage($bitmap)
  try { $graphics.CopyFromScreen($rect.left,$rect.top,0,0,$bitmap.Size); $bitmap.Save($Screenshot,[Drawing.Imaging.ImageFormat]::Png) }
  finally { $graphics.Dispose(); $bitmap.Dispose() }
}
$result | ConvertTo-Json -Compress
