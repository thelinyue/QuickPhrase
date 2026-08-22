using System.IO;
using QuickPhrase.Desktop.Views.Shared;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 正式 WPF 历史搜索界面的单行布局契约。
/// 测试只读取生产 XAML，避免把 src/ 原型链路误当成正式界面依据。
/// </summary>
public sealed class SearchHistoryViewLayoutTests
{
    [Fact]
    public void SharedHistoryViewUsesIconOnlySingleRowLayout()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop",
            "QuickPhrase.Desktop",
            "Views",
            "Shared",
            "SearchHistoryView.xaml"));

        Assert.DoesNotContain("<TextBlock Text=\"清除全部\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"历史搜索\"", markup, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"清除全部历史搜索\"", markup, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"清除全部历史搜索\"", markup, StringComparison.Ordinal);
        Assert.Contains("<UniformGrid Rows=\"1\"", markup, StringComparison.Ordinal);
        Assert.Contains("Columns=\"5\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Columns=\"{Binding Tag, RelativeSource={RelativeSource AncestorType=ListBox}}\"", markup, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"", markup, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Disabled\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void BothHistorySurfacesRemainBoundToTheirSearchBoxWidth()
    {
        var root = FindRepositoryRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));
        var library = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "LibraryView.xaml"));

        Assert.Contains("Width=\"{Binding ActualWidth, ElementName=QueryBox}\"", launcher, StringComparison.Ordinal);
        Assert.Contains("Width=\"{Binding ActualWidth, ElementName=SearchBox}\"", library, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(479)]
    [InlineData(576)]
    [InlineData(768)]
    [InlineData(1200)]
    public void VisibleHistoryCountIsFixedAtFive(double availableWidth)
    {
        Assert.Equal(5, SearchHistoryView.CalculateVisibleEntryLimit(availableWidth));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}