using Microsoft.Data.Sqlite;
using System.Text;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 负责创建当前 SQLite 结构，并按顺序执行 v1→v2→v3 前向迁移。
/// v3 只新增企业同步缓存表；任何校验失败都会回滚，绝不通过删除数据库来“修复”结构。
/// </summary>
internal sealed class DatabaseInitializer
{
    private const int CurrentSchemaVersion = 3;
    private const int Version2Schema = 2;
    private const int LegacySchemaVersion = 1;
    private readonly SqliteConnectionFactory _connections;
    private readonly QuickPhraseDataOptions _options;

    public DatabaseInitializer(QuickPhraseDataOptions options, SqliteConnectionFactory connections)
    {
        _options = options;
        _connections = connections;
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.DatabasePath) || new FileInfo(_options.DatabasePath).Length == 0)
        {
            await CreateCurrentSchemaAsync(cancellationToken);
            return;
        }

        var schemaVersion = await ReadSchemaVersionAsync(cancellationToken);
        switch (schemaVersion)
        {
            case LegacySchemaVersion:
                await MigrateVersion1ToVersion2Async(cancellationToken);
                await MigrateVersion2ToVersion3Async(cancellationToken);
                return;
            case Version2Schema:
                if (!await HasVersion2SchemaAsync(cancellationToken))
                    throw InvalidSchema("v2");
                await MigrateVersion2ToVersion3Async(cancellationToken);
                return;
            case CurrentSchemaVersion:
                if (await HasCurrentSchemaAsync(cancellationToken)) return;
                throw InvalidSchema("v3");
            default:
                throw new DataStoreException(
                    "DATABASE_UNSUPPORTED_VERSION",
                    $"检测到不受支持的本地数据库版本 v{schemaVersion}。为避免误迁移或丢失数据，QuickPhrase 不会自动重建数据库。\n"
                    + $"数据目录：{_options.DataDirectory}");
        }
    }

    private async Task CreateCurrentSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenWriterAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        try
        {
            await ExecuteSqlAsync(connection, transaction, LoadDatabaseScript("InitialSchema.sql"), cancellationToken);
            if (!await HasCurrentSchemaAsync(connection, transaction, cancellationToken))
                throw new InvalidOperationException("首次安装数据库结构校验失败。");

            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await TryRollbackAsync(transaction);
            throw;
        }
        catch (Exception ex) when (ex is not DataStoreException)
        {
            await TryRollbackAsync(transaction);
            throw new DataStoreException("DATABASE_INITIALIZATION_FAILED", "本地数据库初始化失败，请查看日志后重试。", ex);
        }
    }

    private async Task MigrateVersion1ToVersion2Async(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenWriterAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        try
        {
            await ExecuteSqlAsync(connection, transaction, LoadDatabaseScript("MigrateV1ToV2.sql"), cancellationToken);
            if (!await HasVersion2SchemaAsync(connection, transaction, cancellationToken))
                throw new InvalidOperationException("v1 到 v2 迁移后的数据库结构校验失败。");

            await EnsureDatabaseIntegrityAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await TryRollbackAsync(transaction);
            throw;
        }
        catch (Exception ex)
        {
            await TryRollbackAsync(transaction);
            throw new DataStoreException(
                "DATABASE_MIGRATION_FAILED",
                "本地数据库从 v1 升级到 v2 失败，迁移已完整回滚，原有数据未被部分修改。请查看日志后重试。",
                ex);
        }
    }

    private async Task MigrateVersion2ToVersion3Async(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenWriterAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        try
        {
            await ExecuteSqlAsync(connection, transaction, LoadDatabaseScript("MigrateV2ToV3.sql"), cancellationToken);
            if (!await HasCurrentSchemaAsync(connection, transaction, cancellationToken))
                throw new InvalidOperationException("v2 到 v3 迁移后的数据库结构校验失败。");
            await EnsureDatabaseIntegrityAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await TryRollbackAsync(transaction);
            throw;
        }
        catch (Exception ex)
        {
            await TryRollbackAsync(transaction);
            throw new DataStoreException("DATABASE_MIGRATION_FAILED", "本地数据库从 v2 升级到 v3 失败，迁移已完整回滚，原有个人数据保持不变。请查看日志后重试。", ex);
        }
    }

    private DataStoreException InvalidSchema(string version) => new(
        "DATABASE_SCHEMA_INVALID",
        $"本地数据库版本为 {version}，但结构不完整或已损坏。为避免误删数据，QuickPhrase 已停止启动，请先备份数据文件后再处理。\n数据目录：{_options.DataDirectory}");

    private async Task<int> ReadSchemaVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenReadAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<bool> HasCurrentSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenReadAsync(cancellationToken);
        return await HasCurrentSchemaAsync(connection, transaction: null, cancellationToken);
    }

    private async Task<bool> HasVersion2SchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenReadAsync(cancellationToken);
        return await HasVersion2SchemaAsync(connection, transaction: null, cancellationToken);
    }

    private static async Task<bool> HasCurrentSchemaAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        if (await ReadSchemaVersionAsync(connection, transaction, cancellationToken) != CurrentSchemaVersion) return false;
        if (!await HasBaseSchemaShapeAsync(connection, transaction, cancellationToken)) return false;
        foreach (var table in new[] { "sync_accounts", "enterprise_categories_cache", "enterprise_phrases_cache", "enterprise_sync_state" })
            if (!await TableExistsAsync(connection, transaction, table, cancellationToken)) return false;
        return true;
    }

    private static async Task<bool> HasVersion2SchemaAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        if (await ReadSchemaVersionAsync(connection, transaction, cancellationToken) != Version2Schema) return false;
        return await HasBaseSchemaShapeAsync(connection, transaction, cancellationToken);
    }

    private static async Task<bool> HasBaseSchemaShapeAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "categories", "phrases", "settings", "search_history" })
            if (!await TableExistsAsync(connection, transaction, table, cancellationToken)) return false;
        var requiredColumns = new (string Table, string Column)[] { ("categories", "parent_id"), ("phrases", "shortcut_mode"), ("phrases", "color_key"), ("phrases", "sort_order"), ("settings", "value_json"), ("search_history", "normalized_query") };
        foreach (var (table, column) in requiredColumns)
            if (!await ColumnExistsAsync(connection, transaction, table, column, cancellationToken)) return false;
        if (await ColumnExistsAsync(connection, transaction, "phrases", "favorite", cancellationToken)) return false;
        if (await IndexExistsAsync(connection, transaction, "ix_phrases_favorite", cancellationToken)) return false;
        return true;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, SqliteTransaction? transaction, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info([{table}]);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task<bool> IndexExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string index,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", index);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task EnsureDatabaseIntegrityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.Transaction = transaction;
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("迁移后的数据库外键检查失败。");
        }

        await using var integrity = connection.CreateCommand();
        integrity.Transaction = transaction;
        integrity.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"迁移后的数据库完整性检查失败：{result}");
    }

    private static async Task ExecuteSqlAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TryRollbackAsync(SqliteTransaction transaction)
    {
        try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
    }

    private static string LoadDatabaseScript(string fileName)
    {
        var assembly = typeof(DatabaseInitializer).Assembly;
        var suffix = $".Database.{fileName}";
        var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(x => x.EndsWith(suffix, StringComparison.Ordinal));
        if (resourceName is null) throw new InvalidOperationException($"找不到数据库脚本资源：{fileName}");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"无法读取数据库脚本资源：{fileName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
