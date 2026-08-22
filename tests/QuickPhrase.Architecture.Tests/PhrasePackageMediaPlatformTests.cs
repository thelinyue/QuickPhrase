using System.IO.Compression;
using System.Text;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

/// <summary>首发图文 .qphrase 平台测试：覆盖媒体往返、旧包拒绝和 ZIP 输入边界。</summary>
public sealed class PhrasePackageMediaPlatformTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task ExportAndImportRoundTripPreservesOrderedSegmentsAndManagedMediaCopy()
    {
        using var source = new TemporaryDirectory();
        using var target = new TemporaryDirectory();
        await using var sourceRuntime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(source.Path));
        var imagePath = Path.Combine(source.Path, "source.png");
        await File.WriteAllBytesAsync(imagePath, OnePixelPng);
        var imported = await sourceRuntime.MediaAssets.ImportAsync(imagePath);
        Assert.True(imported.IsSuccess, imported.ErrorMessage);
        var category = (await sourceRuntime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "图文"))).Value!;
        var body = new PhraseBody(
            [PhraseSegment.CreateText("第一段"), PhraseSegment.CreateImage(imported.Image!), PhraseSegment.CreateText("第三段")]);
        var phrase = (await sourceRuntime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "图文导出", body, category.Id, ShortcutMode.None, null))).Value!;
        var package = PhrasePackagePlanner.BuildExportDocument(
            await sourceRuntime.CaptureSnapshotAsync(),
            new PhrasePackageExportSelection(PhrasePackageExportScope.Phrases, "图文包", [], [phrase.Id]),
            DateTimeOffset.UtcNow);
        var path = Path.Combine(source.Path, "media.qphrase");

        await sourceRuntime.WriteAsync(path, package);
        Assert.Contains(ReadEntryNames(path), name => name.StartsWith("media/", StringComparison.Ordinal));

        await using var targetRuntime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(target.Path));
        var restored = await targetRuntime.ReadAsync(path);
        var result = await targetRuntime.ImportAsync(PhrasePackagePlanner.BuildImportPlan(restored, await targetRuntime.CaptureSnapshotAsync()));
        var saved = Assert.Single(await targetRuntime.Phrases.ListAsync());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal([PhraseSegmentKind.Text, PhraseSegmentKind.Image, PhraseSegmentKind.Text], saved.Body.Segments.Select(x => x.Kind));
        Assert.NotNull(await targetRuntime.MediaAssets.ReadAsync(saved.Body.Segments[1].Image!.AssetId));
    }

    [Fact]
    public async Task ReadRejectsOldDevelopmentPlainTextPackage()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "old.qphrase");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteText(archive, "manifest.json", "{\"format\":\"QuickPhrase.PhrasePackage\",\"formatVersion\":1,\"packageId\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"旧包\",\"createdAt\":\"2026-01-01T00:00:00Z\",\"phraseCount\":1,\"categoryCount\":1}");
            WriteText(archive, "data.json", "{\"categories\":[{\"id\":\"22222222-2222-2222-2222-222222222222\",\"name\":\"分类\",\"parentId\":null,\"sortOrder\":0}],\"phrases\":[{\"id\":\"33333333-3333-3333-3333-333333333333\",\"title\":\"旧话术\",\"content\":\"旧正文\",\"categoryId\":\"22222222-2222-2222-2222-222222222222\",\"sortOrder\":0}]}");
        }

        var error = await Assert.ThrowsAsync<PhrasePackageFileException>(() => new PhrasePackageFileStore().ReadAsync(path));

        Assert.Equal("PACKAGE_JSON_INVALID", error.Code);
    }

    [Theory]
    [InlineData("media/../escape.png", "PACKAGE_ENTRIES_INVALID")]
    [InlineData("media/11111111111111111111111111111111.gif", "PACKAGE_MEDIA_EXTENSION_INVALID")]
    public async Task ReadRejectsTraversalAndUnsupportedMediaExtensions(string entryName, string code)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "invalid.qphrase");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteText(archive, "manifest.json", "{}");
            WriteText(archive, "data.json", "{}");
            WriteBytes(archive, entryName, OnePixelPng);
        }

        var error = await Assert.ThrowsAsync<PhrasePackageFileException>(() => new PhrasePackageFileStore().ReadAsync(path));

        Assert.Equal(code, error.Code);
        Assert.DoesNotContain(entryName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRejectsMediaWhoseActualFormatDoesNotMatchExtension()
    {
        using var temp = new TemporaryDirectory();
        var assetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var path = Path.Combine(temp.Path, "mismatch.qphrase");
        var document = CreateImageDocument(assetId, "image/jpeg", OnePixelPng.Length, 1, 1);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteBytes(archive, "manifest.json", PhrasePackageJsonSerializer.SerializeManifest(document.Manifest));
            WriteBytes(archive, "data.json", PhrasePackageJsonSerializer.SerializeData(document));
            WriteBytes(archive, $"media/{assetId:N}.jpg", OnePixelPng);
        }

        var error = await Assert.ThrowsAsync<PhrasePackageFileException>(() => new PhrasePackageFileStore().ReadAsync(path));

        Assert.Equal("PACKAGE_MEDIA_FORMAT_INVALID", error.Code);
    }

    [Fact]
    public async Task ReadRejectsTotalUncompressedSizeOverOneHundredMegabytes()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "zip-bomb.qphrase");
        var tenMegabytes = new byte[PhrasePackageFileStore.MaxMediaBytes];
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteText(archive, "manifest.json", "{}");
            WriteText(archive, "data.json", "{}");
            for (var index = 0; index < 11; index++)
                WriteBytes(archive, $"media/{Guid.NewGuid():N}.png", tenMegabytes);
        }

        var error = await Assert.ThrowsAsync<PhrasePackageFileException>(() => new PhrasePackageFileStore().ReadAsync(path));

        Assert.Equal("PACKAGE_UNCOMPRESSED_TOO_LARGE", error.Code);
    }

    [Fact]
    public async Task ReadRejectsMoreThanMaximumMediaEntries()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "too-many-media.qphrase");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteText(archive, "manifest.json", "{}");
            WriteText(archive, "data.json", "{}");
            for (var index = 0; index <= PhrasePackageFormat.MaxMediaCount; index++)
                WriteBytes(archive, $"media/{Guid.NewGuid():N}.png", []);
        }

        var error = await Assert.ThrowsAsync<PhrasePackageFileException>(() => new PhrasePackageFileStore().ReadAsync(path));

        Assert.Equal("PACKAGE_MEDIA_COUNT_EXCEEDED", error.Code);
    }

    [Fact]
    public async Task ReadRejectsSingleMediaLargerThanTenMegabytesBeforeDecoding()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "oversize.qphrase");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteText(archive, "manifest.json", "{}");
            WriteText(archive, "data.json", "{}");
            WriteBytes(archive, "media/11111111111111111111111111111111.png", new byte[PhrasePackageFileStore.MaxMediaBytes + 1]);
        }

        var error = await Assert.ThrowsAsync<PhrasePackageFileException>(() => new PhrasePackageFileStore().ReadAsync(path));

        Assert.Equal("PACKAGE_MEDIA_TOO_LARGE", error.Code);
    }

    private static PhrasePackageDocument CreateImageDocument(Guid assetId, string mimeType, long bytes, int width, int height)
    {
        var categoryId = Guid.NewGuid();
        var image = new PhraseImageReference(assetId, mimeType, bytes, width, height);
        return new PhrasePackageDocument(
            new PhrasePackageManifest(PhrasePackageFormat.Format, PhrasePackageFormat.Version, Guid.NewGuid(), "图片包", DateTimeOffset.UtcNow, 1, 1, 1),
            [new PhrasePackageCategory(categoryId, "图片", null, 0)],
            [new PhrasePackagePhrase(Guid.NewGuid(), "图片", new PhraseBody([PhraseSegment.CreateImage(image)]), categoryId, 0)],
            [new PhrasePackageMedia(image, [])]);
    }

    private static string[] ReadEntryNames(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(entry => entry.FullName).ToArray();
    }

    private static void WriteText(ZipArchive archive, string name, string value) => WriteBytes(archive, name, Encoding.UTF8.GetBytes(value));
    private static void WriteBytes(ZipArchive archive, string name, byte[] value)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QuickPhrase-package-media-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
