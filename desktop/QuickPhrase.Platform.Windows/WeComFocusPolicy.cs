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

    /// <summary>
    /// 企业微信在 Ctrl+V 返回后仍可能异步处理剪贴板消息。显式发送前保留一个短稳定窗口，
    /// 再重新采集焦点/Caret 指纹；该等待不读取正文，也不替代后续目标重校验。
    /// </summary>
    internal static TimeSpan PostPasteStabilizationDelay { get; } = TimeSpan.FromMilliseconds(120);

    internal static Task WaitForPostPasteStabilityAsync(CancellationToken cancellationToken) =>
        Task.Delay(PostPasteStabilizationDelay, cancellationToken);

    /// <summary>
    /// 插入前后允许 Caret 随正文长度移动，但承载输入区的窗口、Caret 宿主、控件类名和客户区尺寸必须保持一致。
    /// 该比较只使用脱敏结构指纹，不读取输入框正文。
    /// </summary>
    public static bool IsStableChatComposer(
        WindowsTargetIdentity target,
        WeComFocusFingerprint before,
        WeComFocusFingerprint after) =>
        IsChatComposer(target, before) &&
        IsChatComposer(target, after) &&
        before.FocusHwnd == after.FocusHwnd &&
        before.CaretHwnd == after.CaretHwnd &&
        string.Equals(before.FocusClass, after.FocusClass, StringComparison.Ordinal) &&
        before.ClientWidth == after.ClientWidth &&
        before.ClientHeight == after.ClientHeight;

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
