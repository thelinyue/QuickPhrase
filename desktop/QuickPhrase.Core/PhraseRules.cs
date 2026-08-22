namespace QuickPhrase.Core;

/// <summary>
/// 话术命令的纯领域校验。它不访问数据库，也不依赖 Windows；媒体文件是否真实存在由持久化层在事务内继续确认。
/// </summary>
public static class PhraseRules
{
    public const int MaxTitleLength = 80;
    public const int MaxSegmentCount = 20;
    public const int MaxImageCount = 10;
    public const int MaxTextLength = 4000;
    public const int MaxSeparatorLength = 32;

    public static bool Validate(CreatePhraseCommand command, out DataError? error) =>
        Validate(command.Title, command.Body, command.CategoryId, out error);

    public static bool Validate(UpdatePhraseCommand command, out DataError? error) =>
        Validate(command.Title, command.Body, command.CategoryId, out error);

    private static bool Validate(string title, PhraseBody? body, Guid categoryId, out DataError? error)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Fail("话术标题不能为空。", out error);

        if (title.Trim().Length > MaxTitleLength)
            return Fail($"话术标题不能超过 {MaxTitleLength} 个字符。", out error);

        if (categoryId == Guid.Empty)
            return Fail("话术必须归属一个分类。", out error);

        if (body is null || body.Segments.IsDefaultOrEmpty)
            return Fail("话术至少需要一个有效内容段。", out error);

        var normalizedSeparator = PhraseBody.NormalizeBatchSeparator(body.BatchSeparator);
        if (normalizedSeparator.Length == 0 || normalizedSeparator.Length > MaxSeparatorLength)
            return Fail($"文字分隔符去除首尾空格后必须为 1–{MaxSeparatorLength} 个非空白字符。", out error);

        if (body.Segments.Length > MaxSegmentCount)
            return Fail($"每条话术最多包含 {MaxSegmentCount} 个内容段。", out error);

        var imageCount = 0;
        var textLength = 0;
        foreach (var segment in body.Segments)
        {
            if (segment.Id == Guid.Empty)
                return Fail("内容段标识无效。", out error);

            if (segment.Kind == PhraseSegmentKind.Text)
            {
                if (string.IsNullOrWhiteSpace(segment.Text) || segment.Image is not null)
                    return Fail("文字段不能为空，且不能同时引用图片。", out error);
                textLength += segment.Text.Length;
                continue;
            }

            if (segment.Kind != PhraseSegmentKind.Image || segment.Text is not null || !IsValidImage(segment.Image))
                return Fail("图片段必须引用有效媒体资产，且不能同时包含文字。", out error);

            imageCount++;
        }

        if (imageCount > MaxImageCount)
            return Fail($"每条话术最多包含 {MaxImageCount} 张图片。", out error);

        if (textLength > MaxTextLength)
            return Fail($"全部文字段合计不能超过 {MaxTextLength} 个字符。", out error);

        error = null;
        return true;
    }

    private static bool IsValidImage(PhraseImageReference? image) =>
        image is not null &&
        image.AssetId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(image.MimeType) &&
        image.ByteLength > 0 &&
        image.PixelWidth > 0 &&
        image.PixelHeight > 0;

    private static bool Fail(string message, out DataError? error)
    {
        error = new DataError("VALIDATION_FAILED", message);
        return false;
    }
}
