using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 话术包批量写入器。所有分类和话术在同一个 SqliteWriteQueue writer connection、同一个事务内提交，禁止复用逐条 CRUD 造成半套数据。
/// </summary>
internal sealed class SqlitePhrasePackageImporter
{
    private readonly SqliteWriteQueue _writes;
    private readonly TimeProvider _clock;

    public SqlitePhrasePackageImporter(SqliteWriteQueue writes, TimeProvider clock)
    {
        _writes = writes;
        _clock = clock;
    }

    public Task<PhrasePackageImportResult> ImportAsync(PhrasePackageImportPlan plan, CancellationToken cancellationToken = default) =>
        _writes.EnqueueAsync((connection, ct) => ImportCoreAsync(connection, plan, ct), cancellationToken);

    private async Task<PhrasePackageImportResult> ImportCoreAsync(SqliteConnection connection, PhrasePackageImportPlan plan, CancellationToken cancellationToken)
    {
        var traceId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var phraseById = plan.Package.Phrases.ToDictionary(x => x.Id);
            var localCategoryIds = new Dictionary<Guid, Guid>();
            foreach (var mapping in plan.CategoryMappings)
            {
                localCategoryIds[mapping.PackageCategoryId] = mapping.TargetCategoryId;
                if (!mapping.Create) continue;

                await using var insertCategory = connection.CreateCommand();
                insertCategory.Transaction = transaction;
                insertCategory.CommandText = "INSERT INTO categories(id, parent_id, name, normalized_name, sort_order, version, created_at_utc, updated_at_utc) VALUES ($id, $parentId, $name, $normalized, $sortOrder, 1, $created, $updated);";
                insertCategory.Parameters.AddWithValue("$id", DbId(mapping.TargetCategoryId));
                insertCategory.Parameters.AddWithValue("$parentId", mapping.ParentTargetCategoryId.HasValue ? DbId(mapping.ParentTargetCategoryId.Value) : (object)DBNull.Value);
                var normalized = NormalizeName(mapping.Name);
                insertCategory.Parameters.AddWithValue("$name", normalized.Display);
                insertCategory.Parameters.AddWithValue("$normalized", normalized.Normalized);
                insertCategory.Parameters.AddWithValue("$sortOrder", mapping.SortOrder);
                var now = _clock.GetUtcNow().ToString("O");
                insertCategory.Parameters.AddWithValue("$created", now);
                insertCategory.Parameters.AddWithValue("$updated", now);
                await insertCategory.ExecuteNonQueryAsync(cancellationToken);
            }

            var imported = 0;
            var skipped = 0;
            foreach (var decision in plan.PhraseDecisions)
            {
                if (!decision.ShouldImport) { skipped++; continue; }
                var phrase = phraseById[decision.PackagePhraseId];
                var categoryId = localCategoryIds[phrase.CategoryId];
                if (await IsDuplicateAsync(connection, transaction, phrase.Title.Trim(), phrase.Content, cancellationToken))
                {
                    skipped++;
                    continue;
                }

                await using var insertPhrase = connection.CreateCommand();
                insertPhrase.Transaction = transaction;
                insertPhrase.CommandText = "INSERT INTO phrases(id, title, content, category_id, shortcut_mode, shortcut_display, shortcut_normalized, usage_count, version, created_at_utc, updated_at_utc, color_key, sort_order) VALUES ($id, $title, $content, $categoryId, 'None', NULL, NULL, 0, 1, $created, $updated, 'default', $sortOrder);";
                insertPhrase.Parameters.AddWithValue("$id", DbId(Guid.NewGuid()));
                insertPhrase.Parameters.AddWithValue("$title", phrase.Title.Trim());
                insertPhrase.Parameters.AddWithValue("$content", phrase.Content);
                insertPhrase.Parameters.AddWithValue("$categoryId", DbId(categoryId));
                insertPhrase.Parameters.AddWithValue("$created", _clock.GetUtcNow().ToString("O"));
                insertPhrase.Parameters.AddWithValue("$updated", _clock.GetUtcNow().ToString("O"));
                insertPhrase.Parameters.AddWithValue("$sortOrder", phrase.SortOrder);
                await insertPhrase.ExecuteNonQueryAsync(cancellationToken);
                imported++;
            }

            await transaction.CommitAsync(cancellationToken);
            Log(traceId, "PACKAGE_IMPORT_COMMIT", started);
            return new PhrasePackageImportResult(true, plan.CategoryMappings.Count(x => x.Create), imported, skipped, "PACKAGE_IMPORT_OK", "话术包导入完成。", traceId);
        }
        catch (OperationCanceledException)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            Log(traceId, "PACKAGE_IMPORT_CANCELLED", started);
            throw;
        }
        catch (SqliteException)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            Log(traceId, "PACKAGE_IMPORT_ROLLED_BACK", started);
            return new PhrasePackageImportResult(false, 0, 0, 0, "PACKAGE_IMPORT_FAILED", "话术包导入失败，数据库未发生变更。", traceId);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            Log(traceId, "PACKAGE_IMPORT_ROLLED_BACK", started);
            return new PhrasePackageImportResult(false, 0, 0, 0, "PACKAGE_IMPORT_FAILED", "话术包导入失败，数据库未发生变更。", traceId);
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    private static async Task<bool> IsDuplicateAsync(SqliteConnection connection, SqliteTransaction transaction, string title, string content, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM phrases WHERE title=$title AND content=$content LIMIT 1;";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$content", content);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static string DbId(Guid id) => id.ToString("D");

    private static (string Display, string Normalized) NormalizeName(string value)
    {
        var display = string.Join(' ', value.Normalize(NormalizationForm.FormKC).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (display, display.ToUpperInvariant());
    }

    private static void Log(Guid traceId, string code, long started) =>
        Console.WriteLine($"话术包导入：TraceId={traceId:N}，结果码={code}，耗时={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms。");
}
