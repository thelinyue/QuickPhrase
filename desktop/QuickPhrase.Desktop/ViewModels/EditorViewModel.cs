using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>
/// 话术编辑器视图模型。新建和编辑共用同一套流程，所有持久化读写经 ICommandService 完成。
/// 当前版本不提供话术级快捷键；保存时始终提交 None/null。
/// </summary>
public partial class EditorViewModel : ObservableObject, INavigationGuard
{
    /// <summary>
    /// 固定的 10 色色板。Key 是唯一持久化值，Label 只用于辅助提示，Hex 用于编辑器色块显示。
    /// 不在这里接入系统取色器或任意颜色输入，避免界面文本成为数据契约。
    /// </summary>
    public static readonly IReadOnlyList<ColorKeyOption> ColorKeys =
    [
        new("default", "无颜色", "#FFFFFF"),
        new("orange", "橙色", "#FF8839"),
        new("blue", "蓝色", "#178BFF"),
        new("magenta", "洋红", "#FF73FF"),
        new("purple", "紫色", "#AF60FF"),
        new("green", "绿色", "#41C028"),
        new("pink", "粉色", "#F67E91"),
        new("teal", "青色", "#00A8A8"),
        new("tan", "卡其色", "#CB9563"),
        new("gray", "灰色", "#5C6772"),
    ];

    private readonly ICommandService _commands;
    private readonly Guid _id;
    private readonly bool _isNew;
    private readonly Guid? _defaultCategoryId;
    private long _version;

    private string _baseTitle = "";
    private string _baseContent = "";
    private Guid _baseCategoryId;
    private string _baseColorKey = "default";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private Guid _selectedCategoryId;
    [ObservableProperty] private ObservableCollection<CategoryItem> _categories = new();
    [ObservableProperty] private string _colorKey = "default";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _headerTitle = "编辑话术";
    [ObservableProperty] private bool _canDelete;

    public bool IsNew => _isNew;

    public event EventHandler<Phrase>? Saved;
    public event EventHandler? Cancelled;
    public event EventHandler? Deleted;

    public EditorViewModel(ICommandService commands, PhraseItemViewModel? existing, Guid? defaultCategoryId = null)
    {
        _commands = commands;
        _isNew = existing is null;
        _id = existing?.Id ?? Guid.NewGuid();
        _version = existing?.Version ?? 0;
        _defaultCategoryId = defaultCategoryId;
        _baseCategoryId = existing?.CategoryId ?? Guid.Empty;
        HeaderTitle = _isNew ? "新建话术" : "编辑话术";
        CanDelete = !_isNew;

        if (existing is not null)
        {
            _baseTitle = existing.Title;
            _baseContent = existing.Content;
            _baseColorKey = NormalizeColorKey(existing.ColorKey);
            Title = _baseTitle;
            Content = _baseContent;
            SelectedCategoryId = _baseCategoryId;
            ColorKey = _baseColorKey;
        }
    }

    public async Task LoadCategoriesAsync()
    {
        var list = await _commands.ListCategoriesAsync();
        Categories = new ObservableCollection<CategoryItem>(
            list.Select(c => new CategoryItem(c.Id, c.Name, c.ParentId)));
        if (_defaultCategoryId.HasValue && Categories.Any(c => c.Id == _defaultCategoryId.Value))
            SelectedCategoryId = _defaultCategoryId.Value;
        else if (_isNew && SelectedCategoryId == Guid.Empty && Categories.Count > 0)
            SelectedCategoryId = Categories[0].Id;
    }

    public bool HasUnsavedChanges =>
        Title != _baseTitle || Content != _baseContent || SelectedCategoryId != _baseCategoryId || NormalizeColorKey(ColorKey) != _baseColorKey;

    public void DiscardChanges()
    {
        Title = _baseTitle;
        Content = _baseContent;
        SelectedCategoryId = _baseCategoryId;
        ColorKey = _baseColorKey;
    }

    public async Task SaveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var colorKey = NormalizeColorKey(ColorKey);
            RepositoryResult<Phrase> result;
            if (_isNew)
            {
                result = await _commands.CreatePhraseAsync(new CreatePhraseCommand(
                    _id, Title.Trim(), Content, SelectedCategoryId,
                    ShortcutMode.None, null, colorKey));
            }
            else
            {
                result = await _commands.UpdatePhraseAsync(new UpdatePhraseCommand(
                    _id, _version, Title.Trim(), Content, SelectedCategoryId,
                    ShortcutMode.None, null, colorKey));
            }
            if (result.IsSuccess && result.Value is not null)
            {
                var phrase = result.Value;
                _version = phrase.Version;
                _baseTitle = phrase.Title;
                _baseContent = phrase.Content;
                _baseCategoryId = phrase.CategoryId;
                _baseColorKey = NormalizeColorKey(phrase.ColorKey);
                ColorKey = _baseColorKey;
                Saved?.Invoke(this, phrase);
            }
            else
            {
                ErrorMessage = result.Error?.Message ?? "保存失败。";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string NormalizeColorKey(string? colorKey) =>
        string.IsNullOrWhiteSpace(colorKey) ? "default" : colorKey.Trim().ToLowerInvariant();

    [RelayCommand]
    private async Task Save() => await SaveAsync();

    [RelayCommand]
    private void Cancel()
    {
        DiscardChanges();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (_isNew) { DiscardChanges(); Cancelled?.Invoke(this, EventArgs.Empty); return; }
        var ok = await _commands.DeletePhraseAsync(_id, _version);
        if (ok) { DiscardChanges(); Deleted?.Invoke(this, EventArgs.Empty); }
        else ErrorMessage = "删除失败。";
    }

}

/// <summary>固定色板选项：Key 持久化，Label 辅助说明，Brush 仅供 WPF 色块渲染。</summary>
public sealed record ColorKeyOption(string Key, string Label, string Hex)
{
    public SolidColorBrush Brush { get; } = CreateBrush(Hex);

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
