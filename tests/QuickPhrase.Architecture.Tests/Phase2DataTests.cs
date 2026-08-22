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
    public async Task FreshDatabaseStartsWithoutBuiltinCategoriesOrPhrases()
    {
        using var temp = new TemporaryDirectory();
        await using (var first = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path)))
        {
            Assert.Empty(await first.Categories.ListAsync());
            Assert.Empty(await first.Phrases.ListAsync());
            var settings = await first.Settings.LoadAsync();
            Assert.False(settings.QuickSendWithoutConfirmation);
            Assert.Equal(new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space), settings.LauncherShortcut);
        }

        await using var reopened = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        Assert.Empty(await reopened.Categories.ListAsync());
        Assert.Empty(await reopened.Phrases.ListAsync());
    }





    [Fact]
    public async Task PhraseCreateIsIdempotentAndUpdateUsesVersion()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "测试分类"))).Value!;
        var id = Guid.NewGuid();
        var command = new CreatePhraseCommand(id, "请求设备序列号", PhraseBody.FromText("请提供设备序列号（SN），方便我们进一步确认设备信息。"), category.Id, ShortcutMode.None, null);

        var created = await runtime.Phrases.CreateAsync(command);
        var repeated = await runtime.Phrases.CreateAsync(command);
        Assert.True(created.IsSuccess);
        Assert.True(repeated.IsSuccess);
        Assert.Equal(created.Value!.Id, repeated.Value!.Id);
        Assert.Single(await runtime.Phrases.ListAsync());

        var updated = await runtime.Phrases.UpdateAsync(new UpdatePhraseCommand(id, created.Value.Version, "请求设备 SN", command.Body, category.Id, ShortcutMode.None, null));
        Assert.True(updated.IsSuccess);
        var stale = await runtime.Phrases.UpdateAsync(new UpdatePhraseCommand(id, created.Value.Version, "过期修改", command.Body, category.Id, ShortcutMode.None, null));
        Assert.Equal("VERSION_CONFLICT", stale.Error?.Code);
    }

    [Fact]
    public async Task NonEmptyCategoryCascadeDeletesPhrasesAndPhraseDeleteIsIdempotent()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "测试分类"))).Value!;
        var phrase = (await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "测试话术", PhraseBody.FromText("测试正文"), category.Id, ShortcutMode.None, null))).Value!;

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
        var saved = await runtime.Settings.SaveAsync(settings with { QuickSendWithoutConfirmation = true }, settings.Version);
        Assert.True(saved.IsSuccess);

        var stale = await runtime.Settings.SaveAsync(settings with { QuickSendWithoutConfirmation = false }, settings.Version);
        Assert.Equal("VERSION_CONFLICT", stale.Error?.Code);
        Assert.True((await runtime.Settings.LoadAsync()).QuickSendWithoutConfirmation);
    }

    [Fact]
    public async Task ShortcutConflictIdentifiesExistingPhrase()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "测试分类"))).Value!;
        var first = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "高频一", PhraseBody.FromText("固定回复一"), category.Id, ShortcutMode.Quick, "Alt + 3"));
        var second = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "高频二", PhraseBody.FromText("固定回复二"), category.Id, ShortcutMode.Quick, "3 + Alt"));

        Assert.True(first.IsSuccess);
        Assert.Equal("SHORTCUT_CONFLICT", second.Error?.Code);
        Assert.Equal(first.Value!.Id, second.Error?.RelatedEntityId);
        Assert.Equal(first.Value.Title, second.Error?.RelatedTitle);
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
    public async Task EmptyCategoryCanBeDeletedAndUsageCommitIncrementsVersion()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "临时分类"));
        Assert.True(category.IsSuccess);
        var deletedCategory = await runtime.Categories.DeleteAsync(category.Value!.Id, category.Value.Version);
        Assert.True(deletedCategory.IsSuccess);
        Assert.True(deletedCategory.Value!.Deleted);

        var usedCategory = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "使用分类"))).Value!;
        var phrase = (await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "使用话术", PhraseBody.FromText("使用正文"), usedCategory.Id, ShortcutMode.None, null))).Value!;
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
    public async Task NewRootCategoriesAppendToEndAndPreserveExplicitSortOrder()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));

        var first = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "第一分类"))).Value!;
        var second = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "第二分类"))).Value!;
        var manuallyOrdered = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "手动排序", SortOrder: 100))).Value!;
        var appended = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "末尾分类"))).Value!;
        var child = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "二级分类", first.Id))).Value!;

        Assert.Equal(0, first.SortOrder);
        Assert.Equal(10, second.SortOrder);
        Assert.Equal(100, manuallyOrdered.SortOrder);
        Assert.Equal(110, appended.SortOrder);
        Assert.Equal(0, child.SortOrder);

        var roots = (await runtime.Categories.ListAsync()).Where(category => category.ParentId is null).ToArray();
        Assert.Equal([first.Id, second.Id, manuallyOrdered.Id, appended.Id], roots.Select(category => category.Id));
    }
    [Fact]
    public async Task DefaultSettingsDoNotPersistLauncherAdapterGate()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));

        var settings = await runtime.Settings.LoadAsync();

        Assert.Null(typeof(AppSettings).GetProperty("LauncherEnabledAdapters"));
        Assert.Equal(new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space), settings.LauncherShortcut);
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
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "并发分类"))).Value!;
        var tasks = Enumerable.Range(0, 24).Select(index => runtime.Phrases.CreateAsync(
            new CreatePhraseCommand(Guid.NewGuid(), $"并发话术 {index}", PhraseBody.FromText($"并发正文 {index}"), category.Id, ShortcutMode.None, null))).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(24, (await runtime.Phrases.ListAsync()).Count);
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
        var rootPhraseResult = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "级联根话术", PhraseBody.FromText("根分类正文"), root.Id, ShortcutMode.None, null));
        Assert.True(rootPhraseResult.IsSuccess);
        var rootPhrase = rootPhraseResult.Value!;
        var childPhraseResult = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "级联子话术", PhraseBody.FromText("子分类正文"), child.Id, ShortcutMode.None, null));
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
        var keepResult = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "保留话术", PhraseBody.FromText("回滚正文一"), category.Id, ShortcutMode.None, null));
        Assert.True(keepResult.IsSuccess);
        var keep = keepResult.Value!;
        var failResult = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "触发回滚", PhraseBody.FromText("回滚正文二"), category.Id, ShortcutMode.None, null));
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
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "取消分类"))).Value!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Phrases.CreateAsync(
            new CreatePhraseCommand(Guid.NewGuid(), "取消写入", PhraseBody.FromText("正文"), category.Id, ShortcutMode.None, null), cancellation.Token));
        Assert.DoesNotContain((await runtime.Phrases.ListAsync()), phrase => phrase.Title == "取消写入");
    }

    [Fact]
    public async Task LockedDatabaseMapsToDatabaseBusyAfterBusyTimeout()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "锁定分类"))).Value!;
        await using var locker = new SqliteConnection($"Data Source={runtime.DatabasePath};Mode=ReadWrite;Pooling=False");
        await locker.OpenAsync();
        await using (var begin = locker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }
        try
        {
            var result = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(Guid.NewGuid(), "锁定测试", PhraseBody.FromText("正文"), category.Id, ShortcutMode.None, null));
            Assert.Equal("DATABASE_BUSY", result.Error?.Code);
        }
        finally
        {
            await using var rollback = locker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CurrentVersionDatabaseMissingSegmentConstraintsIsBackedUpAndRebuilt()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options)) { }

        await ExecuteSqlAsync(options.DatabasePath, """
            PRAGMA foreign_keys=OFF;
            DROP INDEX ux_phrase_segments_phrase_sort;
            DROP INDEX ix_phrase_segments_media_asset;
            DROP TABLE phrase_segments;
            CREATE TABLE phrase_segments (
                segment_id TEXT PRIMARY KEY,
                phrase_id TEXT NOT NULL,
                segment_kind TEXT NOT NULL,
                text_content TEXT NULL,
                media_asset_id TEXT NULL,
                sort_order INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX ux_phrase_segments_phrase_sort ON phrase_segments(phrase_id, sort_order);
            CREATE INDEX ix_phrase_segments_media_asset ON phrase_segments(media_asset_id) WHERE media_asset_id IS NOT NULL;
            """);

        await using (var rebuilt = await QuickPhraseDataRuntime.OpenAsync(options))
        {
            await using var connection = new SqliteConnection($"Data Source={rebuilt.DatabasePath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_list('phrase_segments');"));
            var sql = Convert.ToString(await ScalarAsync(connection, "SELECT sql FROM sqlite_master WHERE type='table' AND name='phrase_segments';"));
            Assert.Contains("CHECK (segment_kind IN ('Text', 'Image'))", sql, StringComparison.Ordinal);
        }

        Assert.True(Directory.Exists(options.DevelopmentBackupDirectory));
        Assert.Contains(
            Directory.EnumerateFiles(options.DevelopmentBackupDirectory, "quickphrase.db", SearchOption.AllDirectories),
            file => new FileInfo(file).Length > 0);
    }
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
