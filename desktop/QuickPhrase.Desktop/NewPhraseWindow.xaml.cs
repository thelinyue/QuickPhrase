using System.Windows;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop;

/// <summary>
/// 应用级的新建话术窗口。它不依附话术库主窗口，所有入口均通过 ApplicationController
/// 激活同一个实例；无分类时在窗口内创建一级分类，再回填编辑器，避免跳转到话术库。
/// </summary>
public partial class NewPhraseWindow : Window
{
    private readonly ICommandService _commands;
    private readonly EditorView _editorView;

    public event EventHandler<Phrase>? PhraseSaved;

    public NewPhraseWindow(ICommandService commands, Guid? defaultCategoryId = null)
    {
        InitializeComponent();
        _commands = commands;
        _editorView = new EditorView(commands, existing: null, defaultCategoryId);
        _editorView.PhraseSaved += EditorView_PhraseSaved;
        _editorView.CloseRequested += EditorView_CloseRequested;
        _editorView.CreateRootCategoryRequested += EditorView_CreateRootCategoryRequested;
        EditorHost.Content = _editorView;
    }

    private void EditorView_PhraseSaved(object? sender, Phrase phrase) => PhraseSaved?.Invoke(this, phrase);

    private void EditorView_CloseRequested(object? sender, EventArgs e) => Close();

    /// <summary>
    /// 分类是话术的必填领域属性。没有分类时只允许用户在当前新建窗口显式创建一级分类；
    /// 成功后以数据库返回的真实 ID 重新加载并选中，避免隐式默认分类或错误归类。
    /// </summary>
    private async void EditorView_CreateRootCategoryRequested(object? sender, EventArgs e)
    {
        var dialog = new CategoryDialog(_commands) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.CreatedCategoryId is not Guid categoryId) return;

        try
        {
            await _editorView.ViewModel.LoadCategoriesAsync(categoryId);
        }
        catch (Exception exception)
        {
            _editorView.ViewModel.ErrorMessage = $"分类加载失败：{exception.Message}";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _editorView.PhraseSaved -= EditorView_PhraseSaved;
        _editorView.CloseRequested -= EditorView_CloseRequested;
        _editorView.CreateRootCategoryRequested -= EditorView_CreateRootCategoryRequested;
        base.OnClosed(e);
    }
}
