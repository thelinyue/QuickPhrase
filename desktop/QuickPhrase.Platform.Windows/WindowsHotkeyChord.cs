using QuickPhrase.Core;
using System.Runtime.InteropServices;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 将 Core 规范化后的快捷键转换成 RegisterHotKey 所需的 Windows 修饰键和虚拟键。
/// 解析本身不注册系统热键，便于在没有交互桌面的测试环境中验证格式。
/// </summary>
public readonly record struct WindowsHotkeyChord(string Normalized, uint Modifiers, uint VirtualKey)
{
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    public static bool TryParse(string? shortcut, out WindowsHotkeyChord chord)
    {
        chord = default;
        var normalized = new ShortcutNormalizer().Normalize(shortcut, ShortcutMode.Custom);
        if (!normalized.IsValid || normalized.Value is null) return false;

        var tokens = normalized.Value.Normalized.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !TryVirtualKey(tokens[^1], out var virtualKey)) return false;
        var modifiers = 0u;
        foreach (var token in tokens[..^1])
        {
            modifiers |= token switch
            {
                "Ctrl" => ModControl,
                "Alt" => ModAlt,
                "Shift" => ModShift,
                "Win" => ModWin,
                _ => 0,
            };
        }

        if (modifiers == 0) return false;
        chord = new WindowsHotkeyChord(normalized.Value.Normalized, modifiers, virtualKey);
        return true;
    }

    private static bool TryVirtualKey(string token, out uint value)
    {
        value = token switch
        {
            "Space" => 0x20,
            "Enter" => 0x0D,
            "Tab" => 0x09,
            "Escape" => 0x1B,
            _ when token.Length == 1 && token[0] is >= '0' and <= '9' => token[0],
            _ when token.Length == 1 && token[0] is >= 'A' and <= 'Z' => token[0],
            _ when token.Length is >= 2 and <= 3 && token[0] == 'F' && uint.TryParse(token[1..], out var function) && function is >= 1 and <= 24 => 0x70u + function - 1,
            _ => 0,
        };
        return value != 0;
    }
}

/// <summary>
/// RegisterHotKey 的最小平台封装。窗口句柄由 Desktop 的隐藏消息窗口提供，平台层只负责注册与释放，不拥有 WPF 生命周期。
/// </summary>
public sealed class WindowsHotkeyService : IDisposable
{
    private readonly IntPtr _windowHandle;
    private readonly Dictionary<int, WindowsHotkeyChord> _registrations = [];
    private bool _disposed;

    public WindowsHotkeyService(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) throw new ArgumentException("快捷键消息窗口句柄不能为空。", nameof(windowHandle));
        _windowHandle = windowHandle;
    }

    public bool TryRegister(int id, WindowsHotkeyChord chord, out string? errorCode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registrations.ContainsKey(id))
        {
            errorCode = "HOTKEY_ALREADY_REGISTERED";
            return false;
        }
        if (!RegisterHotKey(_windowHandle, id, chord.Modifiers, chord.VirtualKey))
        {
            errorCode = "HOTKEY_CONFLICT";
            return false;
        }
        _registrations[id] = chord;
        errorCode = null;
        return true;
    }

    public void Unregister(int id)
    {
        if (_registrations.Remove(id)) UnregisterHotKey(_windowHandle, id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _registrations.Keys.ToArray()) Unregister(id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        UnregisterAll();
        _disposed = true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
