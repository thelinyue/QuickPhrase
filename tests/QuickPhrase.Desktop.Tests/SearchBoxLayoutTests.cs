using System.IO;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 搜索框输入起始位置的回归约束：图标只占用固定视觉区域，文本宿主必须从稳定的左侧输入边界开始。
/// 该测试读取正式 WPF 主题资源，不涉及 src/ 原型链路。
/// </summary>
public sealed class SearchBoxLayoutTests
{
    [Fact]
    public void SearchBoxPinsContentHostToTheLeftInputOrigin()
    {
        var root = FindRepositoryRoot();
        var controls = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Themes", "Controls.xaml"));
        var start = controls.IndexOf("<Style x:Key=\"SearchBoxStyle\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 SearchBoxStyle。");
        var end = controls.IndexOf("</Style>", start, StringComparison.Ordinal);
        Assert.True(end > start, "SearchBoxStyle 缺少结束标记。");
        var searchBoxStyle = controls[start..(end + "</Style>".Length)];

        // 输入偏移只能由 PART_ContentHost 的 Margin 提供，避免 Padding 与 Margin 叠加。
        Assert.Contains("<Setter Property=\"Padding\" Value=\"0\" />", searchBoxStyle);
        Assert.Contains("<Setter Property=\"HorizontalContentAlignment\" Value=\"Left\" />", searchBoxStyle);
        Assert.Contains("<Setter Property=\"TextAlignment\" Value=\"Left\" />", searchBoxStyle);
        Assert.Contains("Margin=\"28,0,16,0\"", searchBoxStyle);
        Assert.Contains("HorizontalScrollBarVisibility=\"Hidden\"", searchBoxStyle);
        Assert.Contains("VerticalScrollBarVisibility=\"Hidden\"", searchBoxStyle);
        Assert.DoesNotContain("Margin=\"{TemplateBinding Padding}\"", searchBoxStyle);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}

