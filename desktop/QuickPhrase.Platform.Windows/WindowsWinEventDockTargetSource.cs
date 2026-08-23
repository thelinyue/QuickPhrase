namespace QuickPhrase.Platform.Windows;

/// <summary>WinEvent 跟踪所需的稳定常量，集中定义以便测试事件过滤规则。</summary>
internal static class WindowsDockEvents
{
    public const uint Foreground = 0x0003;
    public const uint MinimizeStart = 0x0016;
    public const uint Destroy = 0x8001;
    public const uint LocationChange = 0x800B;
    public const int WindowObjectId = 0;
}

/// <summary>
/// 目标跟踪器依赖的最小原生调用面。生产实现只读取句柄、矩形、DPI 和工作区，
/// 不读取窗口标题、联系人或聊天内容；测试实现可稳定驱动 WinEvent 生命周期。
/// </summary>
internal interface IWindowsDockNativeApi
{
    int CurrentProcessId { get; }
    nint GetForegroundWindow();
    uint GetWindowProcessId(nint hwnd);
    bool IsWindow(nint hwnd);
    bool IsWindowVisible(nint hwnd);
    bool IsIconic(nint hwnd);
    bool TryGetWindowRect(nint hwnd, out WindowsPixelBounds bounds);
    uint GetDpiForWindow(nint hwnd);
    bool TryGetWorkArea(nint hwnd, out WindowsPixelBounds bounds);
    IReadOnlyList<nint> InstallWinEventHooks(Action<uint, nint, int> callback);
    void UnhookWinEvent(nint hook);
}

/// <summary>
/// 使用系统 WinEvent Hook 跟踪最近的外部前台窗口及其位置。回调委托由原生代理持有到解除 Hook，
/// 避免 GC 回收后系统回调进入失效地址；Dispose 会逐一解除 Hook，不遗留进程级监听。
/// </summary>
internal sealed class WindowsWinEventDockTargetSource : IWindowsDockTargetSource
{
    private static readonly QuickPhrase.Core.DeliveryTarget PlaceholderTarget =
        new("unknown", "WindowsDesktopWindow", "unknown", "外部窗口", "pending", DateTimeOffset.UnixEpoch);

    private readonly IWindowsDockNativeApi _native;
    private readonly IReadOnlyList<nint> _hooks;
    private bool _disposed;

    public WindowsWinEventDockTargetSource() : this(new WindowsDockNativeApi()) { }

    internal WindowsWinEventDockTargetSource(IWindowsDockNativeApi native)
    {
        _native = native;
        _hooks = native.InstallWinEventHooks(OnWinEvent);
    }

    public event Action<bool, nint, WindowsDockTargetSnapshot?>? ForegroundChanged;
    public event Action<WindowsDockTargetSnapshot?>? LocationChanged;
    internal nint TrackedWindow { get; private set; }

    public void Refresh()
    {
        if (_disposed) return;
        PublishForeground(_native.GetForegroundWindow());
    }

    private void OnWinEvent(uint eventType, nint hwnd, int objectId)
    {
        if (_disposed || hwnd == 0 || objectId != WindowsDockEvents.WindowObjectId) return;
        if (eventType == WindowsDockEvents.Foreground)
        {
            PublishForeground(hwnd);
            return;
        }

        if (hwnd != TrackedWindow) return;
        if (eventType == WindowsDockEvents.Destroy)
        {
            LocationChanged?.Invoke(null);
            // 窗口销毁后立即放弃句柄，避免系统复用 HWND 时把旧目标错误恢复为可用。
            TrackedWindow = 0;
        }
        else if (eventType == WindowsDockEvents.MinimizeStart)
        {
            // 最小化只发布不可用状态，但保留句柄，以便窗口恢复后的位移事件重新生成快照。
            LocationChanged?.Invoke(null);
        }
        else if (eventType == WindowsDockEvents.LocationChange)
        {
            LocationChanged?.Invoke(TryCreateSnapshot(hwnd));
        }
    }

    private void PublishForeground(nint hwnd)
    {
        if (hwnd == 0) return;
        var processId = _native.GetWindowProcessId(hwnd);
        if (processId == (uint)_native.CurrentProcessId)
        {
            ForegroundChanged?.Invoke(true, hwnd, null);
            return;
        }

        var snapshot = TryCreateSnapshot(hwnd);
        if (snapshot is null)
        {
            // 前台已切换到无法形成安全快照的外部窗口时，立即放弃旧句柄并通知上层禁用投递。
            TrackedWindow = 0;
            ForegroundChanged?.Invoke(false, hwnd, null);
            return;
        }
        TrackedWindow = hwnd;
        ForegroundChanged?.Invoke(false, hwnd, snapshot);
    }

    private WindowsDockTargetSnapshot? TryCreateSnapshot(nint hwnd)
    {
        if (!_native.IsWindow(hwnd) || !_native.IsWindowVisible(hwnd) || _native.IsIconic(hwnd)) return null;
        if (!_native.TryGetWindowRect(hwnd, out var windowBounds) || !_native.TryGetWorkArea(hwnd, out var workArea)) return null;
        var dpi = _native.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        return new WindowsDockTargetSnapshot(
            PlaceholderTarget,
            WindowsDockGeometry.ToDips(windowBounds, dpi),
            WindowsDockGeometry.ToDips(workArea, dpi),
            true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var hook in _hooks)
        {
            if (hook != 0) _native.UnhookWinEvent(hook);
        }
        TrackedWindow = 0;
    }
}

/// <summary>WinEvent 目标源的生产原生代理，负责把 P/Invoke 结构转换为平台内部值对象。</summary>
internal sealed class WindowsDockNativeApi : IWindowsDockNativeApi
{
    private WindowsNativeMethods.WinEventDelegate? _callback;

    public int CurrentProcessId => Environment.ProcessId;
    public nint GetForegroundWindow() => WindowsNativeMethods.GetForegroundWindow();
    public uint GetWindowProcessId(nint hwnd)
    {
        _ = WindowsNativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }
    public bool IsWindow(nint hwnd) => WindowsNativeMethods.IsWindow(hwnd);
    public bool IsWindowVisible(nint hwnd) => WindowsNativeMethods.IsWindowVisible(hwnd);
    public bool IsIconic(nint hwnd) => WindowsNativeMethods.IsIconic(hwnd);
    public bool TryGetWindowRect(nint hwnd, out WindowsPixelBounds bounds)
    {
        if (WindowsNativeMethods.GetWindowRect(hwnd, out var rect))
        {
            bounds = new WindowsPixelBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return true;
        }
        bounds = default;
        return false;
    }
    public uint GetDpiForWindow(nint hwnd) => WindowsNativeMethods.GetDpiForWindow(hwnd);
    public bool TryGetWorkArea(nint hwnd, out WindowsPixelBounds bounds)
    {
        var monitor = WindowsNativeMethods.MonitorFromWindow(hwnd, WindowsNativeMethods.MonitorDefaultToNearest);
        var info = WindowsNativeMethods.MonitorInfo.Create();
        if (monitor != 0 && WindowsNativeMethods.GetMonitorInfo(monitor, ref info))
        {
            bounds = new WindowsPixelBounds(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
            return true;
        }
        bounds = default;
        return false;
    }
    public IReadOnlyList<nint> InstallWinEventHooks(Action<uint, nint, int> callback)
    {
        _callback = (_, eventType, hwnd, objectId, _, _, _) => callback(eventType, hwnd, objectId);
        return
        [
            WindowsNativeMethods.SetWinEventHook(WindowsDockEvents.Foreground, WindowsDockEvents.Foreground, 0, _callback, 0, 0, WindowsNativeMethods.WinEventOutOfContext),
            WindowsNativeMethods.SetWinEventHook(WindowsDockEvents.MinimizeStart, WindowsDockEvents.MinimizeStart, 0, _callback, 0, 0, WindowsNativeMethods.WinEventOutOfContext),
            WindowsNativeMethods.SetWinEventHook(WindowsDockEvents.Destroy, WindowsDockEvents.Destroy, 0, _callback, 0, 0, WindowsNativeMethods.WinEventOutOfContext),
            WindowsNativeMethods.SetWinEventHook(WindowsDockEvents.LocationChange, WindowsDockEvents.LocationChange, 0, _callback, 0, 0, WindowsNativeMethods.WinEventOutOfContext),
        ];
    }
    public void UnhookWinEvent(nint hook) => WindowsNativeMethods.UnhookWinEvent(hook);
}
