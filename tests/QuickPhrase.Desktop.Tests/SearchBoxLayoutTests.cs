using System.IO;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 搜索框输入起始位置的回归约束：搜索输入框复用基础输入模板，
/// 文本宿主必须从统一的语义内边距开始，不能再恢复页面或旧样式中的固定偏移。
/// </summary>
public sealed class SearchBoxLayoutTests
{
    [Fact]
    public void SearchBoxPinsContentHostToTheLeftInputOrigin()
    {
        var root = FindRepositoryRoot();
        var inputs = File.ReadAllText(Path.Combine(
            root,
            "desktop",
            "QuickPhrase.Desktop",
            "DesignSystem",
            "Styles",
            "Inputs.xaml"));

        var baseStyle = ExtractStyle(inputs, "Style.Input.Base");
        var searchStyle = ExtractStyle(inputs, "Style.Input.Search");

        Assert.Contains(
            "BasedOn=\"{StaticResource Style.Input.Base}\"",
            searchStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"Padding\" Value=\"{StaticResource Thickness.Control.Input}\" />",
            baseStyle,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PART_ContentHost\"", baseStyle, StringComparison.Ordinal);
        Assert.Contains(
            "Margin=\"{TemplateBinding Padding}\"",
            baseStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "HorizontalScrollBarVisibility=\"Hidden\"",
            baseStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "VerticalScrollBarVisibility=\"Hidden\"",
            baseStyle,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SearchBoxStyle", inputs, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"28,0,16,0\"", inputs, StringComparison.Ordinal);
    }

    private static string ExtractStyle(string xaml, string key)
    {
        var marker = $"<Style x:Key=\"{key}\"";
        var start = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {key}。");

        var selfClosingEnd = xaml.IndexOf("/>", start, StringComparison.Ordinal);
        var openingEnd = xaml.IndexOf('>', start);
        if (selfClosingEnd >= 0 && selfClosingEnd == openingEnd - 1)
            return xaml[start..(selfClosingEnd + 2)];

        var end = xaml.IndexOf("</Style>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{key} 缺少结束标记。");
        return xaml[start..(end + "</Style>".Length)];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}
