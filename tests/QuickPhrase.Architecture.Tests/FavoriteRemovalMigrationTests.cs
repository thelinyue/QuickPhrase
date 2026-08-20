using Microsoft.Data.Sqlite;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

/// <summary>
/// 锁定收藏能力退出后的领域契约和 SQLite v1 到当前版本的数据迁移。
/// 迁移只允许删除收藏列和值，不能以重建整个数据库为代价破坏其他有效开发数据。
/// </summary>
public sealed class FavoriteRemovalMigrationTests
{
    [Fact]
    public void CorePhraseContractsDoNotExposeFavorite()
    {
        Assert.DoesNotContain(typeof(Phrase).GetProperties(), property => property.Name == "Favorite");
        Assert.DoesNotContain(typeof(CreatePhraseCommand).GetProperties(), property => property.Name == "Favorite");
        Assert.DoesNotContain(typeof(UpdatePhraseCommand).GetProperties(), property => property.Name == "Favorite");
    }

    [Fact]
    public void FormalProductSourceContainsNoFavoriteBusinessReferencesOutsideTheVersion1Migration()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var formalProjects = new[]
        {
            Path.Combine(repositoryRoot, "desktop", "QuickPhrase.Core"),
            Path.Combine(repositoryRoot, "desktop", "QuickPhrase.Platform.Windows"),
            Path.Combine(repositoryRoot, "desktop", "QuickPhrase.Desktop"),
        };
        var allowedHistoricalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(repositoryRoot, "desktop", "QuickPhrase.Platform.Windows", "DatabaseInitializer.cs"),
            Path.Combine(repositoryRoot, "desktop", "QuickPhrase.Platform.Windows", "Database", "MigrateV1ToV2.sql"),
        };

        var unexpectedReferences = formalProjects
            .SelectMany(project => Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories))
            .Where(path => Path.GetExtension(path) is ".cs" or ".xaml" or ".sql")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !allowedHistoricalFiles.Contains(path))
            .Where(path =>
            {
                var content = File.ReadAllText(path);
                return content.Contains("Favorite", StringComparison.OrdinalIgnoreCase) ||
                       content.Contains("收藏", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.True(
            unexpectedReferences.Length == 0,
            $"正式产品源码仍包含收藏业务引用：{string.Join(", ", unexpectedReferences)}");
    }

    [Fact]
    public async Task FreshDatabaseUsesCurrentSchemaWithoutFavoriteColumnOrIndex()
    {
        using var temp = new TemporaryDirectory();
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path)))
        {
            Assert.Empty(await runtime.Phrases.ListAsync());
        }

        var databasePath = new QuickPhraseDataOptions(temp.Path).DatabasePath;
        Assert.Equal(3L, await ScalarLongAsync(databasePath, "PRAGMA user_version;"));
        Assert.False(await ColumnExistsAsync(databasePath, "phrases", "favorite"));
        Assert.False(await IndexExistsAsync(databasePath, "ix_phrases_favorite"));
    }

    [Fact]
    public async Task Version1DatabaseMigratesToCurrentVersionAndPreservesNonFavoriteData()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        var categoryId = Guid.NewGuid();
        var childCategoryId = Guid.NewGuid();
        var firstPhraseId = Guid.NewGuid();
        var secondPhraseId = Guid.NewGuid();
        await CreateVersion1DatabaseAsync(options.DatabasePath, categoryId, childCategoryId, firstPhraseId, secondPhraseId);

        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options))
        {
            var categories = await runtime.Categories.ListAsync();
            var phrases = await runtime.Phrases.ListAsync();
            var settings = await runtime.Settings.LoadAsync();
            var history = await runtime.SearchHistory.ListAsync();

            Assert.Equal(2, categories.Count);
            Assert.Contains(categories, category =>
                category.Id == categoryId && category.ParentId is null && category.Name == "迁移分类");
            Assert.Contains(categories, category =>
                category.Id == childCategoryId && category.ParentId == categoryId && category.Name == "迁移子分类");

            Assert.Equal(2, phrases.Count);
            Assert.Contains(phrases, phrase =>
                phrase.Id == firstPhraseId &&
                phrase.Title == "收藏话术" &&
                phrase.Content == "收藏值应被删除，话术应保留。" &&
                phrase.CategoryId == childCategoryId &&
                phrase.ShortcutMode == ShortcutMode.Custom &&
                phrase.Shortcut == new ShortcutValue("Ctrl+Alt+K", "CTRL+ALT+K") &&
                phrase.UsageCount == 7 &&
                phrase.Version == 3 &&
                phrase.ColorKey == "blue" &&
                phrase.SortOrder == 1 &&
                phrase.LastUsedAtUtc is not null);
            Assert.Contains(phrases, phrase =>
                phrase.Id == secondPhraseId &&
                phrase.Title == "普通话术" &&
                phrase.Content == "普通话术正文。" &&
                phrase.CategoryId == categoryId &&
                phrase.ShortcutMode == ShortcutMode.None &&
                phrase.Shortcut is null &&
                phrase.UsageCount == 2 &&
                phrase.Version == 1 &&
                phrase.ColorKey == "default" &&
                phrase.SortOrder == 2);

            Assert.True(settings.StayInTrayOnClose);
            Assert.Equal("迁移搜索", Assert.Single(history).Query);
        }

        Assert.Equal(3L, await ScalarLongAsync(options.DatabasePath, "PRAGMA user_version;"));
        Assert.False(await ColumnExistsAsync(options.DatabasePath, "phrases", "favorite"));
        Assert.False(await IndexExistsAsync(options.DatabasePath, "ix_phrases_favorite"));
        Assert.Equal("ok", await ScalarStringAsync(options.DatabasePath, "PRAGMA integrity_check;"));
        Assert.Equal(0L, await ScalarLongAsync(options.DatabasePath, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));

        // 已迁移数据库必须可重复打开，不能再次执行或破坏数据。
        await using var reopened = await QuickPhraseDataRuntime.OpenAsync(options);
        Assert.Equal(2, (await reopened.Phrases.ListAsync()).Count);
    }

    [Fact]
    public async Task FailedVersion1MigrationRollsBackSchemaVersion()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        Directory.CreateDirectory(options.DataDirectory);

        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE categories(id TEXT PRIMARY KEY, parent_id TEXT NULL, name TEXT NOT NULL, normalized_name TEXT NOT NULL, sort_order INTEGER NOT NULL, version INTEGER NOT NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
                CREATE TABLE phrases(id TEXT PRIMARY KEY, title TEXT NOT NULL, content TEXT NOT NULL, category_id TEXT NOT NULL, shortcut_mode TEXT NOT NULL, shortcut_display TEXT NULL, shortcut_normalized TEXT NULL, usage_count INTEGER NOT NULL, last_used_at_utc TEXT NULL, version INTEGER NOT NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, color_key TEXT NOT NULL, sort_order INTEGER NOT NULL);
                CREATE TABLE settings(key TEXT PRIMARY KEY, value_json TEXT NOT NULL, version INTEGER NOT NULL, updated_at_utc TEXT NOT NULL);
                CREATE TABLE search_history(id INTEGER PRIMARY KEY AUTOINCREMENT, query TEXT NOT NULL, normalized_query TEXT NOT NULL UNIQUE, last_searched_at_utc TEXT NOT NULL);
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<DataStoreException>(() => QuickPhraseDataRuntime.OpenAsync(options));
        Assert.Equal("DATABASE_MIGRATION_FAILED", exception.Code);
        Assert.Equal(1L, await ScalarLongAsync(options.DatabasePath, "PRAGMA user_version;"));
        Assert.False(await ColumnExistsAsync(options.DatabasePath, "phrases", "favorite"));
    }

    [Fact]
    public async Task DamagedVersion2SchemaIsRejectedWithoutRebuildingDatabase()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        Directory.CreateDirectory(options.DataDirectory);
        await ExecuteDatabaseSqlAsync(options.DatabasePath, "CREATE TABLE marker(value TEXT NOT NULL); INSERT INTO marker(value) VALUES ('keep'); PRAGMA user_version = 2;");

        var exception = await Assert.ThrowsAsync<DataStoreException>(() => QuickPhraseDataRuntime.OpenAsync(options));

        Assert.Equal("DATABASE_SCHEMA_INVALID", exception.Code);
        Assert.Equal("keep", await ScalarStringAsync(options.DatabasePath, "SELECT value FROM marker;"));
        Assert.Equal(2L, await ScalarLongAsync(options.DatabasePath, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task UnknownSchemaVersionIsRejectedWithoutRebuildingDatabase()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        Directory.CreateDirectory(options.DataDirectory);
        await ExecuteDatabaseSqlAsync(options.DatabasePath, "CREATE TABLE marker(value TEXT NOT NULL); INSERT INTO marker(value) VALUES ('keep'); PRAGMA user_version = 99;");

        var exception = await Assert.ThrowsAsync<DataStoreException>(() => QuickPhraseDataRuntime.OpenAsync(options));

        Assert.Equal("DATABASE_UNSUPPORTED_VERSION", exception.Code);
        Assert.Equal("keep", await ScalarStringAsync(options.DatabasePath, "SELECT value FROM marker;"));
        Assert.Equal(99L, await ScalarLongAsync(options.DatabasePath, "PRAGMA user_version;"));
    }

    private static async Task CreateVersion1DatabaseAsync(
        string databasePath,
        Guid categoryId,
        Guid childCategoryId,
        Guid firstPhraseId,
        Guid secondPhraseId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE categories (
                id TEXT PRIMARY KEY,
                parent_id TEXT NULL REFERENCES categories(id) ON DELETE RESTRICT,
                name TEXT NOT NULL,
                normalized_name TEXT NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX ux_categories_root_normalized_name ON categories(normalized_name) WHERE parent_id IS NULL;
            CREATE UNIQUE INDEX ux_categories_child_parent_normalized_name ON categories(parent_id, normalized_name) WHERE parent_id IS NOT NULL;
            CREATE INDEX ix_categories_parent_id ON categories(parent_id);

            CREATE TABLE phrases (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 80),
                content TEXT NOT NULL CHECK (length(content) BETWEEN 1 AND 4000),
                category_id TEXT NOT NULL REFERENCES categories(id) ON DELETE RESTRICT,
                favorite INTEGER NOT NULL DEFAULT 0 CHECK (favorite IN (0, 1)),
                shortcut_mode TEXT NOT NULL CHECK (shortcut_mode IN ('None', 'Quick', 'Custom')),
                shortcut_display TEXT NULL,
                shortcut_normalized TEXT NULL,
                usage_count INTEGER NOT NULL DEFAULT 0 CHECK (usage_count >= 0),
                last_used_at_utc TEXT NULL,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                color_key TEXT NOT NULL DEFAULT 'default',
                sort_order INTEGER NOT NULL DEFAULT 0 CHECK (sort_order >= 0),
                CHECK ((shortcut_mode = 'None' AND shortcut_display IS NULL AND shortcut_normalized IS NULL)
                    OR (shortcut_mode <> 'None' AND shortcut_display IS NOT NULL AND shortcut_normalized IS NOT NULL))
            );
            CREATE UNIQUE INDEX ux_phrases_shortcut_normalized ON phrases(shortcut_normalized) WHERE shortcut_normalized IS NOT NULL;
            CREATE INDEX ix_phrases_category_id ON phrases(category_id);
            CREATE INDEX ix_phrases_favorite ON phrases(favorite);
            CREATE INDEX ix_phrases_last_used_at ON phrases(last_used_at_utc);
            CREATE INDEX ix_phrases_category_sort ON phrases(category_id, sort_order);

            CREATE TABLE settings (key TEXT PRIMARY KEY, value_json TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 1, updated_at_utc TEXT NOT NULL);
            CREATE TABLE search_history (id INTEGER PRIMARY KEY AUTOINCREMENT, query TEXT NOT NULL, normalized_query TEXT NOT NULL UNIQUE, last_searched_at_utc TEXT NOT NULL);
            CREATE INDEX ix_search_history_last_searched_at ON search_history(last_searched_at_utc DESC, id DESC);

            INSERT INTO categories(id, parent_id, name, normalized_name, sort_order, version, created_at_utc, updated_at_utc)
            VALUES
                ($categoryId, NULL, '迁移分类', '迁移分类', 1, 1, $now, $now),
                ($childCategoryId, $categoryId, '迁移子分类', '迁移子分类', 1, 2, $now, $now);
            INSERT INTO phrases(id, title, content, category_id, favorite, shortcut_mode, shortcut_display, shortcut_normalized, usage_count, last_used_at_utc, version, created_at_utc, updated_at_utc, color_key, sort_order)
            VALUES
                ($firstPhraseId, '收藏话术', '收藏值应被删除，话术应保留。', $childCategoryId, 1, 'Custom', 'Ctrl+Alt+K', 'CTRL+ALT+K', 7, $now, 3, $now, $now, 'blue', 1),
                ($secondPhraseId, '普通话术', '普通话术正文。', $categoryId, 0, 'None', NULL, NULL, 2, NULL, 1, $now, $now, 'default', 2);
            INSERT INTO settings(key, value_json, version, updated_at_utc)
            VALUES ('app.settings', '{"schemaVersion":2,"shortcuts":{"flashLauncher":{"modifiers":2,"keyCode":1}},"launchOnStartup":false,"startMinimized":false,"stayInTrayOnClose":true,"autoSend":false,"clipboardCompatibilityMode":true}', 1, $now);
            INSERT INTO search_history(query, normalized_query, last_searched_at_utc)
            VALUES ('迁移搜索', '迁移搜索', $now);
            PRAGMA user_version = 1;
            """;
        command.Parameters.AddWithValue("$categoryId", categoryId.ToString("D"));
        command.Parameters.AddWithValue("$childCategoryId", childCategoryId.ToString("D"));
        command.Parameters.AddWithValue("$firstPhraseId", firstPhraseId.ToString("D"));
        command.Parameters.AddWithValue("$secondPhraseId", secondPhraseId.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteDatabaseSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ColumnExistsAsync(string databasePath, string table, string column)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$column;";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> IndexExistsAsync(string databasePath, string index)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$name;";
        command.Parameters.AddWithValue("$name", index);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<long> ScalarLongAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("QuickPhrase-FavoriteRemoval-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
