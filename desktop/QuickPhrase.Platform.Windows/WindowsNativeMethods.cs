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
    private struct Rect
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
