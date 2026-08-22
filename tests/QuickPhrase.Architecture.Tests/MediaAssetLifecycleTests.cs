using Microsoft.Data.Sqlite;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickPhrase.Architecture.Tests;

[Collection(nameof(MediaAssetConsoleCollection))]
public sealed class MediaAssetLifecycleTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".bmp")]
    public async Task ImportSupportedFormatsCreatesManagedPngThatSurvivesSourceDeletion(string extension)
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var source = Path.Combine(temp.Path, "source" + extension);
        await File.WriteAllBytesAsync(source, CreateImageBytes(extension));

        var imported = await runtime.MediaAssets.ImportAsync(source);
        File.Delete(source);
        var content = await runtime.MediaAssets.ReadAsync(imported.Image!.AssetId);

        Assert.True(imported.IsSuccess, imported.ErrorMessage);
        Assert.NotNull(content);
        Assert.Equal("image/png", content!.Image.MimeType);
        Assert.Equal(0x89, content.Bytes[0]);
        Assert.Equal((byte)'P', content.Bytes[1]);
    }

    [Theory]
    [InlineData(".gif")]
    [InlineData(".svg")]
    [InlineData(".webp")]
    public async Task ImportRejectsUnsupportedExtensionsWithoutCreatingMetadata(string extension)
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var source = Path.Combine(temp.Path, "unsupported" + extension);
        await File.WriteAllBytesAsync(source, OnePixelPng);

        var imported = await runtime.MediaAssets.ImportAsync(source);

        Assert.False(imported.IsSuccess);
        Assert.Equal("MEDIA_FORMAT_UNSUPPORTED", imported.ErrorCode);
        Assert.Equal(0L, await CountAsync(runtime.DatabasePath, "media_assets"));
    }

    [Fact]
    public async Task ImportRejectsForgedAndDamagedImages()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var forged = Path.Combine(temp.Path, "forged.jpg");
        var damaged = Path.Combine(temp.Path, "damaged.png");
        await File.WriteAllBytesAsync(forged, OnePixelPng);
        await File.WriteAllBytesAsync(damaged, [1, 2, 3, 4]);

        var forgedResult = await runtime.MediaAssets.ImportAsync(forged);
        var damagedResult = await runtime.MediaAssets.ImportAsync(damaged);

        Assert.Equal("MEDIA_FORMAT_MISMATCH", forgedResult.ErrorCode);
        Assert.Equal("MEDIA_DECODE_FAILED", damagedResult.ErrorCode);
        Assert.Equal(0L, await CountAsync(runtime.DatabasePath, "media_assets"));
    }

    [Fact]
    public async Task ImportRejectsTenMegabyteAndTwentyMegapixelOverflow()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var tooLarge = Path.Combine(temp.Path, "large.png");
        var tooManyPixels = Path.Combine(temp.Path, "pixels.png");
        await File.WriteAllBytesAsync(tooLarge, new byte[WindowsMediaAssetStore.MaxBytes + 1]);
        await File.WriteAllBytesAsync(tooManyPixels, CreatePng(4473, 4473));

        var bytesResult = await runtime.MediaAssets.ImportAsync(tooLarge);
        var pixelsResult = await runtime.MediaAssets.ImportAsync(tooManyPixels);

        Assert.Equal("MEDIA_TOO_LARGE", bytesResult.ErrorCode);
        Assert.Equal("MEDIA_PIXELS_EXCEEDED", pixelsResult.ErrorCode);
        Assert.Equal(0L, await CountAsync(runtime.DatabasePath, "media_assets"));
    }

    [Fact]
    public async Task DeletePhraseRemovesOnlyMediaNoLongerReferenced()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var image = await ImportPngAsync(runtime, temp.Path);
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "共享媒体"))).Value!;
        var first = (await runtime.Phrases.CreateAsync(CreateImagePhrase("第一条", category.Id, image))).Value!;
        var second = (await runtime.Phrases.CreateAsync(CreateImagePhrase("第二条", category.Id, image))).Value!;

        var firstDelete = await runtime.Phrases.DeleteAsync(first.Id, first.Version);

        Assert.True(firstDelete.IsSuccess);
        Assert.NotNull(await runtime.MediaAssets.ReadAsync(image.AssetId));
        Assert.Equal(1L, await ReferenceCountAsync(runtime.DatabasePath, image.AssetId));

        var secondDelete = await runtime.Phrases.DeleteAsync(second.Id, second.Version);

        Assert.True(secondDelete.IsSuccess);
        Assert.Null(await runtime.MediaAssets.ReadAsync(image.AssetId));
        Assert.Equal(0L, await CountAsync(runtime.DatabasePath, "media_assets"));
    }

    [Fact]
    public async Task UpdatePhraseCleansRemovedAssetAfterCommitAndKeepsReplacement()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var oldImage = await ImportPngAsync(runtime, temp.Path);
        var newImage = await ImportPngAsync(runtime, temp.Path);
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "替换媒体"))).Value!;
        var phrase = (await runtime.Phrases.CreateAsync(CreateImagePhrase("替换前", category.Id, oldImage))).Value!;

        var updated = await runtime.Phrases.UpdateAsync(new UpdatePhraseCommand(
            phrase.Id, phrase.Version, "替换后", new PhraseBody([PhraseSegment.CreateImage(newImage)]),
            category.Id, ShortcutMode.None, null));

        Assert.True(updated.IsSuccess, updated.Error?.Message);
        Assert.Null(await runtime.MediaAssets.ReadAsync(oldImage.AssetId));
        Assert.NotNull(await runtime.MediaAssets.ReadAsync(newImage.AssetId));
    }

    [Fact]
    public async Task FailedDatabaseSaveKeepsExistingSharedMediaAndLeavesNoBadReference()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var image = await ImportPngAsync(runtime, temp.Path);
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "失败补偿"))).Value!;
        var phrase = (await runtime.Phrases.CreateAsync(CreateImagePhrase("原话术", category.Id, image))).Value!;
        await ExecuteSqlAsync(runtime.DatabasePath,
            "CREATE TRIGGER fail_phrase_update BEFORE UPDATE ON phrases BEGIN SELECT RAISE(ABORT, '测试保存失败'); END;");

        var result = await runtime.Phrases.UpdateAsync(new UpdatePhraseCommand(
            phrase.Id, phrase.Version, "失败更新", PhraseBody.FromText("替换文字"), category.Id, ShortcutMode.None, null));

        Assert.False(result.IsSuccess);
        Assert.NotNull(await runtime.MediaAssets.ReadAsync(image.AssetId));
        Assert.Equal(1L, await ReferenceCountAsync(runtime.DatabasePath, image.AssetId));
    }

    [Fact]
    public async Task FileCleanupFailureDoesNotRollbackDeleteAndStartupRetriesFromDatabaseOrphans()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        Guid assetId;
        string managedPath;
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options))
        {
            var image = await ImportPngAsync(runtime, temp.Path);
            assetId = image.AssetId;
            managedPath = Path.Combine(options.MediaDirectory, assetId.ToString("N") + ".png");
            var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "重试清理"))).Value!;
            var phrase = (await runtime.Phrases.CreateAsync(CreateImagePhrase("待删除", category.Id, image))).Value!;
            await using var lockStream = new FileStream(managedPath, FileMode.Open, FileAccess.Read, FileShare.None);

            var deleted = await runtime.Phrases.DeleteAsync(phrase.Id, phrase.Version);

            Assert.True(deleted.IsSuccess);
            Assert.Empty(await runtime.Phrases.ListAsync());
            Assert.Equal(1L, await CountAsync(runtime.DatabasePath, "media_assets"));
            Assert.True(File.Exists(managedPath));
        }

        await using (var reopened = await QuickPhraseDataRuntime.OpenAsync(options))
        {
            Assert.Equal(0L, await CountAsync(reopened.DatabasePath, "media_assets"));
            Assert.False(File.Exists(managedPath));
            Assert.Null(await reopened.MediaAssets.ReadAsync(assetId));
        }
    }

    [Fact]
    public async Task ImportFailuresCompensateTemporaryFileMetadataAndFinalMove()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(options);
        var source = Path.Combine(temp.Path, "source.png");
        await File.WriteAllBytesAsync(source, OnePixelPng);

        await ExecuteSqlAsync(runtime.DatabasePath,
            "CREATE TRIGGER fail_media_insert BEFORE INSERT ON media_assets BEGIN SELECT RAISE(ABORT, '测试元数据失败'); END;");
        var metadataFailure = await runtime.MediaAssets.ImportAsync(source);
        await ExecuteSqlAsync(runtime.DatabasePath, "DROP TRIGGER fail_media_insert;");

        Assert.False(metadataFailure.IsSuccess);
        Assert.Equal(0L, await CountAsync(runtime.DatabasePath, "media_assets"));
        Assert.Empty(Directory.EnumerateFiles(options.MediaDirectory));

        var connections = new SqliteConnectionFactory(options.DatabasePath);
        await using var queue = new SqliteWriteQueue(connections, 8, TimeSpan.FromSeconds(5));
        await queue.StartAsync(CancellationToken.None);
        var moveFailureStore = new WindowsMediaAssetStore(options, connections, queue, TimeProvider.System,
            (_, _) => throw new IOException("测试正式移动失败"));
        var moveFailure = await moveFailureStore.ImportAsync(source);

        Assert.Equal("MEDIA_FILE_COMMIT_FAILED", moveFailure.ErrorCode);
        Assert.Equal(0L, await CountAsync(runtime.DatabasePath, "media_assets"));
        Assert.Empty(Directory.EnumerateFiles(options.MediaDirectory));
    }

    [Fact]
    public async Task TemporaryFileWriteFailureCreatesNoMetadataOrManagedFile()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(options);
        var source = Path.Combine(temp.Path, "source.png");
        await File.WriteAllBytesAsync(source, OnePixelPng);
        Directory.Delete(options.MediaDirectory);
        await File.WriteAllTextAsync(options.MediaDirectory, "阻止创建媒体目录");

        var result = await runtime.MediaAssets.ImportAsync(source);

        Assert.False(result.IsSuccess);
        Assert.Equal("MEDIA_IMPORT_FAILED", result.ErrorCode);
        Assert.Equal(0L, await CountAsync(runtime.DatabasePath, "media_assets"));
    }

    [Fact]
    public async Task StartupCleanupDoesNotScanOrDeleteFilesAbsentFromSqlite()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options)) { }
        var unrelated = Path.Combine(options.MediaDirectory, "not-a-managed-asset.png");
        await File.WriteAllBytesAsync(unrelated, OnePixelPng);

        await using (var reopened = await QuickPhraseDataRuntime.OpenAsync(options))
        {
            Assert.True(File.Exists(unrelated));
            Assert.Equal(0L, await CountAsync(reopened.DatabasePath, "media_assets"));
        }
    }
    [Fact]
    public async Task FailureLogsDoNotContainSourceNameAbsolutePathOrImageBytes()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var source = Path.Combine(temp.Path, "客户身份证-绝密.png");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var originalError = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            _ = await runtime.MediaAssets.ImportAsync(source);
        }
        finally
        {
            Console.SetError(originalError);
        }

        var log = captured.ToString();
        Assert.DoesNotContain("客户身份证", log, StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Path, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String([1, 2, 3, 4]), log, StringComparison.Ordinal);
    }

    private static CreatePhraseCommand CreateImagePhrase(string title, Guid categoryId, PhraseImageReference image) =>
        new(Guid.NewGuid(), title, new PhraseBody([PhraseSegment.CreateImage(image)]), categoryId, ShortcutMode.None, null);

    private static async Task<PhraseImageReference> ImportPngAsync(QuickPhraseDataRuntime runtime, string directory)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(path, OnePixelPng);
        var result = await runtime.MediaAssets.ImportAsync(path);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return result.Image!;
    }

    private static byte[] CreateImageBytes(string extension)
    {
        BitmapEncoder encoder = extension switch
        {
            ".png" => new PngBitmapEncoder(),
            ".jpg" => new JpegBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(extension)),
        };
        var pixels = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 };
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] CreatePng(int width, int height)
    {
        var pixels = new byte[checked(width * height)];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static async Task<long> CountAsync(string databasePath, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> ReferenceCountAsync(string databasePath, Guid assetId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM phrase_segments WHERE media_asset_id=$id;";
        command.Parameters.AddWithValue("$id", assetId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("QuickPhrase-Media-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

[CollectionDefinition(nameof(MediaAssetConsoleCollection), DisableParallelization = true)]
public sealed class MediaAssetConsoleCollection;

