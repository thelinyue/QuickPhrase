using System;
using System.IO;

namespace QuickPhrase.Desktop.Tests;

public class IndependentSettingsWindowTests
{
    [Fact]
    public void SettingsWindow_IsNotOwnedByManagementWindow()
    {
        var root = FindRepoRoot();
        var controller = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "ApplicationController.cs"));
        var settingsXaml = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "SettingsWindow.xaml"));

        Assert.Contains("_settingsWindow = new SettingsWindow(_commands);", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("new SettingsWindow(_commands, owner)", controller, StringComparison.Ordinal);
        Assert.Contains("RequestShutdownIfNoProductWindows", controller, StringComparison.Ordinal);
        Assert.Contains("_management is { IsVisible: true } || _settingsWindow is { IsVisible: true }", controller, StringComparison.Ordinal);
        Assert.Contains("WindowStartupLocation=\"CenterScreen\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowStartupLocation=\"CenterOwner\"", settingsXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsSave_UsesStagedHotkeyTransaction_WithoutReconfiguringAfterSave()
    {
        var root = FindRepoRoot();
        var controller = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "ApplicationController.cs"));

        Assert.Contains("_hotkeys.ApplyShortcutChangeAsync(", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySettingsHotkeysAsync", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("await _hotkeys.ConfigureAsync(result.Value", controller, StringComparison.Ordinal);
    }
    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "QuickPhrase.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
    }
}
