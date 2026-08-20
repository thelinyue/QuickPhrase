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
}
