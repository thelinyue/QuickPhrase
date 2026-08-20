using System;
using System.IO;
using System.Text.RegularExpressions;

namespace QuickPhrase.Desktop.Tests;

public class SettingsViewContractTests
{
    [Fact]
    public void SettingsView_UsesImmediateApplySurfaceWithoutGlobalActions()
    {
        var xaml = ReadDesktopFile("Views", "SettingsView.xaml");

        Assert.DoesNotContain("Content=\"保存\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"取消\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<designSystem:ShortcutInput", xaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource Brush.Background.Default}", xaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource Brush.Surface.Default}", xaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource Brush.Border.Default}", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_UsesSettingItemForEverySettingAndPreservesActions()
    {
        var xaml = ReadDesktopFile("Views", "SettingsView.xaml");

        var switchCount = Regex.Matches(xaml, "<CheckBox\\b", RegexOptions.CultureInvariant).Count;
        var styledSwitchCount = Regex.Matches(xaml, "Style=\"\\{StaticResource Style\\.Switch\\.Default\\}\"", RegexOptions.CultureInvariant).Count;
        Assert.True(switchCount > 0);
        Assert.Equal(switchCount, styledSwitchCount);
        Assert.Equal(10, Regex.Matches(xaml, "<designSystem:SettingItem(?:\\s|>)", RegexOptions.CultureInvariant).Count);
        Assert.DoesNotContain("<Border Style=\"{StaticResource SettingRow}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Border Style=\"{StaticResource SettingAction}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.Button.Secondary}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DataManagement.RequestImportCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("DataManagement.RequestExportCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("RestartOnboardingCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("<designSystem:ShortcutInput", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Alt + Space\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Ctrl + Space\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"自定义\"", xaml, StringComparison.Ordinal);
        Assert.Contains("LauncherShortcut", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LauncherShortcutDisplay", xaml, StringComparison.Ordinal);
        Assert.Contains("ApplyRecommendedShortcut_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("ApplyAlternateShortcut_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("EditCustomShortcut_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("Title=\"快捷发送模式\"", xaml, StringComparison.Ordinal);
        Assert.Contains("风险选项", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding QuickSendWithoutConfirmation}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Title=\"自动发送\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_UsesSectionNavigationInsteadOfOneLongForm()
    {
        var xaml = ReadDesktopFile("Views", "SettingsView.xaml");
        var codeBehind = ReadDesktopFile("Views", "SettingsView.xaml.cs");

        Assert.Contains("x:Name=\"SettingsNavigation\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"SettingsNavigation_SelectionChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"通用\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"快捷键\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"发送行为\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"应用适配\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"数据管理\"", xaml, StringComparison.Ordinal);
        Assert.Equal(5, Regex.Matches(xaml, "x:Name=\"(?:General|Hotkeys|Delivery|Adapters|DataManagement)Section\"", RegexOptions.CultureInvariant).Count);
        Assert.DoesNotContain("已自动保存", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsNavigation_SelectionChanged", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.ListItem.Navigation}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"{StaticResource Size.Settings.Sidebar.GridLength}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"{StaticResource Size.Settings.Content.Maximum}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_CollapsesEmptyDataManagementFeedback()
    {
        var xaml = ReadDesktopFile("Views", "SettingsView.xaml");

        Assert.Contains("x:Key=\"Style.Settings.DataFeedback\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(
            xaml,
            Regex.Escape("Style=\"{StaticResource Style.Settings.DataFeedback}\""),
            RegexOptions.CultureInvariant).Count);
        var styleStart = xaml.IndexOf("<Style x:Key=\"Style.Settings.DataFeedback\"", StringComparison.Ordinal);
        Assert.True(styleStart >= 0, "未找到数据管理反馈样式。");
        var styleEnd = xaml.IndexOf("</Style>", styleStart, StringComparison.Ordinal);
        Assert.True(styleEnd > styleStart, "数据管理反馈样式边界异常。");
        var feedbackStyle = xaml[styleStart..(styleEnd + "</Style>".Length)];

        Assert.Contains("<Trigger Property=\"Text\" Value=\"{x:Null}\">", feedbackStyle, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"Text\" Value=\"\">", feedbackStyle, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(
            feedbackStyle,
            "<Setter Property=\"Visibility\" Value=\"Collapsed\" />",
            RegexOptions.CultureInvariant).Count);
    }

    [Fact]
    public void SettingsView_EachModuleUsesTheSharedPageSkeleton()
    {
        var xaml = ReadDesktopFile("Views", "SettingsView.xaml");
        var sectionNames = new[] { "GeneralSection", "HotkeysSection", "DeliverySection", "AdaptersSection", "DataManagementSection" };

        foreach (var sectionName in sectionNames)
        {
            var start = xaml.IndexOf($"x:Name=\"{sectionName}\"", StringComparison.Ordinal);
            Assert.True(start >= 0, $"未找到设置模块 {sectionName}。");
            var end = xaml.Length;
            foreach (var otherName in sectionNames)
            {
                var candidate = xaml.IndexOf($"x:Name=\"{otherName}\"", start + 1, StringComparison.Ordinal);
                if (candidate >= 0 && candidate < end)
                    end = candidate;
            }

            var section = xaml[start..end];
            Assert.Contains("Style.Text.Title.Large", section, StringComparison.Ordinal);
            Assert.Contains("Style.Text.Body.Small", section, StringComparison.Ordinal);
            Assert.Contains("Style.Setting.Group", section, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SettingsView_ContainsNoLegacyVisualResourcesOrStandardVisualLiterals()
    {
        var xaml = ReadDesktopFile("Views", "SettingsView.xaml");
        var legacyKeys = new[]
        {
            "SettingsSidebarWidth", "SettingsContentMaxWidth", "SettingsPagePadding",
            "SettingsHeaderTitle", "SettingsHeaderDescription", "SettingsSectionTitle",
            "SettingsGroup", "SettingRow", "SettingAction", "TextBody", "TextCaption",
            "TextMutedBrush", "DangerBrush", "ToggleSwitchStyle", "NavigationItem",
        };

        foreach (var key in legacyKeys)
            Assert.DoesNotContain($"{{StaticResource {key}}}", xaml, StringComparison.Ordinal);

        Assert.DoesNotMatch(new Regex("#[0-9A-Fa-f]{3,8}", RegexOptions.CultureInvariant), xaml);
        Assert.DoesNotContain("FontSize=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CornerRadius=", xaml, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("(?:Margin|Padding|MinWidth|MaxWidth|Width|Height)=\"[1-9][0-9]*(?:,|\")", RegexOptions.CultureInvariant), xaml);
    }

    [Fact]
    public void SettingsResources_ExposeSemanticLayoutTokensStylesAndComponent()
    {
        var sizes = ReadDesktopFile("DesignSystem", "Tokens", "Sizes.xaml");
        var thickness = ReadDesktopFile("DesignSystem", "Tokens", "Thickness.xaml");
        var surfaces = ReadDesktopFile("DesignSystem", "Styles", "Surfaces.xaml");
        var component = ReadDesktopFile("DesignSystem", "Components", "SettingItem.xaml");

        Assert.Contains("x:Key=\"Size.Settings.Sidebar.GridLength\"", sizes, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Size.Settings.Content.Maximum\"", sizes, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Thickness.Settings.Page\"", thickness, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Style.Setting.Group\"", surfaces, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Style.Setting.Row\"", surfaces, StringComparison.Ordinal);
        Assert.Contains("Style.Component.SettingItem", component, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWindow_DoesNotGuardCloseWithUnsavedSettings()
    {
        var code = ReadDesktopFile("SettingsWindow.xaml.cs");

        Assert.DoesNotContain("HasUnsavedChanges", code, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigationConfirmDialog", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAndLeave", code, StringComparison.Ordinal);
        Assert.Contains("ApplyPendingChangesAsync", code, StringComparison.Ordinal);
    }

    private static string ReadDesktopFile(params string[] segments)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "desktop", "QuickPhrase.Desktop" }.Concat(segments).ToArray());
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "QuickPhrase.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
    }
}
