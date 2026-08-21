using System.Net.Http.Json;
using System.Text.Json;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

[Collection(SettingsDocumentDiagnosticsCollection.Name)]
public sealed class HubSyncRealIntegrationTests
{
    [Fact]
    public async Task RealHubPublishFullIncrementalAndTombstoneFlow()
    {
        var url=Environment.GetEnvironmentVariable("QPH_HUB_E2E_URL");
        var code=Environment.GetEnvironmentVariable("QPH_HUB_INIT_CODE");
        if(string.IsNullOrWhiteSpace(url)||string.IsNullOrWhiteSpace(code)) return;
        const string account="m3client";const string password="M3ClientValidation1234";
        using var cookies=new HttpClientHandler{UseCookies=true};using var admin=new HttpClient(cookies){BaseAddress=new Uri(url)};
        using var bootstrap=await admin.PostAsJsonAsync("/api/v1/bootstrap/complete",new{initialization_code=code,account,display_name="M3客户端验证",password});bootstrap.EnsureSuccessStatusCode();var csrf=bootstrap.Headers.GetValues("X-CSRF-Token").Single();admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf);
        var category=await SendJsonAsync(admin,HttpMethod.Post,"/api/v1/enterprise/categories",new{parent_id=(string?)null,name="客户端验证分类",sort_order=0});var categoryId=category.RootElement.GetProperty("id").GetGuid();
        var phrase=await SendJsonAsync(admin,HttpMethod.Post,"/api/v1/enterprise/phrases",new{category_id=categoryId,title="E2E企业话术",content="首次发布正文",sort_order=0});var phraseId=phrase.RootElement.GetProperty("id").GetGuid();
        using var temp=new TemporaryDirectory();await using var runtime=await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));Assert.True((await runtime.Categories.CreateAsync(new CreateCategoryCommand(categoryId,"个人同ID分类"))).IsSuccess);Assert.True((await runtime.Phrases.CreateAsync(new CreatePhraseCommand(phraseId,"个人同ID话术","个人正文",categoryId,ShortcutMode.None,null))).IsSuccess);
        var connected=await runtime.SyncAccounts.ConnectAsync(new HubConnectionRequest(new Uri(url),account,password,"真实E2E设备","0.3.0"));Assert.Equal(SyncStatus.Succeeded,connected.Status);Assert.Empty(await runtime.EnterpriseCatalog.ListPhrasesAsync());
        await PublishAsync(admin,"首次发布");var full=await runtime.SyncProvider.SynchronizeAsync(new SyncRequest());Assert.Equal(SyncStatus.Succeeded,full.Status);Assert.Equal("E2E企业话术",Assert.Single(await runtime.EnterpriseCatalog.ListPhrasesAsync()).Title);Assert.Contains(runtime.Search.Search(new SearchRequest("E2E企业")).Items,item=>item.Phrase.Scope==PhraseScope.Enterprise);
        await SendJsonAsync(admin,HttpMethod.Patch,$"/api/v1/enterprise/phrases/{phraseId}",new{category_id=categoryId,title="E2E企业话术更新",content="增量正文",sort_order=1,base_version=1});await PublishAsync(admin,"增量发布");var incremental=await runtime.SyncProvider.SynchronizeAsync(new SyncRequest());Assert.Equal(SyncStatus.Succeeded,incremental.Status);Assert.Equal("E2E企业话术更新",Assert.Single(await runtime.EnterpriseCatalog.ListPhrasesAsync()).Title);
        var deletePreview=await SendJsonAsync(admin,HttpMethod.Delete,$"/api/v1/enterprise/phrases/{phraseId}",null);await SendJsonAsync(admin,HttpMethod.Delete,$"/api/v1/enterprise/phrases/{phraseId}",new{preview_token=deletePreview.RootElement.GetProperty("preview_token").GetString(),draft_revision=deletePreview.RootElement.GetProperty("draft_revision").GetInt64()});await PublishAsync(admin,"删除发布");var tombstone=await runtime.SyncProvider.SynchronizeAsync(new SyncRequest());Assert.Equal(SyncStatus.Succeeded,tombstone.Status);Assert.Empty(await runtime.EnterpriseCatalog.ListPhrasesAsync());Assert.Contains(await runtime.Phrases.ListAsync(),item=>item.Id==phraseId&&item.Scope==PhraseScope.Personal);
    }
    private static async Task PublishAsync(HttpClient client,string summary){using var preview=await client.GetAsync("/api/v1/enterprise/releases/preview");preview.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await preview.Content.ReadAsStringAsync());await SendJsonAsync(client,HttpMethod.Post,"/api/v1/enterprise/releases",new{draft_revision=json.RootElement.GetProperty("draft_revision").GetInt64(),summary});}
    private static async Task<JsonDocument> SendJsonAsync(HttpClient client,HttpMethod method,string path,object? body){using var request=new HttpRequestMessage(method,path);if(body is not null)request.Content=JsonContent.Create(body);using var response=await client.SendAsync(request);response.EnsureSuccessStatusCode();return JsonDocument.Parse(await response.Content.ReadAsStringAsync());}
    private sealed class TemporaryDirectory:IDisposable{public string Path{get;}=Directory.CreateTempSubdirectory("QuickPhrase-M3-Real-").FullName;public void Dispose()=>Directory.Delete(Path,true);}
}
