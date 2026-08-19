using System;
using System.Windows;
using System.Windows.Controls;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop;

/// <summary>新建或重命名分类对话框。重命名时传入已有 Category，调用 category.rename；新建时 optional parentId 用于创建二级分类。</summary>
public partial class CategoryDialog : Window
{
    private readonly ICommandService _commands;
    private readonly Category? _existing;
    private readonly Guid? _parentId;

    public CategoryDialog(ICommandService commands, Category? existing = null, Guid? parentId = null)
    {
        InitializeComponent();
        _commands = commands;
        _existing = existing;
        _parentId = parentId;
        Title = existing is null ? (parentId.HasValue ? "新建二级分类" : "新建分类") : "重命名分类";
        if (existing is not null) NameBox.Text = existing.Name;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { ErrorText.Text = "请输入分类名称"; NameBox.Focus(); return; }

        SetBusy(true);
        try
        {
            RepositoryResult<Category> result = _existing is null
                ? await _commands.CreateCategoryAsync(new CreateCategoryCommand(Guid.NewGuid(), name, _parentId))
                : await _commands.RenameCategoryAsync(new RenameCategoryCommand(_existing.Id, _existing.Version, name, _existing.SortOrder));
            if (result.IsSuccess) DialogResult = true;
            else ErrorText.Text = result.Error?.Message ?? "操作失败";
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"操作异常：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SetBusy(bool busy)
    {
        OkButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        NameBox.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }
}
