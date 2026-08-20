using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Data.Sqlite;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

public sealed class HubSyncProviderTests
{
    [Theory]
    [InlineData("https://server:5105")]
    [InlineData("http://user:pass@server:5105")]
    [InlineData("http://server:5105/api")]
    [InlineData("http://server:5105/?x=1")]
    public void HubAddressRejectsUnsafeOrAmbiguousUrls(string value) => Assert.False(HubAddress.TryNormalize(value, out _, out _));

    [Fact]
    public async Task ConnectStoresOnlyTokenReferenceAndCompletesFirstFullSync()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var categoryId = Guid.NewGuid();
        var phraseId = Guid.NewGuid();
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/health") return Json(HttpStatusCode.OK, "{\"status\":\"ok\"}");
            if (request.RequestUri.AbsolutePath == "/sync/v1/auth/login") return Json(HttpStatusCode.OK, "{\"device_token\":\"secret-device-token\",\"device_id\":\"device-1\",\"user\":{\"id\":\"user-1\",\"account\":\"alice\",\"display_name\":\"客服员工\",\"role\":\"employee\",\"must_change_password\":false},\"capabilities\":{\"enterprise_sync\":true,\"personal_sync\":false},\"personal_sync_enabled\":false}");
            return Json(HttpStatusCode.OK, $"{{\"mode\":\"full\",\"cache_generation\":\"generation-1\",\"release_number\":1,\"items\":[{{\"cursor\":\"item-1\",\"release_number\":1,\"entity_type\":\"category\",\"operation\":\"upsert\",\"entity_id\":\"{categoryId}\",\"entity_version\":1,\"tombstone\":false,\"payload\":{{\"parent_id\":null,\"name\":\"企业客服\",\"sort_order\":0}}}},{{\"cursor\":\"item-2\",\"release_number\":1,\"entity_type\":\"phrase\",\"operation\":\"upsert\",\"entity_id\":\"{phraseId}\",\"entity_version\":1,\"tombstone\":false,\"payload\":{{\"category_id\":\"{categoryId}\",\"title\":\"企业退款话术\",\"content\":\"企业正文\",\"sort_order\":0}}}}],\"next_cursor\":\"cursor-1\",\"has_more\":false,\"server_time\":\"2026-08-20T12:00:00Z\"}}");
        });
        var tokens = new MemoryTokenStore();
        await using var provider = new QuickPhraseHubSyncProvider(runtime.EnterpriseSyncStore, new HttpClient(handler), tokens, runtime.RefreshEnterpriseSearchAsync, TimeProvider.System);

        var result = await provider.ConnectAsync(new HubConnectionRequest(new Uri("http://server:5105"), "alice", "password-not-persisted", "测试设备", "0.3.0"));

        Assert.Equal(SyncStatus.Succeeded, result.Status);
        Assert.Equal(2, result.EnterpriseChangesApplied);
        Assert.Equal("secret-device-token", tokens.Value);
        var account = await runtime.EnterpriseSyncStore.ReadAccountAsync();
        Assert.NotNull(account);
        Assert.NotEqual("secret-device-token", account!.TokenReference);
        await using (var connection = new SqliteConnection($"Data Source={runtime.DatabasePath};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('sync_accounts');";
            await using var reader = await command.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
            Assert.DoesNotContain(columns, column => column.Contains("token", StringComparison.OrdinalIgnoreCase) && column != "token_reference");
        }
        var enterprise = Assert.Single(await runtime.EnterpriseCatalog.ListPhrasesAsync());
        Assert.Equal(PhraseScope.Enterprise, enterprise.Scope);
        Assert.Contains(runtime.Search.Search(new SearchRequest("企业退款")).Items, item => item.Phrase.Id == phraseId && item.Phrase.Scope == PhraseScope.Enterprise);
    }


    [Fact]
    public async Task ExpiredCursorAutomaticallyFallsBackToFullSync()
    {
        using var temp=new TemporaryDirectory();await using var runtime=await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));var category=Guid.NewGuid();var phrase=Guid.NewGuid();await runtime.EnterpriseSyncStore.ApplyFullPageAsync("old",new[]{EnterpriseSyncChange.CategoryUpsert(category,null,"旧分类",0,1)});await runtime.EnterpriseSyncStore.CompleteFullAsync("old","expired-cursor",1,DateTimeOffset.UtcNow);await runtime.EnterpriseSyncStore.SaveAccountAsync(new SyncAccountRecord(new Uri("http://server:5105"),"alice","客服","device","ref","Connected",DateTimeOffset.UtcNow));var tokens=new MemoryTokenStore();await tokens.SaveAsync("ref","token");var requests=new List<string>();var handler=new StubHandler(request=>{requests.Add(request.RequestUri!.Query);if(requests.Count==1)return Json(HttpStatusCode.Conflict,"{\"code\":\"SYNC_CURSOR_EXPIRED\",\"message\":\"游标已失效\",\"trace_id\":\"trace-expired\"}");return Json(HttpStatusCode.OK,$"{{\"mode\":\"full\",\"cache_generation\":\"new\",\"release_number\":2,\"items\":[{{\"cursor\":\"i1\",\"release_number\":2,\"entity_type\":\"category\",\"operation\":\"upsert\",\"entity_id\":\"{category}\",\"entity_version\":2,\"tombstone\":false,\"payload\":{{\"parent_id\":null,\"name\":\"新分类\",\"sort_order\":0}}}},{{\"cursor\":\"i2\",\"release_number\":2,\"entity_type\":\"phrase\",\"operation\":\"upsert\",\"entity_id\":\"{phrase}\",\"entity_version\":1,\"tombstone\":false,\"payload\":{{\"category_id\":\"{category}\",\"title\":\"恢复话术\",\"content\":\"正文\",\"sort_order\":0}}}}],\"next_cursor\":\"cursor-new\",\"has_more\":false,\"server_time\":\"2026-08-20T12:00:00Z\"}}");});await using var provider=new QuickPhraseHubSyncProvider(runtime.EnterpriseSyncStore,new HttpClient(handler),tokens,runtime.RefreshEnterpriseSearchAsync,TimeProvider.System);var result=await provider.SynchronizeAsync(new SyncRequest());Assert.Equal(SyncStatus.Succeeded,result.Status);Assert.Equal(2,requests.Count);Assert.Contains("cursor=expired-cursor",requests[0]);Assert.DoesNotContain("cursor=",requests[1]);Assert.Equal("恢复话术",Assert.Single(await runtime.EnterpriseCatalog.ListPhrasesAsync()).Title);Assert.Equal("cursor-new",(await runtime.EnterpriseSyncStore.ReadStateAsync()).Cursor);
    }

    [Fact]
    public async Task OfflineSyncKeepsExistingEnterpriseCache()
    {
        using var temp=new TemporaryDirectory();await using var runtime=await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));var category=Guid.NewGuid();var phrase=Guid.NewGuid();await runtime.EnterpriseSyncStore.ApplyFullPageAsync("active",new[]{EnterpriseSyncChange.CategoryUpsert(category,null,"分类",0,1),EnterpriseSyncChange.PhraseUpsert(phrase,category,"离线可用", "正文",0,1)});await runtime.EnterpriseSyncStore.CompleteFullAsync("active","cursor",1,DateTimeOffset.UtcNow);await runtime.EnterpriseSyncStore.SaveAccountAsync(new SyncAccountRecord(new Uri("http://offline:5105"),"alice","客服","device","ref","Connected",DateTimeOffset.UtcNow));var tokens=new MemoryTokenStore();await tokens.SaveAsync("ref","token");await using var provider=new QuickPhraseHubSyncProvider(runtime.EnterpriseSyncStore,new HttpClient(new ThrowingHandler()),tokens,runtime.RefreshEnterpriseSearchAsync,TimeProvider.System);var result=await provider.SynchronizeAsync(new SyncRequest());Assert.Equal(SyncStatus.Offline,result.Status);Assert.Equal("离线可用",Assert.Single(await runtime.EnterpriseCatalog.ListPhrasesAsync()).Title);Assert.Equal("cursor",(await runtime.EnterpriseSyncStore.ReadStateAsync()).Cursor);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("offline");
    }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
    private sealed class MemoryTokenStore : IProtectedTokenStore
    {
        public string? Value { get; private set; }
        public Task SaveAsync(string reference, string token, CancellationToken cancellationToken = default) { Value = token; return Task.CompletedTask; }
        public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task DeleteAsync(string reference, CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("QuickPhrase-M3-Hub-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
