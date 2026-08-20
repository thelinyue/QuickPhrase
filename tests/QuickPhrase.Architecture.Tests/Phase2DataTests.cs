using Microsoft.Data.Sqlite;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

public sealed class Phase2DataTests
{
    [Fact]
    public void ShortcutNormalizerCanonicalizesModifierOrderAndFullWidthKeys()
    {
        var result = new ShortcutNormalizer().Normalize(" alt + ctrl + ２ ", ShortcutMode.Custom);

        Assert.True(result.IsValid);
        Assert.Equal("Ctrl + Alt + 2", result.Value!.Display);
        Assert.Equal("Ctrl+Alt+2", result.Value.Normalized);
    }

    [Fact]
    public void ShortcutNormalizerRejectsInvalidQuickSlot()
    {
        var result = new ShortcutNormalizer().Normalize("Ctrl + 1", ShortcutMode.Quick);

        Assert.False(result.IsValid);
        Assert.Equal("VALIDATION_FAILED", result.ErrorCode);
    }

    [Fact]
    public async Task FreshDatabaseMigratesAndSeedsOnlyOnce()
    {
        using var temp = new TemporaryDirectory();
        await using (var first = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path)))
        {
            // 7 个内置分类 + 迁移 004 追加的真实一级分类「常用」
            Assert.Equal(8, (await first.Categories.ListAsync()).Count);
            Assert.Equal(18, (await first.Phrases.ListAsync()).Count);
            var settings = await first.Settings.LoadAsync();
            Assert.False(settings.AutoSend);
            Assert.Equal(new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space), settings.LauncherShortcut);
        }

        await using var reopened = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        Assert.Equal(18, (await reopened.Phrases.ListAsync()).Count);
    }

    [Fact]
    public async Task RuntimeBackupCreatesReadableSnapshotForUpgrade()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));

        var backupPath = await runtime.CreateBackupAsync("upgrade-test");

        Assert.True(File.Exists(backupPath));
        await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await backup.OpenAsync();
        await using var command = backup.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM phrases;";
        Assert.Equal(18L, await command.ExecuteScalarAsync());
        await backup.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task BackupOnlyCanRunWithoutOpeningMigrationRuntime()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options)) { }

        var backupPath = await QuickPhraseDataRuntime.CreateBackupOnlyAsync(options, "upgrade");

        Assert.True(File.Exists(backupPath));
    }

    [Fact]
    public async Task PhraseCreateIsIdempotentAndUpdateUsesVersion()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.ListAsync()).Single(x => x.Name == "信息收集");
        var id = Guid.NewGuid();
        var command = new CreatePhraseCommand(id, "请求设备序列号", "请提供设备序列号（SN），方便我们进一步确认设备信息。", category.Id, false, ShortcutMode.None, null);

        var created = await runtime.Phrases.CreateAsync(command);
        var repeated = await runtime.Phrases.CreateAsync(command);
        Assert.True(created.IsSuccess);
        Assert.True(repeated.IsSuccess);
        Assert.Equal(created.Value!.Id, repeated.Value!.Id);
        Assert.Equal(19, (await runtime.Phrases.ListAsync()).Count);

        var updated = await runtime.Phrases.UpdateAsync(new UpdatePhraseCommand(id, created.Value.Version, "请求设备 SN", command.Content, category.Id, false, ShortcutMode.None, null));
        Assert.True(updated.IsSuccess);
        var stale = await runtime.Phrases.UpdateAsync(new UpdatePhraseCommand(id, created.Value.Version, "过期修改", command.Content, category.Id, false, ShortcutMode.None, null));
        Assert.Equal("VERSION_CONFLICT", stale.Error?.Code);
    }

    [Fact]
    public async Task NonEmptyCategoryCascadeDeletesPhrasesAndPhraseDeleteIsIdempotent()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.ListAsync()).Single(x => x.Name == "设备问题");
        var phrase = (await runtime.Phrases.ListAsync()).First(x => x.CategoryId == category.Id);

        var categoryDelete = await runtime.Categories.DeleteAsync(category.Id, category.Version);
        Assert.True(categoryDelete.IsSuccess);
        Assert.True(categoryDelete.Value!.Deleted);
        Assert.Contains(phrase.Id, categoryDelete.Value.DeletedPhraseIds!);
        Assert.DoesNotContain((await runtime.Categories.ListAsync()), item => item.Id == category.Id);
        Assert.DoesNotContain(await runtime.Phrases.ListAsync(), item => item.Id == phrase.Id);

        var repeated = await runtime.Phrases.DeleteAsync(phrase.Id, null);
        Assert.True(repeated.IsSuccess);
        Assert.False(repeated.Value!.Deleted);
    }

    [Fact]
    public async Task SettingsSaveUsesVersionAndPersistsDefaults()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var settings = await runtime.Settings.LoadAsync();
        var saved = await runtime.Settings.SaveAsync(settings with { AutoSend = true }, settings.Version);
        Assert.True(saved.IsSuccess);

        var stale = await runtime.Settings.SaveAsync(settings with { AutoSend = false }, settings.Version);
        Assert.Equal("VERSION_CONFLICT", stale.Error?.Code);
        Assert.False((await runtime.Settings.LoadAsync()).AutoSend);
    }

    [Fact]
    public async Task ShortcutConflictIdentifiesExistingPhrase()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.ListAsync()).Single(x => x.Name == "信息收集");
        var first = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "高频一", "固定回复一", category.Id, false, ShortcutMode.Quick, "Alt + 3"));
        var second = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "高频二", "固定回复二", category.Id, false, ShortcutMode.Quick, "3 + Alt"));

        Assert.True(first.IsSuccess);
        Assert.Equal("SHORTCUT_CONFLICT", second.Error?.Code);
        Assert.Equal(first.Value!.Id, second.Error?.RelatedEntityId);
        Assert.Equal(first.Value.Title, second.Error?.RelatedTitle);
    }

    [Fact]
    public async Task MigrationRunnerAppliesVersionNineAndRemovesTagTables()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        await using var connection = new SqliteConnection($"Data Source={runtime.DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();

        Assert.Equal(10L, await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name IN ('tags', 'phrase_tags');"));
    }

    [Fact]
    public async Task LegacyRemovedTagsChecksumDoesNotBlockOpening()
    {
        using var temp = new TemporaryDirectory();
        string databasePath;
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path)))
        {
            databasePath = runtime.DatabasePath;
        }

        // 模拟旧版本已删除标签表的数据库：应允许修复 008_remove_tags 历史校验值并继续启动。
        await ExecuteSqlAsync(databasePath, "DROP TABLE IF EXISTS phrase_tags; DROP TABLE IF EXISTS tags; UPDATE schema_migrations SET checksum = 'legacy-008-remove-tags' WHERE version = 8 AND name = '008_remove_tags';");
        await using var reopened = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));

        await using var connection = new SqliteConnection($"Data Source={reopened.DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        Assert.Equal(10L, await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(1) FROM schema_migrations WHERE version = 8 AND checksum = 'legacy-008-remove-tags';"));
    }
    [Fact]
    public async Task LegacyNoOpVersionEightAppliesTagRemovalAsNextMigration()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        Directory.CreateDirectory(options.DataDirectory);
        var migrationNames = new[]
        {
            "001_initial",
            "002_category_hierarchy",
            "003_phrase_color_key",
            "004_common_category",
            "005_phrase_sort_order",
            "006_fixed_phrase_colors_and_disable_phrase_shortcuts",
            "007_search_history",
        };

        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath};Mode=ReadWriteCreate;Pooling=False"))
        {
            await connection.OpenAsync();
            var assembly = typeof(QuickPhraseDataRuntime).Assembly;
            for (var index = 0; index < migrationNames.Length; index++)
            {
                var version = index + 1;
                var migrationName = migrationNames[index];
                var sql = ReadMigrationSql(assembly, migrationName);
                await ExecuteSqlAsync(connection, sql);
                await using var record = connection.CreateCommand();
                record.CommandText = "INSERT INTO schema_migrations(version, name, checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAt);";
                record.Parameters.AddWithValue("$version", version);
                record.Parameters.AddWithValue("$name", migrationName);
                record.Parameters.AddWithValue("$checksum", ComputeChecksum(sql));
                record.Parameters.AddWithValue("$appliedAt", "2026-08-19T00:00:00.0000000+00:00");
                await record.ExecuteNonQueryAsync();
            }

            const string legacyMigrationSql = "-- 008_remove_tags 保留为空操作。正式产品仍支持话术标签，避免破坏既有数据模型。\r\n";
            await using var legacyRecord = connection.CreateCommand();
            legacyRecord.CommandText = "INSERT INTO schema_migrations(version, name, checksum, applied_at_utc) VALUES (8, '008_remove_tags', $checksum, $appliedAt);";
            legacyRecord.Parameters.AddWithValue("$checksum", ComputeChecksum(legacyMigrationSql));
            legacyRecord.Parameters.AddWithValue("$appliedAt", "2026-08-19T00:00:00.0000000+00:00");
            await legacyRecord.ExecuteNonQueryAsync();
        }

        await using var upgraded = await QuickPhraseDataRuntime.OpenAsync(options);
        await using var verify = new SqliteConnection($"Data Source={upgraded.DatabasePath};Mode=ReadOnly;Pooling=False");
        await verify.OpenAsync();
        Assert.Equal(10L, await ScalarAsync(verify, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name IN ('tags', 'phrase_tags');"));
    }

    [Fact]
    public async Task DatabaseUsesFrozenPragmasAndPartialShortcutIndex()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var factory = new SqliteConnectionFactory(runtime.DatabasePath);
        await using var connection = await factory.OpenReadAsync(CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(5000L, await ScalarAsync(connection, "PRAGMA busy_timeout;"));
        var journalMode = await ScalarAsync(connection, "PRAGMA journal_mode;");
        Assert.Equal("wal", (journalMode?.ToString() ?? string.Empty).ToLowerInvariant());
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(1) FROM sqlite_master WHERE type='index' AND name='ux_phrases_shortcut_normalized';"));
    }

    [Fact]
    public async Task MigrationChecksumMismatchStopsOpeningWithoutRewritingDatabase()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        Directory.CreateDirectory(options.DataDirectory);
        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath};Mode=ReadWriteCreate;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, name TEXT NOT NULL, checksum TEXT NOT NULL, applied_at_utc TEXT NOT NULL); INSERT INTO schema_migrations VALUES (1, '001_initial', 'bad-checksum', '2026-01-01T00:00:00Z');";
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<DataStoreException>(() => QuickPhraseDataRuntime.OpenAsync(options));
        Assert.Equal("MIGRATION_FAILED", exception.Code);
        Assert.True(File.Exists(options.DatabasePath));
    }

    [Fact]
    public async Task LegacyAdditiveMigrationChecksumIsRepairedWhenSchemaAlreadyExists()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options)) { }

        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath};Mode=ReadWrite;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_migrations SET checksum = 'legacy-checksum' WHERE version = 2;";
            await command.ExecuteNonQueryAsync();
        }

        await using var reopened = await QuickPhraseDataRuntime.OpenAsync(options);
        // 7 个内置分类 + 迁移 004 追加的「常用」
        Assert.Equal(8, (await reopened.Categories.ListAsync()).Count);
        await using var verify = new SqliteConnection($"Data Source={options.DatabasePath};Mode=ReadOnly;Pooling=False");
        await verify.OpenAsync();
        var expectedChecksum = ComputeChecksum(ReadMigrationSql(typeof(QuickPhraseDataRuntime).Assembly, "002_category_hierarchy"));
        Assert.Equal(expectedChecksum,
            await ScalarAsync(verify, "SELECT checksum FROM schema_migrations WHERE version = 2;"));
    }

    [Fact]
    public async Task FixedColorMigrationPreservesLegacyDataAndClearsPhraseShortcuts()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        Directory.CreateDirectory(options.DataDirectory);
        var phraseId = "20000000-0000-4000-8000-000000000001";
        var secondPhraseId = "20000000-0000-4000-8000-000000000002";
        string originalContent;
        string originalCategoryId;
        string originalCreatedAt;
        string originalUpdatedAt;
        long originalSortOrder;

        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath};Mode=ReadWriteCreate;Pooling=False"))
        {
            await connection.OpenAsync();
            var assembly = typeof(QuickPhraseDataRuntime).Assembly;
            foreach (var version in new[] { 1, 2, 3, 4, 5 })
            {
                var migrationName = version switch
                {
                    1 => "001_initial",
                    2 => "002_category_hierarchy",
                    3 => "003_phrase_color_key",
                    4 => "004_common_category",
                    5 => "005_phrase_sort_order",
                    _ => throw new InvalidOperationException(),
                };
                var sql = ReadMigrationSql(assembly, migrationName);
                await ExecuteSqlAsync(connection, sql);
                await using var record = connection.CreateCommand();
                record.CommandText = "INSERT INTO schema_migrations(version, name, checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAt);";
                record.Parameters.AddWithValue("$version", version);
                record.Parameters.AddWithValue("$name", migrationName);
                record.Parameters.AddWithValue("$checksum", ComputeChecksum(sql));
                record.Parameters.AddWithValue("$appliedAt", "2026-08-19T00:00:00.0000000+00:00");
                await record.ExecuteNonQueryAsync();
            }

            await ExecuteSqlAsync(connection, $"UPDATE phrases SET color_key='red', shortcut_mode='Custom', shortcut_display='Ctrl + 1', shortcut_normalized='Ctrl+1' WHERE id='{phraseId}'; UPDATE phrases SET color_key='yellow', shortcut_mode='Quick', shortcut_display='Alt + 2', shortcut_normalized='Alt+2' WHERE id='{secondPhraseId}';");
            originalContent = (string)(await ScalarAsync(connection, $"SELECT content FROM phrases WHERE id='{phraseId}';"))!;
            originalCategoryId = (string)(await ScalarAsync(connection, $"SELECT category_id FROM phrases WHERE id='{phraseId}';"))!;
            originalCreatedAt = (string)(await ScalarAsync(connection, $"SELECT created_at_utc FROM phrases WHERE id='{phraseId}';"))!;
            originalUpdatedAt = (string)(await ScalarAsync(connection, $"SELECT updated_at_utc FROM phrases WHERE id='{phraseId}';"))!;
            originalSortOrder = (long)(await ScalarAsync(connection, $"SELECT sort_order FROM phrases WHERE id='{phraseId}';"))!;
        }

        await using (var upgraded = await QuickPhraseDataRuntime.OpenAsync(options))
        {
            var migrated = (await upgraded.Phrases.GetAsync(Guid.Parse(phraseId)))!;
            var migratedSecond = (await upgraded.Phrases.GetAsync(Guid.Parse(secondPhraseId)))!;
            Assert.Equal(originalContent, migrated.Content);
            Assert.Equal(originalCategoryId, migrated.CategoryId.ToString("D"));
            Assert.Equal(DateTimeOffset.Parse(originalCreatedAt), migrated.CreatedAtUtc);
            Assert.Equal(DateTimeOffset.Parse(originalUpdatedAt), migrated.UpdatedAtUtc);
            Assert.Equal(originalSortOrder, migrated.SortOrder);
            Assert.Equal("pink", migrated.ColorKey);
            Assert.Equal("tan", migratedSecond.ColorKey);
            Assert.Equal(ShortcutMode.None, migrated.ShortcutMode);
            Assert.Null(migrated.Shortcut);
            Assert.Equal(ShortcutMode.None, migratedSecond.ShortcutMode);
            Assert.Null(migratedSecond.Shortcut);

            await using var verify = new SqliteConnection($"Data Source={options.DatabasePath};Mode=ReadOnly;Pooling=False");
            await verify.OpenAsync();
            Assert.Equal(10L, (long)(await ScalarAsync(verify, "SELECT MAX(version) FROM schema_migrations;"))!);
            Assert.Equal(0L, (long)(await ScalarAsync(verify, "SELECT COUNT(1) FROM phrases WHERE shortcut_mode <> 'None' OR shortcut_display IS NOT NULL OR shortcut_normalized IS NOT NULL;"))!);
        }

        Assert.NotEmpty(Directory.GetFiles(options.BackupDirectory, "*.db"));
        await using var reopened = await QuickPhraseDataRuntime.OpenAsync(options);
        Assert.Equal("pink", (await reopened.Phrases.GetAsync(Guid.Parse(phraseId)))!.ColorKey);
        Assert.Equal("tan", (await reopened.Phrases.GetAsync(Guid.Parse(secondPhraseId)))!.ColorKey);
    }
    [Fact]
    public async Task EmptyCategoryCanBeDeletedAndUsageCommitIncrementsVersion()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "临时分类"));
        Assert.True(category.IsSuccess);
        var deletedCategory = await runtime.Categories.DeleteAsync(category.Value!.Id, category.Value.Version);
        Assert.True(deletedCategory.IsSuccess);
        Assert.True(deletedCategory.Value!.Deleted);

        var phrase = (await runtime.Phrases.ListAsync()).First();
        var used = await runtime.Phrases.IncrementUsageAsync(phrase.Id, DateTimeOffset.UtcNow);
        Assert.True(used.IsSuccess);
        Assert.Equal(phrase.UsageCount + 1, used.Value!.UsageCount);
        Assert.Equal(phrase.Version + 1, used.Value.Version);
    }

    [Fact]
    public async Task CategoryHierarchySupportsTwoLevelsAndRejectsThirdLevel()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));

        var root = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "售后层级"));
        var second = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "退款", root.Value!.Id));
        var third = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "已发货", second.Value!.Id));

        Assert.True(root.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal("CATEGORY_DEPTH_EXCEEDED", third.Error?.Code);
    }

    [Fact]
    public async Task LegacySettingsDefaultLauncherEnabledAdaptersToWeCom()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));

        var settings = await runtime.Settings.LoadAsync();

        Assert.True(settings.LauncherEnabledAdapters.TryGetValue("WXWork", out var enabled));
        Assert.True(enabled);
    }

    [Fact]
    public async Task CategoryMoveRejectsCyclesAndKeepsChildrenTogether()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var root = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "移动根"))).Value!;
        var child = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "移动子", root.Id))).Value!;

        // 在二级结构内尝试把 root 移动到其子分类 child 之下（形成环），应被拒。
        var cycle = await runtime.Categories.MoveAsync(new MoveCategoryCommand(root.Id, root.Version, child.Id, 0));

        Assert.Equal("CATEGORY_CYCLE", cycle.Error?.Code);
        Assert.Equal(root.Id, (await runtime.Categories.ListAsync()).Single(x => x.Id == child.Id).ParentId);
    }

    [Fact]
    public async Task ConcurrentCreatesAreSerializedByTheSingleWriter()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.ListAsync()).Single(x => x.Name == "信息收集");
        var tasks = Enumerable.Range(0, 24).Select(index => runtime.Phrases.CreateAsync(
            new CreatePhraseCommand(Guid.NewGuid(), $"并发话术 {index}", $"并发正文 {index}", category.Id, false, ShortcutMode.None, null))).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(42, (await runtime.Phrases.ListAsync()).Count);
    }

    [Fact]
    public async Task PendingMigrationCreatesBackupAndFailedMigrationRollsBack()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options)) { }
        var factory = new SqliteConnectionFactory(options.DatabasePath);
        // 内置迁移包含分类同级唯一约束的 010，测试用追加迁移顺延为 011/012。
        var validV2 = new SqliteMigration(11, "011_test", "CREATE TABLE phase2_test(value TEXT);", "test-checksum");
        await new MigrationRunner(options, factory, [validV2]).EnsureMigratedAsync(CancellationToken.None);
        var backup = Assert.Single(Directory.GetFiles(options.BackupDirectory, "*.db"));
        await using (var backupConnection = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False"))
        {
            await backupConnection.OpenAsync();
            Assert.Equal(10L, await ScalarAsync(backupConnection, "SELECT COUNT(1) FROM schema_migrations;"));
        }

        var failing = new SqliteMigration(12, "012_failure", "CREATE TABLE should_rollback(value TEXT); SELECT no_such_function();", "failure-checksum");
        await Assert.ThrowsAsync<DataStoreException>(() => new MigrationRunner(options, factory, [validV2, failing]).EnsureMigratedAsync(CancellationToken.None));
        await using var connection = await factory.OpenReadAsync(CancellationToken.None);
        Assert.Equal(0L, (long)(await ScalarAsync(connection, "SELECT COUNT(1) FROM sqlite_master WHERE name='should_rollback';"))!);
    }

    [Fact]
    public async Task CategoryDeleteCascadesThroughSubcategoriesAndPhrases()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var rootResult = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "级联根分类"));
        Assert.True(rootResult.IsSuccess);
        var root = rootResult.Value!;
        var childResult = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "级联子分类", root.Id));
        Assert.True(childResult.IsSuccess);
        var child = childResult.Value!;
        var rootPhraseResult = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "级联根话术", "根分类正文", root.Id, false, ShortcutMode.None, null));
        Assert.True(rootPhraseResult.IsSuccess);
        var rootPhrase = rootPhraseResult.Value!;
        var childPhraseResult = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "级联子话术", "子分类正文", child.Id, false, ShortcutMode.None, null));
        Assert.True(childPhraseResult.IsSuccess);
        var childPhrase = childPhraseResult.Value!;

        var deleted = await runtime.Categories.DeleteAsync(root.Id, root.Version);

        Assert.True(deleted.IsSuccess);
        Assert.True(deleted.Value?.Deleted);
        Assert.Equal(new[] { rootPhrase.Id, childPhrase.Id }.OrderBy(id => id), deleted.Value!.DeletedPhraseIds!.OrderBy(id => id));
        Assert.DoesNotContain((await runtime.Categories.ListAsync()), category => category.Id == root.Id || category.Id == child.Id);
        Assert.DoesNotContain(await runtime.Phrases.ListAsync(), phrase => phrase.Id == rootPhrase.Id || phrase.Id == childPhrase.Id);
        Assert.Empty(runtime.Search.Search(new SearchRequest("级联根话术")).Items);
        Assert.Empty(runtime.Search.Search(new SearchRequest("级联子话术")).Items);

    }

    [Fact]
    public async Task CategoryDeleteRollsBackAllRowsWhenPhraseDeleteFails()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var categoryResult = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "回滚分类"));
        Assert.True(categoryResult.IsSuccess);
        var category = categoryResult.Value!;
        var keepResult = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "保留话术", "回滚正文一", category.Id, false, ShortcutMode.None, null));
        Assert.True(keepResult.IsSuccess);
        var keep = keepResult.Value!;
        var failResult = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "触发回滚", "回滚正文二", category.Id, false, ShortcutMode.None, null));
        Assert.True(failResult.IsSuccess);
        var fail = failResult.Value!;
        await ExecuteSqlAsync(runtime.DatabasePath, "CREATE TRIGGER fail_category_delete BEFORE DELETE ON phrases WHEN OLD.title = '触发回滚' BEGIN SELECT RAISE(ABORT, '测试删除失败'); END;");

        var deleted = await runtime.Categories.DeleteAsync(category.Id, category.Version);

        Assert.False(deleted.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", deleted.Error?.Code);
        Assert.Contains((await runtime.Categories.ListAsync()), item => item.Id == category.Id);
        Assert.Contains(await runtime.Phrases.ListAsync(), item => item.Id == keep.Id);
        Assert.Contains(await runtime.Phrases.ListAsync(), item => item.Id == fail.Id);
    }
    [Fact]
    public async Task CancelledWriteDoesNotReachTheDatabase()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.ListAsync()).Single(x => x.Name == "信息收集");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Phrases.CreateAsync(
            new CreatePhraseCommand(Guid.NewGuid(), "取消写入", "正文", category.Id, false, ShortcutMode.None, null), cancellation.Token));
        Assert.DoesNotContain((await runtime.Phrases.ListAsync()), phrase => phrase.Title == "取消写入");
    }

    [Fact]
    public async Task LockedDatabaseMapsToDatabaseBusyAfterBusyTimeout()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.ListAsync()).Single(x => x.Name == "信息收集");
        await using var locker = new SqliteConnection($"Data Source={runtime.DatabasePath};Mode=ReadWrite;Pooling=False");
        await locker.OpenAsync();
        await using (var begin = locker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }
        try
        {
            var result = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "锁定测试", "正文", category.Id, false, ShortcutMode.None, null));
            Assert.Equal("DATABASE_BUSY", result.Error?.Code);
        }
        finally
        {
            await using var rollback = locker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }
    }

    private static string ReadMigrationSql(System.Reflection.Assembly assembly, string migrationName)
    {
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith($".Migrations.{migrationName}.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ComputeChecksum(string sql) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();

    private static async Task ExecuteSqlAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, params (string Name, string Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("QuickPhrase-Phase2-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}




