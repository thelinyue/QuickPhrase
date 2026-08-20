using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;
using QuickPhrase.Desktop;

namespace QuickPhrase.Architecture.Tests;

public sealed class Phase4LauncherTests
{
    [Theory]
    [InlineData("WXWork", true, true)]
    [InlineData("WXWork", false, false)]
    [InlineData("Unknown", true, false)]
    public void LauncherEligibilityRequiresKnownEnabledAdapter(string adapterId, bool enabled, bool expected)
    {
        var result = LauncherEligibilityPolicy.CanOpen(adapterId, new Dictionary<string, bool> { ["WXWork"] = enabled });
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task SettingsPersistExplicitQuickSendRiskChoice()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "QuickPhrase-Phase4-" + Guid.NewGuid().ToString("N"));
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(rootPath));
        var current = await runtime.Settings.LoadAsync();
        var result = await runtime.Settings.SaveAsync(current with { QuickSendWithoutConfirmation = true }, current.Version);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.QuickSendWithoutConfirmation);
    }


    [Theory]
    [InlineData(false, false, SendMode.InsertOnly)]
    [InlineData(false, true, SendMode.InsertAndSend)]
    [InlineData(true, true, SendMode.InsertOnly)]
    public void LauncherEnterMapsToExplicitSendMode(bool practiceMode, bool controlPressed, SendMode expected)
    {
        Assert.Equal(expected, LauncherWindow.ResolveSendMode(practiceMode, controlPressed));
    }

    [Theory]
    [InlineData(SendMode.InsertOnly, false, false)]
    [InlineData(SendMode.InsertAndSend, false, true)]
    [InlineData(SendMode.InsertAndSend, true, false)]
    public void ExplicitSendConfirmationPolicyUsesOnlyModeAndRiskSetting(
        SendMode mode,
        bool quickSendWithoutConfirmation,
        bool expected)
    {
        var settings = new AppSettings(
            1, false, false, true,
            new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space),
            quickSendWithoutConfirmation,
            true);

        Assert.Equal(expected, ApplicationController.RequiresSendConfirmation(mode, settings));
    }

    [Fact]
    public void LegacyStringAndVirtualKeyHotkeyApiIsRemoved()
    {
        var platformAssembly = typeof(WindowsShortcutService).Assembly;

        Assert.Null(platformAssembly.GetType("QuickPhrase.Platform.Windows.WindowsHotkeyChord"));
        Assert.Null(platformAssembly.GetType("QuickPhrase.Platform.Windows.WindowsHotkeyService"));
        Assert.Contains(typeof(IShortcutService), typeof(WindowsShortcutService).GetInterfaces());
    }

    [Fact]
    public void MockDeliveryNeverClaimsRealSend()
    {
        var result = MockDeliverySession.Execute("恢复出厂设置", send: true);

        Assert.False(result.Sent);
        Assert.Equal("CAPABILITY_UNVERIFIED", result.Code);
        Assert.Contains("模拟", result.Message, StringComparison.Ordinal);
    }
}
