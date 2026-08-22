using System.IO;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickPhrase.Desktop.DesignSystem.Components;
using QuickPhrase.Desktop.Tests.Fakes;

namespace QuickPhrase.Desktop.Tests;

/// <summary>单一富文本编辑器的光标插图、撤销和剪贴板临时文件生命周期测试。</summary>
public sealed class PhraseRichTextEditorTests
{
    [Fact]
    public void InsertBatchSeparatorAtCaret_SplitsTextAndRemainsUndoable()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var editor = new PhraseRichTextEditor();
            var window = new System.Windows.Window { Content = editor, Width = 480, Height = 360 };
            window.Show();
            editor.ResetDocument([PhraseSegmentItemViewModel.From(PhraseSegment.CreateText("甲乙"))]);
            var paragraph = Assert.IsType<Paragraph>(editor.Document.Blocks.FirstBlock);
            var run = Assert.IsType<Run>(paragraph.Inlines.FirstInline);
            editor.TextBox.CaretPosition = run.ContentStart.GetPositionAtOffset(1)!;

            var insertMethod = typeof(PhraseRichTextEditor).GetMethod("InsertBatchSeparator", Type.EmptyTypes);
            Assert.NotNull(insertMethod);
            insertMethod!.Invoke(editor, null);

            var readMethod = typeof(PhraseRichDocumentMapper).GetMethod("ReadDocument", [typeof(FlowDocument)]);
            Assert.NotNull(readMethod);
            var draft = Assert.IsType<PhraseRichDocumentDraft>(readMethod!.Invoke(null, [editor.Document]));
            Assert.True(draft.IsValid);
            Assert.Equal(["甲", "乙"], draft.Segments.Select(segment => segment.Text!).ToArray());
            Assert.True(editor.TextBox.CanUndo);

            editor.TextBox.Undo();
            var restored = Assert.IsType<PhraseRichDocumentDraft>(readMethod.Invoke(null, [editor.Document]));
            Assert.True(restored.IsValid);
            Assert.Equal(["甲乙"], restored.Segments.Select(segment => segment.Text!).ToArray());
            window.Close();
        });
    }

    [Fact]
    public void InsertImageAtCaret_SplitsTextIntoVisualOrder_AndUndoRestoresText()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var editor = new PhraseRichTextEditor();
            var window = new System.Windows.Window { Content = editor, Width = 480, Height = 360 };
            window.Show();
            var text = PhraseSegmentItemViewModel.From(PhraseSegment.CreateText("abcd"));
            var image = PhraseSegmentItemViewModel.From(PhraseSegment.CreateImage(
                new PhraseImageReference(Guid.NewGuid(), "image/png", 64, 8, 8)));
            PhraseRichDocumentDraft? latest = null;
            editor.DraftChanged += (_, draft) => latest = draft;
            editor.ResetDocument([text]);
            var paragraph = Assert.IsType<Paragraph>(editor.Document.Blocks.FirstBlock);
            var run = Assert.IsType<Run>(paragraph.Inlines.FirstInline);
            editor.TextBox.CaretPosition = run.ContentStart.GetPositionAtOffset(2)!;

            editor.InsertImage(image);

            Assert.NotNull(latest);
            Assert.Equal(new[] { PhraseSegmentKind.Text, PhraseSegmentKind.Image, PhraseSegmentKind.Text }, latest!.Segments.Select(segment => segment.Kind));
            Assert.Equal("ab", latest.Segments[0].Text);
            Assert.Equal("cd", latest.Segments[2].Text);
            Assert.Equal(image.Image!.AssetId, latest.Segments[1].Image!.AssetId);

            Assert.True(editor.TextBox.CanUndo);
            editor.TextBox.Undo();
            var restored = PhraseRichDocumentMapper.ReadDocument(editor.Document);
            Assert.True(restored.IsValid);
            Assert.Single(restored.Segments);
            Assert.Equal("abcd", restored.Segments[0].Text);
            window.Close();
        });
    }

    [Fact]
    public void DeleteSelectedImage_AndUndoReuseTheSameAssetReference()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var editor = new PhraseRichTextEditor();
            var window = new System.Windows.Window { Content = editor, Width = 480, Height = 360 };
            window.Show();
            editor.ResetDocument([PhraseSegmentItemViewModel.From(PhraseSegment.CreateText("正文"))]);
            var image = PhraseSegmentItemViewModel.From(PhraseSegment.CreateImage(
                new PhraseImageReference(Guid.NewGuid(), "image/png", 64, 8, 8)));
            editor.TextBox.CaretPosition = editor.Document.ContentEnd;
            editor.InsertImage(image);
            var key = new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                System.Windows.PresentationSource.FromVisual(editor.TextBox),
                0,
                System.Windows.Input.Key.Delete)
            {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
            };

            editor.TextBox.RaiseEvent(key);
            Assert.DoesNotContain(PhraseRichDocumentMapper.ReadDocument(editor.Document).Segments, segment => segment.Kind == PhraseSegmentKind.Image);

            editor.TextBox.Undo();
            var restored = PhraseRichDocumentMapper.ReadDocument(editor.Document);
            Assert.Contains(restored.Segments, segment => segment.Image?.AssetId == image.Image!.AssetId);
            window.Close();
        });
    }

    [Fact]
    public void TextChange_PublishesDraftWithoutReplacingFlowDocument()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var editor = new PhraseRichTextEditor();
            editor.ResetDocument([PhraseSegmentItemViewModel.From(PhraseSegment.CreateText("原文"))]);
            var originalDocument = editor.Document;
            var paragraph = Assert.IsType<Paragraph>(originalDocument.Blocks.FirstBlock);
            var run = Assert.IsType<Run>(paragraph.Inlines.FirstInline);
            editor.TextBox.CaretPosition = run.ContentEnd;

            editor.TextBox.CaretPosition.InsertTextInRun("新增");

            Assert.Same(originalDocument, editor.Document);
            var draft = PhraseRichDocumentMapper.ReadDocument(editor.Document);
            Assert.Equal("原文新增", Assert.Single(draft.Segments).Text);
        });
    }

    [Fact]
    public async Task ClipboardImageImport_AlwaysRemovesRandomTemporaryPng()
    {
        var bitmap = WpfTestApplicationHost.Invoke(_ =>
        {
            var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
            source.Freeze();
            return source;
        });
        var before = Directory.GetFiles(Path.GetTempPath(), "QuickPhrase-Clipboard-*.png").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var image = new PhraseImageReference(Guid.NewGuid(), "image/png", 68, 1, 1);
        var fake = new FakeCommandService { NextMediaImportResult = MediaImportResult.Success(image) };
        var viewModel = new EditorViewModel(fake, null);

        var item = await viewModel.ImportClipboardImageAsync(bitmap);

        Assert.NotNull(item);
        var after = Directory.GetFiles(Path.GetTempPath(), "QuickPhrase-Clipboard-*.png").ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(before, after);
    }
}
