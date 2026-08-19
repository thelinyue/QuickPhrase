using System.Linq;
using System.Threading.Tasks;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.Tests.Fakes;

namespace QuickPhrase.Desktop.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public async Task Load_PopulatesAdapters_FromSettings()
    {
        var vm = new SettingsViewModel(new FakeCommandService());
        await vm.LoadAsync();

        Assert.Contains(vm.Adapters, a => a.Id == "WXWork" && a.Enabled);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task ToggleAdapter_ThenSave_Persists()
    {
        var fake = new FakeCommandService();
        var vm = new SettingsViewModel(fake);
        await vm.LoadAsync();
        var wx = vm.Adapters.First(a => a.Id == "WXWork");
        wx.Enabled = false;
        Assert.True(vm.HasUnsavedChanges);

        AppSettings? saved = null;
        vm.Saved += (_, s) => saved = s;
        await vm.SaveAsync();

        Assert.NotNull(saved);
        Assert.False(saved!.LauncherEnabledAdapters["WXWork"]);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Discard_RestoresAdapterState()
    {
        var vm = new SettingsViewModel(new FakeCommandService());
        await vm.LoadAsync();
        vm.Adapters.First(a => a.Id == "WXWork").Enabled = false;
        vm.DiscardChanges();
        Assert.True(vm.Adapters.First(a => a.Id == "WXWork").Enabled);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Save_NormalizesShortcut_ToLowercaseDeduped()
    {
        var vm = new SettingsViewModel(new FakeCommandService());
        await vm.LoadAsync();
        vm.LauncherShortcutDisplay = "ALT + SPACE + ALT";

        AppSettings? saved = null;
        vm.Saved += (_, s) => saved = s;
        await vm.SaveAsync();

        Assert.NotNull(saved);
        Assert.Equal("alt+space", saved!.LauncherShortcutNormalized);
    }
}
