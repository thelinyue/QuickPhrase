using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 当前用户媒体库。所有入口共用完整解码和 PNG 重编码流程，确保文件导入与 .qphrase 导入具有相同的格式、像素和元数据安全边界。
/// SQLite 的 media_assets 行同时承担清理重试清单：只有文件删除成功后才删除元数据，失败时绝不能失去数据库证明。
/// </summary>
internal sealed class WindowsMediaAssetStore : IMediaAssetStore
{
    internal const long MaxBytes = 10L * 1024 * 1024;
    internal const long MaxPixels = 20_000_000;
    private readonly QuickPhraseDataOptions _options;
    private readonly SqliteConnectionFactory _connections;
    private readonly SqliteWriteQueue _writes;
    private readonly TimeProvider _clock;
    private readonly Action<string, string> _commitFile;

    public WindowsMediaAssetStore(QuickPhraseDataOptions options, SqliteConnectionFactory connections, SqliteWriteQueue writes, TimeProvider clock, Action<string, string>? commitFile = null)
    {
        _options = options;
        _connections = connections;
        _writes = writes;
        _clock = clock;
        _commitFile = commitFile ?? File.Move;
    }

    public async Task<MediaImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return Fail("MEDIA_NOT_FOUND", "找不到要导入的图片。");
        var info = new FileInfo(sourcePath);
        if (info.Length > MaxBytes)
            return Fail("MEDIA_TOO_LARGE", "单张图片不能超过 10 MB。");

        try
        {
            var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            var normalized = NormalizeImage(bytes, Path.GetExtension(sourcePath));
            return normalized.IsSuccess ? await SaveNormalizedAsync(normalized, cancellationToken) : normalized.Error!;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"图片导入失败。阶段：MEDIA_IMPORT；结果码：MEDIA_IMPORT_FAILED；异常类型：{exception.GetType().Name}");
            return Fail("MEDIA_IMPORT_FAILED", "图片损坏、格式与扩展名不一致，或无法完整解码。");
        }
    }

    /// <summary>包导入使用内存字节进入同一规范化流程，调用方无需把包内名称或路径写入磁盘和日志。</summary>
    internal async Task<MediaImportResult> ImportPackageAsync(byte[] bytes, string extension, CancellationToken cancellationToken)
    {
        var normalized = NormalizeImage(bytes, extension);
        return normalized.IsSuccess ? await SaveNormalizedAsync(normalized, cancellationToken) : normalized.Error!;
    }

    /// <summary>只做解码、格式核验和无元数据 PNG 重编码，不创建数据库记录。</summary>
    internal static NormalizedImage NormalizeImage(byte[] bytes, string extension)
    {
        if (bytes.LongLength > MaxBytes)
            return NormalizedImage.Failure("MEDIA_TOO_LARGE", "单张图片不能超过 10 MB。");
        var normalizedExtension = extension.ToLowerInvariant();
        if (normalizedExtension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
            return NormalizedImage.Failure("MEDIA_FORMAT_UNSUPPORTED", "仅支持 PNG、JPEG 和 BMP 图片。");

        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var actualExtension = decoder switch
            {
                PngBitmapDecoder => ".png",
                JpegBitmapDecoder => ".jpg",
                BmpBitmapDecoder => ".bmp",
                _ => string.Empty,
            };
            var extensionMatches = actualExtension == ".jpg"
                ? normalizedExtension is ".jpg" or ".jpeg"
                : actualExtension == normalizedExtension;
            if (string.IsNullOrEmpty(actualExtension) || !extensionMatches)
                return NormalizedImage.Failure("MEDIA_FORMAT_MISMATCH", "图片实际格式与文件扩展名不一致。");

            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null || frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                return NormalizedImage.Failure("MEDIA_DECODE_FAILED", "图片损坏或无法完整解码。");
            if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
                return NormalizedImage.Failure("MEDIA_PIXELS_EXCEEDED", "单张图片不能超过 2000 万像素。");

            // 只从像素创建新帧，不复制 EXIF、文件名、注释或其他来源元数据。
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var output = new MemoryStream();
            encoder.Save(output);
            if (output.Length > MaxBytes)
                return NormalizedImage.Failure("MEDIA_TOO_LARGE", "图片规范化后超过 10 MB，请压缩后重试。");
            return NormalizedImage.Success(output.ToArray(), frame.PixelWidth, frame.PixelHeight,
                actualExtension == ".jpg" ? "image/jpeg" : actualExtension == ".bmp" ? "image/bmp" : "image/png");
        }
        catch
        {
            return NormalizedImage.Failure("MEDIA_DECODE_FAILED", "图片损坏或无法完整解码。");
        }
    }

    public async Task<MediaAssetContent?> ReadAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenReadAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT storage_key,mime_type,byte_length,pixel_width,pixel_height FROM media_assets WHERE asset_id=$id;";
        command.Parameters.AddWithValue("$id", assetId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (!TryResolveManagedPath(assetId, reader.GetString(0), out var path) || !File.Exists(path)) return null;
        var image = new PhraseImageReference(assetId, reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4));
        return new MediaAssetContent(image, await File.ReadAllBytesAsync(path, cancellationToken));
    }

    /// <summary>启动清理只读取 SQLite 已证明无引用的资产，不扫描媒体目录猜测“孤儿”文件。</summary>
    public async Task CleanupOrphansAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.MediaDirectory);
        Guid[] candidates;
        await using (var connection = await _connections.OpenReadAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT asset_id FROM media_assets ma WHERE NOT EXISTS(SELECT 1 FROM phrase_segments ps WHERE ps.media_asset_id=ma.asset_id);";
            var ids = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(Guid.Parse(reader.GetString(0)));
            candidates = ids.ToArray();
        }

        foreach (var assetId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteIfUnreferencedAsync(assetId, cancellationToken);
        }
    }

    /// <summary>
    /// 删除在单写者队列中完成：先再次验证无引用，再删除正式文件，最后删除元数据并提交。
    /// 文件删除失败时事务不提交，media_assets 行保留为下次启动的可靠重试状态。
    /// </summary>
    public async Task DeleteIfUnreferencedAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _writes.EnqueueAsync((connection, ct) => DeleteUnreferencedCoreAsync(connection, assetId, ct), cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"媒体清理失败，将在下次启动重试。阶段：MEDIA_CLEANUP；结果码：MEDIA_CLEANUP_FAILED；异常类型：{exception.GetType().Name}");
        }
    }

    private async Task<MediaImportResult> SaveNormalizedAsync(NormalizedImage normalized, CancellationToken cancellationToken)
    {
        var assetId = Guid.NewGuid();
        var storageKey = assetId.ToString("N") + ".png";
        var temporaryPath = Path.Combine(_options.MediaDirectory, assetId.ToString("N") + ".tmp");
        var finalPath = Path.Combine(_options.MediaDirectory, storageKey);
        var metadataSaved = false;
        try
        {
            Directory.CreateDirectory(_options.MediaDirectory);
            await File.WriteAllBytesAsync(temporaryPath, normalized.Bytes!, cancellationToken);
            var image = new PhraseImageReference(assetId, "image/png", normalized.Bytes!.LongLength, normalized.Width, normalized.Height);
            metadataSaved = await _writes.EnqueueAsync((connection, ct) => InsertMetadataAsync(connection, image, storageKey, ct), cancellationToken);
            if (!metadataSaved)
            {
                TryDeleteTemporary(temporaryPath);
                return Fail("MEDIA_DATABASE_FAILED", "图片媒体信息保存失败，请重试。");
            }

            try
            {
                _commitFile(temporaryPath, finalPath);
            }
            catch (Exception exception)
            {
                TryDeleteTemporary(temporaryPath);
                await DeleteIfUnreferencedAsync(assetId, CancellationToken.None);
                Console.Error.WriteLine($"图片正式文件提交失败。阶段：MEDIA_FILE_COMMIT；结果码：MEDIA_FILE_COMMIT_FAILED；异常类型：{exception.GetType().Name}");
                return Fail("MEDIA_FILE_COMMIT_FAILED", "图片文件保存失败，请检查磁盘空间后重试。");
            }
            return MediaImportResult.Success(image);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTemporary(temporaryPath);
            if (metadataSaved) await DeleteIfUnreferencedAsync(assetId, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            TryDeleteTemporary(temporaryPath);
            if (metadataSaved) await DeleteIfUnreferencedAsync(assetId, CancellationToken.None);
            Console.Error.WriteLine($"图片保存失败。阶段：MEDIA_COMMIT；结果码：MEDIA_IMPORT_FAILED；异常类型：{exception.GetType().Name}");
            return Fail("MEDIA_IMPORT_FAILED", "图片保存失败，请检查磁盘空间后重试。");
        }
    }

    private async Task<bool> InsertMetadataAsync(SqliteConnection connection, PhraseImageReference image, string storageKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO media_assets(asset_id,storage_key,mime_type,byte_length,pixel_width,pixel_height,created_at_utc) VALUES($id,$key,$mime,$bytes,$width,$height,$created);";
        command.Parameters.AddWithValue("$id", image.AssetId.ToString("D"));
        command.Parameters.AddWithValue("$key", storageKey);
        command.Parameters.AddWithValue("$mime", image.MimeType);
        command.Parameters.AddWithValue("$bytes", image.ByteLength);
        command.Parameters.AddWithValue("$width", image.PixelWidth);
        command.Parameters.AddWithValue("$height", image.PixelHeight);
        command.Parameters.AddWithValue("$created", _clock.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<bool> DeleteUnreferencedCoreAsync(SqliteConnection connection, Guid assetId, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        string? storageKey;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT storage_key FROM media_assets WHERE asset_id=$id AND NOT EXISTS(SELECT 1 FROM phrase_segments WHERE media_asset_id=$id);";
            select.Parameters.AddWithValue("$id", assetId.ToString("D"));
            storageKey = await select.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (storageKey is null) return false;
        if (!TryResolveManagedPath(assetId, storageKey, out var path))
        {
            Console.Error.WriteLine("媒体清理被拒绝。阶段：MEDIA_CLEANUP；结果码：MEDIA_STORAGE_KEY_INVALID；内部存储标识不合法。");
            return false;
        }

        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"媒体文件清理失败，将保留数据库重试状态。阶段：MEDIA_FILE_DELETE；结果码：MEDIA_FILE_DELETE_FAILED；异常类型：{exception.GetType().Name}");
            return false;
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM media_assets WHERE asset_id=$id AND NOT EXISTS(SELECT 1 FROM phrase_segments WHERE media_asset_id=$id);";
        delete.Parameters.AddWithValue("$id", assetId.ToString("D"));
        if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private bool TryResolveManagedPath(Guid assetId, string storageKey, out string path)
    {
        var expectedKey = assetId.ToString("N") + ".png";
        if (!string.Equals(storageKey, expectedKey, StringComparison.Ordinal))
        {
            path = string.Empty;
            return false;
        }
        path = Path.Combine(_options.MediaDirectory, storageKey);
        return true;
    }

    private static MediaImportResult Fail(string code, string message) => MediaImportResult.Failure(code, message);

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"图片临时文件清理失败。阶段：MEDIA_TEMP_CLEANUP；结果码：MEDIA_TEMP_DELETE_FAILED；异常类型：{exception.GetType().Name}");
        }
    }

    internal sealed record NormalizedImage(bool IsSuccess, byte[]? Bytes, int Width, int Height, string? SourceMimeType, MediaImportResult? Error)
    {
        public static NormalizedImage Success(byte[] bytes, int width, int height, string sourceMimeType) => new(true, bytes, width, height, sourceMimeType, null);
        public static NormalizedImage Failure(string code, string message) => new(false, null, 0, 0, null, MediaImportResult.Failure(code, message));
    }
}
