using QuickPhrase.Core;

namespace QuickPhrase.Desktop.Services;

/// <summary>
/// 管理窗口使用的进程内应用服务。
///
/// 该服务只依赖 Core 契约，由 Composition Root 注入 Platform.Windows 的实现，
/// 因此 View、ViewModel 和对话框不需要知道 SQLite、Windows API 或任何桥接协议。
/// </summary>
public sealed class CommandService : ICommandService
{
    private readonly IPhraseRepository _phrases;
    private readonly ISearchService _search;
    private readonly ICategoryRepository _categories;
    private readonly ISettingsRepository _settings;
    private readonly Func<Guid, CancellationToken, Task<bool>> _insertPhrase;
    private readonly Func<AppSettings, CancellationToken, Task<RepositoryResult<AppSettings>>>? _saveSettings;
    private readonly IPhrasePackageService? _phrasePackages;

    public CommandService(
        IPhraseRepository phrases,
        ISearchService search,
        ICategoryRepository categories,
        ISettingsRepository settings,
        Func<Guid, CancellationToken, Task<bool>>? insertPhrase = null,
        Func<AppSettings, CancellationToken, Task<RepositoryResult<AppSettings>>>? saveSettings = null,
        IPhrasePackageService? phrasePackages = null)
    {
        _phrases = phrases;
        _search = search;
        _categories = categories;
        _settings = settings;
        _insertPhrase = insertPhrase ?? ((_, _) => Task.FromResult(false));
        _saveSettings = saveSettings;
        _phrasePackages = phrasePackages;
    }

    public Task<IReadOnlyList<Phrase>> ListPhrasesAsync(CancellationToken cancellationToken = default) =>
        _phrases.ListAsync(cancellationToken);

    public Task<IReadOnlyList<Phrase>> SearchPhrasesAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = _search.Search(new SearchRequest(query, Math.Clamp(limit, 1, 100)));
        IReadOnlyList<Phrase> phrases = response.Items.Select(result => result.Phrase).ToArray();
        return Task.FromResult(phrases);
    }

    public Task<Phrase?> GetPhraseAsync(Guid id, CancellationToken cancellationToken = default) =>
        _phrases.GetAsync(id, cancellationToken);

    public async Task<RepositoryResult<Phrase>> CreatePhraseAsync(CreatePhraseCommand command, CancellationToken cancellationToken = default)
    {
        var result = await _phrases.CreateAsync(command, cancellationToken);
        return result.IsSuccess && result.Value is null
            ? RepositoryResult<Phrase>.Failure(new DataError("UNEXPECTED", "创建话术后未返回话术数据。"))
            : result;
    }

    public async Task<RepositoryResult<Phrase>> UpdatePhraseAsync(UpdatePhraseCommand command, CancellationToken cancellationToken = default)
    {
        var result = await _phrases.UpdateAsync(command, cancellationToken);
        return result.IsSuccess && result.Value is null
            ? RepositoryResult<Phrase>.Failure(new DataError("UNEXPECTED", "更新话术后未返回话术数据。"))
            : result;
    }

    public async Task<bool> DeletePhraseAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default)
    {
        var result = await _phrases.DeleteAsync(id, expectedVersion, cancellationToken);
        return result.IsSuccess && result.Value?.Deleted == true;
    }

    public Task<bool> InsertPhraseAsync(Guid id, CancellationToken cancellationToken = default) =>
        _insertPhrase(id, cancellationToken);

    public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
        _categories.ListAsync(cancellationToken);

    public async Task<RepositoryResult<Category>> CreateCategoryAsync(CreateCategoryCommand command, CancellationToken cancellationToken = default)
    {
        var result = await _categories.CreateAsync(command, cancellationToken);
        return result.IsSuccess && result.Value is null
            ? RepositoryResult<Category>.Failure(new DataError("UNEXPECTED", "创建分类后未返回分类数据。"))
            : result;
    }

    public async Task<RepositoryResult<Category>> RenameCategoryAsync(RenameCategoryCommand command, CancellationToken cancellationToken = default)
    {
        var result = await _categories.RenameAsync(command, cancellationToken);
        return result.IsSuccess && result.Value is null
            ? RepositoryResult<Category>.Failure(new DataError("UNEXPECTED", "重命名分类后未返回分类数据。"))
            : result;
    }

    public async Task<RepositoryResult<Category>> MoveCategoryAsync(MoveCategoryCommand command, CancellationToken cancellationToken = default)
    {
        var result = await _categories.MoveAsync(command, cancellationToken);
        return result.IsSuccess && result.Value is null
            ? RepositoryResult<Category>.Failure(new DataError("UNEXPECTED", "移动分类后未返回分类数据。"))
            : result;
    }

    public Task<RepositoryResult<DeleteResult>> DeleteCategoryAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default) =>
        _categories.DeleteAsync(id, expectedVersion, cancellationToken);

    public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        _settings.LoadAsync(cancellationToken);

    public Task<PhrasePackageLocalSnapshot> CapturePhrasePackageSnapshotAsync(CancellationToken cancellationToken = default) =>
        RequirePhrasePackages().CaptureSnapshotAsync(cancellationToken);

    public Task<PhrasePackageDocument> ReadPhrasePackageAsync(string path, CancellationToken cancellationToken = default) =>
        RequirePhrasePackages().ReadAsync(path, cancellationToken);

    public Task WritePhrasePackageAsync(string path, PhrasePackageDocument document, CancellationToken cancellationToken = default) =>
        RequirePhrasePackages().WriteAsync(path, document, cancellationToken);

    public Task<PhrasePackageImportResult> ImportPhrasePackageAsync(PhrasePackageImportPlan plan, CancellationToken cancellationToken = default) =>
        RequirePhrasePackages().ImportAsync(plan, cancellationToken);

    private IPhrasePackageService RequirePhrasePackages() =>
        _phrasePackages ?? throw new InvalidOperationException("当前应用未初始化话术包服务。");
    public Task<RepositoryResult<AppSettings>> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        _saveSettings is null
            ? _settings.SaveAsync(settings, settings.Version, cancellationToken)
            : _saveSettings(settings, cancellationToken);
}
