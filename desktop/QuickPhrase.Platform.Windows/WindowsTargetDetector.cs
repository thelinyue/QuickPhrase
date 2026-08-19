using System.Diagnostics;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 使用 Win32 采集和重验证前台目标。窗口句柄等 Windows 细节只进入平台上下文，
/// 对 Desktop/Core 暴露的是可比较、不可解释的 DeliveryTarget。
/// </summary>
public sealed class WindowsTargetDetector : ITargetDetector
{
    private readonly WindowsTargetContextStore _contexts;

    public WindowsTargetDetector() : this(WindowsTargetContextStore.Shared) { }

    internal WindowsTargetDetector(WindowsTargetContextStore contexts) => _contexts = contexts;

    public DeliveryTarget? CaptureForeground()
    {
        var hwnd = WindowsNativeMethods.GetForegroundWindow();
        if (hwnd == 0 || !WindowsNativeMethods.IsWindow(hwnd)) return null;
        try
        {
            var threadId = WindowsNativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (threadId == 0 || processId == 0) return null;
            using var process = Process.GetProcessById((int)processId);
            var identity = new WindowsTargetIdentity(
                hwnd,
                (int)processId,
                (int)threadId,
                process.StartTime.ToUniversalTime(),
                process.ProcessName,
                DateTimeOffset.UtcNow);
            var runtimeKey = _contexts.Register(identity);
            return new DeliveryTarget(
                process.ProcessName,
                "WindowsDesktopWindow",
                process.ProcessName,
                process.ProcessName,
                runtimeKey,
                identity.CapturedAtUtc);
        }
        catch { return null; }
    }

    public TargetValidationResult Validate(DeliveryTarget target, bool requireForeground)
    {
        if (!_contexts.TryGet(target.RuntimeKey, out var expected))
            return TargetValidationResult.Invalid("TARGET_CONTEXT_MISSING", "目标窗口上下文已失效。", target);
        if (!WindowsNativeMethods.IsWindow(expected.Hwnd))
            return TargetValidationResult.Invalid("TARGET_VALIDATION_FAILED", "目标窗口已经不存在。", target);
        var current = CaptureWindowIdentity(expected);
        if (current is null || !TargetIdentityMatcher.Matches(expected, current))
            return TargetValidationResult.Invalid("TARGET_CHANGED", "目标窗口身份已变化。", target);
        if (requireForeground && WindowsNativeMethods.GetForegroundWindow() != expected.Hwnd)
            return TargetValidationResult.Invalid("TARGET_CHANGED", "目标窗口已不在前台。", target);
        return TargetValidationResult.Valid;
    }

    internal bool TryGet(DeliveryTarget target, out WindowsTargetIdentity identity) => _contexts.TryGet(target.RuntimeKey, out identity!);

    internal static bool IsIdentityCurrent(WindowsTargetIdentity identity) =>
        CaptureWindowIdentity(identity) is { } current && TargetIdentityMatcher.Matches(identity, current);

    internal static bool TryActivate(WindowsTargetIdentity identity, TimeSpan timeout)
    {
        if (!WindowsNativeMethods.IsWindow(identity.Hwnd) || !WindowsNativeMethods.SetForegroundWindow(identity.Hwnd)) return false;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (WindowsNativeMethods.GetForegroundWindow() == identity.Hwnd) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    private static WindowsTargetIdentity? CaptureWindowIdentity(WindowsTargetIdentity expected)
    {
        try
        {
            var threadId = WindowsNativeMethods.GetWindowThreadProcessId(expected.Hwnd, out var processId);
            if (threadId == 0 || processId == 0) return null;
            using var process = Process.GetProcessById((int)processId);
            return expected with
            {
                ProcessId = (int)processId,
                WindowThreadId = (int)threadId,
                ProcessStartTimeUtc = process.StartTime.ToUniversalTime(),
                ProcessName = process.ProcessName,
            };
        }
        catch { return null; }
    }
}

internal static class TargetIdentityMatcher
{
    public static bool Matches(WindowsTargetIdentity expected, WindowsTargetIdentity actual) =>
        expected.Hwnd == actual.Hwnd &&
        expected.ProcessId == actual.ProcessId &&
        expected.WindowThreadId == actual.WindowThreadId &&
        expected.ProcessStartTimeUtc == actual.ProcessStartTimeUtc &&
        string.Equals(expected.ProcessName, actual.ProcessName, StringComparison.OrdinalIgnoreCase);
}
