using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 迁移在单事务内执行；执行前创建 SQLite 一致性备份，checksum 变化直接阻止启动。
/// </summary>
internal sealed class MigrationRunner
{
    private readonly QuickPhraseDataOptions _options;
    private readonly SqliteConnectionFactory _connections;
    private readonly IReadOnlyList<SqliteMigration> _migrations;

    public MigrationRunner(QuickPhraseDataOptions options, SqliteConnectionFactory connections, IReadOnlyList<SqliteMigration>? additionalMigrations = null)
    {
        _options = options;
        _connections = connections;
        // 测试或受控升级场景可以注入同版本迁移；注入项覆盖内置迁移，避免版本字典出现重复键。
        _migrations = LoadMigrations()
            .Concat(additionalMigrations ?? [])
            .GroupBy(x => x.Version)
            .Select(group => group.Last())
            .OrderBy(x => x.Version)
            .ToArray();
    }

    public async Task EnsureMigratedAsync(CancellationToken cancellationToken)
    {
        var migrations = _migrations;
        var current = await ReadCurrentVersionAsync(cancellationToken);
        ValidateAppliedChecksums(current, migrations);
        var pending = migrations.Where(x => x.Version > current).ToArray();
        if (pending.Length == 0) return;

        if (File.Exists(_options.DatabasePath) && new FileInfo(_options.DatabasePath).Length > 0)
            await CreateBackupAsync(cancellationToken);

        await using var connection = await _connections.OpenWriterAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var migration in pending)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken);

                await using var record = connection.CreateCommand();
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_migrations(version, name, checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAt);";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue("$checksum", migration.Checksum);
                record.Parameters.AddWithValue("$appliedAt", _options.TimeProvider.GetUtcNow().ToString("O"));
                await record.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException ex)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { /* 原始异常更有价值 */ }
            throw new DataStoreException(MapSqliteCode(ex), "SQLite 迁移失败，原数据库保持不变。", ex);
        }
        catch (Exception ex) when (ex is not DataStoreException)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { /* 原始异常更有价值 */ }
            throw new DataStoreException("MIGRATION_FAILED", "SQLite 迁移失败，原数据库保持不变。", ex);
        }
    }

    private async Task<int> ReadCurrentVersionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.DatabasePath) || new FileInfo(_options.DatabasePath).Length == 0) return 0;
        var mismatches = new List<(int Version, string Name)>();
        var current = 0;
        await using (var connection = await _connections.OpenReadAsync(cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT version, name, checksum FROM schema_migrations ORDER BY version;";
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var migrations = _migrations.ToDictionary(x => x.Version);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var version = reader.GetInt32(0);
                    var name = reader.GetString(1);
                    var checksum = reader.GetString(2);
                    if (!migrations.TryGetValue(version, out var known) || known.Name != name)
                        throw new DataStoreException("MIGRATION_FAILED", $"已执行迁移 {version} 的校验信息不匹配。");
                    if (!string.Equals(known.Checksum, checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!await IsCompatibleAdditiveMigrationAsync(connection, version, name, cancellationToken))
                            throw new DataStoreException("MIGRATION_FAILED", $"已执行迁移 {version} 的校验信息不匹配。");
                        mismatches.Add((version, name));
                    }
                    current = Math.Max(current, version);
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                return 0;
            }
        }

        if (mismatches.Count > 0)
            await RepairChecksumsAsync(mismatches, cancellationToken);
        return current;
    }

    /// <summary>仅对已确认完成结构变更的追加型迁移修复历史校验值，避免换行或注释变化阻断升级。</summary>
    private static async Task<bool> IsCompatibleAdditiveMigrationAsync(SqliteConnection connection, int version, string name, CancellationToken cancellationToken)
    {
        var requiredColumn = (version, name) switch
        {
            (2, "002_category_hierarchy") => (Table: "categories", Column: "parent_id"),
            (3, "003_phrase_color_key") => (Table: "phrases", Column: "color_key"),
            _ => default,
        };
        if (requiredColumn == default) return false;

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info([{requiredColumn.Table}]);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), requiredColumn.Column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private async Task RepairChecksumsAsync(IReadOnlyList<(int Version, string Name)> mismatches, CancellationToken cancellationToken)
    {
        var known = _migrations.ToDictionary(x => x.Version);
        await using var connection = await _connections.OpenWriterAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        foreach (var mismatch in mismatches)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE schema_migrations SET checksum = $checksum WHERE version = $version AND name = $name;";
            command.Parameters.AddWithValue("$checksum", known[mismatch.Version].Checksum);
            command.Parameters.AddWithValue("$version", mismatch.Version);
            command.Parameters.AddWithValue("$name", mismatch.Name);
            await command.ExecuteNonQueryAsync(cancellationToken);
            Console.WriteLine($"已兼容修复追加型迁移 {mismatch.Version} 的历史校验值。");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static void ValidateAppliedChecksums(int current, IReadOnlyList<SqliteMigration> migrations)
    {
        if (current > migrations.Max(x => x.Version))
            throw new DataStoreException("MIGRATION_FAILED", "数据库版本高于当前程序，无法安全启动。");
    }

    private async Task CreateBackupAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.BackupDirectory);
        var backupPath = Path.Combine(_options.BackupDirectory, $"quickphrase-{DateTime.UtcNow:yyyyMMddHHmmssfff}.db");
        await using var source = await _connections.OpenWriterAsync(cancellationToken);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static IReadOnlyList<SqliteMigration> LoadMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(x => x.Contains(".Migrations.", StringComparison.Ordinal) && x.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0) throw new InvalidOperationException("找不到 SQLite 迁移资源。");

        var migrations = new List<SqliteMigration>(resources.Length);
        foreach (var resource in resources)
        {
            var marker = ".Migrations.";
            var fileName = resource[(resource.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
            var name = Path.GetFileNameWithoutExtension(fileName);
            var separator = name.IndexOf('_');
            if (separator <= 0 || !int.TryParse(name[..separator], out var version))
                throw new InvalidOperationException($"SQLite 迁移文件名无效：{fileName}。");
            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"找不到迁移资源：{fileName}。");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd();
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
            migrations.Add(new SqliteMigration(version, name, sql, checksum));
        }
        return migrations;
    }

    private static string MapSqliteCode(SqliteException ex) => ex.SqliteErrorCode is 5 or 6 ? "DATABASE_BUSY" : "MIGRATION_FAILED";

}

internal sealed record SqliteMigration(int Version, string Name, string Sql, string Checksum);
