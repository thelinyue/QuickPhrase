using System;
using System.IO;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 右键移动话术的回归契约：对话框必须把仓储层返回的最新话术交还给主窗口，
/// 不能继续使用移动前的 CategoryId 刷新话术库。
/// </summary>
public sealed class PhraseMoveRefreshContractTests
{
    [Fact]
    public void SuccessfulMove_RefreshesLibraryWithPersistedPhrase()
    {
        var root = FindRepoRoot();
        var dialog = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "Dialogs", "PhraseMoveDialog.xaml.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("public Phrase? MovedPhrase", dialog, StringComparison.Ordinal);
        Assert.Contains("MovedPhrase = movedPhrase", dialog, StringComparison.Ordinal);
        Assert.Contains("_libraryView?.RefreshMovedPhrase(movedPhrase);", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("_libraryView?.RefreshPhrase(item.ToPhrase());", mainWindow, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 QuickPhrase.sln，无法读取移动刷新契约源文件。");
    }
}
