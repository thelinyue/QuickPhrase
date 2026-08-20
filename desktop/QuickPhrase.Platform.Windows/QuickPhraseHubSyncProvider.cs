using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

internal interface IProtectedTokenStore
{
    Task SaveAsync(string reference, string token, CancellationToken cancellationToken = default);
    Task<string?> ReadAsync(string reference, CancellationToken cancellationToken = default);
    Task DeleteAsync(string reference, CancellationToken cancellationToken = default);
}

internal static class HubAddress
{
    public static bool TryNormalize(string value, out Uri? address, out string? error)
    {
        address = null; error = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttp)
        { error = "Hub 地址必须是 http:// 开头的完整内网地址。"; return false; }
        if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment) || (parsed.AbsolutePath != "/" && parsed.AbsolutePath != string.Empty))
        { error = "Hub 地址不能包含账号密码、查询参数、片段或 API 子路径。"; return false; }
        address = new Uri(parsed.GetLeftPart(UriPartial.Authority));
        return true;
    }
}

internal sealed class DpapiTokenStore : IProtectedTokenStore
{
    private readonly string _directory;
    public DpapiTokenStore(string directory) { _directory = directory; }
    public async Task SaveAsync(string reference, string token, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(token), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(PathFor(reference), protectedBytes, cancellationToken);
    }
    public async Task<string?> ReadAsync(string reference, CancellationToken cancellationToken = default)
    {
        var path=PathFor(reference); if(!File.Exists(path)) return null;
        var bytes=await File.ReadAllBytesAsync(path,cancellationToken);
        return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(bytes,null,System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }
    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default) { var path=PathFor(reference); if(File.Exists(path)) File.Delete(path); return Task.CompletedTask; }
    private string PathFor(string reference)=>Path.Combine(_directory,$"hub-token-{reference}.bin");
}

/// <summary>HTTP、设备认证、SQLite 分页提交和索引刷新均在后台执行；任何日志和结果都不包含正文或 Token。</summary>
internal sealed class QuickPhraseHubSyncProvider : ISyncProvider, ISyncAccountService, IAsyncDisposable
{
    private readonly SqliteEnterpriseSyncStore _store; private readonly HttpClient _http; private readonly IProtectedTokenStore _tokens; private readonly Func<CancellationToken,Task> _refreshSearch; private readonly TimeProvider _clock; private readonly SemaphoreSlim _gate=new(1,1);
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);
    public QuickPhraseHubSyncProvider(SqliteEnterpriseSyncStore store,HttpClient http,IProtectedTokenStore tokens,Func<CancellationToken,Task> refreshSearch,TimeProvider clock){_store=store;_http=http;_tokens=tokens;_refreshSearch=refreshSearch;_clock=clock;}
    public string ProviderId=>"quickphrase-hub"; public SyncProviderCapabilities Capabilities {get;}=new(true);
    public async Task<SyncResult> ConnectAsync(HubConnectionRequest request,CancellationToken cancellationToken=default)
    {
        var started=_clock.GetUtcNow(); if(!HubAddress.TryNormalize(request.HubAddress.ToString(),out var address,out var error)) return Failed(SyncStatus.Failed,"HUB_ADDRESS_INVALID",error!,false,null,started);
        try
        {
            using var health=await _http.GetAsync(new Uri(address!,"/health"),cancellationToken);health.EnsureSuccessStatusCode();
            using var login=await _http.PostAsJsonAsync(new Uri(address!,"/sync/v1/auth/login"),new{account=request.Account,password=request.Password,device_name=request.DeviceName,client_version=request.ClientVersion,platform="windows"},JsonOptions,cancellationToken);
            var body=await ReadSuccessAsync<ClientLoginResponse>(login,cancellationToken);var reference=Guid.NewGuid().ToString("N");await _tokens.SaveAsync(reference,body.DeviceToken,cancellationToken);await _store.SaveAccountAsync(new SyncAccountRecord(address!,body.User.Account,body.User.DisplayName,body.DeviceId,reference,"Connected",_clock.GetUtcNow()),cancellationToken);return await SynchronizeAsync(new SyncRequest(ForceFull:true),cancellationToken);
        }
        catch(HttpRequestException){return Failed(SyncStatus.Offline,"HUB_OFFLINE","无法连接闪语中心，请检查内网地址和服务器状态。",true,null,started);}
        catch(HubApiException ex){return Failed(ex.StatusCode==HttpStatusCode.Unauthorized?SyncStatus.AuthenticationRequired:SyncStatus.Failed,ex.Code,ex.Message,false,ex.TraceId,started);}
    }
    public async Task<SyncResult> SynchronizeAsync(SyncRequest request,CancellationToken cancellationToken=default)
    {
        var started=_clock.GetUtcNow();await _gate.WaitAsync(cancellationToken);try
        {
            var account=await _store.ReadAccountAsync(cancellationToken);if(account?.TokenReference is null)return Failed(SyncStatus.Disabled,"SYNC_DISABLED","尚未连接闪语中心。",false,null,started);var token=await _tokens.ReadAsync(account.TokenReference,cancellationToken);if(string.IsNullOrWhiteSpace(token)){await _store.MarkAuthenticationRequiredAsync(cancellationToken);return Failed(SyncStatus.AuthenticationRequired,"AUTHENTICATION_REQUIRED","设备认证已失效，请重新登录闪语中心。",false,null,started);}
            var state=await _store.ReadStateAsync(cancellationToken);var cursor=request.ForceFull?null:state.Cursor;var total=0;var retriedFull=request.ForceFull;
            while(true)
            {
                HubPullResponse page;
                try{page=await PullAsync(account.HubAddress,token,cursor,request.ForceFull||cursor is null,cancellationToken);}catch(HubApiException ex) when(ex.Code=="SYNC_CURSOR_EXPIRED"&&!retriedFull){cursor=null;retriedFull=true;continue;}catch(HubApiException ex) when(ex.StatusCode==HttpStatusCode.Unauthorized){await _tokens.DeleteAsync(account.TokenReference,cancellationToken);await _store.MarkAuthenticationRequiredAsync(cancellationToken);return Failed(SyncStatus.AuthenticationRequired,ex.Code,"设备授权已失效，请重新登录闪语中心。",false,ex.TraceId,started);}
                var changes=page.Items.Select(ToChange).ToArray();if(page.Mode=="full"){if(string.IsNullOrWhiteSpace(page.CacheGeneration))throw new HubApiException(HttpStatusCode.InternalServerError,"SYNC_RESPONSE_INVALID","完整同步响应缺少缓存代次。",null);await _store.ApplyFullPageAsync(page.CacheGeneration,changes,cancellationToken);if(!page.HasMore)await _store.CompleteFullAsync(page.CacheGeneration,page.NextCursor!,page.ReleaseNumber,_clock.GetUtcNow(),cancellationToken);}else await _store.ApplyIncrementalPageAsync(changes,page.NextCursor!,page.ReleaseNumber,_clock.GetUtcNow(),cancellationToken);total+=changes.Length;cursor=page.NextCursor;if(!page.HasMore)break;
            }
            await _refreshSearch(cancellationToken);return new SyncResult(SyncStatus.Succeeded,total,null,"企业话术已同步。",false,null,started,_clock.GetUtcNow());
        }
        catch(HttpRequestException){return Failed(SyncStatus.Offline,"HUB_OFFLINE","闪语中心暂时不可达，仍可继续使用本地和已缓存企业话术。",true,null,started);}catch(HubApiException ex){return Failed(SyncStatus.Failed,ex.Code,ex.Message,true,ex.TraceId,started);}catch(Exception){return Failed(SyncStatus.Failed,"ENTERPRISE_SYNC_FAILED","企业话术同步失败，请稍后重试。",true,null,started);}finally{_gate.Release();}
    }
    public async Task<SyncAccountState> GetStateAsync(CancellationToken cancellationToken=default){var account=await _store.ReadAccountAsync(cancellationToken);var state=await _store.ReadStateAsync(cancellationToken);return new SyncAccountState(account?.Status=="Connected",account?.HubAddress,account?.Account,account?.DisplayName,account?.DeviceId,account?.Status=="AuthenticationRequired"?SyncStatus.AuthenticationRequired:account is null?SyncStatus.Disabled:SyncStatus.Succeeded,state.ReleaseNumber,state.LastSynchronizedAtUtc,state.LastResult,state.TraceId);}
    public async Task DisconnectAsync(bool retainEnterpriseCache=true,CancellationToken cancellationToken=default){var account=await _store.ReadAccountAsync(cancellationToken);if(account?.TokenReference is not null)await _tokens.DeleteAsync(account.TokenReference,cancellationToken);await _store.DeleteAccountAsync(cancellationToken);if(!retainEnterpriseCache){await _store.ClearAsync(cancellationToken);await _refreshSearch(cancellationToken);}}
    public async Task ClearEnterpriseCacheAsync(CancellationToken cancellationToken=default){await _store.ClearAsync(cancellationToken);await _refreshSearch(cancellationToken);}
    private async Task<HubPullResponse> PullAsync(Uri address,string token,string? cursor,bool full,CancellationToken cancellationToken){var query=new List<string>{"limit=500"};if(full)query.Add("mode=full");if(!string.IsNullOrWhiteSpace(cursor))query.Add("cursor="+Uri.EscapeDataString(cursor));using var request=new HttpRequestMessage(HttpMethod.Get,new Uri(address,$"/sync/v1/enterprise/pull?{string.Join("&",query)}"));request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);using var response=await _http.SendAsync(request,cancellationToken);return await ReadSuccessAsync<HubPullResponse>(response,cancellationToken);}
    private static EnterpriseSyncChange ToChange(HubChange item){if(item.Operation=="delete")return item.EntityType=="category"?EnterpriseSyncChange.CategoryDelete(item.EntityId,item.EntityVersion):EnterpriseSyncChange.PhraseDelete(item.EntityId,item.EntityVersion);if(item.EntityType=="category")return EnterpriseSyncChange.CategoryUpsert(item.EntityId,item.Payload.TryGetProperty("parent_id",out var parent)&&parent.ValueKind!=JsonValueKind.Null?parent.GetGuid():null,item.Payload.GetProperty("name").GetString()!,item.Payload.GetProperty("sort_order").GetInt32(),item.EntityVersion);return EnterpriseSyncChange.PhraseUpsert(item.EntityId,item.Payload.GetProperty("category_id").GetGuid(),item.Payload.GetProperty("title").GetString()!,item.Payload.GetProperty("content").GetString()!,item.Payload.GetProperty("sort_order").GetInt32(),item.EntityVersion);}
    private static async Task<T> ReadSuccessAsync<T>(HttpResponseMessage response,CancellationToken cancellationToken){if(response.IsSuccessStatusCode)return (await response.Content.ReadFromJsonAsync<T>(JsonOptions,cancellationToken))??throw new HubApiException(response.StatusCode,"SYNC_RESPONSE_INVALID","闪语中心返回空响应。",null);var error=await response.Content.ReadFromJsonAsync<HubError>(JsonOptions,cancellationToken);throw new HubApiException(response.StatusCode,error?.Code??"HUB_REQUEST_FAILED",error?.Message??"闪语中心请求失败。",error?.TraceId);}
    private SyncResult Failed(SyncStatus status,string code,string message,bool retryable,string? traceId,DateTimeOffset started)=>new(status,0,code,message,retryable,traceId,started,_clock.GetUtcNow());
    public ValueTask DisposeAsync(){_gate.Dispose();_http.Dispose();return ValueTask.CompletedTask;}
    private sealed record ClientLoginResponse([property:JsonPropertyName("device_token")]string DeviceToken,[property:JsonPropertyName("device_id")]string DeviceId,HubUser User);
    private sealed record HubUser(string Account,[property:JsonPropertyName("display_name")]string DisplayName);
    private sealed record HubPullResponse(string Mode,[property:JsonPropertyName("cache_generation")]string? CacheGeneration,[property:JsonPropertyName("release_number")]long ReleaseNumber,IReadOnlyList<HubChange> Items,[property:JsonPropertyName("next_cursor")]string? NextCursor,[property:JsonPropertyName("has_more")]bool HasMore);
    private sealed record HubChange([property:JsonPropertyName("entity_type")]string EntityType,string Operation,[property:JsonPropertyName("entity_id")]Guid EntityId,[property:JsonPropertyName("entity_version")]long EntityVersion,JsonElement Payload);
    private sealed record HubError(string Code,string Message,[property:JsonPropertyName("trace_id")]string? TraceId);
    private sealed class HubApiException(HttpStatusCode statusCode,string code,string message,string? traceId):Exception(message){public HttpStatusCode StatusCode{get;}=statusCode;public string Code{get;}=code;public string? TraceId{get;}=traceId;}
}
