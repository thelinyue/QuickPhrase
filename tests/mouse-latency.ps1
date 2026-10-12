param([int]$Count = 120)
$ErrorActionPreference = 'Stop'
# 只提交左右各1像素的移动事件，记录下游钩子处理时间；不点击、不按键、不访问剪贴板。
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
public static class ShanyuMouseLatency {
    delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct Point { public int x,y; }
    [StructLayout(LayoutKind.Sequential)] struct Mouse { public Point point; public uint data,flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct Message { public IntPtr window; public uint message; public UIntPtr w; public IntPtr l; public uint time; public Point point; public uint privateData; }
    [StructLayout(LayoutKind.Explicit, Size=40)] struct Input {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public int dx;
        [FieldOffset(20)] public uint flags;
        [FieldOffset(32)] public UIntPtr extra;
    }
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetWindowsHookEx(int kind, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern bool PeekMessage(out Message message,IntPtr window,uint first,uint last,uint remove);
    [DllImport("user32.dll")] static extern uint MsgWaitForMultipleObjectsEx(uint count,IntPtr handles,uint milliseconds,uint mask,uint flags);
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count,Input[] input,int size);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    public static object Measure(int count) {
        if(count<20||count>1000) throw new ArgumentException("采样次数须为20到1000");
        var samples=new List<double>(); var marker=new UIntPtr(0x53594D4C);
        Point original; GetCursorPos(out original); bool otherMovement=false;
        Hook callback=delegate(int code,IntPtr message,IntPtr data) {
            var measured=code>=0&&message.ToInt64()==0x200&&((Mouse)Marshal.PtrToStructure(data,typeof(Mouse))).extra==marker;
            if(code>=0&&message.ToInt64()==0x200&&!measured) otherMovement=true;
            var start=Stopwatch.GetTimestamp();
            var result=CallNextHookEx(IntPtr.Zero,code,message,data);
            if(measured) samples.Add((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency);
            return result;
        };
        Message msg; PeekMessage(out msg,IntPtr.Zero,0,0,0);
        var hook=SetWindowsHookEx(14,callback,GetModuleHandle(null),0);
        if(hook==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法安装延迟采样钩子");
        Exception failure=null;
        var sender=new Thread(delegate() {
            try {
                for(int i=0;i<count;i++) {
                    // MOUSEEVENTF_MOVE | MOUSEEVENTF_MOVE_NOCOALESCE；往返移动避免无位移事件被Windows忽略。
                    var input=new Input {type=0,dx=i%2==0?1:-1,flags=0x2001,extra=marker};
                    if(SendInput(1,new[]{input},40)!=1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法提交移动采样事件");
                    Thread.Sleep(8);
                }
            } catch(Exception error) {failure=error;}
        });
        sender.IsBackground=true; var elapsed=Stopwatch.StartNew(); sender.Start();
        try {
            while(samples.Count<count&&elapsed.ElapsedMilliseconds<20000&&failure==null) {
                while(PeekMessage(out msg,IntPtr.Zero,0,0,1)) {}
                MsgWaitForMultipleObjectsEx(0,IntPtr.Zero,20,0x4ff,4);
            }
            if(failure!=null) throw failure;
            if(samples.Count!=count) throw new Exception("鼠标移动采样超时："+samples.Count+"/"+count);
            samples.Sort(); double total=0; foreach(var value in samples) total+=value;
            return new {count=samples.Count, medianMs=samples[samples.Count/2], p95Ms=samples[(int)Math.Ceiling(samples.Count*0.95)-1], maximumMs=samples[samples.Count-1], meanMs=total/samples.Count};
        } finally {UnhookWindowsHookEx(hook); sender.Join(1000); if(!otherMovement) SetCursorPos(original.x,original.y); GC.KeepAlive(callback);}
    }
}
'@
[ShanyuMouseLatency]::Measure($Count) | ConvertTo-Json -Compress
