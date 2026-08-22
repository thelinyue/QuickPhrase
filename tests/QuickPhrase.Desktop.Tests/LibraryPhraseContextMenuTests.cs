using System;
using System.IO;
using System.Linq;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Tests.Fakes;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 约束话术行 ContextMenu 必须从 PlacementTarget 读取话术项，避免 ContextMenu 脱离视觉树后命令绑定失效。
/// </summary>
public sealed class LibraryPhraseContextMenuTests
{
    [Fact]
    public void PhraseDeleteMenuItem_UsesPlacementTargetForCommandAndParameter()
    {
        var markup = ReadDesktopFile("Views", "LibraryView.xaml");
        var contextMenu = Slice(markup, "<ContextMenu x:Key=\"PhraseRowContextMenu\"", "</ContextMenu>");

        Assert.Contains(
            "Command=\"{Binding PlacementTarget.DataContext.Owner.DeleteCommand, RelativeSource={RelativeSource AncestorType=ContextMenu}}\"",
            contextMenu,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandParameter=\"{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource AncestorType=ContextMenu}}\"",
            contextMenu,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{Binding PlacementTarget.DataContext.CanManage, RelativeSource={RelativeSource AncestorType=ContextMenu}}\"",
            contextMenu,
            StringComparison.Ordinal);
    }


    [Fact]
    public void PhraseContextMenu_ContainsManagementActionsOnly()
    {
        var markup = ReadDesktopFile("Views", "LibraryView.xaml");
        var contextMenu = Slice(markup, "<ContextMenu x:Key=\"PhraseRowContextMenu\"", "</ContextMenu>");

        Assert.DoesNotContain("Owner.InsertCommand", contextMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("Owner.InsertSendCommand", contextMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("发送到输入区", contextMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("直接发送", contextMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("插入一条话术", contextMenu, StringComparison.Ordinal);
        Assert.Contains("Owner.EditCommand", contextMenu, StringComparison.Ordinal);
        Assert.Contains("Owner.MoveCommand", contextMenu, StringComparison.Ordinal);
        Assert.Contains("Owner.DeleteCommand", contextMenu, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryViewModel_DoesNotExposeDeliveryCommandsOrEvents()
    {
        var type = typeof(PhraseLibraryViewModel);

        Assert.Null(type.GetProperty("InsertCommand"));
        Assert.Null(type.GetProperty("InsertSendCommand"));
        Assert.Null(type.GetEvent("InsertSendRequested"));
    }

    [Fact]
    public async Task DeleteCommand_RemovesPersonalPhraseFromLibrary()
    {
        var categoryId = Guid.NewGuid();
        var phrase = new Phrase(
            Guid.NewGuid(), "待删除话术", PhraseBody.FromText("正文"), categoryId, ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Scope: PhraseScope.Personal);
        var fake = new FakeCommandService();
        fake.Seed(new[] { new Category(categoryId, null, "个人分类", 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) });
        fake.Seed(new[] { phrase });
        var viewModel = new PhraseLibraryViewModel(fake);

        await viewModel.LoadAsync();
        var item = Assert.Single(viewModel.Phrases);

        await viewModel.DeleteCommand.ExecuteAsync(item);

        Assert.Null(await fake.GetPhraseAsync(phrase.Id));
        Assert.Empty(viewModel.Phrases);
        Assert.Equal("已删除", viewModel.StatusMessage);
    }

    [Fact]
    public async Task DeleteCommand_IgnoresEmptyParameter()
    {
        var fake = new FakeCommandService();
        var viewModel = new PhraseLibraryViewModel(fake);

        await viewModel.DeleteCommand.ExecuteAsync(null);

        Assert.Null(viewModel.StatusMessage);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"未找到起始标记：{start}");
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"未找到结束标记：{end}");
        return source[startIndex..(endIndex + end.Length)];
    }

    private static string ReadDesktopFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
        return File.ReadAllText(Path.Combine(new[] { root, "desktop", "QuickPhrase.Desktop" }.Concat(segments).ToArray()));
    }
}
