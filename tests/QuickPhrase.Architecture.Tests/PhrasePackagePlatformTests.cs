using System.IO.Compression;
using System.Text;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

/// <summary>
/// 话术包平台链路测试：覆盖 ZIP/JSON 往返、重复导入、事务回滚和搜索索引提交时序。
/// </summary>
public sealed class PhrasePackagePlatformTests
{
    [Fact]
    public async Task FileStoreRoundTripsOnlyTheV1PackageEntries()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "导出测试"))).Value!;
        var phrase = (await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "导出话术", PhraseBody.FromText("导出正文"), category.Id, ShortcutMode.None, null))).Value!;
        var snapshot = await runtime.CaptureSnapshotAsync();
        var document = PhrasePackagePlanner.BuildExportDocument(
            snapshot,
            new PhrasePackageExportSelection(PhrasePackageExportScope.Phrases, "测试包", [], [phrase.Id]),
            DateTimeOffset.UtcNow);
        var path = Path.Combine(temp.Path, "roundtrip.qphrase");

        await runtime.WriteAsync(path, document);
        var entries = ReadEntryNames(path);
        var restored = await runtime.ReadAsync(path);

        Assert.Equal(["data.json", "manifest.json"], entries);
        Assert.Equal(document.Manifest.Format, restored.Manifest.Format);
        Assert.Single(restored.Phrases);
        Assert.Equal(document.Phrases[0].Title, restored.Phrases[0].Title);
        Assert.Equal(document.Phrases[0].Body.Segments.Select(x => (x.Kind, x.Text, x.Image)), restored.Phrases[0].Body.Segments.Select(x => (x.Kind, x.Text, x.Image)));
    }

    [Fact]
    public void SerializedPackageDataDoesNotContainPerPhraseSeparator()
    {
        var categoryId = Guid.NewGuid();
        var document = new PhrasePackageDocument(
            new PhrasePackageManifest(PhrasePackageFormat.Format, PhrasePackageFormat.Version, Guid.NewGuid(), "包", DateTimeOffset.UtcNow, 1, 1, 0),
            [new PhrasePackageCategory(categoryId, "分类", null, 0)],
            [new PhrasePackagePhrase(Guid.NewGuid(), "标题", PhraseBody.FromText("正文"), categoryId, 0)],
            []);

        var json = Encoding.UTF8.GetString(PhrasePackageJsonSerializer.SerializeData(document));

        Assert.DoesNotContain("batchSeparator", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportCommitRefreshesSearchAndSecondImportSkipsDuplicate()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var packageCategoryId = Guid.NewGuid();
        var packagePhraseId = Guid.NewGuid();
        var package = CreatePackage(
            Guid.NewGuid(),
            new PhrasePackageCategory(packageCategoryId, "平台导入测试", null, 0),
            new PhrasePackagePhrase(packagePhraseId, "  批量导入测试  ", PhraseBody.FromText("正文精确匹配"), packageCategoryId, 0));

        var firstPlan = PhrasePackagePlanner.BuildImportPlan(package, await runtime.CaptureSnapshotAsync());
        var first = await runtime.ImportAsync(firstPlan);
        var search = runtime.Search.Search(new SearchRequest("批量导入测试", 8));

        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(1, first.NewCategoryCount);
        Assert.Equal(1, first.NewPhraseCount);
        Assert.Contains(search.Items, item => item.Phrase.Body.TextProjection == "正文精确匹配");
        Assert.Equal("orange", (await runtime.Phrases.ListAsync()).Single(phrase => phrase.Title == "批量导入测试").ColorKey);

        var secondPlan = PhrasePackagePlanner.BuildImportPlan(package, await runtime.CaptureSnapshotAsync());
        var second = await runtime.ImportAsync(secondPlan);

        Assert.True(second.Succeeded);
        Assert.Equal(0, second.NewCategoryCount);
        Assert.Equal(0, second.NewPhraseCount);
        Assert.Equal(1, second.SkippedDuplicateCount);
    }

    [Fact]
    public async Task FailedBatchRollsBackEarlierCategoryWrites()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var categoryId = Guid.NewGuid();
        var package = CreatePackage(
            Guid.NewGuid(),
            new PhrasePackageCategory(categoryId, "应整体回滚的分类", null, 0),
            new PhrasePackagePhrase(Guid.NewGuid(), "应整体回滚的话术", PhraseBody.FromText("正文"), categoryId, 0));
        var plan = PhrasePackagePlanner.BuildImportPlan(package, await runtime.CaptureSnapshotAsync());
        var duplicateMapping = plan.CategoryMappings[0];
        var invalidPlan = plan with
        {
            CategoryMappings = [duplicateMapping, duplicateMapping],
        };

        var result = await runtime.ImportAsync(invalidPlan);
        var categories = await runtime.Categories.ListAsync();
        var phrases = await runtime.Phrases.ListAsync();

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(categories, category => category.Name == "应整体回滚的分类");
        Assert.DoesNotContain(phrases, phrase => phrase.Title == "应整体回滚的话术");
    }

    [Fact]
    public async Task FileStoreRejectsUnknownZipEntries()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "invalid.qphrase");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", "{}");
            WriteEntry(archive, "data.json", "{\"categories\":[],\"phrases\":[]}");
            WriteEntry(archive, "extra.txt", "not allowed");
        }

        var store = new PhrasePackageFileStore();
        var exception = await Assert.ThrowsAsync<PhrasePackageFileException>(() => store.ReadAsync(path));

        Assert.Equal("PACKAGE_ENTRIES_INVALID", exception.Code);
        Assert.DoesNotContain("extra.txt", exception.Message, StringComparison.Ordinal);
    }

    private static PhrasePackageDocument CreatePackage(Guid packageId, PhrasePackageCategory category, PhrasePackagePhrase phrase) =>
        new(
            new PhrasePackageManifest(
                PhrasePackageFormat.Format,
                PhrasePackageFormat.Version,
                packageId,
                "平台测试包",
                DateTimeOffset.UtcNow,
                1,
                1,
                phrase.Body.ImageCount),
            [category],
            [phrase],
            phrase.Body.ImageCount == 0 ? [] : phrase.Body.Segments
                .Where(segment => segment.Image is not null)
                .Select(segment => new PhrasePackageMedia(segment.Image!, []))
                .ToArray());

    private static string[] ReadEntryNames(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QuickPhrase-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
