using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// Launcher、引导、标题栏、共享状态和通用对话框的 Design System 迁移契约。
/// 测试只审计本批正式 WPF XAML，防止页面重新引入旧资源别名或标准视觉字面量。
/// </summary>
public sealed class ShellAndDialogDesignSystemMigrationTests
{
    private static readonly string[] TargetRelativePaths =
    [
        "LauncherWindow.xaml",
        "OnboardingWindow.xaml",
        "TitleBar.xaml",
        Path.Combine("Views", "Shared", "SearchHistoryView.xaml"),
        Path.Combine("Views", "Shared", "StatePresenter.xaml"),
        Path.Combine("Views", "Dialogs", "CategoryDialog.xaml"),
        Path.Combine("Views", "Dialogs", "ImportPhrasePackageDialog.xaml"),
        Path.Combine("Views", "Dialogs", "ExportPhrasePackageDialog.xaml"),
        Path.Combine("Views", "Dialogs", "NavigationConfirmDialog.xaml"),
        Path.Combine("Views", "Dialogs", "PhraseMoveDialog.xaml"),
    ];

    private static readonly string[] LegacyResourceKeys =
    [
        "WindowBackgroundBrush",
        "SurfacePrimaryBrush",
        "SurfaceSecondaryBrush",
        "SeparatorBrush",
        "TextPrimaryBrush",
        "TextMutedBrush",
        "TextSecondaryBrush",
        "TextTertiaryBrush",
        "BrandPrimaryBrush",
        "BrandIceLightBrush",
        "DangerBrush",
        "ShadowColor",
        "RadiusLauncher",
        "RadiusPopup",
        "RadiusSmall",
        "RadiusXs",
        "SpaceMd",
        "TextH1",
        "TextH2",
        "TextListTitle",
        "TextBody",
        "TextCaption",
        "TextEyebrow",
        "PrimaryButton",
        "SecondaryButton",
        "GhostButton",
        "DialogWindow",
        "BaseTextBox",
        "SearchBoxStyle",
        "ComboBoxFieldStyle",
        "PhraseListItemContainerStyle",
    ];

    [Fact]
    public void TargetViewsUseSemanticDesignSystemResourcesWithoutVisualLiterals()
    {
        foreach (var relativePath in TargetRelativePaths)
        {
            var markup = ReadDesktopXaml(relativePath);

            Assert.False(Regex.IsMatch(markup, "#[0-9A-Fa-f]{3,8}"), $"{relativePath} 不应包含 Hex 颜色。");
            Assert.False(Regex.IsMatch(markup, "\\bFontSize=\"[0-9]"), $"{relativePath} 不应直接声明 FontSize。");
            Assert.False(Regex.IsMatch(markup, "\\bCornerRadius=\"[0-9]"), $"{relativePath} 不应直接声明 CornerRadius。");
            Assert.False(Regex.IsMatch(markup, @"\bHeight=""(?:32|36)"""), $"{relativePath} 的标准控件高度应引用 Size Token。");
            Assert.False(Regex.IsMatch(markup, @"\bBorderThickness=""1"""), $"{relativePath} 的标准边框应引用 Thickness Token。");
            Assert.False(Regex.IsMatch(markup, @"\bPadding=""(?:4|8|12|16|20|24)"""), $"{relativePath} 的标准内边距应引用 Thickness Token。");
            Assert.False(Regex.IsMatch(markup, @"\bMargin=""0,0,0,(?:4|8|12|16)"""), $"{relativePath} 的标准纵向间距应引用 Stack Gap Token。");
            Assert.False(Regex.IsMatch(markup, @"\bMargin=""0,0,(?:4|8|12|16),0"""), $"{relativePath} 的标准横向间距应引用 Inline Gap Token。");

            foreach (var legacyKey in LegacyResourceKeys)
                Assert.DoesNotContain($"Resource {legacyKey}", markup, StringComparison.Ordinal);

            Assert.DoesNotMatch("\\{StaticResource (?:Brush|Effect)\\.", markup);
            Assert.DoesNotMatch("\\{DynamicResource (?:Typography|Thickness|Radius|Size|Style)\\.", markup);
        }
    }

    [Fact]
    public void LauncherAndSharedViewsUseExistingInputListPopupAndStateStyles()
    {
        var launcher = ReadDesktopXaml("LauncherWindow.xaml");
        var history = ReadDesktopXaml(Path.Combine("Views", "Shared", "SearchHistoryView.xaml"));
        var state = ReadDesktopXaml(Path.Combine("Views", "Shared", "StatePresenter.xaml"));
        var titleBar = ReadDesktopXaml("TitleBar.xaml");

        Assert.Contains("Style=\"{StaticResource Style.Popup.Surface}\"", launcher, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource Style.Input.Search}\"", launcher, StringComparison.Ordinal);
        Assert.Contains("ItemContainerStyle=\"{StaticResource Style.Launcher.ListItem.Phrase}\"", launcher, StringComparison.Ordinal);
        Assert.Contains("VirtualizingStackPanel.IsVirtualizing=\"True\"", launcher, StringComparison.Ordinal);
        Assert.Contains("VirtualizingStackPanel.VirtualizationMode=\"Recycling\"", launcher, StringComparison.Ordinal);

        Assert.Contains("Background=\"Transparent\"", history, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.Button.Icon}\"", history, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.Button.Secondary.Compact}\"", state, StringComparison.Ordinal);
        Assert.Contains("Height=\"{StaticResource Size.TitleBar.Height}\"", titleBar, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.Text.Title.Small}\"", titleBar, StringComparison.Ordinal);
    }

    [Fact]
    public void OnboardingShortcutSummaryUsesSharedCardStyle()
    {
        var onboarding = ReadDesktopXaml("OnboardingWindow.xaml");

        Assert.Matches(
            new Regex(
                "<Border[^>]*Style=\"\\{StaticResource Style\\.Card\\.Default\\}\"[^>]*>.*?快捷键：Alt \\+ Space.*?修改快捷键.*?</Border>",
                RegexOptions.Singleline),
            onboarding);
    }

    [Fact]
    public void OnboardingAndDialogsUseSharedWindowControlAndTextStyles()
    {
        var onboarding = ReadDesktopXaml("OnboardingWindow.xaml");
        Assert.Contains("Style=\"{StaticResource Style.Dialog.Window}\"", onboarding, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource Style.Button.Primary}\"", onboarding, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource Style.Button.Ghost}\"", onboarding, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.Input.Default}\"", onboarding, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource Style.Select.Default}\"", onboarding, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.Switch.Default}\"", onboarding, StringComparison.Ordinal);

        foreach (var dialogPath in TargetRelativePaths.Where(path => path.Contains($"Views{Path.DirectorySeparatorChar}Dialogs", StringComparison.Ordinal)))
        {
            var markup = ReadDesktopXaml(dialogPath);
            Assert.Contains("Style=\"{StaticResource Style.Dialog.Window}\"", markup, StringComparison.Ordinal);
            Assert.Contains("Style.Button.", markup, StringComparison.Ordinal);
        }

        Assert.Contains("Style=\"{StaticResource Style.Input.Default}\"", ReadDesktopXaml(Path.Combine("Views", "Dialogs", "CategoryDialog.xaml")), StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.Input.Default}\"", ReadDesktopXaml(Path.Combine("Views", "Dialogs", "ExportPhrasePackageDialog.xaml")), StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.Select.Default}\"", ReadDesktopXaml(Path.Combine("Views", "Dialogs", "PhraseMoveDialog.xaml")), StringComparison.Ordinal);
    }

    private static string ReadDesktopXaml(string relativePath)
    {
        return File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop",
            "QuickPhrase.Desktop",
            relativePath));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}
