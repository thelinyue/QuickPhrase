using Microsoft.Data.Sqlite;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

public sealed class EnterpriseSyncMigrationTests
{
    [Fact]
    public async Task FreshDatabaseCreatesEnterpriseSyncSchemaVersion3()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        await using var connection = new SqliteConnection($"Data Source={runtime.DatabasePath};Pooling=False");
        await connection.OpenAsync();

        Assert.Equal(3L, Convert.ToInt64(await ScalarAsync(connection, "PRAGMA user_version;")));
        foreach (var table in new[] { "sync_accounts", "enterprise_categories_cache", "enterprise_phrases_cache", "enterprise_sync_state" })
            Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name;", ("$name", table))));
        foreach (var forbidden in new[] { "sync_outbox", "personal_sync_state", "personal_changes" })
            Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name;", ("$name", forbidden))));
    }


    [Fact]
    public async Task Version2DatabaseMigratesToVersion3AndPreservesPersonalData()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        Guid categoryId;
        Guid phraseId;
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options))
        {
            var category = await runtime.Categories.CreateAsync(new QuickPhrase.Core.CreateCategoryCommand(Guid.NewGuid(), "迁移分类", null, 7));
            Assert.True(category.IsSuccess);
            categoryId = category.Value!.Id;
            var phrase = await runtime.Phrases.CreateAsync(new QuickPhrase.Core.CreatePhraseCommand(Guid.NewGuid(), "迁移话术", "迁移正文", categoryId, QuickPhrase.Core.ShortcutMode.Custom, "Ctrl+7", "orange", 9));
            Assert.True(phrase.IsSuccess);
            phraseId = phrase.Value!.Id;
        }
        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE enterprise_phrases_cache; DROP TABLE enterprise_categories_cache; DROP TABLE sync_accounts; DROP TABLE enterprise_sync_state; PRAGMA user_version=2;";
            await command.ExecuteNonQueryAsync();
        }
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options))
        {
            var category = Assert.Single(await runtime.Categories.ListAsync(), item => item.Id == categoryId);
            var phrase = Assert.Single(await runtime.Phrases.ListAsync(), item => item.Id == phraseId);
            Assert.Equal(7, category.SortOrder);
            Assert.Equal("迁移话术", phrase.Title);
            Assert.Equal("迁移正文", phrase.Content);
            Assert.Equal("orange", phrase.ColorKey);
            Assert.Equal(1, phrase.SortOrder);
            Assert.Equal(QuickPhrase.Core.ShortcutMode.Custom, phrase.ShortcutMode);
            Assert.Equal("Ctrl+7", phrase.Shortcut?.Normalized);
        }
        await using var verify = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False");
        await verify.OpenAsync();
        Assert.Equal(3L, Convert.ToInt64(await ScalarAsync(verify, "PRAGMA user_version;")));
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
        public string Path { get; } = Directory.CreateTempSubdirectory("QuickPhrase-M3-Migration-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

