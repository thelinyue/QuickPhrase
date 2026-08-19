using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// .qphrase 文件的 JSON 形状。manifest.json 与 data.json 分离，方便在导入预览前先完成格式校验。
/// </summary>
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

    public static byte[] SerializeData(PhrasePackageDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(new PhrasePackageData(document.Categories, document.Phrases), Options);

    public static PhrasePackageManifest DeserializeManifest(Stream stream) =>
        JsonSerializer.Deserialize<PhrasePackageManifest>(stream, Options)
        ?? throw new PhrasePackageFileException("PACKAGE_MANIFEST_INVALID", "话术包清单为空。");

    public static PhrasePackageData DeserializeData(Stream stream) =>
        JsonSerializer.Deserialize<PhrasePackageData>(stream, Options)
        ?? throw new PhrasePackageFileException("PACKAGE_DATA_INVALID", "话术包数据为空。");

    public sealed record PhrasePackageData(
        IReadOnlyList<PhrasePackageCategory> Categories,
        IReadOnlyList<PhrasePackagePhrase> Phrases);
}

/// <summary>话术包文件读写错误。内部异常不直接透传到用户界面。</summary>
public sealed class PhrasePackageFileException : Exception
{
    public PhrasePackageFileException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}

/// <summary>
/// 话术包 ZIP 文件存储。只接受固定的两个 JSON 条目，并限制压缩包和解压后 JSON 的大小，避免把任意 ZIP 当作业务文件处理。
/// </summary>
public sealed class PhrasePackageFileStore
{
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const long MaxJsonBytes = 100L * 1024 * 1024;
    private static readonly HashSet<string> AllowedEntries = ["manifest.json", "data.json"];

    public async Task<PhrasePackageDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        try
        {
            ValidatePath(path);
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > MaxFileBytes)
                throw new PhrasePackageFileException("PACKAGE_TOO_LARGE", "话术包文件不能超过 50 MB。");

            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            var entries = archive.Entries.Where(x => !string.IsNullOrEmpty(x.Name)).ToArray();
            if (entries.Length != AllowedEntries.Count || entries.Any(x => !AllowedEntries.Contains(x.FullName)) || archive.Entries.Any(x => string.IsNullOrEmpty(x.Name)))
                throw new PhrasePackageFileException("PACKAGE_ENTRIES_INVALID", "话术包只能包含 manifest.json 和 data.json。");

            var manifestEntry = archive.GetEntry("manifest.json") ?? throw new PhrasePackageFileException("PACKAGE_MANIFEST_MISSING", "话术包缺少 manifest.json。");
            var dataEntry = archive.GetEntry("data.json") ?? throw new PhrasePackageFileException("PACKAGE_DATA_MISSING", "话术包缺少 data.json。");
            await using var manifestStream = await OpenLimitedEntryAsync(manifestEntry, cancellationToken);
            await using var dataStream = await OpenLimitedEntryAsync(dataEntry, cancellationToken);
            var manifest = PhrasePackageJsonSerializer.DeserializeManifest(manifestStream);
            var data = PhrasePackageJsonSerializer.DeserializeData(dataStream);
            var document = new PhrasePackageDocument(manifest, data.Categories, data.Phrases);
            var errors = PhrasePackagePlanner.Validate(document);
            if (errors.Count > 0)
                throw new PhrasePackageFileException("PACKAGE_VALIDATION_FAILED", string.Join("；", errors));

            Log("读取", traceId, "PACKAGE_READ_OK", started);
            return document;
        }
        catch (PhrasePackageFileException)
        {
            Log("读取", traceId, "PACKAGE_READ_FAILED", started);
            throw;
        }
        catch (JsonException exception)
        {
            Log("读取", traceId, "PACKAGE_JSON_INVALID", started);
            throw new PhrasePackageFileException("PACKAGE_JSON_INVALID", "话术包 JSON 数据损坏。", exception);
        }
        catch (InvalidDataException exception)
        {
            Log("读取", traceId, "PACKAGE_ZIP_INVALID", started);
            throw new PhrasePackageFileException("PACKAGE_ZIP_INVALID", "话术包 ZIP 容器损坏或格式无效。", exception);
        }
        catch (IOException exception)
        {
            Log("读取", traceId, "PACKAGE_IO_FAILED", started);
            throw new PhrasePackageFileException("PACKAGE_IO_FAILED", "话术包文件无法读取，请检查文件权限。", exception);
        }
    }

    public async Task WriteAsync(string path, PhrasePackageDocument document, CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        try
        {
            ValidatePath(path);
            var errors = PhrasePackagePlanner.Validate(document);
            if (errors.Count > 0)
                throw new PhrasePackageFileException("PACKAGE_VALIDATION_FAILED", string.Join("；", errors));

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
            await WriteEntryAsync(archive, "manifest.json", PhrasePackageJsonSerializer.SerializeManifest(document.Manifest), cancellationToken);
            await WriteEntryAsync(archive, "data.json", PhrasePackageJsonSerializer.SerializeData(document), cancellationToken);
            await file.FlushAsync(cancellationToken);
            Log("写入", traceId, "PACKAGE_WRITE_OK", started);
        }
        catch (PhrasePackageFileException)
        {
            Log("写入", traceId, "PACKAGE_WRITE_FAILED", started);
            throw;
        }
        catch (IOException exception)
        {
            Log("写入", traceId, "PACKAGE_IO_FAILED", started);
            throw new PhrasePackageFileException("PACKAGE_IO_FAILED", "话术包文件无法写入，请检查文件权限。", exception);
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), ".qphrase", StringComparison.OrdinalIgnoreCase))
            throw new PhrasePackageFileException("PACKAGE_EXTENSION_INVALID", "只能选择 .qphrase 话术包文件。");
        if (Directory.Exists(path))
            throw new PhrasePackageFileException("PACKAGE_PATH_INVALID", "选择的路径是文件夹，不是话术包文件。");
    }

    private static async Task<Stream> OpenLimitedEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length > MaxJsonBytes)
            throw new PhrasePackageFileException("PACKAGE_JSON_TOO_LARGE", "话术包 JSON 数据解压后过大。");
        var memory = new MemoryStream(capacity: checked((int)Math.Min(entry.Length, MaxJsonBytes)));
        await using var source = entry.Open();
        await source.CopyToAsync(memory, 64 * 1024, cancellationToken);
        if (memory.Length > MaxJsonBytes)
            throw new PhrasePackageFileException("PACKAGE_JSON_TOO_LARGE", "话术包 JSON 数据解压后过大。");
        memory.Position = 0;
        return memory;
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] data, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(data, cancellationToken);
    }

    private static void Log(string stage, Guid traceId, string code, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Console.WriteLine($"话术包{stage}阶段：TraceId={traceId:N}，结果码={code}，耗时={elapsed:F1}ms。");
    }
}
