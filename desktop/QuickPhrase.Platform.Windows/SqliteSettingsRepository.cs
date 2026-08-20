using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 设置以一个受控 JSON 聚合保存。JSON schemaVersion 只描述设置文档结构，
/// SQLite settings.version 继续单独承担乐观并发，迁移重写不得递增该行版本。
/// </summary>
internal sealed class SqliteSettingsRepository : SqliteRepositoryBase, ISettingsRepository
{
    private const int CurrentSettingsSchemaVersion = 2;
    private const string SettingsKey = "app.settings";
    private static readonly ShortcutChord DefaultLauncherShortcut = new(ShortcutModifiers.Alt, ShortcutKey.Space);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public SqliteSettingsRepository(SqliteConnectionFactory connections, SqliteWriteQueue writes, TimeProvider clock) : base(connections, writes, clock) { }

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

        if (row is null) return Defaults();

        var materialized = Materialize(row.Json, row.Version);
        if (!materialized.ShouldRewrite) return materialized.Settings;

        var rewrite = await RewriteSchemaVersion2Async(row, materialized.Settings, cancellationToken);
        if (rewrite == MigrationRewriteResult.Success) return materialized.Settings;
        if (rewrite == MigrationRewriteResult.ConcurrentChange && allowConcurrentRetry)
            return await LoadCoreAsync(allowConcurrentRetry: false, cancellationToken);

        WriteMigrationDiagnostic(
            rewrite == MigrationRewriteResult.ConcurrentChange
                ? "SETTINGS_SHORTCUT_MIGRATION_CONCURRENT_CHANGE"
                : "SETTINGS_SHORTCUT_MIGRATION_WRITE_FAILED");
        return materialized.Settings with { LauncherShortcut = DefaultLauncherShortcut };
    }

    public Task<RepositoryResult<AppSettings>> SaveAsync(AppSettings settings, long expectedVersion, CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => SaveCoreAsync(connection, settings, expectedVersion, ct), cancellationToken);

    private async Task<RepositoryResult<AppSettings>> SaveCoreAsync(SqliteConnection connection, AppSettings settings, long expectedVersion, CancellationToken cancellationToken)
    {
        // 当前适配目录没有经过验证的自动发送能力，后端始终保持关闭，避免旧客户端或篡改请求绕过 UI 限制。
        settings = settings with { AutoSend = false };
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
            if (currentValue is not long currentVersion) return RepositoryResult<AppSettings>.Failure(NotFound("设置"));
            if (currentVersion != expectedVersion) return RepositoryResult<AppSettings>.Failure(Conflict());

            var next = settings with { Version = expectedVersion + 1 };
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE settings SET value_json=$json, version=version+1, updated_at_utc=$updated WHERE key=$key AND version=$version;";
            update.Parameters.AddWithValue("$json", Serialize(next));
            update.Parameters.AddWithValue("$updated", Now().ToString("O"));
            update.Parameters.AddWithValue("$key", SettingsKey);
            update.Parameters.AddWithValue("$version", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return RepositoryResult<AppSettings>.Failure(Conflict());
            await transaction.CommitAsync(cancellationToken);
            return RepositoryResult<AppSettings>.Success(next, new CommittedDataChange("settings", Guid.Empty, "update", Now()));
        }
        catch (SqliteException ex)
        {
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            }
            return RepositoryResult<AppSettings>.Failure(MapSqliteError(ex));
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private StoredMaterialization Materialize(string json, long version)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var schemaVersion = document.RootElement.TryGetProperty("schemaVersion", out var schemaElement)
                ? schemaElement.GetInt32()
                : 1;

            return schemaVersion switch
            {
                1 => MaterializeSchemaVersion1(json, version),
                CurrentSettingsSchemaVersion => MaterializeSchemaVersion2(json, version),
                _ => MaterializeUnsupportedSchema(json, version),
            };
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            WriteMigrationDiagnostic("SETTINGS_SHORTCUT_MIGRATION_INVALID_JSON", exception);
            return new StoredMaterialization(Defaults(version), ShouldRewrite: true);
        }
    }

    private StoredMaterialization MaterializeSchemaVersion1(string json, long version)
    {
        var legacy = JsonSerializer.Deserialize<StoredSettingsV1>(json, JsonOptions) ?? new StoredSettingsV1();
        var migrated = TryMigrateLegacyShortcut(
            legacy.LauncherShortcutDisplay,
            legacy.LauncherShortcutNormalized,
            out var shortcut);
        if (!migrated)
        {
            shortcut = DefaultLauncherShortcut;
            WriteMigrationDiagnostic("SETTINGS_SHORTCUT_MIGRATION_INVALID_LEGACY_SHORTCUT");
        }

        return new StoredMaterialization(legacy.ToAppSettings(version, shortcut), ShouldRewrite: true);
    }

    private StoredMaterialization MaterializeSchemaVersion2(string json, long version)
    {
        var stored = JsonSerializer.Deserialize<StoredSettingsV2>(json, JsonOptions) ?? new StoredSettingsV2();
        var shortcut = stored.Shortcuts?.FlashLauncher?.ToChord() ?? default;
        if (ShortcutChordValidator.Validate(shortcut).IsValid)
            return new StoredMaterialization(stored.ToAppSettings(version, shortcut), ShouldRewrite: false);

        WriteMigrationDiagnostic("SETTINGS_SHORTCUT_SCHEMA_V2_INVALID_SHORTCUT");
        return new StoredMaterialization(stored.ToAppSettings(version, DefaultLauncherShortcut), ShouldRewrite: true);
    }

    private StoredMaterialization MaterializeUnsupportedSchema(string json, long version)
    {
        WriteMigrationDiagnostic("SETTINGS_SHORTCUT_SCHEMA_VERSION_UNSUPPORTED");
        var stored = JsonSerializer.Deserialize<StoredSettingsV2>(json, JsonOptions) ?? new StoredSettingsV2();
        return new StoredMaterialization(stored.ToAppSettings(version, DefaultLauncherShortcut), ShouldRewrite: false);
    }

    private async Task<MigrationRewriteResult> RewriteSchemaVersion2Async(
        StoredRow original,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Writes.EnqueueAsync(async (connection, ct) =>
            {
                await using var update = connection.CreateCommand();
                // schema 迁移只改 JSON，不递增 settings.version；原 JSON + 行版本条件防止覆盖并发保存。
                update.CommandText = """
                    UPDATE settings
                    SET value_json=$newJson, updated_at_utc=$updated
                    WHERE key=$key AND version=$version AND value_json=$oldJson;
                    """;
                update.Parameters.AddWithValue("$newJson", Serialize(settings));
                update.Parameters.AddWithValue("$updated", Now().ToString("O"));
                update.Parameters.AddWithValue("$key", SettingsKey);
                update.Parameters.AddWithValue("$version", original.Version);
                update.Parameters.AddWithValue("$oldJson", original.Json);
                return await update.ExecuteNonQueryAsync(ct) == 1
                    ? MigrationRewriteResult.Success
                    : MigrationRewriteResult.ConcurrentChange;
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            WriteMigrationDiagnostic("SETTINGS_SHORTCUT_MIGRATION_WRITE_EXCEPTION", exception);
            return MigrationRewriteResult.Failed;
        }
    }

    private static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(StoredSettingsV2.From(settings), JsonOptions);

    private static bool TryMigrateLegacyShortcut(string? display, string? normalized, out ShortcutChord chord)
    {
        var normalizedValid = TryParseLegacyShortcut(normalized, out var normalizedChord);
        var displayValid = TryParseLegacyShortcut(display, out var displayChord);
        if (normalizedValid && displayValid && normalizedChord != displayChord)
        {
            chord = default;
            return false;
        }

        if (normalizedValid)
        {
            chord = normalizedChord;
            return true;
        }
        if (displayValid)
        {
            chord = displayChord;
            return true;
        }

        chord = default;
        return false;
    }

    private static bool TryParseLegacyShortcut(string? value, out ShortcutChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var modifiers = ShortcutModifiers.None;
        ShortcutKey? key = null;
        foreach (var rawToken in value.Split(['+', '-', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = rawToken.Trim();
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                case "ctl":
                    modifiers |= ShortcutModifiers.Ctrl;
                    continue;
                case "alt":
                case "option":
                    modifiers |= ShortcutModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= ShortcutModifiers.Shift;
                    continue;
                case "win":
                case "windows":
                case "meta":
                    modifiers |= ShortcutModifiers.Win;
                    continue;
            }

            if (key is not null || !TryParseLegacyKey(token, out var parsedKey)) return false;
            key = parsedKey;
        }

        if (key is null) return false;
        chord = new ShortcutChord(modifiers, key.Value);
        return ShortcutChordValidator.Validate(chord).IsValid;
    }

    private static bool TryParseLegacyKey(string token, out ShortcutKey key)
    {
        if (token.Equals("space", StringComparison.OrdinalIgnoreCase))
        {
            key = ShortcutKey.Space;
            return true;
        }

        if (token.Length == 1)
        {
            var character = char.ToUpperInvariant(token[0]);
            if (character is >= 'A' and <= 'Z')
            {
                key = ShortcutKey.A + (character - 'A');
                return true;
            }
            if (character is >= '0' and <= '9')
            {
                key = ShortcutKey.Digit0 + (character - '0');
                return true;
            }
        }

        if (token.Length is 2 or 3 && token[0] is 'F' or 'f' &&
            int.TryParse(token.AsSpan(1), out var functionNumber) && functionNumber is >= 1 and <= 12)
        {
            key = ShortcutKey.F1 + (functionNumber - 1);
            return true;
        }

        key = default;
        return false;
    }

    private static AppSettings Defaults(long version = 1) => new(
        version,
        false,
        false,
        true,
        DefaultLauncherShortcut,
        false,
        true,
        false)
    {
        LauncherEnabledAdapters = DefaultLauncherAdapters(),
    };

    private static Dictionary<string, bool> DefaultLauncherAdapters() =>
        new(StringComparer.OrdinalIgnoreCase) { ["WXWork"] = true };

    private static void WriteMigrationDiagnostic(string code, Exception? exception = null)
    {
        var traceId = Guid.NewGuid();
        var exceptionPart = exception is null ? string.Empty : $"，异常类型={exception.GetType().Name}";
        Console.Error.WriteLine($"设置快捷键迁移：TraceId={traceId:N}，阶段=SettingsJsonMigration，结果码={code}{exceptionPart}。");
    }

    private sealed record StoredRow(string Json, long Version);
    private sealed record StoredMaterialization(AppSettings Settings, bool ShouldRewrite);

    private enum MigrationRewriteResult
    {
        Success,
        ConcurrentChange,
        Failed,
    }

    private sealed record StoredShortcut(int Modifiers = (int)ShortcutModifiers.Alt, int KeyCode = (int)ShortcutKey.Space)
    {
        public static StoredShortcut From(ShortcutChord chord) => new((int)chord.Modifiers, (int)chord.Key);
        public ShortcutChord ToChord() => new((ShortcutModifiers)Modifiers, (ShortcutKey)KeyCode);
    }

    private sealed record StoredShortcuts(StoredShortcut? FlashLauncher = null)
    {
        public static StoredShortcuts From(AppSettings settings) => new(StoredShortcut.From(settings.LauncherShortcut));
    }

    private sealed record StoredSettingsV2(
        int SchemaVersion = CurrentSettingsSchemaVersion,
        StoredShortcuts? Shortcuts = null,
        bool LaunchOnStartup = false,
        bool StartMinimized = false,
        bool StayInTrayOnClose = true,
        bool AutoSend = false,
        bool ClipboardCompatibilityMode = true,
        bool HasCompletedOnboarding = false,
        int OnboardingVersion = 0,
        Dictionary<string, bool>? LauncherEnabledAdapters = null)
    {
        public static StoredSettingsV2 From(AppSettings settings) => new(
            CurrentSettingsSchemaVersion,
            StoredShortcuts.From(settings),
            settings.LaunchOnStartup,
            settings.StartMinimized,
            settings.StayInTrayOnClose,
            settings.AutoSend,
            settings.ClipboardCompatibilityMode,
            settings.HasCompletedOnboarding,
            settings.OnboardingVersion,
            new Dictionary<string, bool>(settings.LauncherEnabledAdapters, StringComparer.OrdinalIgnoreCase));

        public AppSettings ToAppSettings(long version, ShortcutChord shortcut) => new(
            version,
            LaunchOnStartup,
            StartMinimized,
            StayInTrayOnClose,
            shortcut,
            AutoSend,
            ClipboardCompatibilityMode,
            HasCompletedOnboarding,
            OnboardingVersion)
        {
            LauncherEnabledAdapters = LauncherEnabledAdapters is null
                ? DefaultLauncherAdapters()
                : new Dictionary<string, bool>(LauncherEnabledAdapters, StringComparer.OrdinalIgnoreCase),
        };
    }

    private sealed record StoredSettingsV1(
        bool LaunchOnStartup = false,
        bool StartMinimized = false,
        bool StayInTrayOnClose = true,
        string LauncherShortcutDisplay = "Alt + Space",
        string LauncherShortcutNormalized = "Alt+Space",
        bool AutoSend = false,
        bool ClipboardCompatibilityMode = true,
        bool HasCompletedOnboarding = false,
        int OnboardingVersion = 0,
        Dictionary<string, bool>? LauncherEnabledAdapters = null)
    {
        public AppSettings ToAppSettings(long version, ShortcutChord shortcut) => new(
            version,
            LaunchOnStartup,
            StartMinimized,
            StayInTrayOnClose,
            shortcut,
            AutoSend,
            ClipboardCompatibilityMode,
            HasCompletedOnboarding,
            OnboardingVersion)
        {
            LauncherEnabledAdapters = LauncherEnabledAdapters is null
                ? DefaultLauncherAdapters()
                : new Dictionary<string, bool>(LauncherEnabledAdapters, StringComparer.OrdinalIgnoreCase),
        };
    }
}
