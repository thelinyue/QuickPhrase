using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 图文话术 Repository。话术头和有序段在同一 SQLite 事务中提交；只有事务成功后调用方才会更新 Core 内存索引。
/// 图片段只能引用已经进入媒体库并登记在 media_assets 的资产，避免产生悬空引用。
/// </summary>
internal sealed class SqlitePhraseRepository : SqliteRepositoryBase, IPhraseRepository
{
    private readonly IMediaAssetStore _mediaAssets;

    public SqlitePhraseRepository(SqliteConnectionFactory connections, SqliteWriteQueue writes, TimeProvider clock, IMediaAssetStore mediaAssets) : base(connections, writes, clock)
    {
        _mediaAssets = mediaAssets;
    }

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

    public async Task<RepositoryResult<Phrase>> CreateAsync(CreatePhraseCommand command, CancellationToken cancellationToken = default)
    {
        try { return await Writes.EnqueueAsync((connection, ct) => CreateCoreAsync(connection, command, ct), cancellationToken); }
        catch (SqliteException exception) { return RepositoryResult<Phrase>.Failure(MapSqliteError(exception)); }
    }

    public async Task<RepositoryResult<Phrase>> UpdateAsync(UpdatePhraseCommand command, CancellationToken cancellationToken = default)
    {
        var before = await GetAsync(command.Id, cancellationToken);
        try
        {
            var result = await Writes.EnqueueAsync((connection, ct) => UpdateCoreAsync(connection, command, ct), cancellationToken);
            if (result.IsSuccess && before is not null)
                await CleanupRemovedAssetsAfterCommitAsync(before.Body, command.Body);
            return result;
        }
        catch (SqliteException exception) { return RepositoryResult<Phrase>.Failure(MapSqliteError(exception)); }
    }

    public async Task<RepositoryResult<DeleteResult>> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default)
    {
        var before = await GetAsync(id, cancellationToken);
        try
        {
            var result = await Writes.EnqueueAsync((connection, ct) => DeleteCoreAsync(connection, id, expectedVersion, ct), cancellationToken);
            if (result.IsSuccess && result.Value?.Deleted == true && before is not null)
                await CleanupRemovedAssetsAfterCommitAsync(before.Body, null);
            return result;
        }
        catch (SqliteException exception) { return RepositoryResult<DeleteResult>.Failure(MapSqliteError(exception)); }
    }

    public Task<RepositoryResult<Phrase>> IncrementUsageAsync(Guid id, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => IncrementUsageCoreAsync(connection, id, usedAtUtc, ct), cancellationToken);

    private async Task<RepositoryResult<Phrase>> CreateCoreAsync(SqliteConnection connection, CreatePhraseCommand command, CancellationToken cancellationToken)
    {
        if (!PhraseRules.Validate(command, out var error)) return RepositoryResult<Phrase>.Failure(error!);
        if (!ValidateColorKey(command.ColorKey, out var colorKey, out error)) return RepositoryResult<Phrase>.Failure(error!);
        if (!PrepareShortcut(Shortcuts, command.ShortcutMode, command.Shortcut, out var shortcut, out error)) return RepositoryResult<Phrase>.Failure(error!);

        await using var transaction = connection.BeginTransaction();
        try
        {
            var existing = await ReadPhraseAsync(connection, DbId(command.Id), transaction, cancellationToken);
            if (existing is not null)
                return SamePhrase(existing, command, shortcut) ? RepositoryResult<Phrase>.Success(existing) : RepositoryResult<Phrase>.Failure(Conflict(existing.Id, existing.Title));
            if (!await CategoryExistsAsync(connection, transaction, command.CategoryId, cancellationToken))
                return RepositoryResult<Phrase>.Failure(NotFound("分类"));
            if (!await HasRootCategoryAsync(connection, transaction, cancellationToken))
                return RepositoryResult<Phrase>.Failure(Validation("创建话术前，请先创建一个一级分类。"));
            if (!await AssetsExistAsync(connection, transaction, command.Body, cancellationToken))
                return RepositoryResult<Phrase>.Failure(Validation("图片段引用的媒体资产不存在，请重新添加图片。"));

            var shortcutConflict = await FindShortcutConflictAsync(connection, transaction, shortcut?.Normalized, null, cancellationToken);
            if (shortcutConflict is not null)
                return RepositoryResult<Phrase>.Failure(new DataError("SHORTCUT_CONFLICT", "快捷键已被其他话术占用。", shortcutConflict.Value.Id, shortcutConflict.Value.Title));

            var now = Now();
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO phrases(id,title,batch_separator,category_id,shortcut_mode,shortcut_display,shortcut_normalized,usage_count,version,created_at_utc,updated_at_utc,color_key,sort_order) VALUES($id,$title,$separator,$categoryId,$mode,$display,$normalized,0,1,$created,$updated,$colorKey,(SELECT COALESCE(MAX(sort_order),0)+1 FROM (SELECT sort_order FROM phrases WHERE category_id=$categoryId)));";
                AddCreateParameters(insert, command, shortcut, colorKey, now);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await WriteSegmentsAsync(connection, transaction, command.Id, command.Body, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var created = await ReadPhraseAsync(connection, DbId(command.Id), null, cancellationToken);
            return RepositoryResult<Phrase>.Success(created!, Change(command.Id, "create"));
        }
        catch (SqliteException exception)
        {
            await TryRollbackAsync(transaction);
            return RepositoryResult<Phrase>.Failure(MapSqliteError(exception));
        }
    }

    private async Task<RepositoryResult<Phrase>> UpdateCoreAsync(SqliteConnection connection, UpdatePhraseCommand command, CancellationToken cancellationToken)
    {
        if (!PhraseRules.Validate(command, out var error)) return RepositoryResult<Phrase>.Failure(error!);
        if (!ValidateColorKey(command.ColorKey, out var colorKey, out error)) return RepositoryResult<Phrase>.Failure(error!);
        if (!PrepareShortcut(Shortcuts, command.ShortcutMode, command.Shortcut, out var shortcut, out error)) return RepositoryResult<Phrase>.Failure(error!);

        await using var transaction = connection.BeginTransaction();
        try
        {
            var existing = await ReadPhraseAsync(connection, DbId(command.Id), transaction, cancellationToken);
            if (existing is null) return RepositoryResult<Phrase>.Failure(NotFound("话术"));
            if (existing.Version != command.ExpectedVersion) return RepositoryResult<Phrase>.Failure(Conflict(existing.Id, existing.Title));
            if (!await CategoryExistsAsync(connection, transaction, command.CategoryId, cancellationToken)) return RepositoryResult<Phrase>.Failure(NotFound("分类"));
            if (!await AssetsExistAsync(connection, transaction, command.Body, cancellationToken))
                return RepositoryResult<Phrase>.Failure(Validation("图片段引用的媒体资产不存在，请重新添加图片。"));
            var shortcutConflict = await FindShortcutConflictAsync(connection, transaction, shortcut?.Normalized, command.Id, cancellationToken);
            if (shortcutConflict is not null) return RepositoryResult<Phrase>.Failure(new DataError("SHORTCUT_CONFLICT", "快捷键已被其他话术占用。", shortcutConflict.Value.Id, shortcutConflict.Value.Title));

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE phrases SET title=$title,batch_separator=$separator,category_id=$categoryId,shortcut_mode=$mode,shortcut_display=$display,shortcut_normalized=$normalized,color_key=$colorKey,sort_order=$sortOrder,version=version+1,updated_at_utc=$updated WHERE id=$id AND version=$version;";
                update.Parameters.AddWithValue("$title", command.Title.Trim());
                update.Parameters.AddWithValue("$separator", command.Body.BatchSeparator);
                update.Parameters.AddWithValue("$categoryId", DbId(command.CategoryId));
                update.Parameters.AddWithValue("$mode", command.ShortcutMode.ToString());
                update.Parameters.AddWithValue("$display", (object?)shortcut?.Display ?? DBNull.Value);
                update.Parameters.AddWithValue("$normalized", (object?)shortcut?.Normalized ?? DBNull.Value);
                update.Parameters.AddWithValue("$colorKey", colorKey);
                update.Parameters.AddWithValue("$sortOrder", command.SortOrder);
                update.Parameters.AddWithValue("$updated", Now().ToString("O"));
                update.Parameters.AddWithValue("$id", DbId(command.Id));
                update.Parameters.AddWithValue("$version", command.ExpectedVersion);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return RepositoryResult<Phrase>.Failure(Conflict(existing.Id, existing.Title));
            }
            await DeleteSegmentsAsync(connection, transaction, command.Id, cancellationToken);
            await WriteSegmentsAsync(connection, transaction, command.Id, command.Body, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var updated = await ReadPhraseAsync(connection, DbId(command.Id), null, cancellationToken);
            return RepositoryResult<Phrase>.Success(updated!, Change(command.Id, "update"));
        }
        catch (SqliteException exception)
        {
            await TryRollbackAsync(transaction);
            return RepositoryResult<Phrase>.Failure(MapSqliteError(exception));
        }
    }

    private async Task<RepositoryResult<DeleteResult>> DeleteCoreAsync(SqliteConnection connection, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        try
        {
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
        catch (SqliteException exception)
        {
            await TryRollbackAsync(transaction);
            return RepositoryResult<DeleteResult>.Failure(MapSqliteError(exception));
        }
    }

    private async Task<RepositoryResult<Phrase>> IncrementUsageCoreAsync(SqliteConnection connection, Guid id, DateTimeOffset usedAtUtc, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        try
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE phrases SET usage_count=usage_count+1,last_used_at_utc=$lastUsed,version=version+1,updated_at_utc=$updated WHERE id=$id;";
            update.Parameters.AddWithValue("$lastUsed", usedAtUtc.ToUniversalTime().ToString("O"));
            update.Parameters.AddWithValue("$updated", Now().ToString("O"));
            update.Parameters.AddWithValue("$id", DbId(id));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return RepositoryResult<Phrase>.Failure(NotFound("话术"));
            await transaction.CommitAsync(cancellationToken);
            var phrase = await ReadPhraseAsync(connection, DbId(id), null, cancellationToken);
            return RepositoryResult<Phrase>.Success(phrase!, Change(id, "increment_usage"));
        }
        catch (SqliteException exception)
        {
            await TryRollbackAsync(transaction);
            return RepositoryResult<Phrase>.Failure(MapSqliteError(exception));
        }
    }

    private static void AddCreateParameters(SqliteCommand command, CreatePhraseCommand phrase, ShortcutValue? shortcut, string colorKey, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$id", DbId(phrase.Id));
        command.Parameters.AddWithValue("$title", phrase.Title.Trim());
        command.Parameters.AddWithValue("$separator", phrase.Body.BatchSeparator);
        command.Parameters.AddWithValue("$categoryId", DbId(phrase.CategoryId));
        command.Parameters.AddWithValue("$mode", phrase.ShortcutMode.ToString());
        command.Parameters.AddWithValue("$display", (object?)shortcut?.Display ?? DBNull.Value);
        command.Parameters.AddWithValue("$normalized", (object?)shortcut?.Normalized ?? DBNull.Value);
        command.Parameters.AddWithValue("$colorKey", colorKey);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
    }

    private static async Task WriteSegmentsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid phraseId, PhraseBody body, CancellationToken cancellationToken)
    {
        for (var index = 0; index < body.Segments.Length; index++)
        {
            var segment = body.Segments[index];
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO phrase_segments(segment_id,phrase_id,segment_kind,text_content,media_asset_id,sort_order) VALUES($segmentId,$phraseId,$kind,$text,$assetId,$sortOrder);";
            insert.Parameters.AddWithValue("$segmentId", DbId(segment.Id));
            insert.Parameters.AddWithValue("$phraseId", DbId(phraseId));
            insert.Parameters.AddWithValue("$kind", segment.Kind.ToString());
            insert.Parameters.AddWithValue("$text", (object?)segment.Text ?? DBNull.Value);
            insert.Parameters.AddWithValue("$assetId", (object?)segment.Image?.AssetId.ToString("D") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sortOrder", index);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task DeleteSegmentsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid phraseId, CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM phrase_segments WHERE phrase_id=$phraseId;";
        delete.Parameters.AddWithValue("$phraseId", DbId(phraseId));
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> AssetsExistAsync(SqliteConnection connection, SqliteTransaction transaction, PhraseBody body, CancellationToken cancellationToken)
    {
        var ids = body.Segments.Where(segment => segment.Kind == PhraseSegmentKind.Image).Select(segment => segment.Image!.AssetId).Distinct().ToArray();
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM media_assets WHERE asset_id=$id LIMIT 1;";
            command.Parameters.AddWithValue("$id", DbId(id));
            if (await command.ExecuteScalarAsync(cancellationToken) is null) return false;
        }
        return true;
    }

    private static async Task<bool> HasRootCategoryAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM categories WHERE parent_id IS NULL LIMIT 1;";
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> CategoryExistsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid categoryId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM categories WHERE id=$id;";
        command.Parameters.AddWithValue("$id", DbId(categoryId));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<(Guid Id, string Title)?> FindShortcutConflictAsync(SqliteConnection connection, SqliteTransaction transaction, string? normalized, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(normalized)) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = exceptId.HasValue
            ? "SELECT id,title FROM phrases WHERE shortcut_normalized=$shortcut AND id<>$id LIMIT 1;"
            : "SELECT id,title FROM phrases WHERE shortcut_normalized=$shortcut LIMIT 1;";
        command.Parameters.AddWithValue("$shortcut", normalized);
        if (exceptId.HasValue) command.Parameters.AddWithValue("$id", DbId(exceptId.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (Guid.Parse(reader.GetString(0)), reader.GetString(1)) : null;
    }

    /// <summary>
    /// 话术事务已经提交后才清理旧媒体。清理失败不得改变已提交结果；媒体库会保留 SQLite 元数据供下次启动重试。
    /// </summary>
    private async Task CleanupRemovedAssetsAfterCommitAsync(PhraseBody before, PhraseBody? after)
    {
        var retained = after?.Segments
            .Where(segment => segment.Kind == PhraseSegmentKind.Image)
            .Select(segment => segment.Image!.AssetId)
            .ToHashSet() ?? [];
        var removed = before.Segments
            .Where(segment => segment.Kind == PhraseSegmentKind.Image)
            .Select(segment => segment.Image!.AssetId)
            .Distinct()
            .Where(assetId => !retained.Contains(assetId));

        foreach (var assetId in removed)
        {
            try
            {
                await _mediaAssets.DeleteIfUnreferencedAsync(assetId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"话术已保存，但旧媒体清理失败，将在下次启动重试。阶段：MEDIA_POST_COMMIT_CLEANUP；结果码：MEDIA_CLEANUP_FAILED；异常类型：{exception.GetType().Name}");
            }
        }
    }
    private static async Task TryRollbackAsync(SqliteTransaction transaction)
    {
        try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
    }

    private CommittedDataChange Change(Guid id, string operation) => new("phrase", id, operation, Clock.GetUtcNow());
}
