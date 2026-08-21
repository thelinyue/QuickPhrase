using Microsoft.Data.Sqlite;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

/// <summary>
/// 验证当前唯一 V1 数据库包含企业同步缓存所需的完整表集合，
/// 不为未发布阶段保留任何历史结构升级路径。
/// </summary>
public sealed class CurrentEnterpriseSyncSchemaTests
{
    [Fact]
    public async Task FreshDatabaseCreatesEnterpriseSyncTablesInV1()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        await using var connection = new SqliteConnection($"Data Source={runtime.DatabasePath};Pooling=False");
        await connection.OpenAsync();

        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(connection, "PRAGMA user_version;")));
        foreach (var table in new[] { "sync_accounts", "enterprise_categories_cache", "enterprise_phrases_cache", "enterprise_sync_state" })
        {
            Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
                connection,
                "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name;",
                ("$name", table))));
        }

        foreach (var forbidden in new[] { "sync_outbox", "personal_sync_state", "personal_changes" })
        {
            Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(
                connection,
                "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name;",
                ("$name", forbidden))));
        }
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, string Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("QuickPhrase-CurrentEnterpriseSchema-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
