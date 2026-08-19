using System.Diagnostics;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 等待企业微信自绘输入区恢复焦点和 Caret。Launcher 隐藏后，目标窗口可能先回到前台，
/// 但 GUIThreadInfo 仍暂时没有有效 Caret；这里仅轮询状态，不重复执行任何输入动作。
/// </summary>
internal static class WeComFocusWaiter
{
    public static async Task<bool> WaitAsync(
        WindowsTargetIdentity target,
        Func<int, WeComFocusFingerprint?> capture,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (timeout <= TimeSpan.Zero) return false;
        if (pollInterval <= TimeSpan.Zero) pollInterval = TimeSpan.FromMilliseconds(1);

        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = capture(target.WindowThreadId);
            if (fingerprint is { } value && WeComFocusPolicy.IsChatComposer(target, value))
                return true;

            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return false;
            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
