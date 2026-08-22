using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using QuickPhrase.Desktop.DesignSystem.Components;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 富文本文档与 Core 段数组之间的映射契约。测试固定“文字可换行、图片独占块、分隔符只拆文字段”的产品语义。
/// </summary>
public sealed class PhraseRichDocumentMapperTests
{
    [Fact]
    public void AdjacentTextSegments_RoundTripThroughSeparatorParagraph()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var source = new[]
            {
                PhraseSegment.CreateText("第一行\n第二行"),
                PhraseSegment.CreateText("下一段"),
            };

            var document = PhraseRichDocumentMapper.CreateDocument(source, "---", _ => new Border());
            var draft = PhraseRichDocumentMapper.ReadDocument(document, "---");

            Assert.True(draft.IsValid);
            Assert.Equal(new[] { "第一行\n第二行", "下一段" }, draft.Segments.Select(segment => segment.Text).ToArray());
            Assert.Equal(3, document.Blocks.Count);
        });
    }

    [Fact]
    public void TextImageText_RoundTripPreservesVisualOrderAndImageIdentity()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var image = new PhraseImageReference(Guid.NewGuid(), "image/png", 128, 80, 60);
            var source = new[]
            {
                PhraseSegment.CreateText("前文"),
                PhraseSegment.CreateImage(image),
                PhraseSegment.CreateText("后文"),
            };

            var document = PhraseRichDocumentMapper.CreateDocument(source, "---", _ => new Border());
            var draft = PhraseRichDocumentMapper.ReadDocument(document, "---");

            Assert.True(draft.IsValid);
            Assert.Equal(new[] { PhraseSegmentKind.Text, PhraseSegmentKind.Image, PhraseSegmentKind.Text }, draft.Segments.Select(segment => segment.Kind));
            Assert.Equal(image.AssetId, draft.Segments[1].Image!.AssetId);
            Assert.Equal(4, draft.CharacterCount);
            Assert.Equal(1, draft.ImageCount);
        });
    }

    [Fact]
    public void ConsecutiveImagesAndImageOnly_AreValidWithoutSyntheticTextSegments()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var first = PhraseSegment.CreateImage(new PhraseImageReference(Guid.NewGuid(), "image/png", 10, 1, 1));
            var second = PhraseSegment.CreateImage(new PhraseImageReference(Guid.NewGuid(), "image/jpeg", 20, 2, 2));
            var document = PhraseRichDocumentMapper.CreateDocument([first, second], "---", _ => new Border());

            var draft = PhraseRichDocumentMapper.ReadDocument(document, "---");

            Assert.True(draft.IsValid);
            Assert.Equal(2, draft.Segments.Length);
            Assert.All(draft.Segments, segment => Assert.Equal(PhraseSegmentKind.Image, segment.Kind));
        });
    }

    [Theory]
    [InlineData("---\n正文")]
    [InlineData("正文\n---")]
    [InlineData("正文\n---\n---\n下一段")]
    public void EmptyTextSegment_ReturnsChineseDocumentError(string text)
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var document = new FlowDocument(new Paragraph(new Run(text)));

            var draft = PhraseRichDocumentMapper.ReadDocument(document, "---");

            Assert.False(draft.IsValid);
            Assert.Equal("EMPTY_TEXT_SEGMENT", draft.ErrorCode);
            Assert.Contains("空文字段", draft.ErrorMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SeparatorAdjacentToImage_ReturnsChineseDocumentError()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var image = PhraseSegment.CreateImage(new PhraseImageReference(Guid.NewGuid(), "image/png", 10, 1, 1));
            var document = PhraseRichDocumentMapper.CreateDocument([PhraseSegment.CreateText("正文"), image], "---", _ => new Border());
            document.Blocks.InsertBefore(document.Blocks.LastBlock!, new Paragraph(new Run("---")));

            var draft = PhraseRichDocumentMapper.ReadDocument(document, "---");

            Assert.False(draft.IsValid);
            Assert.Equal("SEPARATOR_ADJACENT_TO_IMAGE", draft.ErrorCode);
            Assert.Contains("图片", draft.ErrorMessage, StringComparison.Ordinal);
        });
    }
    [Fact]
    public void InlineUiObject_IsRejectedInsteadOfBecomingHiddenText()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Run("正文"));
            paragraph.Inlines.Add(new InlineUIContainer(new Button { Content = "不支持" }));
            var draft = PhraseRichDocumentMapper.ReadDocument(new FlowDocument(paragraph), "---");

            Assert.False(draft.IsValid);
            Assert.Equal("UNSUPPORTED_INLINE_CONTENT", draft.ErrorCode);
            Assert.Contains("嵌入内容", draft.ErrorMessage, StringComparison.Ordinal);
        });
    }
    [Theory]
    [InlineData("正文\n")]
    [InlineData("正文\n\n")]
    [InlineData("\n正文")]
    public void TextRoundTrip_PreservesUserLineBreaks(string text)
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var document = PhraseRichDocumentMapper.CreateDocument([PhraseSegment.CreateText(text)], "---", _ => new Border());
            var draft = PhraseRichDocumentMapper.ReadDocument(document, "---");

            Assert.True(draft.IsValid);
            Assert.Equal(text, Assert.Single(draft.Segments).Text);
        });
    }
}
