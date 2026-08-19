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
    public async Task SettingsAlwaysKeepAutoSendDisabledWhenNoAdapterSupportsIt()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "QuickPhrase-Phase4-" + Guid.NewGuid().ToString("N"));
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(rootPath));
        var current = await runtime.Settings.LoadAsync();
        var result = await runtime.Settings.SaveAsync(current with { AutoSend = true }, current.Version);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.AutoSend);
    }

    [Fact]
    public void WindowsHotkeyChordParsesCanonicalShortcut()
    {
        var result = WindowsHotkeyChord.TryParse("Ctrl + Alt + 2", out var chord);

        Assert.True(result);
        Assert.Equal("Ctrl+Alt+2", chord.Normalized);
        Assert.Equal((uint)'2', chord.VirtualKey);
        Assert.NotEqual(0u, chord.Modifiers);
    }

    [Fact]
    public void WindowsHotkeyChordRejectsShortcutWithoutModifier()
    {
        var result = WindowsHotkeyChord.TryParse("Space", out _);
        Assert.False(result);
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
