using System.Collections.Immutable;
using System.Text;
using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// SQLite 仓储共享辅助逻辑：集中处理颜色键、快捷键和图文话术读模型映射。
/// 读取正文时始终按 phrase_segments.sort_order 构造不可变数组，数据库顺序就是唯一发送顺序。
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
        BodiesEqual(existing.Body, command.Body) &&
        existing.CategoryId == command.CategoryId &&
        existing.ShortcutMode == command.ShortcutMode &&
        existing.ColorKey == NormalizeColorKey(command.ColorKey) &&
        existing.Shortcut?.Normalized == shortcut?.Normalized;

    private static bool BodiesEqual(PhraseBody left, PhraseBody right) =>
        string.Equals(left.BatchSeparator, right.BatchSeparator, StringComparison.Ordinal) &&
        left.Segments.SequenceEqual(right.Segments);
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
        PhraseHeader? header;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id,title,batch_separator,category_id,shortcut_mode,shortcut_display,shortcut_normalized,usage_count,last_used_at_utc,version,created_at_utc,updated_at_utc,color_key,sort_order FROM phrases WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            header = ReadHeader(reader);
        }

        var segments = await ReadSegmentsAsync(connection, id, transaction, cancellationToken);
        return ToPhrase(header, segments);
    }

    protected static async Task<IReadOnlyList<Phrase>> ReadPhrasesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var headers = new List<PhraseHeader>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,title,batch_separator,category_id,shortcut_mode,shortcut_display,shortcut_normalized,usage_count,last_used_at_utc,version,created_at_utc,updated_at_utc,color_key,sort_order FROM phrases ORDER BY sort_order,updated_at_utc DESC,title;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) headers.Add(ReadHeader(reader));
        }

        var result = new List<Phrase>(headers.Count);
        foreach (var header in headers)
        {
            var segments = await ReadSegmentsAsync(connection, DbId(header.Id), null, cancellationToken);
            result.Add(ToPhrase(header, segments));
        }
        return result;
    }

    private static async Task<ImmutableArray<PhraseSegment>> ReadSegmentsAsync(SqliteConnection connection, string phraseId, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var segments = ImmutableArray.CreateBuilder<PhraseSegment>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ps.segment_id,ps.segment_kind,ps.text_content,ma.asset_id,ma.mime_type,ma.byte_length,ma.pixel_width,ma.pixel_height FROM phrase_segments ps LEFT JOIN media_assets ma ON ma.asset_id=ps.media_asset_id WHERE ps.phrase_id=$phraseId ORDER BY ps.sort_order;";
        command.Parameters.AddWithValue("$phraseId", phraseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = Enum.Parse<PhraseSegmentKind>(reader.GetString(1), true);
            PhraseImageReference? image = null;
            if (kind == PhraseSegmentKind.Image)
            {
                image = new PhraseImageReference(ReadId(reader, 3), reader.GetString(4), reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7));
            }
            segments.Add(new PhraseSegment(ReadId(reader, 0), kind, reader.IsDBNull(2) ? null : reader.GetString(2), image));
        }
        return segments.ToImmutable();
    }

    private static PhraseHeader ReadHeader(SqliteDataReader reader) => new(
        ReadId(reader, 0), reader.GetString(1), reader.GetString(2), ReadId(reader, 3),
        Enum.Parse<ShortcutMode>(reader.GetString(4), true),
        reader.IsDBNull(5) ? null : new ShortcutValue(reader.GetString(5), reader.GetString(6)),
        reader.GetInt32(7), ReadNullableTime(reader, 8), reader.GetInt64(9), ReadTime(reader, 10), ReadTime(reader, 11),
        reader.IsDBNull(12) ? "default" : reader.GetString(12), reader.GetInt32(13));

    private static Phrase ToPhrase(PhraseHeader header, ImmutableArray<PhraseSegment> segments) => new(
        header.Id, header.Title, new PhraseBody(segments, header.BatchSeparator), header.CategoryId, header.ShortcutMode,
        header.Shortcut, header.UsageCount, header.LastUsedAtUtc, header.Version, header.CreatedAtUtc, header.UpdatedAtUtc,
        header.ColorKey, header.SortOrder);

    private sealed record PhraseHeader(Guid Id, string Title, string BatchSeparator, Guid CategoryId, ShortcutMode ShortcutMode,
        ShortcutValue? Shortcut, int UsageCount, DateTimeOffset? LastUsedAtUtc, long Version, DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc, string ColorKey, int SortOrder);
}
