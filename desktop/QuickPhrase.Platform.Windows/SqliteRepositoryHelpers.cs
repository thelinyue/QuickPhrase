using System.Text;
using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// SQLite 仓储共享辅助逻辑：集中处理话术校验、颜色键规范化和读模型映射。
/// </summary>
internal abstract class SqliteRepositoryBase
{
    private static readonly HashSet<string> ValidColorKeys = ["default", "orange", "blue", "magenta", "purple", "green", "pink", "teal", "tan", "gray"];
    protected readonly SqliteConnectionFactory Connections;
    protected readonly SqliteWriteQueue Writes;
    protected readonly TimeProvider Clock;
    protected readonly ShortcutNormalizer Shortcuts = new();

    protected SqliteRepositoryBase(SqliteConnectionFactory connections, SqliteWriteQueue writes, TimeProvider clock)
    {
        Connections = connections;
        Writes = writes;
        Clock = clock;
    }

    protected DateTimeOffset Now() => Clock.GetUtcNow();
    protected static string DbId(Guid id) => id.ToString("D");
    protected static Guid ReadId(SqliteDataReader reader, int ordinal) => Guid.Parse(reader.GetString(ordinal));
    protected static DateTimeOffset ReadTime(SqliteDataReader reader, int ordinal) => DateTimeOffset.Parse(reader.GetString(ordinal));
    protected static DateTimeOffset? ReadNullableTime(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

    protected static (string Display, string Normalized) NormalizeName(string value)
    {
        var display = string.Join(' ', value.Normalize(NormalizationForm.FormKC).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (display, display.ToUpperInvariant());
    }

    protected static DataError MapSqliteError(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6
            ? new DataError("DATABASE_BUSY", "SQLite 当前正忙，请稍后重试。")
            : new DataError("VALIDATION_FAILED", "数据写入未通过 SQLite 约束，请检查输入后重试。", null, null);

    protected static DataError Validation(string message) => new("VALIDATION_FAILED", message);
    protected static DataError NotFound(string entity) => new("NOT_FOUND", $"找不到要操作的{entity}。", null, null);
    protected static DataError Conflict(Guid? id = null, string? title = null) => new("VERSION_CONFLICT", "数据已被其他操作更新，请刷新后重试。", id, title);

    protected static bool SamePhrase(Phrase existing, CreatePhraseCommand command, ShortcutValue? shortcut) =>
        existing.Title == command.Title.Trim() &&
        existing.Content == command.Content &&
        existing.CategoryId == command.CategoryId &&
        existing.ShortcutMode == command.ShortcutMode &&
        existing.ColorKey == NormalizeColorKey(command.ColorKey) &&
        existing.Shortcut?.Normalized == shortcut?.Normalized;

    protected static bool ValidatePhraseText(string title, string content, out DataError? error)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 80)
        {
            error = Validation("话术标题不能为空且不能超过 80 个字。");
            return false;
        }
        if (string.IsNullOrEmpty(content) || content.Length > 4000)
        {
            error = Validation("话术正文不能为空且不能超过 4000 个字。");
            return false;
        }
        error = null;
        return true;
    }

    protected static bool ValidateColorKey(string? colorKey, out string normalized, out DataError? error)
    {
        normalized = NormalizeColorKey(colorKey);
        if (ValidColorKeys.Contains(normalized))
        {
            error = null;
            return true;
        }
        error = Validation($"不支持的话术颜色键“{colorKey}”，可选值包括 default、orange、blue、magenta、purple、green、pink、teal、tan、gray。");
        return false;
    }

    /// <summary>把颜色键规范化为当前固定色板使用的小写存储值。</summary>
    protected static string NormalizeColorKey(string? colorKey) =>
        string.IsNullOrWhiteSpace(colorKey) ? "default" : colorKey.Trim().ToLowerInvariant();

    protected static bool PrepareShortcut(ShortcutNormalizer normalizer, ShortcutMode mode, string? input, out ShortcutValue? value, out DataError? error)
    {
        var result = normalizer.Normalize(input, mode);
        value = mode == ShortcutMode.None ? null : result.Value;
        error = result.IsValid ? null : Validation(result.ErrorMessage ?? "快捷键格式无效。");
        return result.IsValid;
    }

    protected static async Task<Phrase?> ReadPhraseAsync(SqliteConnection connection, string id, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        Phrase? phrase;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, title, content, category_id, shortcut_mode, shortcut_display, shortcut_normalized, usage_count, last_used_at_utc, version, created_at_utc, updated_at_utc, color_key, sort_order FROM phrases WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            phrase = ReadPhrase(reader);
        }
        return phrase;
    }

    protected static async Task<IReadOnlyList<Phrase>> ReadPhrasesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var result = new List<Phrase>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, title, content, category_id, shortcut_mode, shortcut_display, shortcut_normalized, usage_count, last_used_at_utc, version, created_at_utc, updated_at_utc, color_key, sort_order FROM phrases ORDER BY sort_order, updated_at_utc DESC, title;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(ReadPhrase(reader));
            }
        }
        return result;
    }

    private static Phrase ReadPhrase(SqliteDataReader reader) => new(
        ReadId(reader, 0),
        reader.GetString(1),
        reader.GetString(2),
        ReadId(reader, 3),
        Enum.Parse<ShortcutMode>(reader.GetString(4), true),
        reader.IsDBNull(5) ? null : new ShortcutValue(reader.GetString(5), reader.GetString(6)),
        reader.GetInt32(7),
        ReadNullableTime(reader, 8),
        reader.GetInt64(9),
        ReadTime(reader, 10),
        ReadTime(reader, 11),
        reader.IsDBNull(12) ? "default" : reader.GetString(12),
        reader.GetInt32(13));

}
