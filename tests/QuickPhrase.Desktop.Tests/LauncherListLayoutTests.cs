using System.Collections.Immutable;
using System.IO;
using QuickPhrase.Core;
using QuickPhrase.Desktop;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// Launcher 与话术库共享列表结构的回归约束。
/// 这些测试只验证正式 WPF 链路的资源接入和领域字段映射，不读取 src/ 原型链路。
/// </summary>
public sealed class LauncherListLayoutTests
{
    [Fact]
    public void LibraryAndLauncherUseTheSamePhraseRowResource()
    {
        var root = FindRepositoryRoot();
        var library = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "LibraryView.xaml"));
        var launcher = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));
        var app = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "App.xaml"));

        Assert.Contains("Template.Phrase.Row", library);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", library);
        Assert.Contains("Template.Phrase.Row", launcher);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", launcher);
        Assert.DoesNotContain("Themes/PhraseListResources.xaml", app);
        Assert.Contains("Themes/Controls.xaml", app);
    }

    [Fact]
    public void LauncherUsesOnlyIndexTitleAndContentAndDoesNotExposeCategoryGuidOrDirectSend()
    {
        var root = FindRepositoryRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml.cs"));

        var sharedResources = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "DesignSystem", "Styles", "Lists.xaml"));

        Assert.Contains("Template.Phrase.Row", launcher);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", launcher);
        Assert.Contains("IndexInCategory", sharedResources);
        Assert.Contains("Title", sharedResources);
        Assert.Contains("Content", sharedResources);
        Assert.Contains("OverflowTextBlock", sharedResources);
        Assert.Equal(2, sharedResources.Split("OverflowTextBlock", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("CategoryId", launcher);
        Assert.DoesNotContain("直接发送", launcher);
        Assert.DoesNotContain("sendRequested", codeBehind);
        Assert.DoesNotContain("ModifierKeys.Control", codeBehind);
    }

    [Fact]
    public void LauncherContextMenuContainsOnlySafeInsertAndCopyActions()
    {
        var root = FindRepositoryRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));

        Assert.Contains("发送到输入区", launcher);
        Assert.Contains("复制内容到剪贴板", launcher);
        Assert.DoesNotContain("直接发送", launcher);
        Assert.DoesNotContain("编辑", launcher);
        Assert.DoesNotContain("删除", launcher);
    }

    [Fact]
    public void LauncherPhraseListItemMapsOnlyTheApprovedDisplayFields()
    {
        var phrase = new Phrase(
            Guid.NewGuid(), "标题", "正文", Guid.NewGuid(),
            false, ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "default");

        var item = LauncherPhraseListItem.FromPhrase(phrase, 3);

        Assert.Equal(3, item.IndexInCategory);
        Assert.Equal(phrase.Title, item.Title);
        Assert.Equal(phrase.Content, item.Content);
        Assert.Equal(phrase.Id, item.PhraseId);
        Assert.DoesNotContain(phrase.CategoryId.ToString(), item.ToString());
    }

    [Fact]
    public void BothViewsReferenceSharedStatePresenter()
    {
        var root = FindRepositoryRoot();
        var library = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "LibraryView.xaml"));
        var launcher = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));

        Assert.Contains("StatePresenter", library);
        Assert.Contains("StatePresenter", launcher);
        Assert.Contains("../DesignSystem/Styles/Lists.xaml", File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Themes", "Controls.xaml")));
    }

    [Theory]
    [InlineData(0, 260)]
    [InlineData(8, 384)]
    [InlineData(20, 520)]
    public void LauncherHeightTracksActualPhraseRowHeightWithoutLargeUnusedViewport(int itemCount, double expectedHeight)
    {
        Assert.Equal(expectedHeight, LauncherWindow.CalculateListHeight(itemCount));
    }
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}




