using System.Collections.Immutable;
using System.IO;
using QuickPhrase.Core;
using QuickPhrase.Desktop;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// Launcher 一体化浮层的回归约束。
/// 这些测试只验证正式 WPF 链路的资源接入和领域字段映射，不读取 src/ 原型链路。
/// </summary>
public sealed class LauncherListLayoutTests
{
    [Fact]
    public void LauncherItemPreservesCategoryPathFromTheCoreSearchResult()
    {
        var now = DateTimeOffset.UtcNow;
        var phrase = new Phrase(
            Guid.NewGuid(),
            "售后问候",
            PhraseBody.FromText("您好，感谢您的联系。"),
            Guid.NewGuid(),
            ShortcutMode.None,
            null,
            0,
            null,
            1,
            now,
            now);

        var item = LauncherPhraseListItem.FromSearchResult(
            new SearchResult(phrase, SearchMatchKind.TitleContains, "客户服务 / 售后"),
            1);

        Assert.Equal("客户服务 / 售后", item.CategoryPath);
    }

    [Fact]
    public void LauncherUsesItsOwnUnifiedPopupRowWithoutListChrome()
    {
        var launcher = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));

        Assert.Contains("Style.Popup.Surface", launcher, StringComparison.Ordinal);
        Assert.Contains("Style.Launcher.ListItem.Phrase", launcher, StringComparison.Ordinal);
        Assert.Contains("CategoryPath", launcher, StringComparison.Ordinal);
        Assert.Contains("搜索话术标题、正文或拼音；Ctrl+Enter 插入并发送", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Template.Phrase.Row", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("AccentBar", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherSearchInputUsesABorderlessTemplateWithoutFocusRing()
    {
        var launcher = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));
        var inputStyleStart = launcher.IndexOf("<Style x:Key=\"Style.Launcher.Input\"", StringComparison.Ordinal);
        var inputStyleEnd = launcher.IndexOf("</Style>", inputStyleStart, StringComparison.Ordinal);
        var inputStyle = launcher[inputStyleStart..(inputStyleEnd + "</Style>".Length)];

        Assert.DoesNotContain("BasedOn=\"{StaticResource Style.Input.Search}\"", inputStyle, StringComparison.Ordinal);
        Assert.Contains("PART_ContentHost", inputStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("FocusRing", inputStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("IsMouseOver", inputStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("IsKeyboardFocused", inputStyle, StringComparison.Ordinal);
        Assert.Contains("<Border x:Name=\"LauncherSurface\" Style=\"{StaticResource Style.Popup.Surface}\"", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("IsKeyboardFocusWithin", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryKeepsTheSharedRowWhileLauncherUsesItsDedicatedCompactRow()
    {
        var root = FindRepositoryRoot();
        var library = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "LibraryView.xaml"));
        var launcher = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));
        var app = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "App.xaml"));

        Assert.Contains("Template.Phrase.CompactRow", library);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", library);
        Assert.DoesNotContain("Template.Phrase.Row", launcher);
        Assert.Contains("LauncherPhraseTemplate", launcher);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", launcher);
        Assert.DoesNotContain("Themes/PhraseListResources.xaml", app);
        Assert.Contains("Themes/Controls.xaml", app);
    }

    [Fact]
    public void LauncherUsesApprovedFieldsAndExplicitCtrlEnterSendIntent()
    {
        var root = FindRepositoryRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml.cs"));

        var sharedResources = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "DesignSystem", "Styles", "Lists.xaml"));

        Assert.Contains("LauncherPhraseTemplate", launcher);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", launcher);
        Assert.Contains("IndexInCategory", launcher);
        Assert.Contains("Title", launcher);
        Assert.Contains("Content", launcher);
        Assert.Contains("CategoryPath", launcher);
        Assert.Equal(6, launcher.Split("OverflowTextBlock", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("CategoryId", launcher);
        Assert.DoesNotContain("直接发送", launcher);
        Assert.DoesNotContain("sendRequested", codeBehind);
        Assert.Contains("ModifierKeys.Control", codeBehind);
        Assert.Contains("SendMode.InsertAndSend", codeBehind);
        Assert.Contains("Ctrl+Enter 插入并发送", codeBehind);
        Assert.Contains("Ctrl+Enter 当前目标不支持插入并发送", codeBehind);
        Assert.DoesNotContain("Ctrl+Enter 显式发送", launcher);
        Assert.DoesNotContain("自动发送不支持", launcher);
        Assert.DoesNotContain("自动发送不支持", codeBehind);
        Assert.Contains("new AsyncRelayCommand<LauncherPhraseListItem>(SendPhraseAsync)", codeBehind);
        Assert.Contains("_canExplicitSend = canExplicitSend && !IsPracticeMode && target is not null;", codeBehind);
        Assert.Contains("await SubmitPhraseAsync(item, SendMode.InsertAndSend);", codeBehind);
    }

    [Fact]
    public void LauncherFooterUsesOneAlignedHintLineWithoutInternalTargetStatus()
    {
        var root = FindRepositoryRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml.cs"));

        Assert.Contains("<TextBlock x:Name=\"KeyboardHints\" Grid.Row=", launcher);
        Assert.Contains("<Run x:Name=\"InsertHintText\"", launcher);
        Assert.Contains("<Run x:Name=\"SendHintText\"", launcher);
        Assert.DoesNotContain("QueueText", launcher);
        Assert.DoesNotContain("TargetText", launcher);
        Assert.DoesNotContain("CapabilityText", launcher);
        Assert.DoesNotContain("已捕获", launcher);
        Assert.DoesNotContain("已验证", codeBehind);
        Assert.DoesNotContain("AdapterStatusSnapshot", codeBehind);
    }

    [Fact]
    public void LauncherContextMenuContainsSafeInsertCopyAndEditActions()
    {
        var root = FindRepositoryRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml.cs"));

        Assert.Contains("发送到输入区", launcher);
        Assert.Contains("复制内容到剪贴板", launcher);
        Assert.Contains("编辑话术", launcher);
        Assert.Contains("EditPhraseRequested", codeBehind);
        Assert.DoesNotContain("直接发送", launcher);
        Assert.DoesNotContain("删除", launcher);
    }

    [Fact]
    public void LauncherPhraseListItemMapsOnlyTheApprovedDisplayFields()
    {
        var phrase = new Phrase(
            Guid.NewGuid(), "标题", PhraseBody.FromText("正文"), Guid.NewGuid(), ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "default");

        var item = LauncherPhraseListItem.FromPhrase(phrase, 3);

        Assert.Equal(3, item.IndexInCategory);
        Assert.Equal(phrase.Title, item.Title);
        Assert.Equal("正文", item.Content);
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
    [InlineData(0, 136)]
    [InlineData(8, 332)]
    [InlineData(20, 520)]
    public void LauncherHeightTracksActualPhraseRowHeightWithoutLargeUnusedViewport(int itemCount, double expectedHeight)
    {
        Assert.Equal(expectedHeight, LauncherWindow.CalculateListHeight(itemCount));
    }

    [Fact]
    public void LauncherPhraseListItem_UsesOnlyFirstTextSegmentForSummary()
    {
        var phrase = new Phrase(
            Guid.NewGuid(),
            "图文话术",
            new PhraseBody(
            [
                PhraseSegment.CreateImage(new PhraseImageReference(Guid.NewGuid(), "image/png", 100, 10, 10)),
                PhraseSegment.CreateText("第一段文字"),
                PhraseSegment.CreateText("第二段文字"),
            ]),
            Guid.NewGuid(), ShortcutMode.None, null, 0, null, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var item = LauncherPhraseListItem.FromPhrase(phrase, 1);

        Assert.Equal("第一段文字", item.Content);
        Assert.Equal("3 段 · 1 图", item.CompositionSummary);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}
