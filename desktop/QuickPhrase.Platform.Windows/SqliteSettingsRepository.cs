using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>设置以一个受控 JSON 聚合保存，SQLite 行版本负责乐观并发，后续可无损扩展键值。</summary>
internal sealed class SqliteSettingsRepository : SqliteRepositoryBase, ISettingsRepository
{
    private const string SettingsKey = "app.settings";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SqliteSettingsRepository(SqliteConnectionFactory connections, SqliteWriteQueue writes, TimeProvider clock) : base(connections, writes, clock) { }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await Connections.OpenReadAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json, version FROM settings WHERE key=$key;";
        command.Parameters.AddWithValue("$key", SettingsKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return Defaults();
        var stored = JsonSerializer.Deserialize<StoredSettings>(reader.GetString(0), JsonOptions) ?? new StoredSettings();
        return stored.ToAppSettings(reader.GetInt64(1));
    }

    public Task<RepositoryResult<AppSettings>> SaveAsync(AppSettings settings, long expectedVersion, CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => SaveCoreAsync(connection, settings, expectedVersion, ct), cancellationToken);

    private async Task<RepositoryResult<AppSettings>> SaveCoreAsync(SqliteConnection connection, AppSettings settings, long expectedVersion, CancellationToken cancellationToken)
    {
        // 当前适配目录没有经过验证的自动发送能力，后端始终保持关闭，避免旧客户端或篡改请求绕过 UI 限制。
        settings = settings with { AutoSend = false };
        var shortcut = Shortcuts.Normalize(settings.LauncherShortcutDisplay, ShortcutMode.Custom);
        if (!shortcut.IsValid || shortcut.Value!.Normalized != settings.LauncherShortcutNormalized)
            return RepositoryResult<AppSettings>.Failure(Validation("Launcher 快捷键格式或规范化值无效。"));
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
            update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(StoredSettings.From(next), JsonOptions));
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

    private static AppSettings Defaults() => new(1, false, false, true, "Alt + Space", "Alt+Space", false, true, false)
    {
        LauncherEnabledAdapters = DefaultLauncherAdapters(),
    };

    private static Dictionary<string, bool> DefaultLauncherAdapters() => new(StringComparer.OrdinalIgnoreCase) { ["WXWork"] = true };

    private sealed record StoredSettings(
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
        public static StoredSettings From(AppSettings settings) => new(settings.LaunchOnStartup, settings.StartMinimized, settings.StayInTrayOnClose, settings.LauncherShortcutDisplay, settings.LauncherShortcutNormalized, settings.AutoSend, settings.ClipboardCompatibilityMode, settings.HasCompletedOnboarding, settings.OnboardingVersion, new Dictionary<string, bool>(settings.LauncherEnabledAdapters, StringComparer.OrdinalIgnoreCase));

        public AppSettings ToAppSettings(long version)
        {
            // 旧版本 JSON 没有 OnboardingVersion：已处理过的用户按版本 1 兼容，未处理用户仍为版本 0。
            var onboardingVersion = HasCompletedOnboarding && OnboardingVersion == 0 ? 1 : OnboardingVersion;
            return new AppSettings(version, LaunchOnStartup, StartMinimized, StayInTrayOnClose,
                LauncherShortcutDisplay, LauncherShortcutNormalized, AutoSend, ClipboardCompatibilityMode,
                HasCompletedOnboarding, onboardingVersion)
            {
                LauncherEnabledAdapters = LauncherEnabledAdapters is null
                    ? DefaultLauncherAdapters()
                    : new Dictionary<string, bool>(LauncherEnabledAdapters, StringComparer.OrdinalIgnoreCase),
            };
        }
    }
}
