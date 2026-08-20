using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

public sealed class EnterpriseCacheStoreTests
{
    [Fact]
    public async Task FullGenerationIsInvisibleUntilAtomicSwitchAndTombstoneDoesNotDeletePersonalPhrase()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var sharedCategoryId = Guid.NewGuid();
        var sharedPhraseId = Guid.NewGuid();
        Assert.True((await runtime.Categories.CreateAsync(new CreateCategoryCommand(sharedCategoryId, "个人分类"))).IsSuccess);
        Assert.True((await runtime.Phrases.CreateAsync(new CreatePhraseCommand(sharedPhraseId, "个人话术", "个人正文", sharedCategoryId, ShortcutMode.None, null))).IsSuccess);

        const string generation = "generation-a";
        await runtime.EnterpriseSyncStore.ApplyFullPageAsync(generation, new[]
        {
            EnterpriseSyncChange.CategoryUpsert(sharedCategoryId, null, "企业分类", 10, 1),
            EnterpriseSyncChange.PhraseUpsert(sharedPhraseId, sharedCategoryId, "企业话术", "企业正文", 20, 1),
        });
        Assert.Empty(await runtime.EnterpriseCatalog.ListPhrasesAsync());

        await runtime.EnterpriseSyncStore.CompleteFullAsync(generation, "cursor-1", 1, DateTimeOffset.UtcNow);
        var enterprisePhrase = Assert.Single(await runtime.EnterpriseCatalog.ListPhrasesAsync());
        Assert.Equal(PhraseScope.Enterprise, enterprisePhrase.Scope);
        Assert.Equal("企业话术", enterprisePhrase.Title);
        Assert.Contains(await runtime.Phrases.ListAsync(), item => item.Id == sharedPhraseId && item.Scope == PhraseScope.Personal);
        await runtime.RefreshEnterpriseSearchAsync();
        var search = runtime.Search.Search(new SearchRequest("话术", 10));
        Assert.Contains(search.Items, item => item.Phrase.Id == sharedPhraseId && item.Phrase.Scope == PhraseScope.Personal);
        Assert.Contains(search.Items, item => item.Phrase.Id == sharedPhraseId && item.Phrase.Scope == PhraseScope.Enterprise);

        await runtime.EnterpriseSyncStore.ApplyIncrementalPageAsync(new[]
        {
            EnterpriseSyncChange.PhraseDelete(sharedPhraseId, 2),
        }, "cursor-2", 2, DateTimeOffset.UtcNow);

        Assert.Empty(await runtime.EnterpriseCatalog.ListPhrasesAsync());
        Assert.Contains(await runtime.Phrases.ListAsync(), item => item.Id == sharedPhraseId && item.Title == "个人话术");
        var state = await runtime.EnterpriseSyncStore.ReadStateAsync();
        Assert.Equal("cursor-2", state.Cursor);
        Assert.Equal(2, state.ReleaseNumber);
    }

    [Fact]
    public async Task FailedOrIncompleteFullGenerationKeepsPreviousActiveCache()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = Guid.NewGuid();
        var first = Guid.NewGuid();
        await runtime.EnterpriseSyncStore.ApplyFullPageAsync("active", new[] { EnterpriseSyncChange.CategoryUpsert(category, null, "分类", 0, 1), EnterpriseSyncChange.PhraseUpsert(first, category, "旧企业话术", "旧正文", 0, 1) });
        await runtime.EnterpriseSyncStore.CompleteFullAsync("active", "cursor-a", 1, DateTimeOffset.UtcNow);

        await runtime.EnterpriseSyncStore.ApplyFullPageAsync("incomplete", new[] { EnterpriseSyncChange.CategoryUpsert(category, null, "分类", 0, 2), EnterpriseSyncChange.PhraseUpsert(Guid.NewGuid(), category, "未完成话术", "正文", 0, 1) });

        var visible = Assert.Single(await runtime.EnterpriseCatalog.ListPhrasesAsync());
        Assert.Equal("旧企业话术", visible.Title);
        var state = await runtime.EnterpriseSyncStore.ReadStateAsync();
        Assert.Equal("active", state.ActiveGeneration);
        Assert.Equal("cursor-a", state.Cursor);
    }

    [Fact]
    public async Task IncrementalTransactionFailureDoesNotAdvanceCursorOrReplaceVisibleCache()
    {
        using var temp=new TemporaryDirectory();await using var runtime=await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));var category=Guid.NewGuid();var phrase=Guid.NewGuid();await runtime.EnterpriseSyncStore.ApplyFullPageAsync("active",new[]{EnterpriseSyncChange.CategoryUpsert(category,null,"分类",0,1),EnterpriseSyncChange.PhraseUpsert(phrase,category,"旧话术","旧正文",0,1)});await runtime.EnterpriseSyncStore.CompleteFullAsync("active","cursor-old",1,DateTimeOffset.UtcNow);
        await using(var connection=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={runtime.DatabasePath};Pooling=False")){await connection.OpenAsync();await using var command=connection.CreateCommand();command.CommandText="CREATE TRIGGER fail_enterprise_update BEFORE UPDATE ON enterprise_phrases_cache WHEN NEW.title='触发失败' BEGIN SELECT RAISE(ABORT,'injected'); END;";await command.ExecuteNonQueryAsync();}
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(()=>runtime.EnterpriseSyncStore.ApplyIncrementalPageAsync(new[]{EnterpriseSyncChange.PhraseUpsert(phrase,category,"触发失败","新正文",0,2)},"cursor-new",2,DateTimeOffset.UtcNow));
        Assert.Equal("旧话术",Assert.Single(await runtime.EnterpriseCatalog.ListPhrasesAsync()).Title);var state=await runtime.EnterpriseSyncStore.ReadStateAsync();Assert.Equal("cursor-old",state.Cursor);Assert.Equal(1,state.ReleaseNumber);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("QuickPhrase-M3-Cache-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

