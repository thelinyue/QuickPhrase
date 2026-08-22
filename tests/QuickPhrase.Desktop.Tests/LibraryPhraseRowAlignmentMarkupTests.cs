using System;
using System.IO;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 约束话术库中所有话术行使用同一左侧起点，避免分类层级重新引入额外缩进。
/// </summary>
public sealed class LibraryPhraseRowAlignmentMarkupTests
{
    [Fact]
    public void PhraseRows_AlignVisibleContentToFirstPrimaryCategory()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "desktop",
            "QuickPhrase.Desktop",
            "Views",
            "LibraryView.xaml"));
        var phraseList = Slice(markup, "<ListBox x:Name=\"PhraseList\"", "</ListBox>");
        var itemStyle = Slice(phraseList, "<ListBox.ItemContainerStyle>", "</ListBox.ItemContainerStyle>");

        Assert.Contains("<Thickness x:Key=\"Thickness.Library.PhraseRow.Horizontal\">16,0</Thickness>", markup, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"{StaticResource Thickness.Library.PhraseRow.Horizontal}\" />", itemStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"{StaticResource Thickness.None}\" />", itemStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSubCategory", itemStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("DataContext.SearchQuery", itemStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("Thickness.Gap.Inline.Before.LG", itemStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("local:PhraseListActions.SendCommand=", phraseList, StringComparison.Ordinal);
        Assert.DoesNotContain("local:PhraseListActions.ShowSendButton=", phraseList, StringComparison.Ordinal);
        Assert.Contains("MouseDoubleClick=\"PhraseList_MouseDoubleClick\"", phraseList, StringComparison.Ordinal);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"未找到起始标记：{start}");

        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"未找到结束标记：{end}");
        return source[startIndex..(endIndex + end.Length)];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
    }
}
