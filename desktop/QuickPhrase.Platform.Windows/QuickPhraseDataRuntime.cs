using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 数据层组合根，统一拥有单写者队列、Repository、搜索运行时和话术包服务。
/// 数据库只在首次运行时初始化当前版本结构，不包含默认分类或示例话术。
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

    public static async Task<QuickPhraseDataRuntime> OpenAsync(QuickPhraseDataOptions options, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var connections = new SqliteConnectionFactory(options.DatabasePath);
        await new DatabaseInitializer(options, connections).EnsureInitializedAsync(cancellationToken);

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
