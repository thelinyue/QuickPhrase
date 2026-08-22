using System;
using System.IO;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 话术库只负责管理内容；共享紧凑行可以继续服务 Launcher，但 Library 不得注入任何投递按钮或命令。
/// </summary>
public sealed class LibrarySendButtonMarkupTests
{
    [Fact]
    public void Library_DoesNotEnableOrBindSharedSendAction()
    {
        var library = ReadDesktopFile("Views", "LibraryView.xaml");
        var sharedRows = ReadDesktopFile("DesignSystem", "Styles", "Lists.xaml");
        var phraseList = Slice(library, "<ListBox x:Name=\"PhraseList\"", "</ListBox>");
        var compactRow = Slice(sharedRows, "<DataTemplate x:Key=\"Template.Phrase.CompactRow\"", "</DataTemplate>");

        Assert.DoesNotContain("PhraseListActions.SendCommand", phraseList, StringComparison.Ordinal);
        Assert.DoesNotContain("PhraseListActions.ShowSendButton=\"True\"", phraseList, StringComparison.Ordinal);
        Assert.DoesNotContain("插入并发送", library, StringComparison.Ordinal);
        Assert.DoesNotContain("直接发送", library, StringComparison.Ordinal);
        Assert.DoesNotContain("发送到输入区", library, StringComparison.Ordinal);
        Assert.DoesNotContain("SendBtn", compactRow, StringComparison.Ordinal);
        Assert.DoesNotContain("PhraseListActions", compactRow, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_RemainsTheOnlyHostThatOwnsTheSharedDeliveryCommand()
    {
        var launcherCode = ReadDesktopFile("LauncherWindow.xaml.cs");
        var libraryCode = ReadDesktopFile("Views", "LibraryView.xaml.cs");

        Assert.Contains("PhraseListActions.SetSendCommand(ResultsList", launcherCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SetSendCommand", libraryCode, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestInsertSend", libraryCode, StringComparison.Ordinal);
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
