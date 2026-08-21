using System.Diagnostics;
using System.Text;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// CSV 批量导入模板的 Windows 文件存储。
/// 文件访问、路径校验和原子替换保留在平台层；Core 仅负责 CSV 文本到领域文档的转换。
/// </summary>
public sealed class PhraseBatchImportCsvFileStore
{
    public const long MaxFileBytes = 50L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>将固定 CSV 模板以 UTF-8 BOM 编码写入目标路径，保证 Windows Excel 可正确识别中文。</summary>
    public async Task WriteTemplateAsync(string path, CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        string? temporaryPath = null;
        try
        {
            var fullPath = ValidatePath(path);
            var directory = Path.GetDirectoryName(fullPath) ?? throw new PhraseBatchImportFileException("CSV_PATH_INVALID", "CSV 模板文件路径无效。");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{traceId:N}.tmp");
            await File.WriteAllTextAsync(temporaryPath, PhraseBatchImportCsv.CreateTemplate(), Utf8WithBom, cancellationToken);
            ReplaceFile(temporaryPath, fullPath);
            temporaryPath = null;
            Log("写入模板", traceId, "CSV_TEMPLATE_WRITE_OK", started);
        }
        catch (OperationCanceledException)
        {
            Log("写入模板", traceId, "CSV_TEMPLATE_WRITE_CANCELLED", started);
            throw;
        }
        catch (PhraseBatchImportFileException exception)
        {
            Log("写入模板", traceId, exception.Code, started);
            throw new PhraseBatchImportCsvException(exception.Code, 0, exception.Message);
        }
        catch (UnauthorizedAccessException)
        {
            Log("写入模板", traceId, "CSV_ACCESS_DENIED", started);
            throw new PhraseBatchImportCsvException("CSV_ACCESS_DENIED", 0, "没有权限写入 CSV 模板文件。");
        }
        catch (ArgumentException)
        {
            Log("写入模板", traceId, "CSV_PATH_INVALID", started);
            throw new PhraseBatchImportCsvException("CSV_PATH_INVALID", 0, "CSV 模板文件路径无效。");
        }
        catch (IOException)
        {
            Log("写入模板", traceId, "CSV_IO_FAILED", started);
            throw new PhraseBatchImportCsvException("CSV_IO_FAILED", 0, "CSV 模板文件无法写入，请检查文件权限。");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    /// <summary>读取 CSV 文件并转换为现有话术包文档；格式错误会保留行号和结果码供界面直接提示。</summary>
    public async Task<PhrasePackageDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        try
        {
            var fullPath = ValidatePath(path);
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
                throw new PhraseBatchImportFileException("CSV_NOT_FOUND", "找不到指定的 CSV 批量导入文件。");
            if (fileInfo.Length > MaxFileBytes)
                throw new PhraseBatchImportFileException("CSV_TOO_LARGE", "CSV 批量导入文件不能超过 50 MB。");

            var csv = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken);
            var document = PhraseBatchImportCsv.Parse(csv);
            Log("读取", traceId, "CSV_READ_OK", started);
            return document;
        }
        catch (OperationCanceledException)
        {
            Log("读取", traceId, "CSV_READ_CANCELLED", started);
            throw;
        }
        catch (PhraseBatchImportCsvException exception)
        {
            Log("读取", traceId, exception.Code, started);
            throw;
        }
        catch (PhraseBatchImportFileException exception)
        {
            Log("读取", traceId, exception.Code, started);
            throw new PhraseBatchImportCsvException(exception.Code, 0, exception.Message);
        }
        catch (UnauthorizedAccessException)
        {
            Log("读取", traceId, "CSV_ACCESS_DENIED", started);
            throw new PhraseBatchImportCsvException("CSV_ACCESS_DENIED", 0, "没有权限读取 CSV 批量导入文件。");
        }
        catch (ArgumentException)
        {
            Log("读取", traceId, "CSV_PATH_INVALID", started);
            throw new PhraseBatchImportCsvException("CSV_PATH_INVALID", 0, "CSV 批量导入文件路径无效。");
        }
        catch (IOException)
        {
            Log("读取", traceId, "CSV_IO_FAILED", started);
            throw new PhraseBatchImportCsvException("CSV_IO_FAILED", 0, "CSV 批量导入文件无法读取，请检查文件权限。");
        }
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new PhraseBatchImportFileException("CSV_PATH_INVALID", "CSV 文件路径不能为空。");
        if (!string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
            throw new PhraseBatchImportFileException("CSV_EXTENSION_INVALID", "只能选择 .csv 批量导入文件。");
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
            throw new PhraseBatchImportFileException("CSV_PATH_INVALID", "选择的路径是文件夹，不是 CSV 文件。");
        return fullPath;
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
        Console.WriteLine($"CSV 批量导入{stage}阶段：TraceId={traceId:N}，结果码={code}，耗时={elapsed:F1}ms。");
    }
}

/// <summary>CSV 模板文件读写错误。错误消息面向非开发者，不包含 CSV 话术正文。</summary>
internal sealed class PhraseBatchImportFileException : Exception
{
    public PhraseBatchImportFileException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}
