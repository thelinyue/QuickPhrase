using QuickPhrase.Core;

namespace QuickPhrase.Desktop.Services;

/// <summary>
/// 管理窗口使用的进程内应用服务契约。
/// ViewModel 只依赖该契约，不直接访问 Platform.Windows 或持久化实现。
/// </summary>
public interface ICommandService
{
    Task<IReadOnlyList<Phrase>> ListPhrasesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Phrase>> SearchPhrasesAsync(string query, int limit, CancellationToken cancellationToken = default);
    Task<Phrase?> GetPhraseAsync(Guid id, CancellationToken cancellationToken = default);
    Task<RepositoryResult<Phrase>> CreatePhraseAsync(CreatePhraseCommand command, CancellationToken cancellationToken = default);
    Task<RepositoryResult<Phrase>> UpdatePhraseAsync(UpdatePhraseCommand command, CancellationToken cancellationToken = default);
    Task<bool> DeletePhraseAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default);
    Task<bool> InsertPhraseAsync(Phrase phrase, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken cancellationToken = default);
    Task<RepositoryResult<Category>> CreateCategoryAsync(CreateCategoryCommand command, CancellationToken cancellationToken = default);
    Task<RepositoryResult<Category>> RenameCategoryAsync(RenameCategoryCommand command, CancellationToken cancellationToken = default);
    Task<RepositoryResult<Category>> MoveCategoryAsync(MoveCategoryCommand command, CancellationToken cancellationToken = default);
    Task<RepositoryResult<DeleteResult>> DeleteCategoryAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default);
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<RepositoryResult<AppSettings>> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<PhrasePackageLocalSnapshot> CapturePhrasePackageSnapshotAsync(CancellationToken cancellationToken = default);
    Task<PhrasePackageDocument> ReadPhrasePackageAsync(string path, CancellationToken cancellationToken = default);
    Task WritePhrasePackageAsync(string path, PhrasePackageDocument document, CancellationToken cancellationToken = default);
    Task<PhrasePackageImportResult> ImportPhrasePackageAsync(PhrasePackageImportPlan plan, CancellationToken cancellationToken = default);
}
