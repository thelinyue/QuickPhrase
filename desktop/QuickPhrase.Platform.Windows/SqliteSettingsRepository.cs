using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 未发布阶段的设置仓储。只接受当前 V1 设置文档；旧格式或损坏 JSON 不做迁移，
/// 而是直接重置为当前默认设置，避免历史字段继续进入正式产品数据模型。
/// </summary>
internal sealed class SqliteSettingsRepository : SqliteRepositoryBase, ISettingsRepository
{
    private const int CurrentSettingsSchemaVersion = 1;
    private const string SettingsKey = "app.settings";
    private static readonly ShortcutChord DefaultLauncherShortcut = new(ShortcutModifiers.Alt, ShortcutKey.Space);
    private static readonly string[] RequiredProperties =
    [
        "schemaVersion",
        "shortcuts",
        "launchOnStartup",
        "startMinimized",
        "stayInTrayOnClose",
        "quickSendWithoutConfirmation",
        "clipboardCompatibilityMode",
        "hasCompletedOnboarding",
        "onboardingVersion",
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public SqliteSettingsRepository(SqliteConnectionFactory connections, SqliteWriteQueue writes, TimeProvider clock)
        : base(connections, writes, clock)
    {
    }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        LoadCoreAsync(allowConcurrentRetry: true, cancellationToken);

    private async Task<AppSettings> LoadCoreAsync(bool allowConcurrentRetry, CancellationToken cancellationToken)
    {
        StoredRow? row;
        await using (var connection = await Connections.OpenReadAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value_json, version FROM settings WHERE key=$key;";
            command.Parameters.AddWithValue("$key", SettingsKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            row = await reader.ReadAsync(cancellationToken)
                ? new StoredRow(reader.GetString(0), reader.GetInt64(1))
                : null;
        }

        if (row is null)
            return Defaults();
        if (TryMaterializeCurrent(row.Json, row.Version, out var settings))
            return settings;

        return await ResetInvalidDocumentAsync(row, allowConcurrentRetry, cancellationToken);
    }

    public Task<RepositoryResult<AppSettings>> SaveAsync(
        AppSettings settings,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => SaveCoreAsync(connection, settings, expectedVersion, ct), cancellationToken);

    private async Task<RepositoryResult<AppSettings>> SaveCoreAsync(
        SqliteConnection connection,
        AppSettings settings,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var shortcutValidation = ShortcutChordValidator.Validate(settings.LauncherShortcut);
        if (!shortcutValidation.IsValid)
            return RepositoryResult<AppSettings>.Failure(Validation("Launcher 快捷键无效，请使用至少一个修饰键和一个受支持按键。"));

        SqliteTransaction? transaction = null;
        try
        {
            transaction = connection.BeginTransaction();
            await using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT version FROM settings WHERE key=$key;";
            read.Parameters.AddWithValue("$key", SettingsKey);
            var currentValue = await read.ExecuteScalarAsync(cancellationToken);
            if (currentValue is not long currentVersion)
                return RepositoryResult<AppSettings>.Failure(NotFound("设置"));
            if (currentVersion != expectedVersion)
                return RepositoryResult<AppSettings>.Failure(Conflict());

            var next = settings with { Version = expectedVersion + 1 };
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE settings SET value_json=$json, version=version+1, updated_at_utc=$updated WHERE key=$key AND version=$version;";
            update.Parameters.AddWithValue("$json", Serialize(next));
            update.Parameters.AddWithValue("$updated", Now().ToString("O"));
            update.Parameters.AddWithValue("$key", SettingsKey);
            update.Parameters.AddWithValue("$version", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                return RepositoryResult<AppSettings>.Failure(Conflict());

            await transaction.CommitAsync(cancellationToken);
            return RepositoryResult<AppSettings>.Success(next, new CommittedDataChange("settings", Guid.Empty, "update", Now()));
        }
        catch (SqliteException exception)
        {
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            }

            return RepositoryResult<AppSettings>.Failure(MapSqliteError(exception));
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private async Task<AppSettings> ResetInvalidDocumentAsync(
        StoredRow original,
        bool allowConcurrentRetry,
        CancellationToken cancellationToken)
    {
        var defaults = Defaults(original.Version);
        var result = await Writes.EnqueueAsync(async (connection, ct) =>
        {
            await using var update = connection.CreateCommand();
            // 重置不是迁移：保留 SQLite 行版本，只替换不受支持的设置文档，避免静默覆盖并发保存。
            update.CommandText = """
                UPDATE settings
                SET value_json=$json, updated_at_utc=$updated
                WHERE key=$key AND version=$version AND value_json=$oldJson;
                """;
            update.Parameters.AddWithValue("$json", Serialize(defaults));
            update.Parameters.AddWithValue("$updated", Now().ToString("O"));
            update.Parameters.AddWithValue("$key", SettingsKey);
            update.Parameters.AddWithValue("$version", original.Version);
            update.Parameters.AddWithValue("$oldJson", original.Json);
            return await update.ExecuteNonQueryAsync(ct) == 1
                ? SettingsResetResult.Success
                : SettingsResetResult.ConcurrentChange;
        }, cancellationToken);

        if (result == SettingsResetResult.Success)
        {
            var traceId = Guid.NewGuid();
            Console.Error.WriteLine(
                $"未发布阶段设置已重置。阶段：SETTINGS_RESET；结果码：SETTINGS_V1_RESET；TraceId：{traceId}");
            return defaults;
        }

        if (allowConcurrentRetry)
            return await LoadCoreAsync(allowConcurrentRetry: false, cancellationToken);

        throw new DataStoreException(
            "SETTINGS_RESET_FAILED",
            "本地设置不符合当前 V1 结构，自动重置失败。请重试。" );
    }

    private static bool TryMaterializeCurrent(string json, long version, out AppSettings settings)
    {
        settings = default!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schema)
                || !schema.TryGetInt32(out var schemaVersion)
                || schemaVersion != CurrentSettingsSchemaVersion
                || !HasExactProperties(root, RequiredProperties)
                || !root.TryGetProperty("shortcuts", out var shortcuts)
                || shortcuts.ValueKind != JsonValueKind.Object
                || !HasExactProperties(shortcuts, ["flashLauncher"])
                || !shortcuts.TryGetProperty("flashLauncher", out var flashLauncher)
                || flashLauncher.ValueKind != JsonValueKind.Object
                || !HasExactProperties(flashLauncher, ["modifiers", "keyCode"]))
            {
                return false;
            }

            var stored = JsonSerializer.Deserialize<StoredSettings>(json, JsonOptions);
            if (stored is null)
                return false;

            var shortcut = stored.Shortcuts?.FlashLauncher?.ToChord() ?? default;
            if (!ShortcutChordValidator.Validate(shortcut).IsValid)
                return false;

            settings = stored.ToAppSettings(version, shortcut);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasExactProperties(JsonElement element, IReadOnlyCollection<string> expectedProperties)
    {
        var actualProperties = element.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        return actualProperties.Count == expectedProperties.Count
            && expectedProperties.All(actualProperties.Contains);
    }
    private static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(StoredSettings.From(settings), JsonOptions);

    private static AppSettings Defaults(long version = 1) => new(
        version,
        false,
        false,
        true,
        DefaultLauncherShortcut,
        false,
        true,
        false,
        0);

    private sealed record StoredRow(string Json, long Version);

    private enum SettingsResetResult
    {
        Success,
        ConcurrentChange,
    }

    private sealed record StoredShortcut(
        int Modifiers = (int)ShortcutModifiers.Alt,
        int KeyCode = (int)ShortcutKey.Space)
    {
        public static StoredShortcut From(ShortcutChord chord) => new((int)chord.Modifiers, (int)chord.Key);
        public ShortcutChord ToChord() => new((ShortcutModifiers)Modifiers, (ShortcutKey)KeyCode);
    }

    private sealed record StoredShortcuts(StoredShortcut? FlashLauncher = null)
    {
        public static StoredShortcuts From(AppSettings settings) => new(StoredShortcut.From(settings.LauncherShortcut));
    }

    private sealed record StoredSettings(
        int SchemaVersion = CurrentSettingsSchemaVersion,
        StoredShortcuts? Shortcuts = null,
        bool LaunchOnStartup = false,
        bool StartMinimized = false,
        bool StayInTrayOnClose = true,
        bool QuickSendWithoutConfirmation = false,
        bool ClipboardCompatibilityMode = true,
        bool HasCompletedOnboarding = false,
        int OnboardingVersion = 0)
    {
        public static StoredSettings From(AppSettings settings) => new(
            CurrentSettingsSchemaVersion,
            StoredShortcuts.From(settings),
            settings.LaunchOnStartup,
            settings.StartMinimized,
            settings.StayInTrayOnClose,
            settings.QuickSendWithoutConfirmation,
            settings.ClipboardCompatibilityMode,
            settings.HasCompletedOnboarding,
            settings.OnboardingVersion);

        public AppSettings ToAppSettings(long version, ShortcutChord shortcut) => new(
            version,
            LaunchOnStartup,
            StartMinimized,
            StayInTrayOnClose,
            shortcut,
            QuickSendWithoutConfirmation,
            ClipboardCompatibilityMode,
            HasCompletedOnboarding,
            OnboardingVersion);
    }
}
