using System.IO;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 闪念窗口扁平化与单行预览的 XAML 回归约束。
/// 测试只读取正式 WPF 项目，不依赖 src/ 原型链路。
/// </summary>
public sealed class LauncherFlatLayoutTests
{
    [Fact]
    public void LauncherUsesNoOuterCardSurfaceInAnyState()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml.cs"));

        Assert.Contains("<Border x:Name=\"LauncherSurface\"", markup);
        Assert.DoesNotContain("Style=\"{StaticResource Style.Card.Elevated}\"", markup);
        Assert.DoesNotContain("ApplyLauncherSurfaceState", codeBehind);
        Assert.DoesNotContain("_isInputOnlyLayout", codeBehind);
    }

    [Fact]
    public void LauncherPreviewShowsSelectedResultAsOneTruncatedRow()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml.cs"));

        Assert.Contains("x:Name=\"PreviewHost\"", markup);
        Assert.Contains("DataContext=\"{Binding SelectedItem, ElementName=ResultsList}\"", markup);
        Assert.Contains("x:Name=\"PreviewIndex\"", markup);
        Assert.Contains("Text=\"{Binding IndexInCategory}\"", markup);
        Assert.Contains("x:Name=\"PreviewTitle\"", markup);
        Assert.Contains("Text=\"{Binding Title}\"", markup);
        Assert.Contains("x:Name=\"PreviewContent\"", markup);
        Assert.Contains("Text=\"{Binding Content}\"", markup);
        Assert.Contains("x:Name=\"PreviewCategory\"", markup);
        Assert.Contains("Text=\"{Binding CategoryPath}\"", markup);
        Assert.Contains("TextWrapping=\"NoWrap\"", markup);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", markup);
        Assert.DoesNotContain("contentLength / 35", codeBehind);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln 所在目录。");
    }
}
