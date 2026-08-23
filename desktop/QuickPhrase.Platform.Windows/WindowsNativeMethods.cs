using System.Runtime.InteropServices;
using System.Text;

namespace QuickPhrase.Platform.Windows;

/// <summary>集中封装本阶段使用的最小 Win32 调用，避免业务代码散落 P/Invoke。</summary>
internal static class WindowsNativeMethods
{
    public const uint InputKeyboard = 1;
    public const uint KeyEventKeyUp = 0x0002;
    public const uint KeyEventUnicode = 0x0004;
    public const ushort VirtualKeyControl = 0x11;
    public const ushort VirtualKeyV = 0x56;
    public const ushort VirtualKeyReturn = 0x0D;

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public Rect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>采集企业微信输入区所需的最小焦点指纹，不读取标题、文本或联系人。</summary>
    public static bool TryCaptureFocusFingerprint(int windowThreadId, out WeComFocusFingerprint fingerprint)
    {
        fingerprint = default;
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo((uint)windowThreadId, ref info) || info.Focus == 0 || info.Caret == 0)
            return false;
        if (!GetClientRect(info.Focus, out var client)) return false;
        var className = new StringBuilder(128);
        if (GetClassName(info.Focus, className, className.Capacity) == 0) return false;
        fingerprint = new WeComFocusFingerprint(
            info.Focus,
            info.Caret,
            className.ToString(),
            info.Flags,
            client.Right - client.Left,
            client.Bottom - client.Top,
            info.CaretRect.Left,
            info.CaretRect.Top,
            info.CaretRect.Right,
            info.CaretRect.Bottom);
        return true;
    }

    public const uint WinEventOutOfContext = 0x0000;
    public const uint MonitorDefaultToNearest = 0x00000002;
    private const int WindowLongStyle = -16;
    private const long WindowStyleMaximizeBox = 0x00010000L;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionFrameChanged = 0x0020;

    internal delegate void WinEventDelegate(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>
    /// 移除管理窗口的原生最大化样式并刷新非客户区。消息钩子仍作为二次保护，
    /// 两层共同避免系统菜单、标题栏双击或辅助工具把窗口带入第三种形态。
    /// </summary>
    internal static bool DisableMaximize(nint hwnd)
    {
        if (hwnd == 0) return false;
        var style = IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, WindowLongStyle).ToInt64() : GetWindowLong32(hwnd, WindowLongStyle);
        var updated = style & ~WindowStyleMaximizeBox;
        if (updated == style) return true;
        var previous = IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, WindowLongStyle, (nint)updated).ToInt64()
            : SetWindowLong32(hwnd, WindowLongStyle, (int)updated);
        if (previous == 0) return false;
        return SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            SetWindowPositionNoSize | SetWindowPositionNoMove | SetWindowPositionNoZOrder |
            SetWindowPositionNoActivate | SetWindowPositionFrameChanged);
    }
    [DllImport("user32.dll")]
    internal static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookModule,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;

        public static MonitorInfo Create() => new() { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
    }

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint inputCount, [In] KeyboardInput[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public uint Type;
        public KeyboardInputData Data;
    }

    // Win32 INPUT 的 union 固定为 32 字节（即使当前只使用 KEYBDINPUT），否则 x64 下
    // SendInput 会收到错误的结构步长并返回 0，表现为剪贴板已写入但 Ctrl+V 没有执行。
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct KeyboardInputData
    {
        [FieldOffset(0)] public KeyboardInputKey Key;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInputKey
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    public static bool SendCtrlV()
    {
        var inputs = new[]
        {
            new KeyboardInput { Type = InputKeyboard, Data = new KeyboardInputData { Key = new KeyboardInputKey { VirtualKey = VirtualKeyControl } } },
            new KeyboardInput { Type = InputKeyboard, Data = new KeyboardInputData { Key = new KeyboardInputKey { VirtualKey = VirtualKeyV } } },
            new KeyboardInput { Type = InputKeyboard, Data = new KeyboardInputData { Key = new KeyboardInputKey { VirtualKey = VirtualKeyV, Flags = KeyEventKeyUp } } },
            new KeyboardInput { Type = InputKeyboard, Data = new KeyboardInputData { Key = new KeyboardInputKey { VirtualKey = VirtualKeyControl, Flags = KeyEventKeyUp } } },
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyboardInput>()) == inputs.Length;
    }

    /// <summary>
    /// 注入一次 Enter，作为企业微信当前“Enter 发送”配置的具体发送协议。
    /// Launcher 的 Ctrl+Enter 只表达通用 InsertAndSend 意图，不要求 Adapter 注入相同组合键。
    /// 部分注入后只尝试释放 Enter，不重复 KeyDown，避免无法判断目标已消费事件时产生重复发送。
    /// </summary>
    internal static KeyboardInjectionResult SendEnter(
        Func<uint, KeyboardInput[], int, uint>? sender = null)
    {
        sender ??= SendInput;
        var inputs = new[]
        {
            KeyDown(VirtualKeyReturn),
            KeyUp(VirtualKeyReturn),
        };
        var size = Marshal.SizeOf<KeyboardInput>();
        var inserted = sender((uint)inputs.Length, inputs, size);
        if (inserted == inputs.Length) return KeyboardInjectionResult.Applied;
        if (inserted == 0) return KeyboardInjectionResult.NotApplied;

        // 只补充 KeyUp，绝不补发 Enter KeyDown。
        var releases = new[] { KeyUp(VirtualKeyReturn) };
        _ = sender((uint)releases.Length, releases, size);
        return KeyboardInjectionResult.Inconclusive;
    }
    private static KeyboardInput KeyDown(ushort virtualKey) =>
        new() { Type = InputKeyboard, Data = new KeyboardInputData { Key = new KeyboardInputKey { VirtualKey = virtualKey } } };

    private static KeyboardInput KeyUp(ushort virtualKey) =>
        new() { Type = InputKeyboard, Data = new KeyboardInputData { Key = new KeyboardInputKey { VirtualKey = virtualKey, Flags = KeyEventKeyUp } } };
    public static bool SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        var inputs = new List<KeyboardInput>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(new KeyboardInput { Type = InputKeyboard, Data = new KeyboardInputData { Key = new KeyboardInputKey { ScanCode = character, Flags = KeyEventUnicode } } });
            inputs.Add(new KeyboardInput { Type = InputKeyboard, Data = new KeyboardInputData { Key = new KeyboardInputKey { ScanCode = character, Flags = KeyEventUnicode | KeyEventKeyUp } } });
        }
        return SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<KeyboardInput>()) == inputs.Count;
    }
}

internal enum KeyboardInjectionResult
{
    NotApplied,
    Applied,
    Inconclusive,
}
