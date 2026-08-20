using System.IO;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>验证所有发布阶段只通过带超时清理的统一 Launcher smoke watchdog。</summary>
public sealed class LauncherSmokeScriptContractTests
{
    [Fact]
    public void PhaseScripts_UseWatchdogInsteadOfDotnetRunSmokeArguments()
    {
        var root = FindRepositoryRoot();
        foreach (var name in new[]
        {
            "verify-phase1.ps1",
            "verify-phase4.ps1",
            "verify-phase5.ps1",
            "verify-phase51.ps1",
        })
        {
            var source = File.ReadAllText(Path.Combine(root, "scripts", name));
            Assert.DoesNotContain("dotnet run", source, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("invoke-launcher-smoke.ps1", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Watchdog_DefinesModeSpecificTimeoutsAndKillsOnlyStartedPid()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "scripts", "invoke-launcher-smoke.ps1"));

        Assert.Contains("Native = 30", source, StringComparison.Ordinal);
        Assert.Contains("Performance = 60", source, StringComparison.Ordinal);
        Assert.Contains("-WindowStyle Hidden", source, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -Id $process.Id -Force", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Process -Name", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Phase4Validation_DefinesActualHotOpenMetricAndIsolationBoundary()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "docs", "phase4-validation.md"));

        Assert.Contains("HotkeyCoordinator 收到", source, StringComparison.Ordinal);
        Assert.Contains("预热 10 次", source, StringComparison.Ordinal);
        Assert.Contains("正式采样 200 次", source, StringComparison.Ordinal);
        Assert.Contains("120ms", source, StringComparison.Ordinal);
        Assert.Contains("不替代 Platform.Windows 的 RegisterHotKey 测试", source, StringComparison.Ordinal);
        Assert.Contains("冷启动", source, StringComparison.Ordinal);
        Assert.Contains("不作为发布门槛", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}
