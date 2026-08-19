using System;
using System.IO;
using System.Text.RegularExpressions;

namespace QuickPhrase.Desktop.Tests;

public class SettingsViewContractTests
{
    [Fact]
    public void SettingsView_UsesImmediateApplySurfaceWithoutGlobalActions()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "SettingsView.xaml"));

        Assert.DoesNotContain("Content=\"保存\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"取消\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource KeyCap}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AppBackgroundBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("SurfaceBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("BorderSubtleBrush", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_UsesUnifiedControlsAndPreservesActionEntries()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "SettingsView.xaml"));

        var switchCount = Regex.Matches(xaml, "<CheckBox\\b", RegexOptions.CultureInvariant).Count;
        var styledSwitchCount = Regex.Matches(xaml, "Style=\"\\{StaticResource ToggleSwitchStyle\\}\"", RegexOptions.CultureInvariant).Count;
        Assert.True(switchCount > 0);
        Assert.Equal(switchCount, styledSwitchCount);
        Assert.Contains("StaticResource SecondaryButton", xaml, StringComparison.Ordinal);
        Assert.Contains("DataManagement.RequestImportCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("DataManagement.RequestExportCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("RestartOnboardingCommand", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_UsesSectionNavigationInsteadOfOneLongForm()
    {
        var root = FindRepoRoot();
        var xamlPath = Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "SettingsView.xaml");
        var codeBehindPath = Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "SettingsView.xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var codeBehind = File.ReadAllText(codeBehindPath);

        Assert.Contains("x:Name=\"SettingsNavigation\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"SettingsNavigation_SelectionChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"通用\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"快捷键\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"发送行为\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"应用适配\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"数据管理\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GeneralSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HotkeysSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DeliverySection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AdaptersSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DataManagementSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Visible\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("已自动保存", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsNavigation_SelectionChanged", codeBehind, StringComparison.Ordinal);
        Assert.Equal(5, Regex.Matches(xaml, "Style=\"\\{StaticResource SettingsSection\\}\"", RegexOptions.CultureInvariant).Count);
        Assert.DoesNotContain("CornerRadius=\"{StaticResource RadiusSmall}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWindow_DoesNotGuardCloseWithUnsavedSettings()
    {
        var root = FindRepoRoot();
        var code = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "SettingsWindow.xaml.cs"));

        Assert.DoesNotContain("HasUnsavedChanges", code, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigationConfirmDialog", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAndLeave", code, StringComparison.Ordinal);
        Assert.Contains("ApplyPendingChangesAsync", code, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "QuickPhrase.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
    }
}
