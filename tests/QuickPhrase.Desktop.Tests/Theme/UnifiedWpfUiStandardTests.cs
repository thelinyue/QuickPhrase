using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuickPhrase.Desktop.Tests.Theme;

/// <summary>
/// 正式 WPF 界面统一标准的回归守卫。
/// 本测试只检查资源边界、共享视觉模式和关键无障碍语义，
/// 不触碰投递、快捷键或窗口生命周期等业务行为。
/// </summary>
public sealed class UnifiedWpfUiStandardTests
{
    private static readonly Regex HexColor = new("#[0-9a-fA-F]{3,8}(?![0-9a-fA-F])", RegexOptions.CultureInvariant);

    [Fact]
    public void FormalShellsAndPages_ConsumeUnifiedSurfaceStyles()
    {
        AssertContains("MainWindow.xaml", "Style=\"{StaticResource Style.Window.Shell}\"", "Style=\"{StaticResource Style.Surface.ContentRegion}\"");
        AssertContains("NewPhraseWindow.xaml", "Style=\"{StaticResource Style.Window.Shell}\"", "Style=\"{StaticResource Style.Surface.ContentRegion}\"");
        AssertContains("SettingsWindow.xaml", "Style=\"{StaticResource Style.Window.Shell}\"", "Style=\"{StaticResource Style.Surface.ContentRegion}\"");
        AssertContains("TitleBar.xaml", "Style=\"{StaticResource Style.Surface.TitleBar}\"");
        AssertContains(Path.Combine("Views", "EditorView.xaml"), "Style=\"{StaticResource Style.Surface.Page}\"");
        AssertContains(Path.Combine("Views", "SettingsView.xaml"), "Style=\"{StaticResource Style.Surface.Page}\"");
        AssertContains(Path.Combine("Views", "LibraryView.xaml"), "Style=\"{StaticResource Style.View.Root}\"");
    }

    [Fact]
    public void FormalDialogWindows_UseTheUnifiedDialogStyle()
    {
        var dialogsRoot = DesktopPath("Views", "Dialogs");
        var missing = Directory.EnumerateFiles(dialogsRoot, "*.xaml")
            .Where(file => !File.ReadAllText(file).Contains("Style=\"{StaticResource Style.Dialog.Window}\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(missing.Length == 0, $"以下对话框未使用统一 Dialog Style：{string.Join("、", missing)}");
    }

    [Fact]
    public void SharedSearchHistory_UsesTheCentralizedListItemStyle()
    {
        var component = ReadDesktopXaml("Views", "Shared", "SearchHistoryView.xaml");
        var listStyles = ReadDesktopXaml("DesignSystem", "Styles", "Lists.xaml");

        Assert.Contains("ItemContainerStyle=\"{StaticResource Style.ListItem.SearchHistory}\"", component, StringComparison.Ordinal);
        Assert.DoesNotContain("<ListBox.ItemContainerStyle>", component, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Style.ListItem.SearchHistory\"", listStyles, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsSelected\" Value=\"True\">", listStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void CriticalInteractiveControls_ExposeAccessibleNames()
    {
        var expectations = new Dictionary<string, string[]>
        {
            [Path.Combine("Views", "EditorView.xaml")] = ["话术标题", "话术正文", "话术分类"],
            [Path.Combine("Views", "LibraryView.xaml")] = ["话术搜索"],
            ["OnboardingWindow.xaml"] = ["引导分类名称", "引导话术分类", "引导话术标题", "引导话术内容", "开机时启动闪语"],
            ["TitleBar.xaml"] = ["最小化窗口", "最大化或还原窗口", "关闭窗口"],
            [Path.Combine("Views", "Dialogs", "CategoryDialog.xaml")] = ["分类名称"],
            [Path.Combine("Views", "Dialogs", "PhraseMoveDialog.xaml")] = ["目标分类"],
            [Path.Combine("Views", "Dialogs", "ExportPhrasePackageDialog.xaml")] = ["导出范围", "话术包名称"],
        };

        foreach (var (relativePath, names) in expectations)
        {
            var markup = ReadDesktopXaml(relativePath.Split(Path.DirectorySeparatorChar));
            foreach (var name in names)
                Assert.Contains($"AutomationProperties.Name=\"{name}\"", markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FormalXaml_ContainsNoRawHexOutsideGovernedThemeAndTokenLayers()
    {
        var excludedRoots = new[]
        {
            Path.GetFullPath(DesktopPath("DesignSystem", "Tokens")) + Path.DirectorySeparatorChar,
            Path.GetFullPath(DesktopPath("DesignSystem", "Themes")) + Path.DirectorySeparatorChar,
        };

        var violations = Directory.EnumerateFiles(DesktopPath(), "*.xaml", SearchOption.AllDirectories)
            .Where(file => !excludedRoots.Any(root => Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            .Where(file => HexColor.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(DesktopPath(), file))
            .ToArray();

        Assert.True(violations.Length == 0, $"正式 XAML 不得直接声明 Hex 颜色：{string.Join("、", violations)}");
    }

    private static void AssertContains(string relativePath, params string[] expectedFragments)
    {
        var markup = ReadDesktopXaml(relativePath.Split(Path.DirectorySeparatorChar));
        foreach (var fragment in expectedFragments)
            Assert.Contains(fragment, markup, StringComparison.Ordinal);
    }

    private static string ReadDesktopXaml(params string[] segments) => File.ReadAllText(DesktopPath(segments));

    private static string DesktopPath(params string[] segments)
    {
        var parts = new List<string> { FindRepositoryRoot(), "desktop", "QuickPhrase.Desktop" };
        parts.AddRange(segments);
        return Path.Combine(parts.ToArray());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
    }
}
