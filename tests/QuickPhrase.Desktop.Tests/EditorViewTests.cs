using System;
using System.IO;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 验证编辑器成功保存后的窗口生命周期契约，避免成功写入后仍停留在新建页面。
/// </summary>
public class EditorViewTests
{
    [Fact]
    public void SavedHandler_RequestsEditorClose()
    {
        var root = FindRepoRoot();
        var code = File.ReadAllText(Path.Combine(
            root, "desktop", "QuickPhrase.Desktop", "Views", "EditorView.xaml.cs"));
        var savedHandlerStart = code.IndexOf(
            "ViewModel.Saved += (_, phrase) =>", StringComparison.Ordinal);
        var cancelledHandlerStart = code.IndexOf(
            "ViewModel.Cancelled +=", savedHandlerStart, StringComparison.Ordinal);

        Assert.True(savedHandlerStart >= 0, "找不到保存成功事件处理器。");
        Assert.True(cancelledHandlerStart > savedHandlerStart, "保存成功事件处理器边界异常。");
        var savedHandler = code[savedHandlerStart..cancelledHandlerStart];
        Assert.Contains("PhraseSaved?.Invoke(this, phrase);", savedHandler, StringComparison.Ordinal);
        Assert.Contains("CloseRequested?.Invoke(this, EventArgs.Empty);", savedHandler, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "QuickPhrase.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
    }
}
