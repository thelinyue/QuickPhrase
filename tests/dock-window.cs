using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 隔离宿主注册原生窗口类；WinForms 会自动添加包装前后缀，产品仅对隔离宿主去掉包装。
// 两者使用同一份窗口类名单，而不是用输入框或标题绕过识别。
// 类只注册在宿主进程内；产品仍要求专用进程名、Debug 编译和隔离数据目录同时成立。
public class ShanyuDockForm : Form {
    delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WindowClass {
        public uint size, style;
        public WindowProc procedure;
        public int classExtra, windowExtra;
        public IntPtr instance, icon, cursor, background;
        public string menu, name;
        public IntPtr smallIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);
    static readonly WindowProc procedure = DefWindowProc;
    static readonly HashSet<string> classes = new HashSet<string>();
    readonly string windowClass;
    public ShanyuDockForm() : this("WeWorkWindow") {}
    public ShanyuDockForm(string className) {
        windowClass = className;
        if (!classes.Contains(className)) {
            // CS_GLOBALCLASS 让 WinForms 在本进程其他模块中也能找到注册类，不影响其他进程。
            var value = new WindowClass { size = (uint)Marshal.SizeOf(typeof(WindowClass)), style = 0x4000,
                procedure = procedure, instance = GetModuleHandle(null), name = className };
            if (RegisterClassEx(ref value) == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册隔离吸附窗口类");
            classes.Add(className);
        }
    }
    protected override CreateParams CreateParams {
        get {
            var parameters = base.CreateParams;
            parameters.ClassName = windowClass;
            return parameters;
        }
    }
}
