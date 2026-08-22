using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;
using QuickPhrase.Desktop;

namespace QuickPhrase.Architecture.Tests;

public sealed class Phase4LauncherTests
{
    [Fact]
    public void UnknownForegroundTargetUsesGenericTextInputWithoutSendCapability()
    {
        using var resolver = new WindowsAdapterResolver(
            productVersionReader: _ => null,
            targetValidator: _ => true);
        var target = new DeliveryTarget(
            "Notepad", "Desktop", "Unknown", "记事本", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

        var adapter = resolver.Resolve(target);
        var capabilities = adapter.DetectCapabilities();

        Assert.Equal("GenericTextInput", adapter.AdapterId);
        Assert.Equal(CapabilityStatus.Verified, capabilities.InsertText);
        Assert.Equal(CapabilityStatus.Verified, capabilities.VerifyTextInsert);
        Assert.Equal(CapabilityStatus.Unsupported, capabilities.InsertImage);
        Assert.Equal(CapabilityStatus.Unsupported, capabilities.VerifyImageInsert);
        Assert.Equal(CapabilityStatus.Unsupported, capabilities.TriggerSend);
        Assert.Equal(CapabilityStatus.Unsupported, capabilities.VerifySend);
    }

    [Fact]
    public void GenericTextInputFocusPolicyRejectsPasswordAndUnstableCandidates()
    {
        var target = new WindowsTargetIdentity((nint)10, 42, 1, DateTimeOffset.UtcNow, "notepad", DateTimeOffset.UtcNow);
        var valid = new GenericTextInputFocusSnapshot(42, (nint)11, "editor", "Edit", [1, 2, 3], true, true, false, true);
        var password = valid with { IsPassword = true };
        var changed = new GenericTextInputFocusFingerprint(42, (nint)11, "editor", "Edit", [1, 2, 4]);
        var before = new GenericTextInputFocusFingerprint(42, (nint)11, "editor", "Edit", [1, 2, 3]);

        Assert.True(GenericTextInputFocusPolicy.IsEligibleEditableTextInput(target, valid));
        Assert.False(GenericTextInputFocusPolicy.IsEligibleEditableTextInput(target, password));
        Assert.False(GenericTextInputFocusPolicy.IsStableEditableTextInput(before, changed));
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

    [Theory]
    [InlineData(QuickSendGuideDecision.Cancel, false, false)]
    [InlineData(QuickSendGuideDecision.ContinueOnce, false, true)]
    [InlineData(QuickSendGuideDecision.EnableAndContinue, false, false)]
    [InlineData(QuickSendGuideDecision.EnableAndContinue, true, true)]
    public void QuickSendGuideOnlyContinuesAfterExplicitChoiceAndSuccessfulSettingSave(
        QuickSendGuideDecision decision,
        bool quickSendEnabledSuccessfully,
        bool expected)
    {
        Assert.Equal(
            expected,
            ApplicationController.CanProceedWithQuickSendGuide(decision, quickSendEnabledSuccessfully));
    }

    [Fact]
    public void QuickSendGuideDialogKeepsExplicitSendSafetyCopyAndChoices()
    {
        var path = Path.Combine(
            FindRepoRoot(), "desktop", "QuickPhrase.Desktop", "Views", "Dialogs", "QuickSendGuideDialog.xaml");
        var xaml = File.ReadAllText(path);

        Assert.Contains("已有草稿可能一并发送", xaml, StringComparison.Ordinal);
        Assert.Contains("不会读取输入框正文", xaml, StringComparison.Ordinal);
        Assert.Contains("无法确认目标应用最终是否完成发送", xaml, StringComparison.Ordinal);
        Assert.Contains("Title=\"确认插入并发送\"", xaml, StringComparison.Ordinal);
        Assert.Contains("插入并发送会发送当前目标输入框中的全部内容", xaml, StringComparison.Ordinal);
        Assert.Contains("开启免确认模式后，按 Ctrl+Enter 插入并发送将不再显示此确认", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"开启免确认并继续\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("快捷发送", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"仅本次继续\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"取消\"", xaml, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "QuickPhrase.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
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
