using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

internal sealed record EnterpriseSyncChange(
    string EntityType,
    string Operation,
    Guid Id,
    long Version,
    Guid? ParentId = null,
    Guid? CategoryId = null,
    string? Name = null,
    string? Title = null,
    string? Content = null,
    int SortOrder = 0)
{
    public static EnterpriseSyncChange CategoryUpsert(Guid id, Guid? parentId, string name, int sortOrder, long version) => new("category", "upsert", id, version, parentId, Name: name, SortOrder: sortOrder);
    public static EnterpriseSyncChange PhraseUpsert(Guid id, Guid categoryId, string title, string content, int sortOrder, long version) => new("phrase", "upsert", id, version, CategoryId: categoryId, Title: title, Content: content, SortOrder: sortOrder);
    public static EnterpriseSyncChange CategoryDelete(Guid id, long version) => new("category", "delete", id, version);
    public static EnterpriseSyncChange PhraseDelete(Guid id, long version) => new("phrase", "delete", id, version);
}


internal sealed record SyncAccountRecord(Uri HubAddress, string Account, string DisplayName, string DeviceId, string? TokenReference, string Status, DateTimeOffset? LastAuthenticatedAtUtc);

internal sealed record EnterpriseSyncStateRecord(string? ActiveGeneration, string? Cursor, long ReleaseNumber, DateTimeOffset? LastSynchronizedAtUtc, string? LastResult, string? LastErrorCode, string? TraceId);

/// <summary>
/// 企业缓存 SQLite 事实源。full 页面只写非活动代次，CompleteFull 才原子切换；incremental 与 cursor 在同一事务提交。
/// </summary>
internal sealed class SqliteEnterpriseSyncStore : IEnterpriseCatalog
{
    private readonly SqliteConnectionFactory _connections;
    private readonly IDatabaseWriteQueue _writes;
    private readonly TimeProvider _clock;

    public SqliteEnterpriseSyncStore(SqliteConnectionFactory connections, IDatabaseWriteQueue writes, TimeProvider clock)
    {
        _connections = connections;
        _writes = writes;
        _clock = clock;
    }

    public async Task<SyncAccountRecord?> ReadAccountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenReadAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT hub_address,account,display_name,device_id,token_reference,status,last_authenticated_at_utc FROM sync_accounts WHERE id=1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SyncAccountRecord(new Uri(reader.GetString(0)),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),reader.GetString(5),reader.IsDBNull(6)?null:DateTimeOffset.Parse(reader.GetString(6)));
    }

    public Task SaveAccountAsync(SyncAccountRecord account, CancellationToken cancellationToken = default) => _writes.EnqueueAsync(async (connection, ct) =>
    {
        var now=_clock.GetUtcNow().ToString("O");
        await using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO sync_accounts(id,hub_address,account,display_name,device_id,token_reference,status,last_authenticated_at_utc,created_at_utc,updated_at_utc) VALUES(1,$hub,$account,$display,$device,$token,$status,$authenticated,$now,$now) ON CONFLICT(id) DO UPDATE SET hub_address=excluded.hub_address,account=excluded.account,display_name=excluded.display_name,device_id=excluded.device_id,token_reference=excluded.token_reference,status=excluded.status,last_authenticated_at_utc=excluded.last_authenticated_at_utc,updated_at_utc=excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$hub",account.HubAddress.ToString().TrimEnd('/'));command.Parameters.AddWithValue("$account",account.Account);command.Parameters.AddWithValue("$display",account.DisplayName);command.Parameters.AddWithValue("$device",account.DeviceId);command.Parameters.AddWithValue("$token",(object?)account.TokenReference??DBNull.Value);command.Parameters.AddWithValue("$status",account.Status);command.Parameters.AddWithValue("$authenticated",(object?)account.LastAuthenticatedAtUtc?.ToString("O")??DBNull.Value);command.Parameters.AddWithValue("$now",now);await command.ExecuteNonQueryAsync(ct);return true;
    },cancellationToken);

    public Task MarkAuthenticationRequiredAsync(CancellationToken cancellationToken=default)=>_writes.EnqueueAsync(async(connection,ct)=>{await using var command=connection.CreateCommand();command.CommandText="UPDATE sync_accounts SET token_reference=NULL,status='AuthenticationRequired',updated_at_utc=$now WHERE id=1;";command.Parameters.AddWithValue("$now",_clock.GetUtcNow().ToString("O"));await command.ExecuteNonQueryAsync(ct);return true;},cancellationToken);
    public Task DeleteAccountAsync(CancellationToken cancellationToken=default)=>_writes.EnqueueAsync(async(connection,ct)=>{await using var command=connection.CreateCommand();command.CommandText="DELETE FROM sync_accounts WHERE id=1;";await command.ExecuteNonQueryAsync(ct);return true;},cancellationToken);

    public async Task<IReadOnlyList<Category>> ListCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenReadAsync(cancellationToken);
        var generation = await ReadActiveGenerationAsync(connection, cancellationToken);
        if (generation is null) return Array.Empty<Category>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,parent_id,name,sort_order,version FROM enterprise_categories_cache WHERE generation=$generation ORDER BY parent_id IS NOT NULL,parent_id,sort_order,name,id;";
        command.Parameters.AddWithValue("$generation", generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<Category>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new Category(Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetInt32(3), reader.GetInt64(4), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, PhraseScope.Enterprise));
        return items;
    }

    public async Task<IReadOnlyList<Phrase>> ListPhrasesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenReadAsync(cancellationToken);
        var generation = await ReadActiveGenerationAsync(connection, cancellationToken);
        if (generation is null) return Array.Empty<Phrase>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,category_id,title,content,sort_order,version FROM enterprise_phrases_cache WHERE generation=$generation ORDER BY sort_order,title,id;";
        command.Parameters.AddWithValue("$generation", generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<Phrase>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new Phrase(Guid.Parse(reader.GetString(0)), reader.GetString(2), reader.GetString(3), Guid.Parse(reader.GetString(1)), ShortcutMode.None, null, 0, null, reader.GetInt64(5), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "default", reader.GetInt32(4), PhraseScope.Enterprise));
        return items;
    }

    public Task ApplyFullPageAsync(string generation, IReadOnlyList<EnterpriseSyncChange> changes, CancellationToken cancellationToken = default) =>
        _writes.EnqueueAsync(async (connection, ct) => { await ApplyPageCoreAsync(connection, generation, changes, updateState: null, ct); return true; }, cancellationToken);

    public Task CompleteFullAsync(string generation, string cursor, long releaseNumber, DateTimeOffset synchronizedAtUtc, CancellationToken cancellationToken = default) =>
        _writes.EnqueueAsync(async (connection, ct) =>
        {
            await using var transaction = connection.BeginTransaction();
            try
            {
                await ExecuteAsync(connection, transaction, "UPDATE enterprise_sync_state SET active_generation=$generation,cursor=$cursor,release_number=$release,last_synchronized_at_utc=$at,last_result='Succeeded',last_error_code=NULL,trace_id=NULL WHERE id=1;", ct, ("$generation", generation), ("$cursor", cursor), ("$release", releaseNumber), ("$at", synchronizedAtUtc.ToString("O")));
                await ExecuteAsync(connection, transaction, "DELETE FROM enterprise_phrases_cache WHERE generation<>$generation;", ct, ("$generation", generation));
                await ExecuteAsync(connection, transaction, "DELETE FROM enterprise_categories_cache WHERE generation<>$generation;", ct, ("$generation", generation));
                await transaction.CommitAsync(ct);
                return true;
            }
            catch { try { await transaction.RollbackAsync(CancellationToken.None); } catch { } throw; }
        }, cancellationToken);

    public Task ApplyIncrementalPageAsync(IReadOnlyList<EnterpriseSyncChange> changes, string cursor, long releaseNumber, DateTimeOffset synchronizedAtUtc, CancellationToken cancellationToken = default) =>
        _writes.EnqueueAsync(async (connection, ct) =>
        {
            var generation = await ReadActiveGenerationAsync(connection, ct) ?? throw new DataStoreException("ENTERPRISE_FULL_SYNC_REQUIRED", "企业缓存尚未初始化，请先执行完整同步。");
            await ApplyPageCoreAsync(connection, generation, changes, new EnterpriseSyncStateRecord(generation, cursor, releaseNumber, synchronizedAtUtc, "Succeeded", null, null), ct);
            return true;
        }, cancellationToken);

    public async Task<EnterpriseSyncStateRecord> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenReadAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT active_generation,cursor,release_number,last_synchronized_at_utc,last_result,last_error_code,trace_id FROM enterprise_sync_state WHERE id=1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new DataStoreException("ENTERPRISE_SYNC_STATE_MISSING", "企业同步状态不存在，请检查本地数据库结构。");
        return new EnterpriseSyncStateRecord(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) => _writes.EnqueueAsync(async (connection, ct) =>
    {
        await using var transaction = connection.BeginTransaction();
        try
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM enterprise_phrases_cache; DELETE FROM enterprise_categories_cache; UPDATE enterprise_sync_state SET active_generation=NULL,cursor=NULL,release_number=0,last_synchronized_at_utc=NULL,last_result=NULL,last_error_code=NULL,trace_id=NULL WHERE id=1;", ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch { try { await transaction.RollbackAsync(CancellationToken.None); } catch { } throw; }
    }, cancellationToken);

    private static async Task ApplyPageCoreAsync(SqliteConnection connection, string generation, IReadOnlyList<EnterpriseSyncChange> changes, EnterpriseSyncStateRecord? updateState, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var change in changes)
            {
                if (change.EntityType == "category")
                {
                    if (change.Operation == "delete") await ExecuteAsync(connection, transaction, "DELETE FROM enterprise_categories_cache WHERE id=$id AND generation=$generation;", cancellationToken, ("$id", change.Id.ToString()), ("$generation", generation));
                    else await ExecuteAsync(connection, transaction, "INSERT INTO enterprise_categories_cache(id,generation,parent_id,name,sort_order,version) VALUES($id,$generation,$parent,$name,$sort,$version) ON CONFLICT(id,generation) DO UPDATE SET parent_id=excluded.parent_id,name=excluded.name,sort_order=excluded.sort_order,version=excluded.version;", cancellationToken, ("$id", change.Id.ToString()), ("$generation", generation), ("$parent", (object?)change.ParentId?.ToString() ?? DBNull.Value), ("$name", change.Name!), ("$sort", change.SortOrder), ("$version", change.Version));
                }
                else
                {
                    if (change.Operation == "delete") await ExecuteAsync(connection, transaction, "DELETE FROM enterprise_phrases_cache WHERE id=$id AND generation=$generation;", cancellationToken, ("$id", change.Id.ToString()), ("$generation", generation));
                    else await ExecuteAsync(connection, transaction, "INSERT INTO enterprise_phrases_cache(id,generation,category_id,title,content,sort_order,version) VALUES($id,$generation,$category,$title,$content,$sort,$version) ON CONFLICT(id,generation) DO UPDATE SET category_id=excluded.category_id,title=excluded.title,content=excluded.content,sort_order=excluded.sort_order,version=excluded.version;", cancellationToken, ("$id", change.Id.ToString()), ("$generation", generation), ("$category", change.CategoryId!.Value.ToString()), ("$title", change.Title!), ("$content", change.Content!), ("$sort", change.SortOrder), ("$version", change.Version));
                }
            }
            if (updateState is not null)
                await ExecuteAsync(connection, transaction, "UPDATE enterprise_sync_state SET cursor=$cursor,release_number=$release,last_synchronized_at_utc=$at,last_result=$result,last_error_code=NULL,trace_id=NULL WHERE id=1;", cancellationToken, ("$cursor", updateState.Cursor!), ("$release", updateState.ReleaseNumber), ("$at", updateState.LastSynchronizedAtUtc!.Value.ToString("O")), ("$result", updateState.LastResult!));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { try { await transaction.RollbackAsync(CancellationToken.None); } catch { } throw; }
    }

    private static async Task<string?> ReadActiveGenerationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT active_generation FROM enterprise_sync_state WHERE id=1;";
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
