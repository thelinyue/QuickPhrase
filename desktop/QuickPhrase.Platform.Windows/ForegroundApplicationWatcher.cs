using System.Runtime.InteropServices;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 使用 WinEventHook 监听前台窗口变化，不做定时轮询。
/// 回调只通知状态变化，目标身份和能力仍由调用方在动作前重新捕获与验证。
/// </summary>
public sealed class ForegroundApplicationWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private readonly WinEventDelegate _callback;
    private readonly nint _hook;
    private bool _disposed;

    public ForegroundApplicationWatcher()
    {
        _callback = OnForegroundChanged;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WineventOutOfContext);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("无法监听 Windows 前台窗口变化。");
    }

    public event Action? Changed;

    private void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint eventTime) => Changed?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnhookWinEvent(_hook);
    }

    private delegate void WinEventDelegate(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);
}
