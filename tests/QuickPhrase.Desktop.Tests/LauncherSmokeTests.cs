using System.IO;
using QuickPhrase.Desktop;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>锁定 Launcher smoke 的参数、性能统计和生命周期稳定契约。</summary>
public sealed class LauncherSmokeTests
{
    [Theory]
    [InlineData("--smoke-native-launcher", "Native")]
    [InlineData("--smoke-launcher-performance", "Performance")]
    public void Options_ParseSingleMode(string argument, string expected)
    {
        var result = LauncherSmokeOptions.Parse([argument]);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Options.Mode.ToString());
    }

    [Fact]
    public void Options_RejectConflictingModes()
    {
        var result = LauncherSmokeOptions.Parse([
            "--smoke-native-launcher",
            "--smoke-launcher-performance",
        ]);

        Assert.False(result.IsSuccess);
        Assert.Equal("LAUNCHER_SMOKE_ARGUMENT_INVALID", result.ErrorCode);
    }

    [Fact]
    public void Options_IgnoreOrdinaryStartup()
    {
        var result = LauncherSmokeOptions.Parse(["--background"]);

        Assert.True(result.IsSuccess);
        Assert.Equal(LauncherSmokeMode.None, result.Options.Mode);
    }

    [Fact]
    public void PerformanceSummary_UsesNearestRank()
    {
        var samples = Enumerable.Range(1, 200)
            .Select(value => TimeSpan.FromMilliseconds(value))
            .ToArray();

        var summary = LauncherPerformanceSummary.Create(samples, TimeSpan.FromMilliseconds(120));

        Assert.Equal(100, summary.P50.TotalMilliseconds);
        Assert.Equal(190, summary.P95.TotalMilliseconds);
        Assert.Equal(198, summary.P99.TotalMilliseconds);
        Assert.False(summary.Passed);
    }

    [Fact]
    public void PerformanceSummary_AllowsP95EqualToThreshold()
    {
        var samples = Enumerable.Repeat(TimeSpan.FromMilliseconds(120), 200).ToArray();

        var summary = LauncherPerformanceSummary.Create(samples, TimeSpan.FromMilliseconds(120));

        Assert.True(summary.Passed);
    }

    [Fact]
    public void PerformanceSummary_RejectsEmptySamples()
    {
        Assert.Throws<ArgumentException>(() =>
            LauncherPerformanceSummary.Create([], TimeSpan.FromMilliseconds(120)));
    }

    [Fact]
    public void LauncherLifecycleState_ContainsStableReuseStates()
    {
        Assert.Equal(new[]
        {
            "Created",
            "Activating",
            "Visible",
            "Interactive",
            "Hiding",
            "Hidden",
            "Disposed",
            "Faulted",
        }, Enum.GetNames<LauncherLifecycleState>());
    }

    [Fact]
    public void Runner_DoesNotReferenceExternalDeliveryOrPersistenceServices()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "desktop", "QuickPhrase.Desktop", "LauncherSmokeRunner.cs"));

        foreach (var forbidden in new[]
        {
            "QuickPhraseDataRuntime",
            "QuickPhraseDataOptions.ForCurrentUser",
            "WindowsShortcutService",
            "WindowsTargetDetector",
            "WindowsAdapterResolver",
            "TextDeliveryFactory",
            "Clipboard",
            "AutomationElement",
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);

        Assert.Equal(1, source.Split("new LauncherWindow", StringSplitOptions.None).Length - 1);
        Assert.Contains("ReferenceEquals", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_CreateRunDirectoryUnderConfiguredRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuickPhrase-Smoke-Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var diagnostics = LauncherSmokeDiagnostics.Create(root, LauncherSmokeMode.Native);
            Assert.StartsWith(Path.GetFullPath(root), diagnostics.RunDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(diagnostics.RunDirectory));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PerformanceContract_UsesConfirmedCountsAndThreshold()
    {
        Assert.Equal(10, LauncherSmokeRunner.PerformanceWarmupCount);
        Assert.Equal(200, LauncherSmokeRunner.PerformanceSampleCount);
        Assert.Equal(TimeSpan.FromMilliseconds(120), LauncherSmokeRunner.PerformanceThreshold);
    }

    [Fact]
    public void App_HandlesSmokeBeforeApplicationControllerConstruction()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "desktop", "QuickPhrase.Desktop", "App.xaml.cs"));
        var parseIndex = source.IndexOf("LauncherSmokeOptions.Parse", StringComparison.Ordinal);
        var controllerIndex = source.IndexOf("new ApplicationController", StringComparison.Ordinal);

        Assert.True(parseIndex >= 0, "App 未解析 Launcher smoke 参数。");
        Assert.True(controllerIndex > parseIndex, "Smoke 必须在 ApplicationController 创建前分流。");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}
