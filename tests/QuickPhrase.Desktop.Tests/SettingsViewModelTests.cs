using System;
using System.Linq;
using System.Threading.Tasks;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Tests.Fakes;

namespace QuickPhrase.Desktop.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public async Task Load_PopulatesAdapters_WithoutPersisting()
    {
        var fake = new FakeCommandService();
        var vm = new SettingsViewModel(fake);

        await vm.LoadAsync();

        Assert.Contains(vm.Adapters, a => a.Id == "WXWork" && a.Enabled);
        Assert.Equal(0, fake.SettingsUpdateCalls);
    }

    [Fact]
    public async Task ToggleSetting_AppliesImmediately()
    {
        var fake = new FakeCommandService();
        var vm = new SettingsViewModel(fake);
        await vm.LoadAsync();

        vm.LaunchOnStartup = true;
        await vm.ApplyPendingChangesAsync();

        Assert.True((await fake.GetSettingsAsync()).LaunchOnStartup);
        Assert.Equal(1, fake.SettingsUpdateCalls);
    }

    [Fact]
    public async Task ToggleAdapter_AppliesImmediately()
    {
        var fake = new FakeCommandService();
        var vm = new SettingsViewModel(fake);
        await vm.LoadAsync();

        vm.Adapters.First(a => a.Id == "WXWork").Enabled = false;
        await vm.ApplyPendingChangesAsync();

        Assert.False((await fake.GetSettingsAsync()).LauncherEnabledAdapters["WXWork"]);
    }

    [Fact]
    public async Task ShortcutChange_AppliesImmediately_AndNormalizes()
    {
        var fake = new FakeCommandService();
        var vm = new SettingsViewModel(fake);
        await vm.LoadAsync();

        vm.LauncherShortcutDisplay = "ALT + SPACE + ALT";
        await vm.ApplyPendingChangesAsync();

        var saved = await fake.GetSettingsAsync();
        Assert.Equal("alt+space", saved.LauncherShortcutNormalized);
    }

    [Fact]
    public async Task RapidChanges_KeepLatestValue()
    {
        var fake = new FakeCommandService();
        var vm = new SettingsViewModel(fake);
        await vm.LoadAsync();

        vm.StartMinimized = true;
        await vm.ApplyPendingChangesAsync();
        vm.StartMinimized = false;
        vm.StartMinimized = true;
        await vm.ApplyPendingChangesAsync();

        Assert.True((await fake.GetSettingsAsync()).StartMinimized);
    }

    [Fact]
    public async Task VersionConflict_DoesNotSilentlyOverwrite()
    {
        var fake = new FakeCommandService { ReturnSettingsConflictOnce = true };
        var vm = new SettingsViewModel(fake);
        await vm.LoadAsync();

        vm.StartMinimized = true;
        await vm.ApplyPendingChangesAsync();

        Assert.Contains("其他操作", vm.ErrorMessage);
        Assert.True((await fake.GetSettingsAsync()).StartMinimized);
    }

    [Fact]
    public async Task ImmediateApply_PreservesCompletedOnboardingState()
    {
        var fake = new FakeCommandService();
        await fake.UpdateSettingsAsync(new AppSettings(1, false, false, true, "Alt + Space", "Alt+Space", false, true, true, 1));
        var vm = new SettingsViewModel(fake);
        await vm.LoadAsync();

        vm.StartMinimized = true;
        await vm.ApplyPendingChangesAsync();

        var saved = await fake.GetSettingsAsync();
        Assert.True(saved.HasCompletedOnboarding);
        Assert.Equal(1, saved.OnboardingVersion);
        Assert.True(saved.StartMinimized);
    }
}
