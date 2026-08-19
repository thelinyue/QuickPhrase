using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;
using Xunit.Abstractions;

namespace QuickPhrase.Architecture.Tests;

public sealed class Phase3SearchTests
{
    private readonly ITestOutputHelper _output;

    public Phase3SearchTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PinyinProviderSupportsInitialsFullPinyinAndMixedText()
    {
        var provider = new PinyinMProvider();
        var terms = provider.BuildTerms("恢复出厂设置");
        var mixed = provider.BuildTerms("Windows相机");

        Assert.Contains("hfccsz", terms.Initials);
        Assert.Contains(terms.FullSpellings, value => value.StartsWith("huifuchuchang", StringComparison.Ordinal));
        Assert.Contains(mixed.FullSpellings, value => value.Contains("windowsxiangji", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DataRuntimeExposesSearchOverSeededPhrases()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));

        var initials = runtime.Search.Search(new SearchRequest("hfcc"));
        var multiple = runtime.Search.Search(new SearchRequest("hf"));

        Assert.Contains(initials.Items, result => result.Phrase.Title == "恢复出厂设置");
        Assert.InRange(multiple.Items.Length, 6, 8);
    }

    [Fact]
    public async Task TenThousandPhraseSearchReportsPercentiles()
    {
        var now = DateTimeOffset.UtcNow;
        var phrases = Enumerable.Range(0, 10_000)
            .Select(index => Phrase($"perf-{index}", $"恢复出厂设置 {index}", $"请检查设备网络并记录问题 {index}。", index % 200, now.AddSeconds(-index)))
            .ToArray();
        await using var runtime = await PhraseSearchRuntime.CreateAsync(new FakePhraseRepository(phrases), new PinyinMProvider());
        var queries = new[] { "恢复", "hfcc", "huifuchuchang", "sn", "网络", "没有匹配结果" };
        foreach (var query in queries) _ = runtime.Search.Search(new SearchRequest(query));

        var samples = new List<double>(queries.Length * 100);
        for (var round = 0; round < 100; round++)
        {
            foreach (var query in queries)
            {
                var stopwatch = Stopwatch.StartNew();
                _ = runtime.Search.Search(new SearchRequest(query));
                stopwatch.Stop();
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        samples.Sort();
        var p50 = Percentile(samples, 0.50);
        var p95 = Percentile(samples, 0.95);
        var p99 = Percentile(samples, 0.99);
        _output.WriteLine($"SEARCH_PERF count=10000 p50={p50:F3}ms p95={p95:F3}ms p99={p99:F3}ms");
#if DEBUG
        Assert.True(p95 <= 150, $"Debug 搜索 P95 超过 150ms：{p95:F3}ms");
#else
        Assert.True(p95 <= 50, $"Release 搜索 P95 超过 50ms：{p95:F3}ms");
#endif
    }

    [Fact]
    public async Task SearchRanksTitleBeforePinyinAndContent()
    {
        var now = DateTimeOffset.UtcNow;
        var phrases = new[]
        {
            Phrase("title-exact", "恢复", "普通正文", usage: 1, now),
            Phrase("title-prefix", "恢复出厂设置", "普通正文", usage: 1, now),
            Phrase("pinyin", "设备说明", "普通正文", usage: 1, now),
            Phrase("content", "其他说明", "请恢复设备后重试。", usage: 1, now),
        };
        var provider = new MappingPinyinProvider(new Dictionary<string, PinyinSearchTerms>
        {
            ["设备说明"] = Terms("shebeishuoming", "sbsm"),
            ["其他"] = Terms("qita", "qt"),
        });
        await using var runtime = await PhraseSearchRuntime.CreateAsync(new FakePhraseRepository(phrases), provider);

        var results = runtime.Search.Search(new SearchRequest("恢复", 10));

        Assert.Equal(new[] { "title-exact", "title-prefix", "content" }.Select(StableId), results.Items.Select(x => x.Phrase.Id));
        Assert.Equal(SearchMatchKind.TitleExact, results.Items[0].MatchKind);
        Assert.Equal(SearchMatchKind.TitlePrefix, results.Items[1].MatchKind);
        Assert.Equal(SearchMatchKind.ContentContains, results.Items[2].MatchKind);
    }

    [Fact]
    public async Task SearchSupportsInitialsAndFullPinyin()
    {
        var phrase = Phrase("factory", "恢复出厂设置", "请先备份数据。", usage: 1, DateTimeOffset.UtcNow);
        var provider = new MappingPinyinProvider(new Dictionary<string, PinyinSearchTerms>
        {
            [phrase.Title] = Terms("huifuchuchangshezh i", "hfccsz"),

        });
        await using var runtime = await PhraseSearchRuntime.CreateAsync(new FakePhraseRepository([phrase]), provider);

        Assert.Equal(phrase.Id, runtime.Search.Search(new SearchRequest("hfcc")).Items.Single().Phrase.Id);
        Assert.Equal(SearchMatchKind.PinyinInitialsPrefix, runtime.Search.Search(new SearchRequest("hfcc")).Items.Single().MatchKind);
        Assert.Equal(SearchMatchKind.PinyinFullPrefix, runtime.Search.Search(new SearchRequest("huifuchuchang")).Items.Single().MatchKind);
    }

    [Fact]
    public async Task EmptyQueryUsesCommonOrderingAndClampsLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var phrases = new List<Phrase>
        {
            Phrase("low", "低频", "正文", 1, now.AddMinutes(-5)),
            Phrase("high", "高频", "正文", 20, now.AddMinutes(-5)),
            Phrase("recent", "最近", "正文", 2, now),
        };
        phrases.AddRange(Enumerable.Range(0, 120).Select(index => Phrase($"extra-{index}", $"普通话术 {index}", "正文", 0, now.AddMinutes(-10))));
        await using var runtime = await PhraseSearchRuntime.CreateAsync(new FakePhraseRepository(phrases), new MappingPinyinProvider());

        var result = runtime.Search.Search(new SearchRequest("   ", 500));

        Assert.Equal(new[] { "high", "recent", "low" }.Select(StableId), result.Items.Take(3).Select(x => x.Phrase.Id));
        Assert.Equal(SearchIndexState.Ready, result.Status.State);
        Assert.Equal(100, runtime.Search.Search(new SearchRequest(string.Empty, 500)).Items.Length);
    }

    [Fact]
    public async Task FuzzyMatchingIsOnlyFallbackAndDoesNotSearchContent()
    {
        var phrase = Phrase("factory", "恢复出厂设置", "网络连接异常", 1, DateTimeOffset.UtcNow);
        await using var runtime = await PhraseSearchRuntime.CreateAsync(new FakePhraseRepository([phrase]), new MappingPinyinProvider());

        var fuzzy = runtime.Search.Search(new SearchRequest("恢复出场设置"));
        var contentOnly = runtime.Search.Search(new SearchRequest("网络连接亦"));
        var tooShort = runtime.Search.Search(new SearchRequest("复"));

        Assert.Equal(SearchMatchKind.FuzzyTitle, fuzzy.Items.Single().MatchKind);
        Assert.Empty(contentOnly.Items);
        Assert.Equal(SearchMatchKind.TitleContains, tooShort.Items.Single().MatchKind);
    }

    [Fact]
    public async Task PhraseMutationsUpdateSearchOnlyAfterCommit()
    {
        var category = Guid.NewGuid();
        var original = Phrase("original", "原始话术", "原始正文", 1, DateTimeOffset.UtcNow, category);
        var repository = new FakePhraseRepository([original]);
        await using var runtime = await PhraseSearchRuntime.CreateAsync(repository, new MappingPinyinProvider());

        var created = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "新增话术", "新增正文", category, false, ShortcutMode.None, null));
        Assert.True(created.IsSuccess);
        Assert.Equal(created.Value!.Id, runtime.Search.Search(new SearchRequest("新增话术")).Items.Single().Phrase.Id);

        var updated = await runtime.Phrases.UpdateAsync(new UpdatePhraseCommand(original.Id, original.Version, "更新话术", "更新正文", category, false, ShortcutMode.None, null));
        Assert.True(updated.IsSuccess);
        Assert.Empty(runtime.Search.Search(new SearchRequest("原始话术")).Items);
        Assert.Equal(original.Id, runtime.Search.Search(new SearchRequest("更新话术")).Items.Single().Phrase.Id);

        var used = await runtime.Phrases.IncrementUsageAsync(original.Id, DateTimeOffset.UtcNow);
        Assert.True(used.IsSuccess);
        var deleted = await runtime.Phrases.DeleteAsync(original.Id, used.Value!.Version);
        Assert.True(deleted.IsSuccess);
        Assert.Empty(runtime.Search.Search(new SearchRequest("更新话术")).Items);
    }

    [Fact]
    public async Task SearchDoesNotReadRepositoryAfterStartup()
    {
        var repository = new FakePhraseRepository([Phrase("one", "设备说明", "正文", 1, DateTimeOffset.UtcNow)]);
        await using var runtime = await PhraseSearchRuntime.CreateAsync(repository, new MappingPinyinProvider());
        repository.ListCalls = 0;

        _ = Enumerable.Range(0, 100).Select(_ => runtime.Search.Search(new SearchRequest("设备"))).ToArray();

        Assert.Equal(0, repository.ListCalls);
        Assert.Equal(0, repository.GetCalls);
    }

    [Fact]
    public async Task FailedPinyinUpdateKeepsOldSnapshotAndRecoversFromRepository()
    {
        var repository = new FakePhraseRepository([Phrase("one", "设备说明", "正文", 1, DateTimeOffset.UtcNow)]);
        var provider = new MappingPinyinProvider { AlwaysFail = true };
        await using var runtime = await PhraseSearchRuntime.CreateAsync(repository, provider);
        var created = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "新话术", "新正文", Guid.NewGuid(), false, ShortcutMode.None, null));

        Assert.True(created.IsSuccess);
        Assert.True(runtime.Search.Status.State is SearchIndexState.Dirty or SearchIndexState.Rebuilding);
        Assert.Empty(runtime.Search.Search(new SearchRequest("新话术")).Items);

        provider.AlwaysFail = false;
        var retry = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "恢复索引", "恢复正文", Guid.NewGuid(), false, ShortcutMode.None, null));
        Assert.True(retry.IsSuccess);
        await WaitForAsync(() => runtime.Search.Status.State == SearchIndexState.Ready);

        Assert.Equal(created.Value!.Id, runtime.Search.Search(new SearchRequest("新话术")).Items.Single().Phrase.Id);
    }

    [Fact]
    public async Task InitialPinyinFailureKeepsChineseSearchAvailable()
    {
        var phrase = Phrase("one", "设备说明", "请检查设备。", 1, DateTimeOffset.UtcNow);
        var provider = new MappingPinyinProvider { AlwaysFail = true };
        await using var runtime = await PhraseSearchRuntime.CreateAsync(new FakePhraseRepository([phrase]), provider);

        Assert.Equal(phrase.Id, runtime.Search.Search(new SearchRequest("设备说明")).Items.Single().Phrase.Id);
        Assert.True(runtime.Search.Status.State is SearchIndexState.Dirty or SearchIndexState.Rebuilding);
    }

    private static Phrase Phrase(string id, string title, string content, int usage, DateTimeOffset updated, Guid? categoryId = null) =>
        new(StableId(id), title, content, categoryId ?? StableId($"category-{id}"), false, ShortcutMode.None, null, usage, updated, 1, updated.AddMinutes(-1), updated);


    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private static double Percentile(IReadOnlyList<double> values, double percentile) => values[(int)Math.Ceiling(values.Count * percentile) - 1];

    private static PinyinSearchTerms Terms(string full, string initials) => new([full.Replace(" ", string.Empty)], [initials]);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class MappingPinyinProvider : IPinyinProvider
    {
        private readonly IReadOnlyDictionary<string, PinyinSearchTerms> _terms;
        public bool AlwaysFail { get; set; }
        public MappingPinyinProvider(IReadOnlyDictionary<string, PinyinSearchTerms>? terms = null) => _terms = terms ?? new Dictionary<string, PinyinSearchTerms>();
        public PinyinSearchTerms BuildTerms(string text)
        {
            if (AlwaysFail) throw new InvalidOperationException("测试拼音构建失败。");
            return _terms.TryGetValue(text, out var terms) ? terms : new PinyinSearchTerms([], []);
        }
    }

    private sealed class FakePhraseRepository : IPhraseRepository
    {
        private readonly Dictionary<Guid, Phrase> _phrases;
        public int ListCalls { get; set; }
        public int GetCalls { get; set; }
        public FakePhraseRepository(IEnumerable<Phrase> phrases) => _phrases = phrases.ToDictionary(x => x.Id);
        public Task<IReadOnlyList<Phrase>> ListAsync(CancellationToken cancellationToken = default) { ListCalls++; return Task.FromResult<IReadOnlyList<Phrase>>(_phrases.Values.ToArray()); }
        public Task<Phrase?> GetAsync(Guid id, CancellationToken cancellationToken = default) { GetCalls++; _phrases.TryGetValue(id, out var phrase); return Task.FromResult(phrase); }
        public Task<RepositoryResult<Phrase>> CreateAsync(CreatePhraseCommand command, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var phrase = new Phrase(command.Id, command.Title, command.Content, command.CategoryId, command.Favorite, command.ShortcutMode, null, 0, null, 1, now, now);
            _phrases[phrase.Id] = phrase;
            return Task.FromResult(RepositoryResult<Phrase>.Success(phrase, new CommittedDataChange("phrase", phrase.Id, "create", now)));
        }
        public Task<RepositoryResult<Phrase>> UpdateAsync(UpdatePhraseCommand command, CancellationToken cancellationToken = default)
        {
            if (!_phrases.TryGetValue(command.Id, out var current) || current.Version != command.ExpectedVersion)
                return Task.FromResult(RepositoryResult<Phrase>.Failure(new DataError("VERSION_CONFLICT", "版本冲突。")));
            var now = DateTimeOffset.UtcNow;
            var updated = current with { Title = command.Title, Content = command.Content, Version = current.Version + 1, UpdatedAtUtc = now };
            _phrases[updated.Id] = updated;
            return Task.FromResult(RepositoryResult<Phrase>.Success(updated, new CommittedDataChange("phrase", updated.Id, "update", now)));
        }
        public Task<RepositoryResult<DeleteResult>> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default)
        {
            if (!_phrases.Remove(id)) return Task.FromResult(RepositoryResult<DeleteResult>.Success(new DeleteResult(false, null)));
            var change = new CommittedDataChange("phrase", id, "delete", DateTimeOffset.UtcNow);
            return Task.FromResult(RepositoryResult<DeleteResult>.Success(new DeleteResult(true, change), change));
        }
        public Task<RepositoryResult<Phrase>> IncrementUsageAsync(Guid id, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default)
        {
            if (!_phrases.TryGetValue(id, out var current)) return Task.FromResult(RepositoryResult<Phrase>.Failure(new DataError("NOT_FOUND", "找不到话术。")));
            var updated = current with { UsageCount = current.UsageCount + 1, LastUsedAtUtc = usedAtUtc, Version = current.Version + 1, UpdatedAtUtc = usedAtUtc };
            _phrases[id] = updated;
            return Task.FromResult(RepositoryResult<Phrase>.Success(updated, new CommittedDataChange("phrase", id, "increment_usage", usedAtUtc)));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("QuickPhrase-Phase3-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
