using System.IO;

namespace QuickPhrase.Desktop.Tests;

/// <summary>话术库正式投递入口的 WPF 契约：左侧发送、单行标题/内容、键盘与双击语义一致。</summary>
public sealed class LibrarySendButtonMarkupTests
{
    [Fact]
    public void MainAndSearchRows_UseSendTitleContentThreeColumnLayout()
    {
        var library = ReadDesktopFile("Views", "LibraryView.xaml");
        var mainRow = Slice(library, "<DataTemplate x:Key=\"PhraseRowTemplate\"", "</DataTemplate>");
        var searchRow = Slice(library, "<DataTemplate x:Key=\"SearchResultRowTemplate\"", "</DataTemplate>");

        foreach (var row in new[] { mainRow, searchRow })
        {
            Assert.Contains("Width=\"{StaticResource Size.Library.PhraseRow.SendColumn}\"", row, StringComparison.Ordinal);
            Assert.Contains("Width=\"{StaticResource Size.Library.PhraseRow.TitleColumn}\"", row, StringComparison.Ordinal);
            Assert.Contains("Command=\"{Binding Owner.SendCommand}\"", row, StringComparison.Ordinal);
            Assert.Contains("ToolTipService.ShowOnDisabled=\"True\"", row, StringComparison.Ordinal);
            Assert.Contains("TextTrimming=\"CharacterEllipsis\"", row, StringComparison.Ordinal);
            Assert.Contains("TextWrapping=\"NoWrap\"", row, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("AccentBar", ReadDesktopFile("DesignSystem", "Styles", "Lists.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("企业微信", library, StringComparison.Ordinal);
    }

    [Fact]
    public void Library_MapsDoubleClickAndKeyboardToExplicitDeliveryModesWithoutButtonBubble()
    {
        var code = ReadDesktopFile("Views", "LibraryView.xaml.cs");
        var xaml = ReadDesktopFile("Views", "LibraryView.xaml");

        Assert.Contains("InsertCommand.ExecuteAsync", code, StringComparison.Ordinal);
        Assert.Contains("SendCommand.ExecuteAsync", code, StringComparison.Ordinal);
        Assert.Contains("Key.Enter", code, StringComparison.Ordinal);
        Assert.Contains("Keyboard.Modifiers.HasFlag(ModifierKeys.Control)", code, StringComparison.Ordinal);
        Assert.Contains("Key.F2", code, StringComparison.Ordinal);
        Assert.Contains("FindAncestor<System.Windows.Controls.Button>", code, StringComparison.Ordinal);
        var dragMouseDown = Slice(code, "private void PhraseItem_PreviewMouseLeftButtonDown", "private void PhraseItem_PreviewMouseMove");
        var clearDragIndex = dragMouseDown.IndexOf("_dragPhraseSource = null;", StringComparison.Ordinal);
        var buttonGuardIndex = dragMouseDown.IndexOf("FindAncestor<System.Windows.Controls.Button>", StringComparison.Ordinal);
        Assert.True(clearDragIndex >= 0 && clearDragIndex < buttonGuardIndex,
            "发送按钮按下前必须清空任何残留的话术拖拽源。");
        Assert.Contains("Placement=\"Bottom\"", xaml, StringComparison.Ordinal);
        Assert.True(xaml.IndexOf("x:Name=\"SearchBox\"", StringComparison.Ordinal) < xaml.IndexOf("x:Name=\"PhraseList\"", StringComparison.Ordinal));
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
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName, "desktop", "QuickPhrase.Desktop" }.Concat(segments).ToArray()));
    }
}
