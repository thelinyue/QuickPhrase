namespace QuickPhrase.Desktop;

/// <summary>每次 Launcher 打开只允许一次 Enter；重新打开时由 Reset 开启新的提交会话。</summary>
internal sealed class LauncherSubmissionGuard
{
    private int _submitted;

    public bool TrySubmit() => Interlocked.Exchange(ref _submitted, 1) == 0;
    public void Reset() => Volatile.Write(ref _submitted, 0);
}
