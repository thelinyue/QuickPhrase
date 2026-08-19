using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>璇濇湳 Repository锛氳鎿嶄綔鐭繛鎺ワ紝鍐欐搷浣滃叏閮ㄨ繘鍏ュ崟鍐欒€呴槦鍒楀苟鍦ㄤ簨鍔℃彁浜ゅ悗鍙戝竷缁撴灉銆?/summary>
internal sealed class SqlitePhraseRepository : SqliteRepositoryBase, IPhraseRepository
{
    public SqlitePhraseRepository(SqliteConnectionFactory connections, SqliteWriteQueue writes, TimeProvider clock) : base(connections, writes, clock) { }

    public async Task<IReadOnlyList<Phrase>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await Connections.OpenReadAsync(cancellationToken);
        return await ReadPhrasesAsync(connection, cancellationToken);
    }

    public async Task<Phrase?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await Connections.OpenReadAsync(cancellationToken);
        return await ReadPhraseAsync(connection, DbId(id), null, cancellationToken);
    }

    public Task<RepositoryResult<Phrase>> CreateAsync(CreatePhraseCommand command, CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => CreateCoreAsync(connection, command, ct), cancellationToken);

    public Task<RepositoryResult<Phrase>> UpdateAsync(UpdatePhraseCommand command, CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => UpdateCoreAsync(connection, command, ct), cancellationToken);

    public Task<RepositoryResult<DeleteResult>> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => DeleteCoreAsync(connection, id, expectedVersion, ct), cancellationToken);

    public Task<RepositoryResult<Phrase>> IncrementUsageAsync(Guid id, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => IncrementUsageCoreAsync(connection, id, usedAtUtc, ct), cancellationToken);

    private async Task<RepositoryResult<Phrase>> CreateCoreAsync(SqliteConnection connection, CreatePhraseCommand command, CancellationToken cancellationToken)
    {
        if (!ValidatePhraseText(command.Title, command.Content, out var validationError)) return RepositoryResult<Phrase>.Failure(validationError!);
        if (!ValidateColorKey(command.ColorKey, out var colorKey, out validationError)) return RepositoryResult<Phrase>.Failure(validationError!);
        if (!PrepareShortcut(Shortcuts, command.ShortcutMode, command.Shortcut, out var shortcut, out validationError)) return RepositoryResult<Phrase>.Failure(validationError!);
        SqliteTransaction? transaction = null;
        try
        {
            transaction = connection.BeginTransaction();
            var existing = await ReadPhraseAsync(connection, DbId(command.Id), transaction, cancellationToken);
            if (existing is not null)
                return SamePhrase(existing, command, shortcut) ? RepositoryResult<Phrase>.Success(existing) : RepositoryResult<Phrase>.Failure(Conflict(existing.Id, existing.Title));

            if (!await CategoryExistsAsync(connection, transaction, command.CategoryId, cancellationToken))
                return RepositoryResult<Phrase>.Failure(NotFound("鍒嗙被"));
            var shortcutConflict = await FindShortcutConflictAsync(connection, transaction, shortcut?.Normalized, cancellationToken);
            if (shortcutConflict is not null)
                return RepositoryResult<Phrase>.Failure(new DataError("SHORTCUT_CONFLICT", "快捷键已被其他话术占用。", shortcutConflict.Value.Id, shortcutConflict.Value.Title));

            var now = Now();
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            // 新建话术时 sort_order 总是追加到所属分类末尾，避免插入到中间导致持久化顺序混乱。
            insert.CommandText = "INSERT INTO phrases(id, title, content, category_id, favorite, shortcut_mode, shortcut_display, shortcut_normalized, usage_count, version, created_at_utc, updated_at_utc, color_key, sort_order) VALUES ($id, $title, $content, $categoryId, $favorite, $mode, $display, $normalized, 0, 1, $created, $updated, $colorKey, (SELECT COALESCE(MAX(sort_order),0)+1 FROM (SELECT sort_order FROM phrases WHERE category_id=$categoryId)));";
            AddPhraseParameters(insert, command, shortcut, colorKey, now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var created = await ReadPhraseAsync(connection, DbId(command.Id), null, cancellationToken);
            return RepositoryResult<Phrase>.Success(created!, Change(command.Id, "create"));
        }
        catch (SqliteException ex)
        {
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            }
            return RepositoryResult<Phrase>.Failure(MapSqliteError(ex));
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private async Task<RepositoryResult<Phrase>> UpdateCoreAsync(SqliteConnection connection, UpdatePhraseCommand command, CancellationToken cancellationToken)
    {
        if (!ValidatePhraseText(command.Title, command.Content, out var validationError)) return RepositoryResult<Phrase>.Failure(validationError!);
        if (!ValidateColorKey(command.ColorKey, out var colorKey, out validationError)) return RepositoryResult<Phrase>.Failure(validationError!);
        if (!PrepareShortcut(Shortcuts, command.ShortcutMode, command.Shortcut, out var shortcut, out validationError)) return RepositoryResult<Phrase>.Failure(validationError!);
        SqliteTransaction? transaction = null;
        try
        {
            transaction = connection.BeginTransaction();
            var existing = await ReadPhraseAsync(connection, DbId(command.Id), transaction, cancellationToken);
            if (existing is null) return RepositoryResult<Phrase>.Failure(NotFound("璇濇湳"));
            if (existing.Version != command.ExpectedVersion) return RepositoryResult<Phrase>.Failure(Conflict(existing.Id, existing.Title));
            if (!await CategoryExistsAsync(connection, transaction, command.CategoryId, cancellationToken)) return RepositoryResult<Phrase>.Failure(NotFound("鍒嗙被"));
            var shortcutConflict = await FindShortcutConflictAsync(connection, transaction, shortcut?.Normalized, command.Id, cancellationToken);
            if (shortcutConflict is not null) return RepositoryResult<Phrase>.Failure(new DataError("SHORTCUT_CONFLICT", "快捷键已被其他话术占用。", shortcutConflict.Value.Id, shortcutConflict.Value.Title));

            var now = Now();
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE phrases SET title=$title, content=$content, category_id=$categoryId, favorite=$favorite, shortcut_mode=$mode, shortcut_display=$display, shortcut_normalized=$normalized, color_key=$colorKey, sort_order=$sortOrder, version=version+1, updated_at_utc=$updated WHERE id=$id AND version=$version;";
            update.Parameters.AddWithValue("$title", command.Title.Trim());
            update.Parameters.AddWithValue("$content", command.Content);
            update.Parameters.AddWithValue("$categoryId", DbId(command.CategoryId));
            update.Parameters.AddWithValue("$favorite", command.Favorite ? 1 : 0);
            update.Parameters.AddWithValue("$mode", command.ShortcutMode.ToString());
            update.Parameters.AddWithValue("$display", (object?)shortcut?.Display ?? DBNull.Value);
            update.Parameters.AddWithValue("$normalized", (object?)shortcut?.Normalized ?? DBNull.Value);
            update.Parameters.AddWithValue("$colorKey", colorKey);
            update.Parameters.AddWithValue("$sortOrder", command.SortOrder);
            update.Parameters.AddWithValue("$updated", now.ToString("O"));
            update.Parameters.AddWithValue("$id", DbId(command.Id));
            update.Parameters.AddWithValue("$version", command.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return RepositoryResult<Phrase>.Failure(Conflict(existing.Id, existing.Title));
            await transaction.CommitAsync(cancellationToken);
            var updated = await ReadPhraseAsync(connection, DbId(command.Id), null, cancellationToken);
            return RepositoryResult<Phrase>.Success(updated!, Change(command.Id, "update"));
        }
        catch (SqliteException ex)
        {
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            }
            return RepositoryResult<Phrase>.Failure(MapSqliteError(ex));
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private async Task<RepositoryResult<DeleteResult>> DeleteCoreAsync(SqliteConnection connection, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        SqliteTransaction? transaction = null;
        try
        {
            transaction = connection.BeginTransaction();
            var existing = await ReadPhraseAsync(connection, DbId(id), transaction, cancellationToken);
            if (existing is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return RepositoryResult<DeleteResult>.Success(new DeleteResult(false, null));
            }
            if (expectedVersion.HasValue && expectedVersion.Value != existing.Version)
                return RepositoryResult<DeleteResult>.Failure(Conflict(existing.Id, existing.Title));
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM phrases WHERE id=$id;";
            delete.Parameters.AddWithValue("$id", DbId(id));
            await delete.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RepositoryResult<DeleteResult>.Success(new DeleteResult(true, Change(id, "delete")), Change(id, "delete"));
        }
        catch (SqliteException ex)
        {
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            }
            return RepositoryResult<DeleteResult>.Failure(MapSqliteError(ex));
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private async Task<RepositoryResult<Phrase>> IncrementUsageCoreAsync(SqliteConnection connection, Guid id, DateTimeOffset usedAtUtc, CancellationToken cancellationToken)
    {
        SqliteTransaction? transaction = null;
        try
        {
            transaction = connection.BeginTransaction();
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE phrases SET usage_count=usage_count+1, last_used_at_utc=$lastUsed, version=version+1, updated_at_utc=$updated WHERE id=$id;";
            update.Parameters.AddWithValue("$lastUsed", usedAtUtc.ToUniversalTime().ToString("O"));
            update.Parameters.AddWithValue("$updated", Now().ToString("O"));
            update.Parameters.AddWithValue("$id", DbId(id));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return RepositoryResult<Phrase>.Failure(NotFound("璇濇湳"));
            await transaction.CommitAsync(cancellationToken);
            await using var read = await Connections.OpenReadAsync(cancellationToken);
            var phrase = await ReadPhraseAsync(read, DbId(id), null, cancellationToken);
            return RepositoryResult<Phrase>.Success(phrase!, Change(id, "increment_usage"));
        }
        catch (SqliteException ex)
        {
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            }
            return RepositoryResult<Phrase>.Failure(MapSqliteError(ex));
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static void AddPhraseParameters(SqliteCommand command, CreatePhraseCommand phrase, ShortcutValue? shortcut, string colorKey, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$id", DbId(phrase.Id));
        command.Parameters.AddWithValue("$title", phrase.Title.Trim());
        command.Parameters.AddWithValue("$content", phrase.Content);
        command.Parameters.AddWithValue("$categoryId", DbId(phrase.CategoryId));
        command.Parameters.AddWithValue("$favorite", phrase.Favorite ? 1 : 0);
        command.Parameters.AddWithValue("$mode", phrase.ShortcutMode.ToString());
        command.Parameters.AddWithValue("$display", (object?)shortcut?.Display ?? DBNull.Value);
        command.Parameters.AddWithValue("$normalized", (object?)shortcut?.Normalized ?? DBNull.Value);
        command.Parameters.AddWithValue("$colorKey", colorKey);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
    }



    private static async Task<bool> CategoryExistsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid categoryId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM categories WHERE id=$id;";
        command.Parameters.AddWithValue("$id", DbId(categoryId));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<(Guid Id, string Title)?> FindShortcutConflictAsync(SqliteConnection connection, SqliteTransaction transaction, string? normalized, CancellationToken cancellationToken) =>
        await FindShortcutConflictAsync(connection, transaction, normalized, null, cancellationToken);

    private static async Task<(Guid Id, string Title)?> FindShortcutConflictAsync(SqliteConnection connection, SqliteTransaction transaction, string? normalized, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(normalized)) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = exceptId.HasValue
            ? "SELECT id, title FROM phrases WHERE shortcut_normalized=$shortcut AND id<>$id LIMIT 1;"
            : "SELECT id, title FROM phrases WHERE shortcut_normalized=$shortcut LIMIT 1;";
        command.Parameters.AddWithValue("$shortcut", normalized);
        if (exceptId.HasValue) command.Parameters.AddWithValue("$id", DbId(exceptId.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (Guid.Parse(reader.GetString(0)), reader.GetString(1)) : null;
    }
    private DateTimeOffset NowUtc() => Clock.GetUtcNow();
    private CommittedDataChange Change(Guid id, string operation) => new("phrase", id, operation, NowUtc());
}

