namespace QuickPhrase.Core;

/// <summary>
/// 话术 Repository 与内存搜索索引的组合根。它把“提交成功后更新索引”固化在同一条异步门内，避免并发完成顺序导致快照倒退。
/// </summary>
public sealed class PhraseSearchRuntime : IAsyncDisposable
{
    private readonly IPhraseRepository _source;
    private readonly SearchService _search;
    private readonly IEnterpriseCatalog? _enterprise;
    private ICategoryRepository? _categories;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _rebuildLock = new();
    private Task? _rebuildTask;
    private bool _rebuildRequested;

    private PhraseSearchRuntime(IPhraseRepository source, SearchService search, IEnterpriseCatalog? enterprise, ICategoryRepository? categories)
    {
        _source = source;
        _search = search;
        _enterprise = enterprise;
        _categories = categories;
        Phrases = new IndexedPhraseRepository(this);
        Search = search;
    }

    public IPhraseRepository Phrases { get; }
    public ISearchService Search { get; }
    /// <summary>
    /// 执行一次外部批量写入。写入事务成功后才从事实源重建内存索引；失败或回滚不会修改当前索引。
    /// </summary>
    public async Task<T> MutateBatchAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<T, bool> isCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(isCommitted);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _mutationGate.WaitAsync(linked.Token);
        try
        {
            var result = await operation(linked.Token);
            if (!isCommitted(result)) return result;
            try
            {
                var phrases = await LoadAllPhrasesAsync(linked.Token);
                var categoryNames = await LoadCategoryNamesAsync(linked.Token);
                _search.Replace(phrases, categoryNames, allowPinyinFallback: false, out var degraded);
                if (degraded) ScheduleRebuild();
            }
            catch (Exception)
            {
                _search.MarkDirty("话术包已提交，但搜索索引刷新失败；正在后台恢复。");
                ScheduleRebuild();
            }
            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }
    /// <summary>为分类仓储增加同一条搜索索引提交门，确保分类级联删除成功后再移除被删除话术。</summary>
    public ICategoryRepository WrapCategoryRepository(ICategoryRepository source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _categories ??= source;
        return new IndexedCategoryRepository(this, source);
    }

    public static async Task<PhraseSearchRuntime> CreateAsync(
        IPhraseRepository repository,
        IPinyinProvider pinyinProvider,
        IEnterpriseCatalog? enterpriseCatalog = null,
        CancellationToken cancellationToken = default,
        ICategoryRepository? categoryRepository = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(pinyinProvider);
        var search = new SearchService(pinyinProvider);
        var runtime = new PhraseSearchRuntime(repository, search, enterpriseCatalog, categoryRepository);
        var phrases = await runtime.LoadAllPhrasesAsync(cancellationToken);
        var categoryNames = await runtime.LoadCategoryNamesAsync(cancellationToken);
        search.Replace(phrases, categoryNames, allowPinyinFallback: true, out var degraded);
        if (degraded) runtime.ScheduleRebuild();
        return runtime;
    }

    /// <summary>企业缓存提交后在短临界区重建个人+企业联合索引；数据库失败时调用方不得调用本方法。</summary>
    public async Task RefreshEnterpriseAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _mutationGate.WaitAsync(linked.Token);
        try
        {
            var phrases = await LoadAllPhrasesAsync(linked.Token);
            var categoryNames = await LoadCategoryNamesAsync(linked.Token);
            _search.Replace(phrases, categoryNames, allowPinyinFallback: false, out var degraded);
            if (degraded) ScheduleRebuild();
        }
        catch
        {
            _search.MarkDirty("企业话术已同步，但搜索索引刷新失败；正在后台恢复。");
            ScheduleRebuild();
            throw;
        }
        finally { _mutationGate.Release(); }
    }

    private async Task<IReadOnlyList<Phrase>> LoadAllPhrasesAsync(CancellationToken cancellationToken)
    {
        var personal = await _source.ListAsync(cancellationToken);
        if (_enterprise is null) return personal;
        var enterprise = await _enterprise.ListPhrasesAsync(cancellationToken);
        return personal.Concat(enterprise).ToArray();
    }

    private async Task<IReadOnlyDictionary<Guid, string>> LoadCategoryNamesAsync(CancellationToken cancellationToken)
    {
        if (_categories is null) return new Dictionary<Guid, string>();
        var categories = await _categories.ListAsync(cancellationToken);
        return categories.ToDictionary(category => category.Id, category => category.Name);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        Task? rebuild;
        lock (_rebuildLock) rebuild = _rebuildTask;
        if (rebuild is not null)
        {
            try { await rebuild; } catch (OperationCanceledException) { }
        }
        _mutationGate.Dispose();
        _shutdown.Dispose();
    }

    private async Task<RepositoryResult<T>> MutateAsync<T>(
        Func<CancellationToken, Task<RepositoryResult<T>>> operation,
        Func<RepositoryResult<T>, CancellationToken, Task> publish,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _mutationGate.WaitAsync(linked.Token);
        try
        {
            var result = await operation(linked.Token);
            if (result.IsSuccess) await publish(result, linked.Token);
            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task PublishPhraseAsync(Phrase? phrase, CommittedDataChange? change, CancellationToken cancellationToken)
    {
        if (phrase is null || change is null) return;
        var wasDirty = _search.Status.State != SearchIndexState.Ready;
        var categoryNames = await LoadCategoryNamesAsync(cancellationToken);
        categoryNames.TryGetValue(phrase.CategoryId, out var categoryName);
        if (!_search.TryBuildEntry(phrase, categoryName, out var entry))
        {
            _search.MarkDirty("话术已保存，但拼音索引更新失败；正在保留旧索引并后台恢复。");
            ScheduleRebuild();
            return;
        }
        _searchInternalUpsert(entry, markReady: !wasDirty);
        if (wasDirty) ScheduleRebuild();
        await Task.CompletedTask;
    }

    private async Task PublishDeleteAsync(DeleteResult? deletion, Guid id, CancellationToken cancellationToken)
    {
        if (deletion is null || !deletion.Deleted || deletion.Change is null) return;
        var wasDirty = _search.Status.State != SearchIndexState.Ready;
        _searchInternalRemove(id);
        if (wasDirty) ScheduleRebuild();
        await Task.CompletedTask;
    }

    private async Task PublishCategoryDeleteAsync(DeleteResult? deletion, CancellationToken cancellationToken)
    {
        if (deletion is null || !deletion.Deleted || deletion.Change is null || deletion.DeletedPhraseIds is not { Count: > 0 }) return;
        var wasDirty = _search.Status.State != SearchIndexState.Ready;
        foreach (var phraseId in deletion.DeletedPhraseIds) _searchInternalRemove(phraseId);
        if (wasDirty) ScheduleRebuild();
        await Task.CompletedTask;
    }

    private async Task PublishCategoryRenameAsync(Category? category, CancellationToken cancellationToken)
    {
        if (category is null) return;
        var phrases = await LoadAllPhrasesAsync(cancellationToken);
        var categoryNames = await LoadCategoryNamesAsync(cancellationToken);
        _search.Replace(phrases, categoryNames, allowPinyinFallback: false, out var degraded);
        if (degraded) ScheduleRebuild();
    }

    private void _searchInternalUpsert(SearchService.SearchEntry entry, bool markReady = true) => _search.Upsert(entry, markReady);
    private void _searchInternalRemove(Guid id) => _search.Remove(id);

    private void ScheduleRebuild()
    {
        if (_shutdown.IsCancellationRequested) return;
        lock (_rebuildLock)
        {
            _rebuildRequested = true;
            if (_rebuildTask is { IsCompleted: false }) return;
            _rebuildTask = Task.Run(ProcessRebuildRequestsAsync, CancellationToken.None);
        }
    }

    /// <summary>
    /// 串行消费索引重建请求。若一次重建尚未结束时又发生提交，保留下一次请求，
    /// 避免当前任务结束前的竞态让索引永久停留在脏状态。
    /// </summary>
    private async Task ProcessRebuildRequestsAsync()
    {
        while (true)
        {
            lock (_rebuildLock)
            {
                if (_shutdown.IsCancellationRequested || !_rebuildRequested)
                {
                    _rebuildTask = null;
                    return;
                }
                _rebuildRequested = false;
            }

            await RebuildAsync();
        }
    }

    private async Task RebuildAsync()
    {
        _search.MarkRebuilding();
        try
        {
            await _mutationGate.WaitAsync(_shutdown.Token);
            try
            {
                var phrases = await LoadAllPhrasesAsync(_shutdown.Token);
                var categoryNames = await LoadCategoryNamesAsync(_shutdown.Token);
                _search.Replace(phrases, categoryNames, allowPinyinFallback: false, out _);
            }
            finally
            {
                _mutationGate.Release();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception)
        {
            _search.MarkDirty("搜索索引恢复失败，将在下一次话术变更后重试。");
        }
    }

    private sealed class IndexedCategoryRepository : ICategoryRepository
    {
        private readonly PhraseSearchRuntime _owner;
        private readonly ICategoryRepository _source;

        public IndexedCategoryRepository(PhraseSearchRuntime owner, ICategoryRepository source)
        {
            _owner = owner;
            _source = source;
        }

        public Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken = default) => _source.ListAsync(cancellationToken);
        public Task<RepositoryResult<Category>> CreateAsync(CreateCategoryCommand command, CancellationToken cancellationToken = default) => _source.CreateAsync(command, cancellationToken);
        public Task<RepositoryResult<Category>> RenameAsync(RenameCategoryCommand command, CancellationToken cancellationToken = default) =>
            _owner.MutateAsync(
                ct => _source.RenameAsync(command, ct),
                (result, ct) => _owner.PublishCategoryRenameAsync(result.Value, ct),
                cancellationToken);
        public Task<RepositoryResult<Category>> MoveAsync(MoveCategoryCommand command, CancellationToken cancellationToken = default) => _source.MoveAsync(command, cancellationToken);

        public Task<RepositoryResult<DeleteResult>> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default) =>
            _owner.MutateAsync(
                ct => _source.DeleteAsync(id, expectedVersion, ct),
                (result, ct) => _owner.PublishCategoryDeleteAsync(result.Value, ct),
                cancellationToken);
    }
    private sealed class IndexedPhraseRepository : IPhraseRepository
    {
        private readonly PhraseSearchRuntime _owner;
        public IndexedPhraseRepository(PhraseSearchRuntime owner) => _owner = owner;

        public Task<IReadOnlyList<Phrase>> ListAsync(CancellationToken cancellationToken = default) => _owner._source.ListAsync(cancellationToken);
        public Task<Phrase?> GetAsync(Guid id, CancellationToken cancellationToken = default) => _owner._source.GetAsync(id, cancellationToken);

        public Task<RepositoryResult<Phrase>> CreateAsync(CreatePhraseCommand command, CancellationToken cancellationToken = default) =>
            _owner.MutateAsync(
                ct => _owner._source.CreateAsync(command, ct),
                (result, ct) => _owner.PublishPhraseAsync(result.Value, result.Change, ct),
                cancellationToken);

        public Task<RepositoryResult<Phrase>> UpdateAsync(UpdatePhraseCommand command, CancellationToken cancellationToken = default) =>
            _owner.MutateAsync(
                ct => _owner._source.UpdateAsync(command, ct),
                (result, ct) => _owner.PublishPhraseAsync(result.Value, result.Change, ct),
                cancellationToken);

        public Task<RepositoryResult<DeleteResult>> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default) =>
            _owner.MutateAsync(
                ct => _owner._source.DeleteAsync(id, expectedVersion, ct),
                (result, ct) => _owner.PublishDeleteAsync(result.Value, id, ct),
                cancellationToken);

        public Task<RepositoryResult<Phrase>> IncrementUsageAsync(Guid id, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default) =>
            _owner.MutateAsync(
                ct => _owner._source.IncrementUsageAsync(id, usedAtUtc, ct),
                (result, ct) => _owner.PublishPhraseAsync(result.Value, result.Change, ct),
                cancellationToken);
    }
}
