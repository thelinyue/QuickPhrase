using System.Collections.Immutable;

namespace QuickPhrase.Core;

/// <summary>将粘贴的整段文字按“独占一行”的普通文本分隔符解析为有序文字段。</summary>
public static class PhraseBodyParser
{
    public static PhraseBodySplitResult SplitText(string source, string separator)
    {
        var normalizedSeparator = PhraseBody.NormalizeBatchSeparator(separator);
        if (normalizedSeparator.Length == 0 || normalizedSeparator.Length > PhraseRules.MaxSeparatorLength)
            return PhraseBodySplitResult.Failure("INVALID_SEPARATOR", $"文字分隔符去除首尾空格后必须为 1–{PhraseRules.MaxSeparatorLength} 个非空白字符。");

        var normalized = (source ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var segments = ImmutableArray.CreateBuilder<string>();
        var current = new List<string>();

        foreach (var line in lines)
        {
            if (!string.Equals(line.Trim(), normalizedSeparator, StringComparison.Ordinal))
            {
                current.Add(line);
                continue;
            }

            if (!TryAppend(current, segments))
                return EmptySegment();
            current.Clear();
        }

        if (!TryAppend(current, segments))
            return EmptySegment();

        return PhraseBodySplitResult.Success(segments.ToImmutable());
    }

    private static bool TryAppend(List<string> lines, ImmutableArray<string>.Builder segments)
    {
        var value = string.Join('\n', lines);
        if (string.IsNullOrWhiteSpace(value)) return false;
        segments.Add(value);
        return true;
    }

    private static PhraseBodySplitResult EmptySegment() =>
        PhraseBodySplitResult.Failure("EMPTY_SEGMENT", "分隔符不能位于开头、结尾或连续出现，否则会产生空文字段。");
}

public sealed record PhraseBodySplitResult(
    bool IsSuccess,
    IReadOnlyList<string> Segments,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static PhraseBodySplitResult Success(IReadOnlyList<string> segments) => new(true, segments, null, null);
    public static PhraseBodySplitResult Failure(string code, string message) => new(false, Array.Empty<string>(), code, message);
}

