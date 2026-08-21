using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 分类持久化与二级树校验（分类层级最多支持两级）。
/// 分类名称按 ParentId + normalized_name 在同级内唯一；不同父级下允许复用名称。
/// </summary>
internal sealed class SqliteCategoryRepository : SqliteRepositoryBase, ICategoryRepository
{
    public SqliteCategoryRepository(SqliteConnectionFactory connections, SqliteWriteQueue writes, TimeProvider clock) : base(connections, writes, clock) { }

    public async Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await Connections.OpenReadAsync(cancellationToken);
        return await ReadCategoriesAsync(connection, null, cancellationToken);
    }

    public Task<RepositoryResult<Category>> CreateAsync(CreateCategoryCommand command, CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => CreateCoreAsync(connection, command, ct), cancellationToken);

    public Task<RepositoryResult<Category>> RenameAsync(RenameCategoryCommand command, CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => RenameCoreAsync(connection, command, ct), cancellationToken);

    public Task<RepositoryResult<Category>> MoveAsync(MoveCategoryCommand command, CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => MoveCoreAsync(connection, command, ct), cancellationToken);

    public Task<RepositoryResult<DeleteResult>> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken = default) =>
        Writes.EnqueueAsync((connection, ct) => DeleteCoreAsync(connection, id, expectedVersion, ct), cancellationToken);

    private async Task<RepositoryResult<Category>> CreateCoreAsync(SqliteConnection connection, CreateCategoryCommand command, CancellationToken cancellationToken)
    {
        var normalized = NormalizeName(command.Name);
        if (normalized.Display.Length == 0) return RepositoryResult<Category>.Failure(Validation("分类名称不能为空。"));
        await using var transaction = connection.BeginTransaction();
        var categories = await ReadCategoriesAsync(connection, transaction, cancellationToken);
        if (command.ParentId.HasValue && !categories.Any(x => x.Id == command.ParentId.Value))
            return RepositoryResult<Category>.Failure(NotFound("父分类"));
        if (command.ParentId.HasValue && GetDepth(categories, command.ParentId.Value) >= 2)
            return RepositoryResult<Category>.Failure(new DataError("CATEGORY_DEPTH_EXCEEDED", "分类最多支持二级。", command.ParentId));
        if (await CategoryNameExistsAsync(connection, transaction, command.ParentId, normalized.Normalized, null, cancellationToken))
            return RepositoryResult<Category>.Failure(Validation("分类名称已经存在。"));

        // 分类写入经过单写者队列。一级分类的末尾排序必须在同一写事务内计算，
        // 否则并发新建可能读取到相同的最大排序值，导致界面顺序不稳定。
        var sortOrder = command.SortOrder ?? (command.ParentId is null
            ? categories.Where(category => category.ParentId is null).Select(category => category.SortOrder).DefaultIfEmpty(-10).Max() + 10
            : 0);
        var now = Now();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO categories(id, parent_id, name, normalized_name, sort_order, version, created_at_utc, updated_at_utc) VALUES ($id, $parentId, $name, $normalized, $sortOrder, 1, $created, $updated);";
        insert.Parameters.AddWithValue("$id", DbId(command.Id));
        insert.Parameters.AddWithValue("$parentId", (object?)command.ParentId?.ToString() ?? DBNull.Value);
        insert.Parameters.AddWithValue("$name", normalized.Display);
        insert.Parameters.AddWithValue("$normalized", normalized.Normalized);
        insert.Parameters.AddWithValue("$sortOrder", sortOrder);
        insert.Parameters.AddWithValue("$created", now.ToString("O"));
        insert.Parameters.AddWithValue("$updated", now.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RepositoryResult<Category>.Success(new Category(command.Id, command.ParentId, normalized.Display, sortOrder, 1, now, now), Change(command.Id, "create"));
    }

    private async Task<RepositoryResult<Category>> RenameCoreAsync(SqliteConnection connection, RenameCategoryCommand command, CancellationToken cancellationToken)
    {
        var normalized = NormalizeName(command.Name);
        if (normalized.Display.Length == 0) return RepositoryResult<Category>.Failure(Validation("分类名称不能为空。"));
        await using var transaction = connection.BeginTransaction();
        var current = await ReadCategoryAsync(connection, transaction, command.Id, cancellationToken);
        if (current is null) return RepositoryResult<Category>.Failure(NotFound("分类"));
        if (current.Version != command.ExpectedVersion) return RepositoryResult<Category>.Failure(Conflict(current.Id, current.Name));
        if (await CategoryNameExistsAsync(connection, transaction, current.ParentId, normalized.Normalized, command.Id, cancellationToken))
            return RepositoryResult<Category>.Failure(Validation("分类名称已经存在。"));
        var now = Now();
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE categories SET name=$name, normalized_name=$normalized, sort_order=$sortOrder, version=version+1, updated_at_utc=$updated WHERE id=$id AND version=$version;";
        update.Parameters.AddWithValue("$name", normalized.Display);
        update.Parameters.AddWithValue("$normalized", normalized.Normalized);
        update.Parameters.AddWithValue("$sortOrder", command.SortOrder);
        update.Parameters.AddWithValue("$updated", now.ToString("O"));
        update.Parameters.AddWithValue("$id", DbId(command.Id));
        update.Parameters.AddWithValue("$version", command.ExpectedVersion);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RepositoryResult<Category>.Success(current with { Name = normalized.Display, SortOrder = command.SortOrder, Version = current.Version + 1, UpdatedAtUtc = now }, Change(command.Id, "rename"));
    }

    private async Task<RepositoryResult<Category>> MoveCoreAsync(SqliteConnection connection, MoveCategoryCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        var categories = await ReadCategoriesAsync(connection, transaction, cancellationToken);
        var current = categories.FirstOrDefault(x => x.Id == command.Id);
        if (current is null) return RepositoryResult<Category>.Failure(NotFound("分类"));
        if (current.Version != command.ExpectedVersion) return RepositoryResult<Category>.Failure(Conflict(current.Id, current.Name));
        if (command.ParentId == command.Id) return RepositoryResult<Category>.Failure(new DataError("CATEGORY_CYCLE", "分类不能移动到自身下面。", command.Id, current.Name));
        if (command.ParentId.HasValue && !categories.Any(x => x.Id == command.ParentId.Value))
            return RepositoryResult<Category>.Failure(NotFound("父分类"));
        if (command.ParentId.HasValue && IsDescendant(categories, command.Id, command.ParentId.Value))
            return RepositoryResult<Category>.Failure(new DataError("CATEGORY_CYCLE", "分类不能移动到自己的子分类下面。", command.Id, current.Name));
        var parentDepth = command.ParentId.HasValue ? GetDepth(categories, command.ParentId.Value) : 0;
        var subtreeDepth = GetSubtreeDepth(categories, command.Id);
        if (parentDepth + subtreeDepth > 2)
            return RepositoryResult<Category>.Failure(new DataError("CATEGORY_DEPTH_EXCEEDED", "移动后分类树不能超过二级。", command.Id, current.Name));
        if (await CategoryNameExistsAsync(connection, transaction, command.ParentId, NormalizeName(current.Name).Normalized, command.Id, cancellationToken))
            return RepositoryResult<Category>.Failure(Validation("目标父分类下已经存在同名分类。"));

        var now = Now();
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE categories SET parent_id=$parentId, sort_order=$sortOrder, version=version+1, updated_at_utc=$updated WHERE id=$id AND version=$version;";
        update.Parameters.AddWithValue("$parentId", (object?)command.ParentId?.ToString() ?? DBNull.Value);
        update.Parameters.AddWithValue("$sortOrder", command.SortOrder);
        update.Parameters.AddWithValue("$updated", now.ToString("O"));
        update.Parameters.AddWithValue("$id", DbId(command.Id));
        update.Parameters.AddWithValue("$version", command.ExpectedVersion);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RepositoryResult<Category>.Success(current with { ParentId = command.ParentId, SortOrder = command.SortOrder, Version = current.Version + 1, UpdatedAtUtc = now }, Change(command.Id, "move"));
    }

    /// <summary>
    /// 在同一个 SQLite 事务内删除分类子树、话术和分类，确保级联删除整体提交或整体回滚。
    /// 这样即使中途某条 SQL 失败，也不会留下半成品数据。
    /// </summary>
    private async Task<RepositoryResult<DeleteResult>> DeleteCoreAsync(SqliteConnection connection, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        SqliteTransaction? transaction = null;
        try
        {
            transaction = connection.BeginTransaction();
            var categories = await ReadCategoriesAsync(connection, transaction, cancellationToken);
            var current = categories.FirstOrDefault(category => category.Id == id);
            if (current is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return RepositoryResult<DeleteResult>.Success(new DeleteResult(false, null));
            }
            if (expectedVersion.HasValue && current.Version != expectedVersion.Value)
                return RepositoryResult<DeleteResult>.Failure(Conflict(current.Id, current.Name));

            var subtree = GetSubtree(categories, id);
            var categoryIds = subtree.Select(category => category.Id).ToArray();
            var phraseIds = await ReadPhraseIdsAsync(connection, transaction, categoryIds, cancellationToken);
            if (phraseIds.Count > 0)
            {
                await ExecuteDeleteByIdsAsync(connection, transaction, "phrases", "id", phraseIds, cancellationToken);
            }

            foreach (var category in subtree.OrderByDescending(category => GetDepth(categories, category.Id)))
            {
                await using var deleteCategory = connection.CreateCommand();
                deleteCategory.Transaction = transaction;
                deleteCategory.CommandText = "DELETE FROM categories WHERE id=$id;";
                deleteCategory.Parameters.AddWithValue("$id", DbId(category.Id));
                await deleteCategory.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            var change = Change(id, "delete");
            return RepositoryResult<DeleteResult>.Success(new DeleteResult(true, change, phraseIds), change);
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

    private static IReadOnlyList<Category> GetSubtree(IReadOnlyList<Category> categories, Guid rootId)
    {
        var children = categories
            .Where(category => category.ParentId.HasValue)
            .GroupBy(category => category.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<Category>();
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var current = categories.FirstOrDefault(category => category.Id == currentId);
            if (current is null) continue;
            result.Add(current);
            if (!children.TryGetValue(currentId, out var descendants)) continue;
            foreach (var child in descendants) queue.Enqueue(child.Id);
        }
        return result;
    }

    private static async Task<IReadOnlyList<Guid>> ReadPhraseIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Guid> categoryIds,
        CancellationToken cancellationToken)
    {
        var placeholders = categoryIds.Select((_, index) => $"$category{index}").ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id FROM phrases WHERE category_id IN ({string.Join(",", placeholders)});";
        for (var index = 0; index < categoryIds.Count; index++)
            command.Parameters.AddWithValue(placeholders[index], DbId(categoryIds[index]));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Guid.Parse(reader.GetString(0)));
        return result;
    }

    private static async Task ExecuteDeleteByIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken)
    {
        var placeholders = ids.Select((_, index) => $"$id{index}").ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE {column} IN ({string.Join(",", placeholders)});";
        for (var index = 0; index < ids.Count; index++)
            command.Parameters.AddWithValue(placeholders[index], DbId(ids[index]));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Category?> ReadCategoryAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        var categories = await ReadCategoriesAsync(connection, transaction, cancellationToken);
        return categories.FirstOrDefault(x => x.Id == id);
    }

    private static async Task<IReadOnlyList<Category>> ReadCategoriesAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, parent_id, name, sort_order, version, created_at_utc, updated_at_utc FROM categories ORDER BY parent_id, sort_order, name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Category>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new Category(ReadId(reader, 0), reader.IsDBNull(1) ? null : ReadId(reader, 1), reader.GetString(2), reader.GetInt32(3), reader.GetInt64(4), ReadTime(reader, 5), ReadTime(reader, 6)));
        return result;
    }

    private static async Task<bool> CategoryNameExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid? parentId,
        string normalized,
        Guid? exceptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parentPredicate = parentId.HasValue ? "parent_id=$parentId" : "parent_id IS NULL";
        var exceptPredicate = exceptId.HasValue ? " AND id<>$id" : string.Empty;
        command.CommandText = $"SELECT id FROM categories WHERE {parentPredicate} AND normalized_name=$normalized{exceptPredicate} LIMIT 1;";
        if (parentId.HasValue) command.Parameters.AddWithValue("$parentId", DbId(parentId.Value));
        command.Parameters.AddWithValue("$normalized", normalized);
        if (exceptId.HasValue) command.Parameters.AddWithValue("$id", DbId(exceptId.Value));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static int GetDepth(IReadOnlyList<Category> categories, Guid id)
    {
        var map = categories.ToDictionary(x => x.Id);
        var depth = 1;
        var cursor = id;
        var visited = new HashSet<Guid>();
        while (map.TryGetValue(cursor, out var category) && category.ParentId.HasValue)
        {
            if (!visited.Add(cursor)) return int.MaxValue;
            depth++;
            cursor = category.ParentId.Value;
        }
        return depth;
    }

    private static int GetSubtreeDepth(IReadOnlyList<Category> categories, Guid id)
    {
        var children = new Dictionary<Guid, List<Guid>>();
        foreach (var category in categories)
        {
            if (!category.ParentId.HasValue) continue;
            if (!children.TryGetValue(category.ParentId.Value, out var descendants))
                children[category.ParentId.Value] = descendants = [];
            descendants.Add(category.Id);
        }
        var max = 1;
        var queue = new Queue<(Guid Id, int Depth)>();
        queue.Enqueue((id, 1));
        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            max = Math.Max(max, depth);
            if (!children.TryGetValue(current, out var descendants)) continue;
            foreach (var child in descendants) queue.Enqueue((child, depth + 1));
        }
        return max;
    }

    private static bool IsDescendant(IReadOnlyList<Category> categories, Guid rootId, Guid candidateId)
    {
        var map = categories.ToDictionary(x => x.Id);
        var cursor = candidateId;
        var visited = new HashSet<Guid>();
        while (map.TryGetValue(cursor, out var category) && category.ParentId.HasValue)
        {
            if (!visited.Add(cursor)) return true;
            if (category.ParentId.Value == rootId) return true;
            cursor = category.ParentId.Value;
        }
        return false;
    }

    private CommittedDataChange Change(Guid id, string operation) => new("category", id, operation, Now());
}
