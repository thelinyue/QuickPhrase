using System;
using System.IO;
using System.Linq;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 话术库搜索浮层的生产 XAML 契约：主列表与搜索结果必须使用独立显示结构，
/// 搜索状态通过遮罩和上方 Popup 聚焦用户注意力，并复用同一份话术右键菜单。
/// </summary>
public sealed class LibrarySearchOverlayContractTests
{
    [Fact]
    public void LibraryRow_HidesIndex_WhileSearchResultRowOwnsIndexAndCategory()
    {
        var library = ReadDesktopFile("Views", "LibraryView.xaml");
        var rows = ReadDesktopFile("DesignSystem", "Styles", "Lists.xaml");
        var libraryRow = Slice(rows, "<DataTemplate x:Key=\"Template.Phrase.CompactRow\"", "</DataTemplate>");
        var searchRow = Slice(library, "<DataTemplate x:Key=\"SearchResultRowTemplate\"", "</DataTemplate>");

        Assert.DoesNotContain("IndexText", libraryRow, StringComparison.Ordinal);
        Assert.DoesNotContain("IndexInCategory", libraryRow, StringComparison.Ordinal);
        Assert.Contains("SearchResultIndex", searchRow, StringComparison.Ordinal);
        Assert.Contains("CategoryName", searchRow, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.Text.Mono}\"", searchRow, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Title}\"", searchRow, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Content}\"", searchRow, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchOverlay_IsAboveSearchBox_AndMasksOnlyLibraryContent()
    {
        var markup = ReadDesktopFile("Views", "LibraryView.xaml");

        Assert.Contains("x:Name=\"SearchBackdrop\"", markup, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"0\"", Slice(markup, "<Border x:Name=\"SearchBackdrop\"", "/>"), StringComparison.Ordinal);
        Assert.Contains("Grid.RowSpan=\"2\"", Slice(markup, "<Border x:Name=\"SearchBackdrop\"", "/>"), StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource Brush.Overlay}\"", markup, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SearchResultsPopup\"", markup, StringComparison.Ordinal);
        Assert.Contains("Placement=\"Top\"", Slice(markup, "<Popup x:Name=\"SearchResultsPopup\"", "</Popup>"), StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding SearchResults}\"", markup, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"{StaticResource Size.Library.SearchResults.MaximumHeight}\"", markup, StringComparison.Ordinal);
        Assert.Contains("ClearSearch", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryAndSearchRows_ReferenceOneSharedPhraseContextMenu()
    {
        var markup = ReadDesktopFile("Views", "LibraryView.xaml");

        Assert.Contains("x:Key=\"PhraseRowContextMenu\"", markup, StringComparison.Ordinal);
        Assert.True(
            markup.Split("ContextMenu=\"{StaticResource PhraseRowContextMenu}\"", StringSplitOptions.None).Length - 1 >= 2,
            "主列表和搜索结果行都必须引用共享话术右键菜单。");
        Assert.Contains("PlacementTarget.DataContext.Owner.DeleteCommand", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Owner.InsertCommand", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Owner.InsertSendCommand", markup, StringComparison.Ordinal);
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
