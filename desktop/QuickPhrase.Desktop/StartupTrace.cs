using System.Diagnostics;

namespace QuickPhrase.Desktop;

/// <summary>只输出阶段、耗时和 UTC 时间的启动诊断，不记录窗口、话术或用户内容。</summary>
internal static class StartupTrace
{
    private static readonly long Started = Stopwatch.GetTimestamp();

    public static void Mark(string stage)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("QUICKPHRASE_TRACE"), "1", StringComparison.Ordinal)) return;
        Console.WriteLine($"STARTUP_TRACE stage={stage} durationMs={Stopwatch.GetElapsedTime(Started).TotalMilliseconds:F1} utc={DateTimeOffset.UtcNow:O}");
    }
}
