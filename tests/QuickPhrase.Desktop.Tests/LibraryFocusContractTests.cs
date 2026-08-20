using System;
using System.IO;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 话术库焦点行为的源码契约：管理页面加载时不抢占搜索焦点，只有用户主动按 Ctrl+F
/// 或进入已有搜索交互后才把焦点交给搜索框。该测试只约束正式 WPF 链路，不涉及 src/ 原型。
/// </summary>
public sealed class LibraryFocusContractTests
{
    [Fact]
    public void LibraryLoad_DoesNotFocusSearchBox()
    {
        var code = ReadLibraryCode();
        var onLoaded = ExtractMethod(code, "private async void OnLoaded", "private void PhraseList_MouseDoubleClick");

        Assert.DoesNotContain("SearchBox.Focus()", onLoaded, StringComparison.Ordinal);
        Assert.DoesNotContain("Keyboard.Focus(SearchBox)", onLoaded, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryRootKeyHandler_FocusesSearchBoxForCtrlF()
    {
        var code = ReadLibraryCode();
        var rootKeyHandler = ExtractMethod(code, "private void RootLayout_PreviewKeyDown", "private void ConfigureBlankAreaMenu");

        Assert.Contains("Key.F", rootKeyHandler, StringComparison.Ordinal);
        Assert.Contains("ModifierKeys.Control", rootKeyHandler, StringComparison.Ordinal);
        Assert.Contains("SearchBox.Focus()", rootKeyHandler, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", rootKeyHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchHistorySelection_StillRestoresSearchFocus()
    {
        var code = ReadLibraryCode();
        var querySelected = ExtractMethod(code, "private void SearchHistoryPanel_QuerySelected", "private async void SearchHistoryPanel_ClearRequested");

        Assert.Contains("SearchBox.Focus()", querySelected, StringComparison.Ordinal);
        Assert.Contains("Keyboard.Focus(SearchBox)", querySelected, StringComparison.Ordinal);
    }

    [Fact]
    public void PhraseListDoubleClick_UsesActualListItemInsteadOfStaleSelection()
    {
        var code = ReadLibraryCode();
        var handler = ExtractMethod(code, "private void PhraseList_MouseDoubleClick", "private void OnListKeyDown");

        Assert.Contains("e.OriginalSource", handler, StringComparison.Ordinal);
        Assert.Contains("FindAncestor<ListBoxItem>", handler, StringComparison.Ordinal);
        Assert.Contains("DataContext is not PhraseItemViewModel", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("PhraseList.SelectedItem", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryRootMouseHandler_MovesFocusToRootOnlyForBlankArea()
    {
        var code = ReadLibraryCode();
        var handler = ExtractMethod(code, "private void RootLayout_PreviewMouseLeftButtonDown", "private void RootLayout_PreviewMouseRightButtonDown");

        Assert.Contains("e.OriginalSource", handler, StringComparison.Ordinal);
        Assert.Contains("IsLibraryNodeHit", handler, StringComparison.Ordinal);
        Assert.Contains("IsNonBlankInteractiveControl", handler, StringComparison.Ordinal);
        Assert.Contains("Keyboard.Focus(RootLayout)", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryMarkup_RegistersBlankAreaMouseHandlerAndFocusableRoot()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "LibraryView.xaml"));
        var rootStart = markup.IndexOf("<Grid x:Name=\"RootLayout\"", StringComparison.Ordinal);
        Assert.True(rootStart >= 0, "找不到话术库根容器");
        var rootMarkup = markup[rootStart..markup.IndexOf('>', rootStart)];

        Assert.Contains("PreviewMouseLeftButtonDown=\"RootLayout_PreviewMouseLeftButtonDown\"", rootMarkup, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"True\"", rootMarkup, StringComparison.Ordinal);
        Assert.Contains("KeyboardNavigation.IsTabStop=\"False\"", rootMarkup, StringComparison.Ordinal);
    }
    [Fact]
    public void LibraryMarkup_DoesNotDeclareAutoFocus()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "LibraryView.xaml"));

        Assert.DoesNotContain("autoFocus", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AutoFocus", markup, StringComparison.Ordinal);
    }

    private static string ReadLibraryCode()
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "LibraryView.xaml.cs"));
    }

    private static string ExtractMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到方法标记：{startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"找不到方法结束边界：{endMarker}");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}
