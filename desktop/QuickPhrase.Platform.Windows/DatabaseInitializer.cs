using Microsoft.Data.Sqlite;
using System.Text;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 未发布阶段的 SQLite 初始化器。产品只承认一份当前 V1 结构；
/// 任何版本或表结构不符合该定义的开发数据库都会被清空并按初始脚本重建，不执行迁移。
/// </summary>
internal sealed class DatabaseInitializer
{
    private const int CurrentSchemaVersion = 1;
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
        if (schemaVersion == CurrentSchemaVersion && await HasCurrentSchemaAsync(cancellationToken))
            return;

        await RebuildDevelopmentDatabaseAsync(schemaVersion, cancellationToken);
    }

    private async Task RebuildDevelopmentDatabaseAsync(int detectedSchemaVersion, CancellationToken cancellationToken)
    {
        var traceId = Guid.NewGuid();
        try
        {
            var backupDirectory = BackupDatabaseFiles();
            DeleteDatabaseFiles();
            await CreateCurrentSchemaAsync(cancellationToken);
            Console.Error.WriteLine(
                $"检测到未发布阶段的旧开发数据，已备份但未迁移，并按新的正式 Schema V1 重建。阶段：DATABASE_REBUILD；结果码：DATABASE_V1_REBUILT；" +
                $"检测版本：{detectedSchemaVersion}；备份目录：{backupDirectory}；TraceId：{traceId}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"未发布阶段本地数据重建失败。阶段：DATABASE_REBUILD；结果码：DATABASE_REBUILD_FAILED；" +
                $"数据目录：{_options.DataDirectory}；TraceId：{traceId}；异常类型：{exception.GetType().Name}");
            throw new DataStoreException(
                "DATABASE_REBUILD_FAILED",
                $"本地开发数据不符合当前 V1 结构，自动重建失败。请关闭占用数据库的程序后重试。数据目录：{_options.DataDirectory}；TraceId：{traceId}",
                exception);
        }
    }

    /// <summary>在删除任何开发库文件前复制数据库及 WAL/SHM 旁路文件；备份失败会中止重建。</summary>
    private string BackupDatabaseFiles()
    {
        var backupDirectory = Path.Combine(_options.DevelopmentBackupDirectory, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(backupDirectory);
        foreach (var path in DatabaseFiles().Where(File.Exists))
            File.Copy(path, Path.Combine(backupDirectory, Path.GetFileName(path)), overwrite: false);
        return backupDirectory;
    }
    private void DeleteDatabaseFiles()
    {
        // SQLite 在 WAL 和 rollback-journal 模式下可能留下旁路文件；重建时必须一并删除，避免旧页再次被附着。
        foreach (var path in DatabaseFiles())

        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private IEnumerable<string> DatabaseFiles()
    {
        yield return _options.DatabasePath;
        yield return _options.DatabasePath + "-wal";
        yield return _options.DatabasePath + "-shm";
        yield return _options.DatabasePath + "-journal";
    }
    private async Task CreateCurrentSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenWriterAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        try
        {
            await ExecuteSqlAsync(connection, transaction, LoadInitialSchemaScript(), cancellationToken);
            if (!await HasCurrentSchemaAsync(connection, transaction, cancellationToken))
                throw new InvalidOperationException("当前 V1 数据库结构校验失败。");

            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await TryRollbackAsync(transaction);
            throw;
        }
        catch (Exception exception) when (exception is not DataStoreException)
        {
            await TryRollbackAsync(transaction);
            throw new DataStoreException("DATABASE_INITIALIZATION_FAILED", "本地数据库初始化失败，请查看日志后重试。", exception);
        }
    }

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

    private static async Task<bool> HasCurrentSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (await ReadSchemaVersionAsync(connection, transaction, cancellationToken) != CurrentSchemaVersion)
            return false;
        if (!await HasCurrentSchemaShapeAsync(connection, transaction, cancellationToken))
            return false;

        foreach (var table in new[] { "sync_accounts", "enterprise_categories_cache", "enterprise_phrases_cache", "enterprise_sync_state" })
        {
            if (!await TableExistsAsync(connection, transaction, table, cancellationToken))
                return false;
        }

        return true;
    }

    private static async Task<bool> HasCurrentSchemaShapeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        // 完整比对当前 V1 的表列和显式索引，确保即使 user_version 被误标为 1，
        // 也不会把残缺或历史开发库当成可继续使用的当前数据。
        var expectedColumns = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["categories"] = ["id", "parent_id", "name", "normalized_name", "sort_order", "version", "created_at_utc", "updated_at_utc"],
            ["phrases"] = ["id", "title", "category_id", "shortcut_mode", "shortcut_display", "shortcut_normalized", "usage_count", "last_used_at_utc", "version", "created_at_utc", "updated_at_utc", "color_key", "sort_order"],
            ["phrase_segments"] = ["segment_id", "phrase_id", "segment_kind", "text_content", "media_asset_id", "sort_order"],
            ["media_assets"] = ["asset_id", "storage_key", "mime_type", "byte_length", "pixel_width", "pixel_height", "created_at_utc"],
            ["settings"] = ["key", "value_json", "version", "updated_at_utc"],
            ["search_history"] = ["id", "query", "normalized_query", "last_searched_at_utc"],
            ["sync_accounts"] = ["id", "hub_address", "account", "display_name", "device_id", "token_reference", "status", "last_authenticated_at_utc", "created_at_utc", "updated_at_utc"],
            ["enterprise_categories_cache"] = ["id", "generation", "parent_id", "name", "sort_order", "version"],
            ["enterprise_phrases_cache"] = ["id", "generation", "category_id", "title", "content", "sort_order", "version"],
            ["enterprise_sync_state"] = ["id", "active_generation", "cursor", "release_number", "last_synchronized_at_utc", "last_result", "last_error_code", "trace_id"],
        };
        foreach (var (table, columns) in expectedColumns)
        {
            if (!await TableExistsAsync(connection, transaction, table, cancellationToken)
                || !await HasExactColumnsAsync(connection, transaction, table, columns, cancellationToken))
            {
                return false;
            }
        }

        foreach (var index in new[]
                 {
                     "ux_categories_root_normalized_name",
                     "ux_categories_child_parent_normalized_name",
                     "ix_categories_parent_id",
                     "ux_phrases_shortcut_normalized",
                     "ix_phrases_category_id",
                     "ix_phrases_last_used_at",
                     "ix_phrases_category_sort",
                     "ux_phrase_segments_phrase_sort",
                     "ix_phrase_segments_media_asset",
                     "ix_search_history_last_searched_at",
                     "ix_enterprise_categories_generation_sort",
                     "ix_enterprise_phrases_generation_category_sort",
                 })
        {
            if (!await IndexExistsAsync(connection, transaction, index, cancellationToken))
                return false;
        }

        // 列名和索引不足以证明约束完整：同名表可能被手工重建后丢失外键或 CHECK，
        // 若继续启动会允许无效图文段和悬空媒体引用进入事实源，因此必须按开发库不兼容流程备份重建。
        if (!await HasForeignKeyAsync(connection, transaction, "phrases", "category_id", "categories", "id", "RESTRICT", cancellationToken)
            || !await HasForeignKeyAsync(connection, transaction, "phrase_segments", "phrase_id", "phrases", "id", "CASCADE", cancellationToken)
            || !await HasForeignKeyAsync(connection, transaction, "phrase_segments", "media_asset_id", "media_assets", "asset_id", "RESTRICT", cancellationToken)
            || !await HasConstraintFragmentsAsync(connection, transaction, "phrases",
                ["CHECK (LENGTH(TITLE) BETWEEN 1 AND 80)"],
                cancellationToken)
            || !await HasConstraintFragmentsAsync(connection, transaction, "media_assets",
                ["CHECK (MIME_TYPE IN ('IMAGE/PNG', 'IMAGE/JPEG'))", "CHECK (BYTE_LENGTH BETWEEN 1 AND 10485760)"],
                cancellationToken)
            || !await HasConstraintFragmentsAsync(connection, transaction, "phrase_segments",
                ["CHECK (SEGMENT_KIND IN ('TEXT', 'IMAGE'))", "SEGMENT_KIND = 'TEXT'", "SEGMENT_KIND = 'IMAGE'"],
                cancellationToken))
        {
            return false;
        }

        return true;
    }

    private static async Task<bool> HasForeignKeyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string fromColumn,
        string targetTable,
        string targetColumn,
        string onDelete,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA foreign_key_list([{table}]);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(3), fromColumn, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reader.GetString(2), targetTable, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reader.GetString(4), targetColumn, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reader.GetString(6), onDelete, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> HasConstraintFragmentsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyList<string> expectedFragments,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        var sql = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
        var normalized = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return expectedFragments.All(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
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

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasExactColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyCollection<string> expectedColumns,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info([{table}]);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var actualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
            actualColumns.Add(reader.GetString(1));

        return actualColumns.Count == expectedColumns.Count
            && expectedColumns.All(actualColumns.Contains);
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

    private static string LoadInitialSchemaScript()
    {
        var assembly = typeof(DatabaseInitializer).Assembly;
        const string suffix = ".Database.InitialSchema.sql";
        var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidOperationException("找不到当前 V1 数据库初始化脚本。");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("无法读取当前 V1 数据库初始化脚本。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
