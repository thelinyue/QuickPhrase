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
/// .qphrase ZIP 文件存储。输入只允许固定的两个 JSON 条目，并使用流式上限、路径校验和临时文件替换保护本地文件。
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
            var fullPath = ValidatePath(path);
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
                throw new PhrasePackageFileException("PACKAGE_NOT_FOUND", "找不到指定的话术包文件。");
            if (fileInfo.Length > MaxFileBytes)
                throw new PhrasePackageFileException("PACKAGE_TOO_LARGE", "话术包文件不能超过 50 MB。");

            await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            ValidateEntries(archive);

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
        catch (OperationCanceledException)
        {
            Log("读取", traceId, "PACKAGE_READ_CANCELLED", started);
            throw;
        }
        catch (PhrasePackageFileException exception)
        {
            Log("读取", traceId, exception.Code, started);
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
        catch (UnauthorizedAccessException exception)
        {
            Log("读取", traceId, "PACKAGE_ACCESS_DENIED", started);
            throw new PhrasePackageFileException("PACKAGE_ACCESS_DENIED", "没有权限读取话术包文件。", exception);
        }
        catch (ArgumentException exception)
        {
            Log("读取", traceId, "PACKAGE_PATH_INVALID", started);
            throw new PhrasePackageFileException("PACKAGE_PATH_INVALID", "话术包文件路径无效。", exception);
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
        string? temporaryPath = null;
        try
        {
            var fullPath = ValidatePath(path);
            var errors = PhrasePackagePlanner.Validate(document);
            if (errors.Count > 0)
                throw new PhrasePackageFileException("PACKAGE_VALIDATION_FAILED", string.Join("；", errors));

            var directory = Path.GetDirectoryName(fullPath) ?? throw new PhrasePackageFileException("PACKAGE_PATH_INVALID", "话术包文件路径无效。");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{traceId:N}.tmp");
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                await WriteEntryAsync(archive, "manifest.json", PhrasePackageJsonSerializer.SerializeManifest(document.Manifest), cancellationToken);
                await WriteEntryAsync(archive, "data.json", PhrasePackageJsonSerializer.SerializeData(document), cancellationToken);
                archive.Dispose();
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }

            if (new FileInfo(temporaryPath).Length > MaxFileBytes)
                throw new PhrasePackageFileException("PACKAGE_TOO_LARGE", "生成的话术包文件不能超过 50 MB。");

            ReplaceFile(temporaryPath, fullPath);
            temporaryPath = null;
            Log("写入", traceId, "PACKAGE_WRITE_OK", started);
        }
        catch (OperationCanceledException)
        {
            Log("写入", traceId, "PACKAGE_WRITE_CANCELLED", started);
            throw;
        }
        catch (PhrasePackageFileException exception)
        {
            Log("写入", traceId, exception.Code, started);
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            Log("写入", traceId, "PACKAGE_ACCESS_DENIED", started);
            throw new PhrasePackageFileException("PACKAGE_ACCESS_DENIED", "没有权限写入话术包文件。", exception);
        }
        catch (ArgumentException exception)
        {
            Log("写入", traceId, "PACKAGE_PATH_INVALID", started);
            throw new PhrasePackageFileException("PACKAGE_PATH_INVALID", "话术包文件路径无效。", exception);
        }
        catch (IOException exception)
        {
            Log("写入", traceId, "PACKAGE_IO_FAILED", started);
            throw new PhrasePackageFileException("PACKAGE_IO_FAILED", "话术包文件无法写入，请检查文件权限。", exception);
        }
        catch (Exception exception)
        {
            Log("写入", traceId, "PACKAGE_WRITE_FAILED", started);
            throw new PhrasePackageFileException("PACKAGE_WRITE_FAILED", "话术包写入失败。", exception);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new PhrasePackageFileException("PACKAGE_PATH_INVALID", "话术包文件路径不能为空。");
        if (!string.Equals(Path.GetExtension(path), ".qphrase", StringComparison.OrdinalIgnoreCase))
            throw new PhrasePackageFileException("PACKAGE_EXTENSION_INVALID", "只能选择 .qphrase 话术包文件。");
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
            throw new PhrasePackageFileException("PACKAGE_PATH_INVALID", "选择的路径是文件夹，不是话术包文件。");
        return fullPath;
    }

    private static void ValidateEntries(ZipArchive archive)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (string.IsNullOrWhiteSpace(name) || entry.Name.Length == 0 || name.Contains('\\') || name.StartsWith("/", StringComparison.Ordinal) || Path.IsPathFullyQualified(name.Replace('/', Path.DirectorySeparatorChar)))
                throw new PhrasePackageFileException("PACKAGE_ENTRIES_INVALID", "话术包包含目录或非法路径条目。");
            if (name.Split('/', StringSplitOptions.None).Any(part => part is "." or ".."))
                throw new PhrasePackageFileException("PACKAGE_ENTRIES_INVALID", "话术包包含路径穿越条目。");
            if (!AllowedEntries.Contains(name) || !seen.Add(name))
                throw new PhrasePackageFileException("PACKAGE_ENTRIES_INVALID", "话术包只能包含 manifest.json 和 data.json，且不能包含重复条目。");
        }
        if (seen.Count != AllowedEntries.Count)
            throw new PhrasePackageFileException("PACKAGE_ENTRIES_INVALID", "话术包必须同时包含 manifest.json 和 data.json。");
    }

    private static async Task<Stream> OpenLimitedEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length > MaxJsonBytes)
            throw new PhrasePackageFileException("PACKAGE_JSON_TOO_LARGE", "话术包 JSON 数据解压后过大。");

        var memory = new MemoryStream(capacity: checked((int)Math.Min(entry.Length, MaxJsonBytes)));
        await using var source = entry.Open();
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > MaxJsonBytes)
            {
                memory.Dispose();
                throw new PhrasePackageFileException("PACKAGE_JSON_TOO_LARGE", "话术包 JSON 数据解压后过大。");
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        memory.Position = 0;
        return memory;
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] data, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(data, cancellationToken);
    }

    private static void ReplaceFile(string temporaryPath, string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            File.Move(temporaryPath, targetPath);
            return;
        }

        File.Replace(temporaryPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
    }

    private static void Log(string stage, Guid traceId, string code, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Console.WriteLine($"话术包{stage}阶段：TraceId={traceId:N}，结果码={code}，耗时={elapsed:F1}ms。");
    }
}
