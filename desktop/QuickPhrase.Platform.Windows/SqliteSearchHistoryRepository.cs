using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 搜索历史 SQLite 仓储。写入统一进入单写者队列，并在同一事务内完成去重更新和容量淘汰；
/// 读取遇到单行坏数据时跳过该行，避免一条异常历史阻断整个搜索界面。
/// </summary>
internal sealed class SqliteSearchHistoryRepository : SqliteRepositoryBase, ISearchHistoryRepository
{
    private const int MaxEntries = 10;

    public SqliteSearchHistoryRepository(SqliteConnectionFactory connections, SqliteWriteQueue writes, TimeProvider clock)
        : base(connections, writes, clock)
    {
    }

    public async Task<IReadOnlyList<SearchHistoryEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<SearchHistoryEntry>();
        try
        {
            await using var connection = await Connections.OpenReadAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT query, last_searched_at_utc FROM search_history ORDER BY julianday(last_searched_at_utc) DESC, id DESC LIMIT 10;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                try
                {
                    var query = reader.GetString(0).Trim();
                    var timestamp = DateTimeOffset.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
                    if (query.Length == 0) throw new FormatException("关键词为空。");
                    entries.Add(new SearchHistoryEntry(query, timestamp));
                }
                catch (Exception exception) when (exception is FormatException or InvalidCastException or ArgumentException)
                {
                    Console.Error.WriteLine($"搜索历史读取时跳过异常记录：时间或关键词格式无效（{exception.GetType().Name}）。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"搜索历史加载失败：{exception.GetType().Name}。已回退为空列表。");
            return [];
        }

        return entries;
    }

    public Task<RepositoryResult<SearchHistoryEntry>> RecordAsync(string query, CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => RecordCoreAsync(connection, query, ct), cancellationToken);

    private async Task<RepositoryResult<SearchHistoryEntry>> RecordCoreAsync(SqliteConnection connection, string query, CancellationToken cancellationToken)
    {
        var display = query?.Trim() ?? string.Empty;
        if (display.Length == 0)
            return RepositoryResult<SearchHistoryEntry>.Failure(Validation("搜索关键词不能为空。"));
        if (display.Length > 200)
            return RepositoryResult<SearchHistoryEntry>.Failure(Validation("搜索关键词不能超过 200 个字符。"));

        var normalized = display.ToUpperInvariant();
        var searchedAt = Clock.GetLocalNow();
        await using var transaction = connection.BeginTransaction();
        try
        {
            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO search_history(query, normalized_query, last_searched_at_utc)
                    VALUES ($query, $normalized, $searchedAt)
                    ON CONFLICT(normalized_query) DO UPDATE SET
                        query=excluded.query,
                        last_searched_at_utc=excluded.last_searched_at_utc;
                    """;
                upsert.Parameters.AddWithValue("$query", display);
                upsert.Parameters.AddWithValue("$normalized", normalized);
                upsert.Parameters.AddWithValue("$searchedAt", searchedAt.ToString("O"));
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var trim = connection.CreateCommand())
            {
                trim.Transaction = transaction;
                trim.CommandText = """
                    DELETE FROM search_history
                    WHERE id NOT IN (
                        SELECT id FROM search_history
                        ORDER BY julianday(last_searched_at_utc) DESC, id DESC
                        LIMIT 10
                    );
                    """;
                await trim.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return RepositoryResult<SearchHistoryEntry>.Success(
                new SearchHistoryEntry(display, searchedAt),
                new CommittedDataChange("search_history", Guid.Empty, "record", searchedAt));
        }
        catch (SqliteException exception)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            return RepositoryResult<SearchHistoryEntry>.Failure(MapSqliteError(exception));
        }
        catch (OperationCanceledException)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    public Task<RepositoryResult<bool>> ClearAsync(CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => ClearCoreAsync(connection, ct), cancellationToken);

    private async Task<RepositoryResult<bool>> ClearCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM search_history;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return RepositoryResult<bool>.Success(true,
                new CommittedDataChange("search_history", Guid.Empty, "clear", Clock.GetLocalNow()));
        }
        catch (SqliteException exception)
        {
            return RepositoryResult<bool>.Failure(MapSqliteError(exception));
        }
    }
}



