using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 企业微信自绘输入区的脱敏焦点指纹。由于 UI Automation 只暴露顶层 WeWorkWindow，
/// 使用 Win32 caret 的相对位置区分聊天编辑区与顶部搜索框；不读取任何控件文本。
/// </summary>
internal readonly record struct WeComFocusFingerprint(
    nint FocusHwnd,
    nint CaretHwnd,
    string FocusClass,
    uint Flags,
    int ClientWidth,
    int ClientHeight,
    int CaretLeft,
    int CaretTop,
    int CaretRight,
    int CaretBottom);

internal static class WeComFocusPolicy
{
    private const uint GuiCaretBlinking = 0x00000001;

    public static bool IsChatComposer(WindowsTargetIdentity target, WeComFocusFingerprint fingerprint)
    {
        if (fingerprint.FocusHwnd != (nint)target.Hwnd || fingerprint.CaretHwnd != (nint)target.Hwnd)
            return false;
        if (!string.Equals(fingerprint.FocusClass, "WeWorkWindow", StringComparison.Ordinal))
            return false;
        if ((fingerprint.Flags & GuiCaretBlinking) == 0 || fingerprint.ClientWidth < 480 || fingerprint.ClientHeight < 320)
            return false;

        var caretWidth = fingerprint.CaretRight - fingerprint.CaretLeft;
        var caretHeight = fingerprint.CaretBottom - fingerprint.CaretTop;
        var xRatio = (double)fingerprint.CaretLeft / fingerprint.ClientWidth;
        var yRatio = (double)fingerprint.CaretTop / fingerprint.ClientHeight;
        return caretWidth >= 0 && caretHeight is >= 8 and <= 64 &&
               xRatio is >= 0.15 and <= 0.98 && yRatio is >= 0.55 and <= 0.98;
    }
}
