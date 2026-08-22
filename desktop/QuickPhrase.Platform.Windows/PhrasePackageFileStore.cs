using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>.qphrase 的 JSON 形状。媒体字节只写入 media/，不会进入 data.json 或日志。</summary>
public static class PhrasePackageJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static byte[] SerializeManifest(PhrasePackageManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest, Options);
    public static byte[] SerializeData(PhrasePackageDocument document) => JsonSerializer.SerializeToUtf8Bytes(
        new PhrasePackageData(document.Categories, document.Phrases, document.Media.Select(item => item.Image).ToArray()), Options);
    public static PhrasePackageManifest DeserializeManifest(Stream stream) => JsonSerializer.Deserialize<PhrasePackageManifest>(stream, Options)
        ?? throw new PhrasePackageFileException("PACKAGE_MANIFEST_INVALID", "话术包清单为空。");
    public static PhrasePackageData DeserializeData(Stream stream) => JsonSerializer.Deserialize<PhrasePackageData>(stream, Options)
        ?? throw new PhrasePackageFileException("PACKAGE_DATA_INVALID", "话术包数据为空。");

    public sealed record PhrasePackageData(
        IReadOnlyList<PhrasePackageCategory> Categories,
        IReadOnlyList<PhrasePackagePhrase> Phrases,
        IReadOnlyList<PhraseImageReference> Media);
}

/// <summary>话术包文件读写错误。消息不得包含包内文件名、用户路径、正文或图片内容。</summary>
public sealed class PhrasePackageFileException : Exception
{
    public PhrasePackageFileException(string code, string message, Exception? innerException = null) : base(message, innerException) => Code = code;
    public string Code { get; }
}

/// <summary>
/// 首发 .qphrase ZIP 存储：固定 manifest/data 和 media/ 图片条目，逐条限制解压大小并校验引用闭包、扩展名和实际图片格式。
/// </summary>
public sealed class PhrasePackageFileStore
{
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const long MaxJsonBytes = 50L * 1024 * 1024;
    public const long MaxMediaBytes = 10L * 1024 * 1024;
    public const long MaxTotalUncompressedBytes = 100L * 1024 * 1024;
    private readonly IMediaAssetStore? _mediaAssets;

    public PhrasePackageFileStore(IMediaAssetStore? mediaAssets = null) => _mediaAssets = mediaAssets;

    public async Task<PhrasePackageDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid(); var started = Stopwatch.GetTimestamp();
        try
        {
            var fullPath = ValidatePath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists) throw Error("PACKAGE_NOT_FOUND", "找不到指定的话术包文件。");
            if (info.Length > MaxFileBytes) throw Error("PACKAGE_TOO_LARGE", "话术包文件不能超过 50 MB。");
            await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, false);
            var mediaEntries = ValidateEntries(archive);
            var budget = new ExtractionBudget();
            var manifest = PhrasePackageJsonSerializer.DeserializeManifest(await ReadEntryAsync(Require(archive, "manifest.json", "PACKAGE_MANIFEST_MISSING", "话术包缺少 manifest.json。"), MaxJsonBytes, budget, cancellationToken));
            var data = PhrasePackageJsonSerializer.DeserializeData(await ReadEntryAsync(Require(archive, "data.json", "PACKAGE_DATA_MISSING", "话术包缺少 data.json。"), MaxJsonBytes, budget, cancellationToken));
            if (data.Categories is null || data.Phrases is null || data.Media is null)
                throw Error("PACKAGE_VALIDATION_FAILED", "话术包 JSON 数据集合不完整。");
            var descriptorDocument = new PhrasePackageDocument(
                manifest,
                data.Categories,
                data.Phrases,
                data.Media.OfType<PhraseImageReference>().Select(image => new PhrasePackageMedia(image, [])).ToArray());
            var descriptorErrors = PhrasePackagePlanner.Validate(descriptorDocument);
            if (descriptorErrors.Count > 0) throw Error("PACKAGE_VALIDATION_FAILED", string.Join("；", descriptorErrors));
            if (data.Media.Count != mediaEntries.Count) throw Error("PACKAGE_MEDIA_REFERENCE_INVALID", "话术包媒体清单与文件条目不一致。");

            var normalizedById = new Dictionary<Guid, PhrasePackageMedia>();
            foreach (var descriptor in data.Media)
            {
                if (descriptor.AssetId == Guid.Empty || normalizedById.ContainsKey(descriptor.AssetId))
                    throw Error("PACKAGE_MEDIA_REFERENCE_INVALID", "话术包包含无效或重复的媒体标识。");
                var expected = MediaEntryName(descriptor);
                if (!mediaEntries.TryGetValue(expected, out var entry))
                    throw Error("PACKAGE_MEDIA_REFERENCE_INVALID", "话术包缺少话术所引用的媒体文件。");
                var bytes = (await ReadEntryAsync(entry, MaxMediaBytes, budget, cancellationToken)).ToArray();
                if (descriptor.ByteLength != bytes.LongLength)
                    throw Error("PACKAGE_MEDIA_METADATA_INVALID", "话术包媒体元数据与文件不一致。");
                var normalized = WindowsMediaAssetStore.NormalizeImage(bytes, Path.GetExtension(expected));
                if (!normalized.IsSuccess)
                    throw Error("PACKAGE_MEDIA_FORMAT_INVALID", "话术包包含损坏、伪造扩展名或不受支持的图片。");
                if (!string.Equals(descriptor.MimeType, normalized.SourceMimeType, StringComparison.OrdinalIgnoreCase) ||
                    descriptor.PixelWidth != normalized.Width || descriptor.PixelHeight != normalized.Height)
                    throw Error("PACKAGE_MEDIA_METADATA_INVALID", "话术包媒体元数据与实际图片不一致。");
                var image = new PhraseImageReference(descriptor.AssetId, "image/png", normalized.Bytes!.LongLength, normalized.Width, normalized.Height);
                normalizedById.Add(descriptor.AssetId, new PhrasePackageMedia(image, normalized.Bytes));
            }

            var phrases = data.Phrases.Select(phrase => phrase with { Body = ReplaceImageReferences(phrase.Body, normalizedById) }).ToArray();
            var document = new PhrasePackageDocument(manifest, data.Categories, phrases, normalizedById.Values.ToArray());
            var errors = PhrasePackagePlanner.Validate(document);
            if (errors.Count > 0) throw Error("PACKAGE_VALIDATION_FAILED", string.Join("；", errors));
            Log("读取", traceId, "PACKAGE_READ_OK", started); return document;
        }
        catch (OperationCanceledException) { Log("读取", traceId, "PACKAGE_READ_CANCELLED", started); throw; }
        catch (PhrasePackageFileException ex) { Log("读取", traceId, ex.Code, started); throw; }
        catch (JsonException ex) { Log("读取", traceId, "PACKAGE_JSON_INVALID", started); throw Error("PACKAGE_JSON_INVALID", "话术包 JSON 数据损坏。", ex); }
        catch (InvalidDataException ex) { Log("读取", traceId, "PACKAGE_ZIP_INVALID", started); throw Error("PACKAGE_ZIP_INVALID", "话术包 ZIP 容器损坏或格式无效。", ex); }
        catch (UnauthorizedAccessException ex) { Log("读取", traceId, "PACKAGE_ACCESS_DENIED", started); throw Error("PACKAGE_ACCESS_DENIED", "没有权限读取话术包文件。", ex); }
        catch (ArgumentException ex) { Log("读取", traceId, "PACKAGE_PATH_INVALID", started); throw Error("PACKAGE_PATH_INVALID", "话术包文件路径无效。", ex); }
        catch (IOException ex) { Log("读取", traceId, "PACKAGE_IO_FAILED", started); throw Error("PACKAGE_IO_FAILED", "话术包文件无法读取，请检查文件权限。", ex); }
    }

    public async Task WriteAsync(string path, PhrasePackageDocument document, CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid(); var started = Stopwatch.GetTimestamp(); string? temporaryPath = null;
        try
        {
            var fullPath = ValidatePath(path);
            var errors = PhrasePackagePlanner.Validate(document);
            if (errors.Count > 0) throw Error("PACKAGE_VALIDATION_FAILED", string.Join("；", errors));
            var materialized = await MaterializeMediaAsync(document, cancellationToken);
            var directory = Path.GetDirectoryName(fullPath) ?? throw Error("PACKAGE_PATH_INVALID", "话术包文件路径无效。");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{traceId:N}.tmp");
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(file, ZipArchiveMode.Create, true);
                await WriteEntryAsync(archive, "manifest.json", PhrasePackageJsonSerializer.SerializeManifest(materialized.Manifest), cancellationToken);
                await WriteEntryAsync(archive, "data.json", PhrasePackageJsonSerializer.SerializeData(materialized), cancellationToken);
                foreach (var media in materialized.Media)
                    await WriteEntryAsync(archive, MediaEntryName(media.Image), media.Content, cancellationToken);
                archive.Dispose(); await file.FlushAsync(cancellationToken); file.Flush(true);
            }
            if (new FileInfo(temporaryPath).Length > MaxFileBytes) throw Error("PACKAGE_TOO_LARGE", "生成的话术包文件不能超过 50 MB。");
            ReplaceFile(temporaryPath, fullPath); temporaryPath = null; Log("写入", traceId, "PACKAGE_WRITE_OK", started);
        }
        catch (OperationCanceledException) { Log("写入", traceId, "PACKAGE_WRITE_CANCELLED", started); throw; }
        catch (PhrasePackageFileException ex) { Log("写入", traceId, ex.Code, started); throw; }
        catch (UnauthorizedAccessException ex) { Log("写入", traceId, "PACKAGE_ACCESS_DENIED", started); throw Error("PACKAGE_ACCESS_DENIED", "没有权限写入话术包文件。", ex); }
        catch (IOException ex) { Log("写入", traceId, "PACKAGE_IO_FAILED", started); throw Error("PACKAGE_IO_FAILED", "话术包文件无法写入，请检查文件权限。", ex); }
        finally { if (temporaryPath is not null) { try { File.Delete(temporaryPath); } catch { } } }
    }

    private async Task<PhrasePackageDocument> MaterializeMediaAsync(PhrasePackageDocument document, CancellationToken cancellationToken)
    {
        var media = new List<PhrasePackageMedia>(document.Media.Count);
        foreach (var item in document.Media)
        {
            byte[] bytes;
            if (item.Content.Length > 0) bytes = item.Content;
            else
            {
                if (_mediaAssets is null) throw Error("PACKAGE_MEDIA_SOURCE_MISSING", "无法读取话术包引用的图片媒体。");
                var source = await _mediaAssets.ReadAsync(item.Image.AssetId, cancellationToken);
                if (source is null) throw Error("PACKAGE_MEDIA_SOURCE_MISSING", "无法读取话术包引用的图片媒体。");
                bytes = source.Bytes;
            }
            var normalized = WindowsMediaAssetStore.NormalizeImage(bytes, ".png");
            if (!normalized.IsSuccess) throw Error("PACKAGE_MEDIA_FORMAT_INVALID", "导出图片损坏或无法完整解码。");
            var image = new PhraseImageReference(item.Image.AssetId, "image/png", normalized.Bytes!.LongLength, normalized.Width, normalized.Height);
            media.Add(new PhrasePackageMedia(image, normalized.Bytes));
        }
        var map = media.ToDictionary(x => x.Image.AssetId);
        var phrases = document.Phrases.Select(x => x with { Body = ReplaceImageReferences(x.Body, map) }).ToArray();
        return document with { Phrases = phrases, Media = media, Manifest = document.Manifest with { MediaCount = media.Count } };
    }

    private static PhraseBody ReplaceImageReferences(PhraseBody body, IReadOnlyDictionary<Guid, PhrasePackageMedia> media) =>
        new(body.Segments.Select(segment => segment.Kind == PhraseSegmentKind.Image && segment.Image is not null && media.TryGetValue(segment.Image.AssetId, out var item)
            ? segment with { Image = item.Image }
            : segment).ToImmutableArray());

    private static Dictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var media = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (string.IsNullOrWhiteSpace(name) || entry.Name.Length == 0 || name.Contains('\\') || name.StartsWith('/') || name.Split('/').Any(x => x is "." or ".."))
                throw Error("PACKAGE_ENTRIES_INVALID", "话术包包含目录、绝对路径或路径穿越条目。");
            if (!seen.Add(name)) throw Error("PACKAGE_ENTRIES_INVALID", "话术包包含重复条目。");
            total = checked(total + entry.Length);
            if (total > MaxTotalUncompressedBytes) throw Error("PACKAGE_UNCOMPRESSED_TOO_LARGE", "话术包解压后总大小超过 100 MB。");
            if (name is "manifest.json" or "data.json") continue;
            if (!name.StartsWith("media/", StringComparison.Ordinal) || name.Count(c => c == '/') != 1)
                throw Error("PACKAGE_ENTRIES_INVALID", "话术包包含不允许的文件条目。");
            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
                throw Error("PACKAGE_MEDIA_EXTENSION_INVALID", "话术包包含不受支持的媒体扩展名。");
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _))
                throw Error("PACKAGE_ENTRIES_INVALID", "话术包媒体条目标识无效。");
            if (entry.Length > MaxMediaBytes) throw Error("PACKAGE_MEDIA_TOO_LARGE", "话术包中的单张图片不能超过 10 MB。");
            media.Add(name, entry);
        }
        if (!seen.Contains("manifest.json") || !seen.Contains("data.json")) throw Error("PACKAGE_ENTRIES_INVALID", "话术包必须包含 manifest.json 和 data.json。");
        if (media.Count > PhrasePackageFormat.MaxMediaCount) throw Error("PACKAGE_MEDIA_COUNT_EXCEEDED", "话术包媒体数量超过上限。");
        return media;
    }

    private static string MediaEntryName(PhraseImageReference image) =>
        $"media/{image.AssetId:N}" + (image.MimeType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/bmp" => ".bmp",
            _ => throw Error("PACKAGE_MEDIA_EXTENSION_INVALID", "话术包媒体类型不受支持。"),
        });
    private static ZipArchiveEntry Require(ZipArchive archive, string name, string code, string message) => archive.GetEntry(name) ?? throw Error(code, message);
    private static async Task<MemoryStream> ReadEntryAsync(ZipArchiveEntry entry, long limit, ExtractionBudget budget, CancellationToken cancellationToken)
    {
        if (entry.Length > limit) throw Error(entry.FullName.StartsWith("media/", StringComparison.Ordinal) ? "PACKAGE_MEDIA_TOO_LARGE" : "PACKAGE_JSON_TOO_LARGE", "话术包条目解压后过大。");
        var memory = new MemoryStream((int)Math.Min(entry.Length, limit)); await using var source = entry.Open(); var buffer = new byte[64 * 1024]; long total = 0;
        while (true) { var read = await source.ReadAsync(buffer, cancellationToken); if (read == 0) break; total += read; budget.Add(read); if (total > limit) { memory.Dispose(); throw Error("PACKAGE_UNCOMPRESSED_TOO_LARGE", "话术包条目解压后过大。"); } await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken); }
        memory.Position = 0; return memory;
    }
    private sealed class ExtractionBudget { private long _total; public void Add(int bytes) { _total += bytes; if (_total > MaxTotalUncompressedBytes) throw Error("PACKAGE_UNCOMPRESSED_TOO_LARGE", "话术包解压后总大小超过 100 MB。"); } }
    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] data, CancellationToken token) { var entry = archive.CreateEntry(name, CompressionLevel.Optimal); await using var stream = entry.Open(); await stream.WriteAsync(data, token); }
    private static string ValidatePath(string path) { if (string.IsNullOrWhiteSpace(path)) throw Error("PACKAGE_PATH_INVALID", "话术包文件路径不能为空。"); if (!string.Equals(Path.GetExtension(path), ".qphrase", StringComparison.OrdinalIgnoreCase)) throw Error("PACKAGE_EXTENSION_INVALID", "只能选择 .qphrase 话术包文件。"); var full = Path.GetFullPath(path); if (Directory.Exists(full)) throw Error("PACKAGE_PATH_INVALID", "选择的路径是文件夹，不是话术包文件。"); return full; }
    private static void ReplaceFile(string temporaryPath, string targetPath) { if (!File.Exists(targetPath)) File.Move(temporaryPath, targetPath); else File.Replace(temporaryPath, targetPath, null, true); }
    private static PhrasePackageFileException Error(string code, string message, Exception? inner = null) => new(code, message, inner);
    private static void Log(string stage, Guid traceId, string code, long started) => Console.WriteLine($"话术包{stage}阶段：TraceId={traceId:N}，结果码={code}，耗时={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms。");
}
