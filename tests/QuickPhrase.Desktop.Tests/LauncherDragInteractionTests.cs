using System;
using System.IO;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 闪念窗口鼠标拖动的源码契约，确保无边框窗口的拖动入口不会覆盖搜索和选择交互。
/// </summary>
public sealed class LauncherDragInteractionTests
{
    [Fact]
    public void LauncherSurface_RegistersDragPreviewHandlers_AndQueryBoxRegistersBlankAreaHandler()
    {
        var markup = ReadDesktopFile("LauncherWindow.xaml");
        var surfaceStart = markup.IndexOf("<Border x:Name=\"LauncherSurface\"", StringComparison.Ordinal);
        var queryStart = markup.IndexOf("<TextBox x:Name=\"QueryBox\"", StringComparison.Ordinal);

        Assert.True(surfaceStart >= 0, "找不到闪念外层浮层。");
        Assert.True(queryStart >= 0, "找不到闪念搜索框。");

        var surfaceMarkup = markup[surfaceStart..markup.IndexOf('>', surfaceStart)];
        var queryMarkup = markup[queryStart..markup.IndexOf('>', queryStart)];
        Assert.Contains("PreviewMouseLeftButtonDown=\"LauncherSurface_PreviewMouseLeftButtonDown\"", surfaceMarkup, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseMove=\"LauncherSurface_PreviewMouseMove\"", surfaceMarkup, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonUp=\"LauncherSurface_PreviewMouseLeftButtonUp\"", surfaceMarkup, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonDown=\"QueryBox_PreviewMouseLeftButtonDown\"", queryMarkup, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherDrag_UsesSystemThresholdAndPreservesTextSelectionBoundary()
    {
        var code = ReadDesktopFile("LauncherWindow.xaml.cs");

        Assert.Contains("SystemParameters.MinimumHorizontalDragDistance", code, StringComparison.Ordinal);
        Assert.Contains("SystemParameters.MinimumVerticalDragDistance", code, StringComparison.Ordinal);
        Assert.Contains("QueryBox.GetCharacterIndexFromPoint", code, StringComparison.Ordinal);
        Assert.Contains("DragMove()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherDrag_ExcludesInteractiveControlAncestors()
    {
        var code = ReadDesktopFile("LauncherWindow.xaml.cs");

        Assert.Contains("IsInteractiveDragSource", code, StringComparison.Ordinal);
        Assert.Contains("Control", code, StringComparison.Ordinal);
        Assert.Contains("VisualTreeHelper.GetParent", code, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherDrag_HandlesTextContentSourcesWhenWalkingParents()
    {
        var code = ReadDesktopFile("LauncherWindow.xaml.cs");

        Assert.Contains("FrameworkContentElement", code, StringComparison.Ordinal);
        Assert.Contains("ContentOperations.GetParent", code, StringComparison.Ordinal);
    }

    private static string ReadDesktopFile(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "desktop", "QuickPhrase.Desktop", fileName));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln 所在目录。");
    }
}
