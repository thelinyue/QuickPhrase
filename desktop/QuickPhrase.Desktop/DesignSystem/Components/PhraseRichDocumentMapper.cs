using System.Collections.Immutable;
using System.Windows;
using System.Windows.Documents;
using QuickPhrase.Core;

namespace QuickPhrase.Desktop.DesignSystem.Components;

/// <summary>
/// 富文本文档的脱敏草稿。Desktop 只把 FlowDocument 转换为 Core 已定义的有序段，
/// 不在文档中保存路径、文件名或图片二进制，错误也只暴露稳定错误码和中文说明。
/// </summary>
internal sealed record PhraseRichDocumentDraft(
    ImmutableArray<PhraseSegment> Segments,
    int CharacterCount,
    int ImageCount,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsValid => ErrorCode is null;

    public static PhraseRichDocumentDraft Failure(string code, string message) =>
        new([], 0, 0, code, message);
}

/// <summary>
/// 在受控 FlowDocument 与 PhraseBody 段数组之间进行单向映射。
/// 文档顶层仅接受 Paragraph 与携带图片段引用的 BlockUIContainer，因而不会把 RTF、HTML 或任意控件带入持久化模型。
/// </summary>
internal static class PhraseRichDocumentMapper
{
    private static readonly DependencyProperty ImageSegmentProperty = DependencyProperty.RegisterAttached(
        "ImageSegment",
        typeof(PhraseSegment),
        typeof(PhraseRichDocumentMapper));

    public static FlowDocument CreateDocument(
        IEnumerable<PhraseSegment> segments,
        Func<PhraseSegment, UIElement> imageFactory)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(imageFactory);

        var document = new FlowDocument();
        PhraseSegmentKind? previousKind = null;

        foreach (var segment in segments)
        {
            if (segment.Kind == PhraseSegmentKind.Text)
            {
                if (previousKind == PhraseSegmentKind.Text)
                    document.Blocks.Add(new Paragraph(new Run(PhraseBody.DefaultBatchSeparator)));

                document.Blocks.Add(new Paragraph(new Run(segment.Text ?? string.Empty)));
            }
            else if (segment.Kind == PhraseSegmentKind.Image && segment.Image is not null)
            {
                var container = new BlockUIContainer(imageFactory(segment));
                SetImageSegment(container, segment);
                document.Blocks.Add(container);
            }

            previousKind = segment.Kind;
        }

        if (document.Blocks.FirstBlock is null)
            document.Blocks.Add(new Paragraph());

        return document;
    }

    public static PhraseRichDocumentDraft ReadDocument(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var segments = ImmutableArray.CreateBuilder<PhraseSegment>();
        var currentLines = new List<string>();
        var separatorPending = false;

        foreach (var block in document.Blocks)
        {
            if (block is Paragraph paragraph)
            {
                if (ContainsUnsupportedInline(paragraph))
                    return PhraseRichDocumentDraft.Failure("UNSUPPORTED_INLINE_CONTENT", "正文包含不支持的嵌入内容，请只保留纯文字和图片。");
                foreach (var line in ReadParagraphText(paragraph).Split('\n'))
                {
                    if (!string.Equals(line.Trim(), PhraseBody.DefaultBatchSeparator, StringComparison.Ordinal))
                    {
                        currentLines.Add(line);
                        if (separatorPending && currentLines.Any(value => !string.IsNullOrWhiteSpace(value)))
                            separatorPending = false;
                        continue;
                    }

                    if (!TryFlushText(currentLines, segments))
                    {
                        var adjacentToImage = segments.Count > 0 && segments[^1].Kind == PhraseSegmentKind.Image;
                        return adjacentToImage
                            ? PhraseRichDocumentDraft.Failure("SEPARATOR_ADJACENT_TO_IMAGE", "文字分隔符不能紧邻图片，否则会产生空文字段。")
                            : PhraseRichDocumentDraft.Failure("EMPTY_TEXT_SEGMENT", "文字分隔符不能位于开头、结尾或连续出现，否则会产生空文字段。");
                    }

                    separatorPending = true;
                }

                continue;
            }

            if (block is not BlockUIContainer imageBlock || GetImageSegment(imageBlock) is not { Kind: PhraseSegmentKind.Image, Image: not null } imageSegment)
                return PhraseRichDocumentDraft.Failure("UNSUPPORTED_DOCUMENT_BLOCK", "正文包含不支持的内容，请只保留文字和图片。");

            if (separatorPending)
                return PhraseRichDocumentDraft.Failure("SEPARATOR_ADJACENT_TO_IMAGE", "文字分隔符不能紧邻图片，否则会产生空文字段。");

            _ = TryFlushText(currentLines, segments);
            segments.Add(imageSegment);
        }

        if (separatorPending)
            return PhraseRichDocumentDraft.Failure("EMPTY_TEXT_SEGMENT", "文字分隔符不能位于开头、结尾或连续出现，否则会产生空文字段。");

        _ = TryFlushText(currentLines, segments);
        var immutable = segments.ToImmutable();
        return new PhraseRichDocumentDraft(
            immutable,
            immutable.Where(segment => segment.Kind == PhraseSegmentKind.Text).Sum(segment => segment.Text?.Length ?? 0),
            immutable.Count(segment => segment.Kind == PhraseSegmentKind.Image),
            null,
            null);
    }

    internal static void SetImageSegment(BlockUIContainer container, PhraseSegment segment) =>
        container.SetValue(ImageSegmentProperty, segment);

    internal static PhraseSegment? GetImageSegment(BlockUIContainer container) =>
        container.GetValue(ImageSegmentProperty) as PhraseSegment;

    private static bool ContainsUnsupportedInline(Paragraph paragraph) => paragraph.Inlines.Any(ContainsUnsupportedInline);

    private static bool ContainsUnsupportedInline(Inline inline) => inline switch
    {
        Run or LineBreak => false,
        Span span => span.Inlines.Any(ContainsUnsupportedInline),
        _ => true,
    };

    private static string ReadParagraphText(Paragraph paragraph)
    {
        var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return text;
    }

    private static bool TryFlushText(List<string> lines, ImmutableArray<PhraseSegment>.Builder segments)
    {
        var value = string.Join('\n', lines);
        lines.Clear();
        if (string.IsNullOrWhiteSpace(value)) return false;
        segments.Add(PhraseSegment.CreateText(value));
        return true;
    }
}
