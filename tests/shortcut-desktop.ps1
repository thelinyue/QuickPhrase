param([ValidatePattern('^[\p{L}\p{N} _／]+$')][string]$Filter, [ValidateSet('shortcuts','member','ime')][string]$Suite = 'shortcuts', [switch]$Keyboard)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$node = (Get-Command node.exe).Source
$log = Join-Path $repo ".build\$Suite-desktop.log"
New-Item -ItemType Directory -Path (Split-Path $log -Parent) -Force | Out-Null

# 验收期间使用独立的 Windows 输入桌面，防止真实聊天软件弹窗抢焦点。
# 子进程及其宿主继承这个桌面；无论测试成功、失败或超时，都先切回原桌面。
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShanyuShortcutDesktop {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct StartupInfo {
        public int cb;
        public string reserved, desktop, title;
        public int x,y,width,height,charsX,charsY,fill,flags;
        public short show,reservedBytes;
        public IntPtr reservedData,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo { public IntPtr process,thread; public uint processId,threadId; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
    [DllImport("user32.dll", SetLastError=true)]
    static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll", SetLastError=true)]
    static extern bool SwitchDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool CreateProcess(string app,System.Text.StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr handle,out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr handle,uint code);
    static Exception Error(string action) { return new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),action); }
    public static int Run(string command,string directory) {
        var original=OpenInputDesktop(0,false,0x100);
        if(original==IntPtr.Zero)throw Error("无法记录原 Windows 输入桌面");
        var name="ShanyuShortcutsQA-"+System.Diagnostics.Process.GetCurrentProcess().Id;
        var isolated=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x1ff,IntPtr.Zero);
        var child=new ProcessInfo();
        try {
            if(isolated==IntPtr.Zero)throw Error("无法创建隔离输入桌面");
            if(!SwitchDesktop(isolated))throw Error("无法切换到隔离输入桌面");
            var startup=new StartupInfo {cb=Marshal.SizeOf(typeof(StartupInfo)),desktop="WinSta0\\"+name};
            if(!CreateProcess(null,new System.Text.StringBuilder(command),IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,directory,ref startup,out child))throw Error("无法启动隔离快捷键验收");
            if(WaitForSingleObject(child.process,600000)!=0) {
                TerminateProcess(child.process,1);
                throw new TimeoutException("隔离快捷键验收超过十分钟，已切回原桌面。");
            }
            uint code;GetExitCodeProcess(child.process,out code);return (int)code;
        } finally {
            SwitchDesktop(original);
            if(child.thread!=IntPtr.Zero)CloseHandle(child.thread);
            if(child.process!=IntPtr.Zero)CloseHandle(child.process);
            if(isolated!=IntPtr.Zero)CloseDesktop(isolated);
            CloseDesktop(original);
        }
    }
}
'@
$filterArg = if ($Filter) { ' "--filter=' + $Filter + '"' } else { '' }
if ($Keyboard) { $filterArg += ' --keyboard' }
$command = '"' + $env:ComSpec + '" /d /s /c ""' + $node + '" "' + (Join-Path $repo "scripts\verify-$Suite.mjs") + '"' + $filterArg + ' > "' + $log + '" 2>&1"'
$result = [ShanyuShortcutDesktop]::Run($command, $repo)
Get-Content -LiteralPath $log -Encoding UTF8
exit $result
