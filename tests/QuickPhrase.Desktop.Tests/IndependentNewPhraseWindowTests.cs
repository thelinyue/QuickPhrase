using System;
using System.IO;
using System.Threading.Tasks;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Tests.Fakes;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 独立新建话术窗口的回归契约：高频入口不能重新把编辑器塞回话术库，
/// 且分类缺失时必须留在当前窗口内完成显式创建流程。
/// </summary>
public sealed class IndependentNewPhraseWindowTests
{
    [Fact]
    public void NewPhraseWindow_IsIndependentAndApplicationManaged()
    {
        var root = FindRepoRoot();
        var controller = Read(root, "desktop", "QuickPhrase.Desktop", "ApplicationController.cs");
        var mainWindow = Read(root, "desktop", "QuickPhrase.Desktop", "MainWindow.xaml.cs");
        var window = Read(root, "desktop", "QuickPhrase.Desktop", "NewPhraseWindow.xaml");

        Assert.Contains("private NewPhraseWindow? _newPhraseWindow;", controller, StringComparison.Ordinal);
        Assert.Contains("public void OpenNewPhrase(Guid? defaultCategoryId = null)", controller, StringComparison.Ordinal);
        Assert.Contains("new NewPhraseWindow(_commands, defaultCategoryId)", controller, StringComparison.Ordinal);
        Assert.Contains("if (_newPhraseWindow is { IsVisible: true })", controller, StringComparison.Ordinal);
        Assert.Contains("_newPhraseWindow.WindowState == WindowState.Minimized", controller, StringComparison.Ordinal);
        Assert.Contains("ExecuteTrayAction(() => OpenNewPhrase())", controller, StringComparison.Ordinal);
        Assert.Contains("OpenNewPhrase();", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenManagement(\"editor\")", controller, StringComparison.Ordinal);
        Assert.Contains("_newPhraseWindow is { IsVisible: true }", controller, StringComparison.Ordinal);

        Assert.Contains("WindowStartupLocation=\"CenterScreen\"", window, StringComparison.Ordinal);
        Assert.Contains("Width=\"{StaticResource Size.PhraseEditorWindow.Width}\"", window, StringComparison.Ordinal);
        Assert.Contains("Height=\"{StaticResource Size.PhraseEditorWindow.Height}\"", window, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"{StaticResource Size.PhraseEditorWindow.MinimumWidth}\"", window, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"{StaticResource Size.PhraseEditorWindow.MinimumHeight}\"", window, StringComparison.Ordinal);
        Assert.Contains("Width = (double)FindResource(\"Size.PhraseEditorWindow.Width\")", mainWindow, StringComparison.Ordinal);
        Assert.Contains("MinWidth = (double)FindResource(\"Size.PhraseEditorWindow.MinimumWidth\")", mainWindow, StringComparison.Ordinal);
        Assert.Contains("SizeToContent = SizeToContent.Manual", mainWindow, StringComparison.Ordinal);
        Assert.Contains("WindowStartupLocation = WindowStartupLocation.CenterOwner", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.Window.Shell}\"", window, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.Surface.ContentRegion}\"", window, StringComparison.Ordinal);
        Assert.Contains("_management?.RefreshPhrase(phrase);", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Owner=", window, StringComparison.Ordinal);
        Assert.Contains("RequestNewPhrase(null)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RequestNewPhrase(c.Id)", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowNewPhraseAsync", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorView_ProvidesInWindowCategoryCreationWhenNoCategoriesExist()
    {
        var root = FindRepoRoot();
        var markup = Read(root, "desktop", "QuickPhrase.Desktop", "Views", "EditorView.xaml");
        var code = Read(root, "desktop", "QuickPhrase.Desktop", "Views", "EditorView.xaml.cs");
        var windowCode = Read(root, "desktop", "QuickPhrase.Desktop", "NewPhraseWindow.xaml.cs");

        Assert.Contains("还没有可用分类，请先创建一个一级分类。", markup, StringComparison.Ordinal);
        Assert.Contains("Click=\"CreateRootCategory_Click\"", markup, StringComparison.Ordinal);
        Assert.Contains("CreateRootCategoryRequested", code, StringComparison.Ordinal);
        Assert.Contains("new CategoryDialog(_commands) { Owner = this }", windowCode, StringComparison.Ordinal);
        Assert.Contains("LoadCategoriesAsync(categoryId)", windowCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReloadCategories_PrefersNewlyCreatedCategory()
    {
        var firstId = Guid.NewGuid();
        var createdId = Guid.NewGuid();
        var commands = new FakeCommandService();
        await commands.CreateCategoryAsync(new CreateCategoryCommand(firstId, "已有分类"));

        var viewModel = new EditorViewModel(commands, null);
        await viewModel.LoadCategoriesAsync();
        Assert.True(viewModel.HasCategories);
        Assert.Equal(firstId, viewModel.SelectedCategoryId);

        await commands.CreateCategoryAsync(new CreateCategoryCommand(createdId, "刚创建的分类"));
        await viewModel.LoadCategoriesAsync(createdId);

        Assert.Equal(createdId, viewModel.SelectedCategoryId);
        Assert.Equal(createdId, viewModel.SelectedPrimaryCategory!.Id);
        Assert.Null(viewModel.SelectedSecondaryCategory!.CategoryId);
    }

    private static string Read(string root, params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { root }.Concat(segments).ToArray()));

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "QuickPhrase.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
    }
}
