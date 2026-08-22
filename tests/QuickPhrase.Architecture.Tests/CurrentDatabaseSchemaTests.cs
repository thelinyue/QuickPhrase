using Microsoft.Data.Sqlite;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

/// <summary>
/// 锁定未发布阶段唯一有效的数据库定义：当前结构固定为 V1，
/// 任何版本不符或结构残缺的本地开发库都必须清空并重新创建。
/// </summary>
public sealed class CurrentDatabaseSchemaTests
{
    [Fact]
    public void CorePhraseContractsDoNotExposeFavorite()
    {
        Assert.DoesNotContain(typeof(Phrase).GetProperties(), property => property.Name == "Favorite");
        Assert.DoesNotContain(typeof(CreatePhraseCommand).GetProperties(), property => property.Name == "Favorite");
        Assert.DoesNotContain(typeof(UpdatePhraseCommand).GetProperties(), property => property.Name == "Favorite");
    }

    [Fact]
    public void FormalProductSourceContainsNoFavoriteBusinessReferences()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var formalProjects = new[]
        {
            Path.Combine(repositoryRoot, "desktop", "QuickPhrase.Core"),
            Path.Combine(repositoryRoot, "desktop", "QuickPhrase.Platform.Windows"),
            Path.Combine(repositoryRoot, "desktop", "QuickPhrase.Desktop"),
        };

        var unexpectedReferences = formalProjects
            .SelectMany(project => Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories))
            .Where(path => Path.GetExtension(path) is ".cs" or ".xaml" or ".sql")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var content = File.ReadAllText(path);
                return content.Contains("Favorite", StringComparison.OrdinalIgnoreCase)
                    || content.Contains("收藏", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.True(
            unexpectedReferences.Length == 0,
            $"正式产品源码仍包含收藏业务引用：{string.Join(", ", unexpectedReferences)}");
    }

    [Fact]
    public async Task FreshDatabaseUsesCompleteV1Schema()
    {
        using var temp = new TemporaryDirectory();
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path)))
        {
            Assert.Empty(await runtime.Phrases.ListAsync());
        }

        var databasePath = new QuickPhraseDataOptions(temp.Path).DatabasePath;
        Assert.Equal(1L, await ScalarLongAsync(databasePath, "PRAGMA user_version;"));
        foreach (var table in CurrentTables)
            Assert.True(await TableExistsAsync(databasePath, table), $"缺少当前 V1 表：{table}");
        foreach (var index in CurrentIndexes)
            Assert.True(await IndexExistsAsync(databasePath, index), $"缺少当前 V1 索引：{index}");
        Assert.False(await ColumnExistsAsync(databasePath, "phrases", "favorite"));
        Assert.False(await ColumnExistsAsync(databasePath, "phrases", "batch_separator"));
    }

    [Fact]
    public async Task CurrentV1Schema_AllowsEmptyTitleOnCreate()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var categoryId = Guid.NewGuid();
        var category = await runtime.Categories.CreateAsync(new CreateCategoryCommand(categoryId, "空标题分类"));
        Assert.True(category.IsSuccess, category.Error?.Message);

        var result = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(
            Guid.NewGuid(), string.Empty, PhraseBody.FromText("正文"), categoryId, ShortcutMode.None, null));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(string.Empty, result.Value!.Title);
        Assert.Equal(string.Empty, (await runtime.Phrases.GetAsync(result.Value.Id))!.Title);
    }

    [Fact]
    public async Task CurrentV1Schema_NormalizesWhitespaceTitleToEmptyOnUpdate()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var categoryId = Guid.NewGuid();
        var category = await runtime.Categories.CreateAsync(new CreateCategoryCommand(categoryId, "编辑空标题分类"));
        Assert.True(category.IsSuccess, category.Error?.Message);
        var created = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(
            Guid.NewGuid(), "原标题", PhraseBody.FromText("正文"), categoryId, ShortcutMode.None, null));
        Assert.True(created.IsSuccess, created.Error?.Message);

        var result = await runtime.Phrases.UpdateAsync(new UpdatePhraseCommand(
            created.Value!.Id, created.Value.Version, "   ", created.Value.Body, categoryId, ShortcutMode.None, null));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(string.Empty, result.Value!.Title);
        Assert.Equal(string.Empty, (await runtime.Phrases.GetAsync(result.Value.Id))!.Title);
    }

    [Fact]
    public async Task CurrentV1Schema_StillRejectsTitleLongerThanEightyCharacters()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var categoryId = Guid.NewGuid();
        var category = await runtime.Categories.CreateAsync(new CreateCategoryCommand(categoryId, "标题长度分类"));
        Assert.True(category.IsSuccess, category.Error?.Message);

        var result = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(
            Guid.NewGuid(), new string('字', PhraseRules.MaxTitleLength + 1), PhraseBody.FromText("正文"), categoryId, ShortcutMode.None, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
    }

    [Fact]
    public async Task NonV1DatabaseIsClearedAndRebuilt()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options))
        {
            var category = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "待清空分类"));
            Assert.True(category.IsSuccess, category.Error?.Message);
        }

        await ExecuteSqlAsync(options.DatabasePath, "PRAGMA user_version = 99;");

        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options))
        {
            Assert.Empty(await runtime.Categories.ListAsync());
            Assert.Empty(await runtime.Phrases.ListAsync());
        }

        Assert.Equal(1L, await ScalarLongAsync(options.DatabasePath, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task V1DatabaseWithMissingCurrentTableIsClearedAndRebuilt()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options))
        {
            var category = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "损坏结构分类"));
            Assert.True(category.IsSuccess, category.Error?.Message);
        }

        await ExecuteSqlAsync(options.DatabasePath, "DROP TABLE phrases;");

        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options))
            Assert.Empty(await runtime.Categories.ListAsync());

        Assert.Equal(1L, await ScalarLongAsync(options.DatabasePath, "PRAGMA user_version;"));
        Assert.True(await TableExistsAsync(options.DatabasePath, "phrases"));
    }

    [Fact]
    public async Task V1DatabaseWithMissingCurrentIndexIsClearedAndRebuilt()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options))
        {
            var category = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "缺索引分类"));
            Assert.True(category.IsSuccess, category.Error?.Message);
        }

        await ExecuteSqlAsync(options.DatabasePath, "DROP INDEX ix_phrases_category_sort;");

        await using (var runtime = await QuickPhraseDataRuntime.OpenAsync(options))
            Assert.Empty(await runtime.Categories.ListAsync());

        Assert.Equal(1L, await ScalarLongAsync(options.DatabasePath, "PRAGMA user_version;"));
        Assert.True(await IndexExistsAsync(options.DatabasePath, "ix_phrases_category_sort"));
    }

    private static readonly string[] CurrentTables =
    [
        "categories", "phrases", "settings", "search_history",
        "sync_accounts", "enterprise_categories_cache", "enterprise_phrases_cache", "enterprise_sync_state",
    ];

    private static readonly string[] CurrentIndexes =
    [
        "ux_categories_root_normalized_name", "ux_categories_child_parent_normalized_name", "ix_categories_parent_id",
        "ux_phrases_shortcut_normalized", "ix_phrases_category_id", "ix_phrases_last_used_at", "ix_phrases_category_sort",
        "ix_search_history_last_searched_at", "ix_enterprise_categories_generation_sort", "ix_enterprise_phrases_generation_category_sort",
    ];

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(string databasePath, string table) =>
        await ObjectExistsAsync(databasePath, "table", table);

    private static async Task<bool> IndexExistsAsync(string databasePath, string index) =>
        await ObjectExistsAsync(databasePath, "index", index);

    private static async Task<bool> ObjectExistsAsync(string databasePath, string type, string name)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type=$type AND name=$name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
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

    private static async Task<long> ScalarLongAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("QuickPhrase-CurrentSchema-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
