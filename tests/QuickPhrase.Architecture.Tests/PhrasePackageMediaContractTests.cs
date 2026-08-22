using QuickPhrase.Core;

namespace QuickPhrase.Architecture.Tests;

/// <summary>首发图文话术包领域契约测试：只验证有序段、媒体清单和引用闭包，不接触 ZIP 或 Windows 图片解码。</summary>
public sealed class PhrasePackageMediaContractTests
{
    [Fact]
    public void ValidateAcceptsOrderedTextAndImageSegmentsAndRejectsMissingMediaReference()
    {
        var categoryId = Guid.NewGuid();
        var image = new PhraseImageReference(Guid.NewGuid(), "image/png", 68, 1, 1);
        var phrase = new PhrasePackagePhrase(
            Guid.NewGuid(),
            "图文话术",
            new PhraseBody(
                [
                    PhraseSegment.CreateText("请提供订单号"),
                    PhraseSegment.CreateImage(image),
                    PhraseSegment.CreateText("收到后马上查询"),
                ],
                "---"),
            categoryId,
            0);
        var document = CreateDocument(categoryId, phrase, [new PhrasePackageMedia(image, [])]);

        Assert.Empty(PhrasePackagePlanner.Validate(document));

        var missingMedia = document with { Media = [] };
        Assert.Contains(PhrasePackagePlanner.Validate(missingMedia), error => error.Contains("媒体", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildExportDocumentPreservesOrderedBodyAndAddsEachReferencedMediaOnce()
    {
        var categoryId = Guid.NewGuid();
        var image = new PhraseImageReference(Guid.NewGuid(), "image/png", 68, 1, 1);
        var body = new PhraseBody(
            [
                PhraseSegment.CreateText("第一段"),
                PhraseSegment.CreateImage(image),
                PhraseSegment.CreateImage(image),
                PhraseSegment.CreateText("最后一段"),
            ],
            "###");
        var phrase = new Phrase(
            Guid.NewGuid(), "导出图文", body, categoryId, ShortcutMode.None, null, 0, null, 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var snapshot = new PhrasePackageLocalSnapshot(
            [new Category(categoryId, null, "分类", 0, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            [phrase]);

        var document = PhrasePackagePlanner.BuildExportDocument(
            snapshot,
            new PhrasePackageExportSelection(PhrasePackageExportScope.All, "图文包", [], []),
            DateTimeOffset.UnixEpoch);

        var exported = Assert.Single(document.Phrases);
        Assert.Equal("###", exported.Body.BatchSeparator);
        Assert.Equal(
            [PhraseSegmentKind.Text, PhraseSegmentKind.Image, PhraseSegmentKind.Image, PhraseSegmentKind.Text],
            exported.Body.Segments.Select(segment => segment.Kind));
        var media = Assert.Single(document.Media);
        Assert.Equal(image.AssetId, media.Image.AssetId);
        Assert.Equal(1, document.Manifest.MediaCount);
    }

    private static PhrasePackageDocument CreateDocument(
        Guid categoryId,
        PhrasePackagePhrase phrase,
        IReadOnlyList<PhrasePackageMedia> media) =>
        new(
            new PhrasePackageManifest(
                PhrasePackageFormat.Format,
                PhrasePackageFormat.Version,
                Guid.NewGuid(),
                "测试包",
                DateTimeOffset.UtcNow,
                1,
                1,
                media.Count),
            [new PhrasePackageCategory(categoryId, "分类", null, 0)],
            [phrase],
            media);
}
