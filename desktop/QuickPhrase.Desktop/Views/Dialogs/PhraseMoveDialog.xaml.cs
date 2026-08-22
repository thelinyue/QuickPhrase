using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop;

/// <summary>把话术移动到另一个分类。仅变更 CategoryId，其余图文正文和属性原样回写。</summary>
public partial class PhraseMoveDialog : Window
{
    private readonly ICommandService _commands;
    private readonly PhraseItemViewModel _item;

    public ObservableCollection<CategoryItem> Categories { get; } = new();

    /// <summary>移动成功后由仓储层返回的最新话术，供调用方刷新当前筛选列表。</summary>
    public Phrase? MovedPhrase { get; private set; }

    public PhraseMoveDialog(ICommandService commands, PhraseItemViewModel item)
    {
        InitializeComponent();
        DataContext = this;
        _commands = commands;
        _item = item;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var cats = await _commands.ListCategoriesAsync();
        foreach (var c in cats) Categories.Add(new CategoryItem(c.Id, c.Name, c.ParentId));
        CategoryCombo.SelectedValue = _item.CategoryId;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryCombo.SelectedValue is not Guid target) { ErrorText.Text = "请选择目标分类"; return; }
        var phrase = _item.ToPhrase();
        var command = new UpdatePhraseCommand(
            phrase.Id, phrase.Version, phrase.Title, phrase.Body, target,
            phrase.ShortcutMode, phrase.Shortcut?.Display, phrase.ColorKey);
        var result = await _commands.UpdatePhraseAsync(command);
        if (result.IsSuccess && result.Value is { } movedPhrase)
        {
            MovedPhrase = movedPhrase;
            DialogResult = true;
        }
        else ErrorText.Text = result.Error?.Message ?? "移动失败，未返回最新话术数据。";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
