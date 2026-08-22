using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop.Tests.Fakes;

/// <summary>
/// 内存版 ICommandService，供 ViewModel 单元测试使用。不依赖 Windows 平台实现或持久化。
/// 通过 Seed 注入预设数据，写操作直接反映到内存集合并返回成功结果。
/// </summary>
public sealed class FakeCommandService : ICommandService
{
    private readonly List<Phrase> _phrases = new();
    private readonly List<Category> _categories = new();
    private AppSettings _settings = new(1, false, false, true, new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space), false, true);

    public void Seed(IEnumerable<Phrase> phrases) => _phrases.AddRange(phrases);
    public int DeleteCategoryCalls { get; private set; }
    public bool ReturnSettingsConflictOnce { get; set; }
    public DataError? NextSettingsError { get; set; }
    public int SettingsUpdateCalls { get; private set; }
    public CreatePhraseCommand? LastCreatedPhraseCommand { get; private set; }
    public UpdatePhraseCommand? LastUpdatedPhraseCommand { get; private set; }
    public MediaImportResult? NextMediaImportResult { get; set; }
    public Exception? ReadMediaException { get; set; }
    public MediaAssetContent? NextMediaContent { get; set; }
    public DataError? NextPhraseSaveError { get; set; }
    public List<Guid> ReleasedMediaAssetIds { get; } = [];
    // 测试钩子：允许模拟搜索请求乱序完成，验证 ViewModel 只接受最新查询结果。
    public Func<string, CancellationToken, Task>? BeforeSearchAsync { get; set; }
    public event Action<string>? SearchCompleted;
    public void Seed(IEnumerable<Category> categories) => _categories.AddRange(categories);
    public PhrasePackageDocument? NextPackageDocument { get; set; }
    public PhrasePackageDocument? NextBatchImportCsvDocument { get; set; }
    public string? LastBatchImportTemplatePath { get; private set; }
    public PhrasePackageDocument? LastWrittenPackage { get; private set; }
    public PhrasePackageImportPlan? LastImportedPlan { get; private set; }
    public PhrasePackageImportResult ImportResult { get; set; } = new(true, 0, 0, 0, "PACKAGE_IMPORT_OK", "话术包导入完成。", Guid.NewGuid());

    public Task<IReadOnlyList<Phrase>> ListPhrasesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Phrase>>(_phrases.ToArray());

    public async Task<IReadOnlyList<Phrase>> SearchPhrasesAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        var q = (query ?? string.Empty).Trim();
        if (BeforeSearchAsync is not null) await BeforeSearchAsync(q, cancellationToken);

        var result = _phrases
            .Where(p => p.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || p.Body.TextProjection.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToArray();
        SearchCompleted?.Invoke(q);
        return result;
    }

    public Task<Phrase?> GetPhraseAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_phrases.FirstOrDefault(p => p.Id == id));

    public Task<RepositoryResult<Phrase>> CreatePhraseAsync(CreatePhraseCommand command, CancellationToken cancellationToken = default)
    {
        LastCreatedPhraseCommand = command;
        if (NextPhraseSaveError is { } createError) return Task.FromResult(RepositoryResult<Phrase>.Failure(createError));
        var phrase = new Phrase(
            command.Id, command.Title, command.Body, command.CategoryId, command.ShortcutMode,
            command.Shortcut is null ? null : new ShortcutValue(command.Shortcut, command.Shortcut),
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, command.ColorKey,
            command.SortOrder != 0 ? command.SortOrder : _phrases.Count(p => p.CategoryId == command.CategoryId) + 1);
        _phrases.Add(phrase);
        return Task.FromResult(RepositoryResult<Phrase>.Success(phrase));
    }

    public Task<RepositoryResult<Phrase>> UpdatePhraseAsync(UpdatePhraseCommand command, CancellationToken cancellationToken = default)
    {
        LastUpdatedPhraseCommand = command;
        if (NextPhraseSaveError is { } updateError) return Task.FromResult(RepositoryResult<Phrase>.Failure(updateError));
        var index = _phrases.FindIndex(p => p.Id == command.Id);
        if (index < 0) return Task.FromResult(RepositoryResult<Phrase>.Failure(new DataError("NOT_FOUND", "话术不存在")));
        var updated = _phrases[index] with
        {
            Title = command.Title,
            Body = command.Body,
            CategoryId = command.CategoryId,
            ShortcutMode = command.ShortcutMode,
            Shortcut = command.Shortcut is null ? null : new ShortcutValue(command.Shortcut, command.Shortcut),
            ColorKey = command.ColorKey,
            SortOrder = command.SortOrder,
            Version = command.ExpectedVersion + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _phrases[index] = updated;
        return Task.FromResult(RepositoryResult<Phrase>.Success(updated));
    }

    public Task<bool> DeletePhraseAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default)
    {
        var index = _phrases.FindIndex(p => p.Id == id);
        if (index < 0) return Task.FromResult(false);
        _phrases.RemoveAt(index);
        return Task.FromResult(true);
    }

    public Task<bool> InsertPhraseAsync(Phrase phrase, CancellationToken cancellationToken = default)
        => Task.FromResult(_phrases.Any(p => p.Id == phrase.Id && p.Scope == phrase.Scope));

    public Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Category>>(_categories.ToArray());

    public Task<RepositoryResult<Category>> CreateCategoryAsync(CreateCategoryCommand command, CancellationToken cancellationToken = default)
    {
        var sortOrder = command.SortOrder ?? (command.ParentId is null
            ? _categories.Where(category => category.ParentId is null).Select(category => category.SortOrder).DefaultIfEmpty(-10).Max() + 10
            : 0);
        var category = new Category(command.Id, command.ParentId, command.Name, sortOrder, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        _categories.Add(category);
        return Task.FromResult(RepositoryResult<Category>.Success(category));
    }

    public Task<RepositoryResult<Category>> RenameCategoryAsync(RenameCategoryCommand command, CancellationToken cancellationToken = default)
    {
        var index = _categories.FindIndex(c => c.Id == command.Id);
        if (index < 0) return Task.FromResult(RepositoryResult<Category>.Failure(new DataError("NOT_FOUND", "分类不存在")));
        var updated = _categories[index] with { Name = command.Name, SortOrder = command.SortOrder };
        _categories[index] = updated;
        return Task.FromResult(RepositoryResult<Category>.Success(updated));
    }

    public Task<RepositoryResult<DeleteResult>> DeleteCategoryAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default)
    {
        DeleteCategoryCalls++;
        var category = _categories.FirstOrDefault(item => item.Id == id);
        if (category is null)
            return Task.FromResult(RepositoryResult<DeleteResult>.Success(new DeleteResult(false, null)));
        if (expectedVersion.HasValue && category.Version != expectedVersion.Value)
            return Task.FromResult(RepositoryResult<DeleteResult>.Failure(new DataError("VERSION_CONFLICT", "分类已被其他操作修改。", id, category.Name)));

        var subtreeIds = new HashSet<Guid> { id };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var child in _categories.Where(item => item.ParentId.HasValue && subtreeIds.Contains(item.ParentId.Value)))
                changed |= subtreeIds.Add(child.Id);
        }
        var deletedPhraseIds = _phrases.Where(phrase => subtreeIds.Contains(phrase.CategoryId)).Select(phrase => phrase.Id).ToArray();
        _phrases.RemoveAll(phrase => deletedPhraseIds.Contains(phrase.Id));
        _categories.RemoveAll(item => subtreeIds.Contains(item.Id));
        return Task.FromResult(RepositoryResult<DeleteResult>.Success(new DeleteResult(true, null, deletedPhraseIds)));
    }
    public Task<RepositoryResult<Category>> MoveCategoryAsync(MoveCategoryCommand command, CancellationToken cancellationToken = default)
    {
        var index = _categories.FindIndex(c => c.Id == command.Id);
        if (index < 0) return Task.FromResult(RepositoryResult<Category>.Failure(new DataError("NOT_FOUND", "分类不存在")));
        var updated = _categories[index] with { ParentId = command.ParentId, SortOrder = command.SortOrder, Version = command.ExpectedVersion + 1 };
        _categories[index] = updated;
        return Task.FromResult(RepositoryResult<Category>.Success(updated));
    }

    public Task<MediaImportResult> ImportImageAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(NextMediaImportResult ?? MediaImportResult.Failure("MEDIA_NOT_CONFIGURED", "测试未配置图片导入结果。"));

    public Task<MediaAssetContent?> ReadMediaAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        if (ReadMediaException is not null) return Task.FromException<MediaAssetContent?>(ReadMediaException);
        return Task.FromResult<MediaAssetContent?>(NextMediaContent);
    }

    public Task DeleteMediaIfUnreferencedAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        ReleasedMediaAssetIds.Add(assetId);
        return Task.CompletedTask;
    }
    public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_settings);

    public Task<RepositoryResult<AppSettings>> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        SettingsUpdateCalls++;
        if (NextSettingsError is { } nextError)
        {
            NextSettingsError = null;
            return Task.FromResult(RepositoryResult<AppSettings>.Failure(nextError));
        }
        if (ReturnSettingsConflictOnce)
        {
            ReturnSettingsConflictOnce = false;
            _settings = _settings with { Version = _settings.Version + 1, StartMinimized = true };
            return Task.FromResult(RepositoryResult<AppSettings>.Failure(
                new DataError("VERSION_CONFLICT", "设置已被其他操作修改。")));
        }

        _settings = settings with { Version = settings.Version + 1 };
        return Task.FromResult(RepositoryResult<AppSettings>.Success(_settings));
    }

    public Task<PhrasePackageLocalSnapshot> CapturePhrasePackageSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PhrasePackageLocalSnapshot(_categories.ToArray(), _phrases.ToArray()));

    public Task<PhrasePackageDocument> ReadPhrasePackageAsync(string path, CancellationToken cancellationToken = default) =>
        NextPackageDocument is null
            ? Task.FromException<PhrasePackageDocument>(new NotSupportedException("测试替身未配置话术包。"))
            : Task.FromResult(NextPackageDocument);

    public Task WritePhrasePackageAsync(string path, PhrasePackageDocument document, CancellationToken cancellationToken = default)
    {
        LastWrittenPackage = document;
        return Task.CompletedTask;
    }

    public Task<PhrasePackageDocument> ReadBatchImportCsvAsync(string path, CancellationToken cancellationToken = default) =>
        NextBatchImportCsvDocument is null
            ? Task.FromException<PhrasePackageDocument>(new NotSupportedException("测试替身未配置 CSV 批量导入数据。"))
            : Task.FromResult(NextBatchImportCsvDocument);

    public Task WriteBatchImportTemplateAsync(string path, CancellationToken cancellationToken = default)
    {
        LastBatchImportTemplatePath = path;
        return Task.CompletedTask;
    }

    public Task<PhrasePackageImportResult> ImportPhrasePackageAsync(PhrasePackageImportPlan plan, CancellationToken cancellationToken = default)
    {
        LastImportedPlan = plan;
        return Task.FromResult(ImportResult);
    }
}
