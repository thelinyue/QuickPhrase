using System.Collections.Immutable;
using QuickPhrase.Core;

namespace QuickPhrase.Architecture.Tests;

/// <summary>
/// 首发图文话术领域契约。测试只依赖 Core，确保段落、分隔符和校验规则不泄漏 WPF 或文件系统类型。
/// </summary>
public sealed class PhraseBodyTests
{
    [Fact]
    public void MixedBody_ExposesOrderedTextProjectionAndMediaCounts()
    {
        var body = new PhraseBody(
            [
                new PhraseSegment(Guid.NewGuid(), PhraseSegmentKind.Text, "请提供订单号", null),
                new PhraseSegment(Guid.NewGuid(), PhraseSegmentKind.Image, null,
                    new PhraseImageReference(Guid.NewGuid(), "image/png", 1024, 800, 600)),
                new PhraseSegment(Guid.NewGuid(), PhraseSegmentKind.Text, "收到后马上查询", null),
            ],
            "---");

        Assert.Equal("请提供订单号\n收到后马上查询", body.TextProjection);
        Assert.Equal(3, body.SegmentCount);
        Assert.Equal(1, body.ImageCount);
        Assert.True(body.RequiresBatchDelivery);
    }

    [Fact]
    public void SeparatorParser_SplitsOnlyWholeTrimmedLines()
    {
        var result = PhraseBodyParser.SplitText(
            "第一段中的---不拆分\r\n  ---  \r\n第二段",
            "---");

        Assert.True(result.IsSuccess);
        Assert.Equal(["第一段中的---不拆分", "第二段"], result.Segments);
    }


    [Theory]
    [InlineData("  分隔  ", "第一段\n 分隔 \n第二段")]
    [InlineData("  ***  ", "第一段\n***\n第二段")]
    public void SeparatorParser_TrimsConfiguredChineseAndSymbolSeparators(string separator, string source)
    {
        var result = PhraseBodyParser.SplitText(source, separator);

        Assert.True(result.IsSuccess);
        Assert.Equal(["第一段", "第二段"], result.Segments);
    }

    [Fact]
    public void SeparatorLengthValidation_UsesTrimmedValueAtOneThirtyTwoAndThirtyThreeCharacters()
    {
        var categoryId = Guid.NewGuid();
        CreatePhraseCommand Command(string separator) => new(
            Guid.NewGuid(), "分隔符边界", PhraseBody.FromText("正文", separator), categoryId, ShortcutMode.None, null);

        Assert.True(PhraseRules.Validate(Command(" x "), out _));
        Assert.True(PhraseRules.Validate(Command("  " + new string('x', 32) + "  "), out _));
        Assert.False(PhraseRules.Validate(Command(new string('x', 33)), out var error));
        Assert.Contains("32", error!.Message);
    }

    [Theory]
    [InlineData("---\n正文")]
    [InlineData("正文\n---")]
    [InlineData("正文\n---\n---\n下一段")]
    public void SeparatorParser_RejectsEmptySegments(string source)
    {
        var result = PhraseBodyParser.SplitText(source, "---");

        Assert.False(result.IsSuccess);
        Assert.Equal("EMPTY_SEGMENT", result.ErrorCode);
    }

    [Fact]
    public void PhraseRules_AcceptsImageOnlyBodyWhenTitleAndCategoryAreValid()
    {
        var body = new PhraseBody(
            [new PhraseSegment(Guid.NewGuid(), PhraseSegmentKind.Image, null,
                new PhraseImageReference(Guid.NewGuid(), "image/jpeg", 2048, 1280, 720))],
            "---");
        var command = new CreatePhraseCommand(
            Guid.NewGuid(), "操作示意图", body, Guid.NewGuid(), ShortcutMode.None, null);

        Assert.True(PhraseRules.Validate(command, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void PhraseRules_RejectsInvalidSeparatorAndExcessiveSegments()
    {
        var invalidSeparator = new CreatePhraseCommand(
            Guid.NewGuid(), "无效分隔符",
            new PhraseBody([PhraseSegment.CreateText("正文")], "   "),
            Guid.NewGuid(), ShortcutMode.None, null);
        var tooManySegments = new CreatePhraseCommand(
            Guid.NewGuid(), "段数超限",
            new PhraseBody(
                Enumerable.Range(0, 21).Select(index => PhraseSegment.CreateText($"第 {index + 1} 段")).ToImmutableArray(),
                "---"),
            Guid.NewGuid(), ShortcutMode.None, null);

        Assert.False(PhraseRules.Validate(invalidSeparator, out var separatorError));
        Assert.Equal("VALIDATION_FAILED", separatorError!.Code);
        Assert.Contains("分隔符", separatorError.Message);
        Assert.False(PhraseRules.Validate(tooManySegments, out var segmentError));
        Assert.Contains("20", segmentError!.Message);
    }

    [Fact]
    public void PhraseRules_RejectsEmptyTextSegmentAndTooManyImages()
    {
        var emptyText = new CreatePhraseCommand(
            Guid.NewGuid(), "空文字段",
            new PhraseBody([new PhraseSegment(Guid.NewGuid(), PhraseSegmentKind.Text, " ", null)], "---"),
            Guid.NewGuid(), ShortcutMode.None, null);
        var tooManyImages = new CreatePhraseCommand(
            Guid.NewGuid(), "图片超限",
            new PhraseBody(
                Enumerable.Range(0, 11)
                    .Select(_ => new PhraseSegment(Guid.NewGuid(), PhraseSegmentKind.Image, null,
                        new PhraseImageReference(Guid.NewGuid(), "image/png", 100, 10, 10)))
                    .ToImmutableArray(),
                "---"),
            Guid.NewGuid(), ShortcutMode.None, null);

        Assert.False(PhraseRules.Validate(emptyText, out var emptyError));
        Assert.Contains("文字段", emptyError!.Message);
        Assert.False(PhraseRules.Validate(tooManyImages, out var imageError));
        Assert.Contains("10", imageError!.Message);
    }
}

