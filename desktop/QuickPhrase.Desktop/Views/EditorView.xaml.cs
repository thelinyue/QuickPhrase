using System.Windows;
using System.Windows.Input;
using QuickPhrase.Core;
using QuickPhrase.Desktop.DesignSystem.Components;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop;

/// <summary>
/// 新建/编辑话术共享表单。正文交互集中在 PhraseRichTextEditor，View 只负责窗口级命令、文件选择和分类创建编排。
/// </summary>
public partial class EditorView : System.Windows.Controls.UserControl
{
    private bool _documentInitialized;
    public EditorViewModel ViewModel { get; }

    public event EventHandler<Phrase>? PhraseSaved;
    public event EventHandler? CloseRequested;
    /// <summary>新建窗口在没有分类时请求就地打开一级分类对话框。</summary>
    public event EventHandler? CreateRootCategoryRequested;

    public EditorView(ICommandService commands, PhraseItemViewModel? existing, Guid? defaultCategoryId = null)
    {
        InitializeComponent();
        ViewModel = new EditorViewModel(commands, existing, defaultCategoryId);
        DataContext = ViewModel;
        RichEditor.ClipboardImageImporter = ViewModel.ImportClipboardImageAsync;
        RichEditor.ImageProcessingFailed += RichEditor_ImageProcessingFailed;
        RichEditor.DraftChanged += RichEditor_DraftChanged;

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
        if (!_documentInitialized)
        {
            RichEditor.ResetDocument(ViewModel.Segments);
            _documentInitialized = true;
        }
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private void RichEditor_ImageProcessingFailed(object? sender, string message) => ViewModel.ErrorMessage = message;

    private void RichEditor_DraftChanged(object? sender, PhraseRichDocumentDraft draft) =>
        ViewModel.ApplyDocumentDraft(draft);

    private async void InsertImage_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsReadOnly || ViewModel.IsBusy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "插入图片",
            Filter = "支持的图片|*.png;*.jpg;*.jpeg;*.bmp|PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|BMP 图片|*.bmp",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;

        var item = await ViewModel.ImportImageItemAsync(dialog.FileName);
        if (item is not null) RichEditor.InsertImage(item);
    }

    private void InsertSeparator_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsReadOnly || ViewModel.IsBusy) return;
        RichEditor.InsertBatchSeparator();
    }

    private void CreateRootCategory_Click(object sender, RoutedEventArgs e) =>
        CreateRootCategoryRequested?.Invoke(this, EventArgs.Empty);

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
