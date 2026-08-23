using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>Win32 返回的物理像素矩形；只在平台层短暂使用。</summary>
internal readonly record struct WindowsPixelBounds(int Left, int Top, int Right, int Bottom);

/// <summary>把目标窗口所在显示器的物理像素转换为 WPF 使用的 DIP，避免高 DPI 下贴边偏移。</summary>
internal static class WindowsDockGeometry
{
    internal static DesktopWindowBounds ToDips(WindowsPixelBounds bounds, uint dpi)
    {
        var scale = 96d / dpi;
        return new DesktopWindowBounds(
            bounds.Left * scale,
            bounds.Top * scale,
            (bounds.Right - bounds.Left) * scale,
            (bounds.Bottom - bounds.Top) * scale);
    }
}

/// <summary>平台层向 Desktop 暴露的 DIP 窗口矩形，不包含 HWND、显示器句柄或 WPF 类型。</summary>
public readonly record struct DesktopWindowBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>当前可贴边目标的脱敏快照；窗口身份仍通过 Core 的不透明 RuntimeKey 关联。</summary>
public sealed record WindowsDockTargetSnapshot(
    DeliveryTarget Target,
    DesktopWindowBounds TargetBounds,
    DesktopWindowBounds WorkArea,
    bool IsAvailable);

/// <summary>
/// 隔离 WinEvent 来源，便于验证“忽略自身窗口、保留最后外部目标、位置变化更新”的生命周期。
/// 具体 P/Invoke 实现在 WindowsWinEventDockTargetSource 中。
/// </summary>
internal interface IWindowsDockTargetSource : IDisposable
{
    event Action<bool, nint, WindowsDockTargetSnapshot?>? ForegroundChanged;
    event Action<WindowsDockTargetSnapshot?>? LocationChanged;
    void Refresh();
}

/// <summary>
/// 管理最近的外部业务窗口。QuickPhrase 自身获得焦点时不会清空目标；目标暂时不可用时保留身份，
/// 只把 IsAvailable 置为 false，从而让常驻话术库安全禁用发送而不突然消失。
/// </summary>
public sealed class WindowsDockTargetTracker : IDisposable
{
    private readonly IWindowsDockTargetSource _source;
    private readonly Func<nint, DeliveryTarget?> _captureWindow;
    private bool _disposed;

    public WindowsDockTargetTracker(WindowsTargetDetector detector)
        : this(new WindowsWinEventDockTargetSource(), detector.CaptureWindow)
    {
    }

    internal WindowsDockTargetTracker(IWindowsDockTargetSource source, Func<nint, DeliveryTarget?> captureWindow)
    {
        _source = source;
        _captureWindow = captureWindow;
        _source.ForegroundChanged += OnForegroundChanged;
        _source.LocationChanged += OnLocationChanged;
        _source.Refresh();
    }

    public WindowsDockTargetSnapshot? Current { get; private set; }
    public event Action<WindowsDockTargetSnapshot>? Changed;

    private void OnForegroundChanged(bool isCurrentProcessWindow, nint hwnd, WindowsDockTargetSnapshot? snapshot)
    {
        if (_disposed || isCurrentProcessWindow) return;
        var target = snapshot is null ? null : _captureWindow(hwnd);
        if (target is null || snapshot is null)
        {
            if (Current is null) return;
            Current = Current with { IsAvailable = false };
            Changed?.Invoke(Current);
            return;
        }
        Current = snapshot with { Target = target, IsAvailable = true };
        Changed?.Invoke(Current);
    }

    private void OnLocationChanged(WindowsDockTargetSnapshot? snapshot)
    {
        if (_disposed || Current is null) return;
        Current = snapshot is null
            ? Current with { IsAvailable = false }
            : snapshot with { Target = Current.Target, IsAvailable = true };
        Changed?.Invoke(Current);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.ForegroundChanged -= OnForegroundChanged;
        _source.LocationChanged -= OnLocationChanged;
        _source.Dispose();
        Current = null;
    }
}
