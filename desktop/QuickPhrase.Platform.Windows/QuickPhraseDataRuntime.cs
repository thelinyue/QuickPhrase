using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 数据层组合根，统一拥有迁移器、单写者队列、Repository、搜索运行时和话术包服务。
/// 话术包写入仍由本类编排：先拿搜索变更门，再由 SQLite 写队列执行单事务，提交成功后刷新内存索引。
/// </summary>
public sealed class QuickPhraseDataRuntime : IAsyncDisposable, IPhrasePackageService
{
    private readonly SqliteWriteQueue _writeQueue;
    private readonly PhraseSearchRuntime _searchRuntime;
    private readonly SqlitePhrasePackageImporter _packageImporter;
    private readonly PhrasePackageFileStore _packageFiles = new();

    private QuickPhraseDataRuntime(
        QuickPhraseDataOptions options,
        SqliteWriteQueue writeQueue,
        PhraseSearchRuntime searchRuntime,
        SqlitePhrasePackageImporter packageImporter,
        ICategoryRepository categories,
        ISettingsRepository settings,
        ISearchHistoryRepository searchHistory)
    {
        Options = options;
        _writeQueue = writeQueue;
        _searchRuntime = searchRuntime;
        _packageImporter = packageImporter;
        Phrases = searchRuntime.Phrases;
        Search = searchRuntime.Search;
        Categories = categories;
        Settings = settings;
        SearchHistory = searchHistory;
    }

    public QuickPhraseDataOptions Options { get; }
    public string DatabasePath => Options.DatabasePath;
    public IPhraseRepository Phrases { get; }
    public ISearchService Search { get; }
    public ICategoryRepository Categories { get; }
    public ISettingsRepository Settings { get; }
    public ISearchHistoryRepository SearchHistory { get; }

    public Task<string> CreateBackupAsync(string reason, CancellationToken cancellationToken = default) =>
        SqliteBackupService.CreateAsync(Options, reason, cancellationToken);

    public static Task<string> CreateBackupOnlyAsync(QuickPhraseDataOptions options, string reason, CancellationToken cancellationToken = default) =>
        SqliteBackupService.CreateAsync(options, reason, cancellationToken);

    public static async Task<QuickPhraseDataRuntime> OpenAsync(QuickPhraseDataOptions options, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(options.BackupDirectory);
        var connections = new SqliteConnectionFactory(options.DatabasePath);
        await new MigrationRunner(options, connections).EnsureMigratedAsync(cancellationToken);

        var queue = new SqliteWriteQueue(connections, options.WriteQueueCapacity, options.ShutdownTimeout);
        try
        {
            await queue.StartAsync(cancellationToken);
            var clock = options.TimeProvider;
            var rawPhrases = new SqlitePhraseRepository(connections, queue, clock);
            var rawCategories = new SqliteCategoryRepository(connections, queue, clock);
            var searchRuntime = await PhraseSearchRuntime.CreateAsync(rawPhrases, new PinyinMProvider(), cancellationToken);
            var searchHistory = new SqliteSearchHistoryRepository(connections, queue, clock);
            return new QuickPhraseDataRuntime(
                options,
                queue,
                searchRuntime,
                new SqlitePhrasePackageImporter(queue, clock),
                searchRuntime.WrapCategoryRepository(rawCategories),
                new SqliteSettingsRepository(connections, queue, clock),
                searchHistory);
        }
        catch
        {
            await queue.DisposeAsync();
            throw;
        }
    }

    public Task<PhrasePackageDocument> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        _packageFiles.ReadAsync(path, cancellationToken);

    public Task WriteAsync(string path, PhrasePackageDocument document, CancellationToken cancellationToken = default) =>
        _packageFiles.WriteAsync(path, document, cancellationToken);

    public async Task<PhrasePackageLocalSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
        new(await Categories.ListAsync(cancellationToken), await Phrases.ListAsync(cancellationToken));

    public Task<PhrasePackageImportResult> ImportAsync(PhrasePackageImportPlan plan, CancellationToken cancellationToken = default) =>
        _searchRuntime.MutateBatchAsync(
            ct => _packageImporter.ImportAsync(plan, ct),
            result => result.Succeeded,
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _searchRuntime.DisposeAsync();
        await _writeQueue.DisposeAsync();
    }
}
