namespace QuickPhrase.Core;

/// <summary>话术包批量导入提交后的统计结果，不携带话术正文或本机路径。</summary>
public sealed record PhrasePackageImportResult(
    bool Succeeded,
    int NewCategoryCount,
    int NewPhraseCount,
    int SkippedDuplicateCount,
    string Code,
    string Message,
    Guid TraceId);

/// <summary>话术包服务的进程内契约，Desktop 只依赖此接口，不依赖平台文件或数据库实现。</summary>
public interface IPhrasePackageService
{
    Task<PhrasePackageDocument> ReadAsync(string path, CancellationToken cancellationToken = default);
    Task WriteAsync(string path, PhrasePackageDocument document, CancellationToken cancellationToken = default);
    Task<PhrasePackageDocument> ReadBatchImportCsvAsync(string path, CancellationToken cancellationToken = default);
    Task WriteBatchImportTemplateAsync(string path, CancellationToken cancellationToken = default);
    Task<PhrasePackageLocalSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default);
    Task<PhrasePackageImportResult> ImportAsync(PhrasePackageImportPlan plan, CancellationToken cancellationToken = default);
}
