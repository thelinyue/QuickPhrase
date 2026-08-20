using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Converters;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop;

/// <summary>话术编辑器：标题/正文/分类/固定颜色/保存/取消/删除。纯 WPF，不依赖外部页面运行时。</summary>
public partial class EditorView : System.Windows.Controls.UserControl
{
    public EditorViewModel ViewModel { get; }

    public event EventHandler<Phrase>? PhraseSaved;
    public event EventHandler? CloseRequested;

    public EditorView(ICommandService commands, PhraseItemViewModel? existing, Guid? defaultCategoryId = null)
    {
        InitializeComponent();
        ViewModel = new EditorViewModel(commands, existing, defaultCategoryId);
        DataContext = ViewModel;

        ViewModel.Saved += (_, phrase) =>
        {
            PhraseSaved?.Invoke(this, phrase);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };
        ViewModel.Cancelled += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        ViewModel.Deleted += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        await ViewModel.LoadCategoriesAsync();
        // 分类下拉分组：一级分类 / 二级分类（对齐 design-system.md 5.7 optgroup）
        var grouped = new ListCollectionView(ViewModel.Categories);
        grouped.GroupDescriptions.Add(new PropertyGroupDescription("ParentId", new CategoryLevelConverter()));
        CategoryCombo.ItemsSource = grouped;

        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ViewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.S)
        {
            ViewModel.SaveCommand.Execute(null);
            e.Handled = true;
        }
    }
}

