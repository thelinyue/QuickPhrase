using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuickPhrase.Desktop;

/// <summary>Launcher smoke 支持的独立运行模式；None 表示普通产品启动。</summary>
internal enum LauncherSmokeMode
{
    None,
    Native,
    Performance,
}

/// <summary>
/// Launcher 窗口可重复显示/隐藏的稳定生命周期状态。该状态不改变产品行为，
/// 只为诊断和 smoke 循环提供可验证的状态边界。
/// </summary>
internal enum LauncherLifecycleState
{
    Created,
    Activating,
    Visible,
    Interactive,
    Hiding,
    Hidden,
    Disposed,
    Faulted,
}

/// <summary>Launcher smoke 命令行选项；输出目录只允许在 smoke 模式下指定。</summary>
internal sealed record LauncherSmokeOptions(LauncherSmokeMode Mode, string? OutputDirectory)
{
    public static LauncherSmokeParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var native = arguments.Contains("--smoke-native-launcher", StringComparer.OrdinalIgnoreCase);
        var performance = arguments.Contains("--smoke-launcher-performance", StringComparer.OrdinalIgnoreCase);
        if (native && performance)
        {
            return LauncherSmokeParseResult.Failure(
                "LAUNCHER_SMOKE_ARGUMENT_INVALID",
                "Launcher smoke 模式不能同时指定。");
        }

        string? outputDirectory = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--smoke-output", StringComparison.OrdinalIgnoreCase))
                continue;
            index++;
            if (index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
            {
                return LauncherSmokeParseResult.Failure(
                    "LAUNCHER_SMOKE_ARGUMENT_INVALID",
                    "--smoke-output 缺少目录参数。");
            }
            outputDirectory = Path.GetFullPath(arguments[index]);
        }

        var mode = native
            ? LauncherSmokeMode.Native
            : performance ? LauncherSmokeMode.Performance : LauncherSmokeMode.None;
        if (mode == LauncherSmokeMode.None && outputDirectory is not null)
        {
            return LauncherSmokeParseResult.Failure(
                "LAUNCHER_SMOKE_ARGUMENT_INVALID",
                "--smoke-output 只能用于 Launcher smoke 模式。");
        }

        return LauncherSmokeParseResult.Success(new LauncherSmokeOptions(mode, outputDirectory));
    }
}

internal sealed record LauncherSmokeParseResult(
    bool IsSuccess,
    LauncherSmokeOptions Options,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static LauncherSmokeParseResult Success(LauncherSmokeOptions options) =>
        new(true, options, null, null);

    public static LauncherSmokeParseResult Failure(string code, string message) =>
        new(false, new LauncherSmokeOptions(LauncherSmokeMode.None, null), code, message);
}

/// <summary>使用 nearest-rank 计算 Launcher 热呼出性能分位值和发布门槛结果。</summary>
internal sealed record LauncherPerformanceSummary(
    TimeSpan P50,
    TimeSpan P95,
    TimeSpan P99,
    TimeSpan Threshold,
    bool Passed)
{
    public static LauncherPerformanceSummary Create(
        IReadOnlyCollection<TimeSpan> samples,
        TimeSpan threshold)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("性能样本不能为空。", nameof(samples));

        var ordered = samples.OrderBy(sample => sample).ToArray();
        TimeSpan Percentile(double percentile)
        {
            var rank = (int)Math.Ceiling(percentile * ordered.Length);
            return ordered[Math.Clamp(rank - 1, 0, ordered.Length - 1)];
        }

        var p50 = Percentile(0.50);
        var p95 = Percentile(0.95);
        var p99 = Percentile(0.99);
        return new LauncherPerformanceSummary(p50, p95, p99, threshold, p95 <= threshold);
    }
}
