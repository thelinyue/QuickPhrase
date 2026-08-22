using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using QuickPhrase.Core;
using QuickPhrase.Desktop.ViewModels;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace QuickPhrase.Desktop.DesignSystem.Components;

/// <summary>
/// 原生 WPF 单一图文编辑器。控件只允许纯文字段落和独占一行的图片块，
/// 文档变化向外发布段草稿而不反向重建 FlowDocument，从而保留光标、选区与 WPF 撤销栈。
/// </summary>
public partial class PhraseRichTextEditor : UserControl
{
    private const string InternalImageDragFormat = "QuickPhrase.Internal.RichImageBlock";
    private bool _suppressDocumentChanged;
    private BlockUIContainer? _selectedImageBlock;
    private Point _dragStart;

    public static readonly DependencyProperty BatchSeparatorProperty = DependencyProperty.Register(
        nameof(BatchSeparator),
        typeof(string),
        typeof(PhraseRichTextEditor),
        new FrameworkPropertyMetadata(PhraseBody.DefaultBatchSeparator, OnDocumentSettingChanged));

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly),
        typeof(bool),
        typeof(PhraseRichTextEditor),
        new FrameworkPropertyMetadata(false, OnIsReadOnlyChanged));

    public static readonly DependencyProperty IsProcessingProperty = DependencyProperty.Register(
        nameof(IsProcessing),
        typeof(bool),
        typeof(PhraseRichTextEditor),
        new FrameworkPropertyMetadata(false, OnIsProcessingChanged));

    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.Register(
        nameof(HasError),
        typeof(bool),
        typeof(PhraseRichTextEditor),
        new FrameworkPropertyMetadata(false, OnHasErrorChanged));

    public PhraseRichTextEditor()
    {
        InitializeComponent();
        DataObject.AddPastingHandler(EditorBox, EditorBox_OnPaste);
    }

    public string BatchSeparator
    {
        get => (string)GetValue(BatchSeparatorProperty);
        set => SetValue(BatchSeparatorProperty, value);
    }

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public bool IsProcessing
    {
        get => (bool)GetValue(IsProcessingProperty);
        set => SetValue(IsProcessingProperty, value);
    }

    public bool HasError
    {
        get => (bool)GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    /// <summary>剪贴板图片由宿主导入媒体库；返回 null 表示导入失败且文档保持不变。</summary>
    public Func<BitmapSource, Task<PhraseSegmentItemViewModel?>>? ClipboardImageImporter { get; set; }

    internal FlowDocument Document => EditorBox.Document;
    internal RichTextBox TextBox => EditorBox;

    internal event EventHandler<PhraseRichDocumentDraft>? DraftChanged;
    internal event EventHandler<string>? ImageProcessingFailed;

    /// <summary>仅在首次加载或显式恢复时重建文档；普通输入绝不调用此方法。</summary>
    public void ResetDocument(IEnumerable<PhraseSegmentItemViewModel> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var items = segments.ToArray();
        var byId = items.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First());
        _suppressDocumentChanged = true;
        try
        {
            EditorBox.Document = PhraseRichDocumentMapper.CreateDocument(
                items.Select(item => item.ToModel()),
                BatchSeparator,
                segment => CreateImageVisual(byId[segment.Id], null));

            foreach (var container in EditorBox.Document.Blocks.OfType<BlockUIContainer>())
            {
                var segment = PhraseRichDocumentMapper.GetImageSegment(container);
                if (segment is not null && byId.TryGetValue(segment.Id, out var item))
                    container.Child = CreateImageVisual(item, container);
            }

            EditorBox.CaretPosition = EditorBox.Document.ContentStart;
            EditorBox.IsReadOnly = IsReadOnly;
            UpdateImageActions();
        }
        finally
        {
            _suppressDocumentChanged = false;
        }

        PublishDraft();
    }

    /// <summary>在当前选区或光标处插入已进入媒体库的图片，整个结构变化属于一个 WPF 撤销单元。</summary>
    public void InsertImage(PhraseSegmentItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsReadOnly || item.Kind != PhraseSegmentKind.Image) return;

        EditorBox.Focus();
        var imageBlock = CreateImageBlock(item);
        EditorBox.BeginChange();
        try
        {
            if (!EditorBox.Selection.IsEmpty)
                EditorBox.Selection.Text = string.Empty;

            var caret = EditorBox.CaretPosition;
            var paragraph = caret.Paragraph;
            if (paragraph is null)
            {
                EditorBox.Document.Blocks.Add(imageBlock);
                var trailing = new Paragraph();
                EditorBox.Document.Blocks.Add(trailing);
                EditorBox.CaretPosition = trailing.ContentStart;
            }
            else
            {
                var before = ReadRange(paragraph.ContentStart, caret);
                var after = ReadRange(caret, paragraph.ContentEnd);
                if (!string.IsNullOrEmpty(before))
                    EditorBox.Document.Blocks.InsertBefore(paragraph, new Paragraph(new Run(before)));
                EditorBox.Document.Blocks.InsertBefore(paragraph, imageBlock);
                var trailing = new Paragraph(new Run(after));
                EditorBox.Document.Blocks.InsertBefore(paragraph, trailing);
                EditorBox.Document.Blocks.Remove(paragraph);
                EditorBox.CaretPosition = trailing.ContentStart;
            }
        }
        finally
        {
            EditorBox.EndChange();
        }

        SelectImageBlock(imageBlock);
        PublishDraft();
    }

    public void FocusEditor() => EditorBox.Focus();

    private BlockUIContainer CreateImageBlock(PhraseSegmentItemViewModel item)
    {
        var block = new BlockUIContainer();
        PhraseRichDocumentMapper.SetImageSegment(block, item.ToModel());
        block.Child = CreateImageVisual(item, block);
        return block;
    }

    private UIElement CreateImageVisual(PhraseSegmentItemViewModel item, BlockUIContainer? owner)
    {
        var image = new Image { Stretch = Stretch.Uniform, MaxHeight = FindDoubleResource("Size.PhraseRichEditor.Image.MaximumHeight", 180) };
        image.SetBinding(Image.SourceProperty, new Binding(nameof(PhraseSegmentItemViewModel.Thumbnail)) { Source = item });

        var delete = new Button
        {
            Content = "×",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Style = TryFindResource("Style.Button.Ghost") as Style,
            Visibility = IsReadOnly ? Visibility.Collapsed : Visibility.Visible,
            Tag = "PhraseRichImageDelete",
        };
        delete.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(PhraseSegmentItemViewModel.DeleteImageAutomationName)) { Source = item });
        delete.Click += (_, _) =>
        {
            if (owner is not null) DeleteImageBlock(owner);
        };

        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        status.SetResourceReference(ForegroundProperty, "Brush.Status.Error");
        AutomationProperties.SetName(status, "图片加载错误");
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(PhraseSegmentItemViewModel.LoadError)) { Source = item });
        if (TryFindResource("BoolToVisibility") is System.Windows.Data.IValueConverter visibilityConverter)
            status.SetBinding(VisibilityProperty, new Binding(nameof(PhraseSegmentItemViewModel.HasLoadError)) { Source = item, Converter = visibilityConverter });

        var grid = new Grid();
        grid.Children.Add(image);
        grid.Children.Add(status);
        grid.Children.Add(delete);

        var border = new Border
        {
            Child = grid,
            Padding = FindThicknessResource("Thickness.SM", new Thickness(8)),
            Margin = FindThicknessResource("Thickness.Gap.Stack.SM", new Thickness(0, 0, 0, 8)),
            BorderThickness = FindThicknessResource("Thickness.Border.Default", new Thickness(1)),
            CornerRadius = FindCornerRadiusResource("Radius.Control", new CornerRadius(4)),
            Focusable = true,
            Tag = "PhraseRichImageContainer",
        };
        border.SetResourceReference(BackgroundProperty, "Brush.Surface.Subtle");
        border.SetResourceReference(BorderBrushProperty, "Brush.Border.Default");
        border.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(PhraseSegmentItemViewModel.ImageAutomationName)) { Source = item });
        AutomationProperties.SetHelpText(border, "Delete 删除；Alt+上方向键或 Alt+下方向键移动。");
        border.PreviewMouseLeftButtonDown += (_, eventArgs) =>
        {
            if (owner is null) return;
            SelectImageBlock(owner);
            _dragStart = eventArgs.GetPosition(EditorBox);
        };
        return border;
    }

    private void EditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressDocumentChanged) PublishDraft();
    }

    private void PublishDraft()
    {
        if (_suppressDocumentChanged) return;
        DraftChanged?.Invoke(this, PhraseRichDocumentMapper.ReadDocument(EditorBox.Document, BatchSeparator));
    }

    private void EditorBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (IsReadOnly || IsProcessing) { e.CancelCommand(); return; }
        try
        {
            var source = e.SourceDataObject;
            if (source.GetDataPresent(DataFormats.Bitmap, autoConvert: true))
            {
                var bitmap = source.GetData(DataFormats.Bitmap, autoConvert: true) as BitmapSource ?? Clipboard.GetImage();
                e.CancelCommand();
                if (bitmap is not null) _ = PasteImageAsync(bitmap);
                else ImageProcessingFailed?.Invoke(this, "无法读取剪贴板图片，请重新复制后再试。");
                return;
            }

            if (!source.GetDataPresent(DataFormats.UnicodeText, autoConvert: true))
            {
                e.CancelCommand();
                return;
            }

            var text = source.GetData(DataFormats.UnicodeText, autoConvert: true) as string ?? string.Empty;
            e.CancelCommand();
            EditorBox.Selection.Text = text;
        }
        catch (Exception)
        {
            e.CancelCommand();
            ImageProcessingFailed?.Invoke(this, "无法读取剪贴板内容，请重新复制后再试。");
        }
    }

    private async Task PasteImageAsync(BitmapSource bitmap)
    {
        var importer = ClipboardImageImporter;
        if (importer is null) return;

        SetCurrentValue(IsProcessingProperty, true);
        try
        {
            var item = await importer(bitmap);
            if (item is not null) InsertImage(item);
        }
        catch (Exception)
        {
            ImageProcessingFailed?.Invoke(this, "剪贴板图片处理失败，请重新复制后再试。");
        }
        finally
        {
            SetCurrentValue(IsProcessingProperty, false);
            EditorBox.Focus();
        }
    }

    private void EditorBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsReadOnly) return;
        if (_selectedImageBlock is not null && e.Key is Key.Delete or Key.Back)
        {
            DeleteImageBlock(_selectedImageBlock);
            e.Handled = true;
            return;
        }

        if (_selectedImageBlock is not null && Keyboard.Modifiers == ModifierKeys.Alt && e.Key is Key.Up or Key.Down)
        {
            MoveImageBlock(_selectedImageBlock, e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
        }
    }

    private void EditorBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Border>(e.OriginalSource as DependencyObject)?.Tag as string != "PhraseRichImageContainer")
            SelectImageBlock(null);
    }

    private void EditorBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (IsReadOnly || _selectedImageBlock is null || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(EditorBox);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var data = new DataObject(InternalImageDragFormat, _selectedImageBlock);
        DragDrop.DoDragDrop(EditorBox, data, DragDropEffects.Move);
    }

    private void EditorBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !IsReadOnly && e.Data.GetDataPresent(InternalImageDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void EditorBox_Drop(object sender, DragEventArgs e)
    {
        if (IsReadOnly || e.Data.GetData(InternalImageDragFormat) is not BlockUIContainer source) return;
        var point = e.GetPosition(EditorBox);
        var target = EditorBox.Document.Blocks.Cast<Block>()
            .FirstOrDefault(block => block != source && block.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top >= point.Y);

        EditorBox.BeginChange();
        try
        {
            EditorBox.Document.Blocks.Remove(source);
            if (target is null) EditorBox.Document.Blocks.Add(source);
            else EditorBox.Document.Blocks.InsertBefore(target, source);
        }
        finally { EditorBox.EndChange(); }
        SelectImageBlock(source);
        PublishDraft();
        e.Handled = true;
    }

    private void DeleteImageBlock(BlockUIContainer block)
    {
        if (IsReadOnly || !EditorBox.Document.Blocks.Contains(block)) return;
        EditorBox.BeginChange();
        try
        {
            EditorBox.Document.Blocks.Remove(block);
            if (EditorBox.Document.Blocks.FirstBlock is null)
                EditorBox.Document.Blocks.Add(new Paragraph());
        }
        finally { EditorBox.EndChange(); }
        SelectImageBlock(null);
        PublishDraft();
    }

    private void MoveImageBlock(BlockUIContainer source, int delta)
    {
        var blocks = EditorBox.Document.Blocks.Cast<Block>().ToList();
        var index = blocks.IndexOf(source);
        var targetIndex = index + delta;
        if (index < 0 || targetIndex < 0 || targetIndex >= blocks.Count) return;
        var target = blocks[targetIndex];

        EditorBox.BeginChange();
        try
        {
            EditorBox.Document.Blocks.Remove(source);
            if (delta < 0) EditorBox.Document.Blocks.InsertBefore(target, source);
            else EditorBox.Document.Blocks.InsertAfter(target, source);
        }
        finally { EditorBox.EndChange(); }
        SelectImageBlock(source);
        PublishDraft();
    }

    private void SelectImageBlock(BlockUIContainer? selected)
    {
        _selectedImageBlock = selected;
        foreach (var block in EditorBox.Document.Blocks.OfType<BlockUIContainer>())
        {
            if (block.Child is not Border border) continue;
            border.SetResourceReference(BorderBrushProperty, ReferenceEquals(block, selected) ? "Brush.Border.Focus" : "Brush.Border.Default");
        }
    }

    private void UpdateImageActions()
    {
        foreach (var container in EditorBox.Document.Blocks.OfType<BlockUIContainer>())
        foreach (var button in FindVisualChildren<Button>(container.Child))
            if (Equals(button.Tag, "PhraseRichImageDelete")) button.Visibility = IsReadOnly ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EditorBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateEditorBorder();

    private void EditorBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateEditorBorder();

    private void UpdateEditorBorder() => EditorBorder.SetResourceReference(
        BorderBrushProperty, EditorBox.IsKeyboardFocusWithin ? "Brush.Border.Focus" : HasError ? "Brush.Status.Error" : "Brush.Border.Default");

    private static void OnDocumentSettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PhraseRichTextEditor)d).PublishDraft();

    private static void OnIsReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (PhraseRichTextEditor)d;
        editor.EditorBox.IsReadOnly = (bool)e.NewValue;
        editor.UpdateImageActions();
    }

    private static void OnHasErrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PhraseRichTextEditor)d).UpdateEditorBorder();

    private static void OnIsProcessingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (PhraseRichTextEditor)d;
        editor.EditorBox.IsEnabled = !(bool)e.NewValue;
    }

    private static string ReadRange(TextPointer start, TextPointer end)
    {
        var text = new TextRange(start, end).Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return text;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T value) return value;
            source = source switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(source),
                FrameworkContentElement content => content.Parent,
                ContentElement content => ContentOperations.GetParent(content),
                _ => LogicalTreeHelper.GetParent(source),
            };
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject? source) where T : DependencyObject
    {
        if (source is null) yield break;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is T value) yield return value;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private double FindDoubleResource(string key, double fallback) => TryFindResource(key) is double value ? value : fallback;
    private Thickness FindThicknessResource(string key, Thickness fallback) => TryFindResource(key) is Thickness value ? value : fallback;
    private CornerRadius FindCornerRadiusResource(string key, CornerRadius fallback) => TryFindResource(key) is CornerRadius value ? value : fallback;
}






