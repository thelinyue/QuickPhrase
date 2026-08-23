using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Desktop;

/// <summary>
/// 管理窗口几何的纯计算器。输入、输出均为 DIP，避免把显示器和 HWND 逻辑散落到 WPF 窗口事件中。
/// </summary>
internal static class ManagementWindowPlacementCalculator
{
    public const double DockedWidth = 380;
    public const double MinimumHeight = 520;

    public static bool TryCalculateDockedRight(
        WindowsDockTargetSnapshot snapshot,
        out DesktopWindowBounds bounds)
    {
        bounds = default;
        if (!snapshot.IsAvailable) return false;
        var visible = Intersect(snapshot.TargetBounds, snapshot.WorkArea);
        if (visible.Width < DockedWidth || visible.Height < MinimumHeight) return false;
        bounds = new DesktopWindowBounds(visible.Right - DockedWidth, visible.Top, DockedWidth, visible.Height);
        return true;
    }

    public static DesktopWindowBounds ClampFloating(
        ManagementWindowPlacement placement,
        DesktopWindowBounds workArea)
    {
        var width = Math.Min(Math.Max(380, placement.FloatingWidth), workArea.Width);
        var height = Math.Min(Math.Max(MinimumHeight, placement.FloatingHeight), workArea.Height);
        var requestedLeft = placement.FloatingLeft ?? workArea.Left + (workArea.Width - width) / 2;
        var requestedTop = placement.FloatingTop ?? workArea.Top + (workArea.Height - height) / 2;
        var left = Math.Clamp(requestedLeft, workArea.Left, workArea.Right - width);
        var top = Math.Clamp(requestedTop, workArea.Top, workArea.Bottom - height);
        return new DesktopWindowBounds(left, top, width, height);
    }

    private static DesktopWindowBounds Intersect(DesktopWindowBounds first, DesktopWindowBounds second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right <= left || bottom <= top
            ? default
            : new DesktopWindowBounds(left, top, right - left, bottom - top);
    }
}
