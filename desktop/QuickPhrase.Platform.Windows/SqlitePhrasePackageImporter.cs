using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 图文话术包批量写入器。包内图片先通过 WindowsMediaAssetStore 规范化并取得新的本机 AssetId，
/// 随后分类、话术头和有序段在单写者连接的同一事务内提交；事务失败会补偿清理由本次导入创建且未被引用的媒体。
/// </summary>
internal sealed class SqlitePhrasePackageImporter
{
    private readonly SqliteWriteQueue _writes;
    private readonly TimeProvider _clock;
    private readonly WindowsMediaAssetStore _mediaAssets;

    public SqlitePhrasePackageImporter(SqliteWriteQueue writes, TimeProvider clock, WindowsMediaAssetStore mediaAssets)
    {
        _writes = writes; _clock = clock; _mediaAssets = mediaAssets;
    }

    public async Task<PhrasePackageImportResult> ImportAsync(PhrasePackageImportPlan plan, CancellationToken cancellationToken = default)
    {
        var requiredIds = plan.PhraseDecisions.Where(x => x.ShouldImport)
            .Select(x => plan.Package.Phrases.Single(p => p.Id == x.PackagePhraseId))
            .SelectMany(x => x.Body.Segments)
            .Where(x => x.Kind == PhraseSegmentKind.Image && x.Image is not null)
            .Select(x => x.Image!.AssetId).Distinct().ToArray();
        var packageMedia = plan.Package.Media.ToDictionary(x => x.Image.AssetId);
        var localMedia = new Dictionary<Guid, PhraseImageReference>();
        var created = new List<Guid>();
        try
        {
            foreach (var packageAssetId in requiredIds)
            {
                if (!packageMedia.TryGetValue(packageAssetId, out var media) || media.Content.Length == 0)
                {
                    foreach (var id in created) await _mediaAssets.DeleteIfUnreferencedAsync(id, CancellationToken.None);
                    return Failure("PACKAGE_MEDIA_REFERENCE_INVALID", "话术包图片引用不完整。");
                }
                var imported = await _mediaAssets.ImportPackageAsync(media.Content, ".png", cancellationToken);
                if (!imported.IsSuccess)
                {
                    foreach (var id in created) await _mediaAssets.DeleteIfUnreferencedAsync(id, CancellationToken.None);
                    return Failure(imported.ErrorCode ?? "PACKAGE_MEDIA_IMPORT_FAILED", imported.ErrorMessage ?? "话术包图片导入失败。");
                }
                localMedia.Add(packageAssetId, imported.Image!); created.Add(imported.Image!.AssetId);
            }

            var localizedPackage = plan.Package with
            {
                Phrases = plan.Package.Phrases.Select(p => p with { Body = ReplaceImages(p.Body, localMedia) }).ToArray(),
                Media = localMedia.Select(x => new PhrasePackageMedia(x.Value, [])).ToArray(),
            };
            var localizedPlan = plan with { Package = localizedPackage };
            var result = await _writes.EnqueueAsync((connection, ct) => ImportCoreAsync(connection, localizedPlan, ct), cancellationToken);
            if (!result.Succeeded)
                foreach (var id in created) await _mediaAssets.DeleteIfUnreferencedAsync(id, CancellationToken.None);
            return result;
        }
        catch
        {
            foreach (var id in created) await _mediaAssets.DeleteIfUnreferencedAsync(id, CancellationToken.None);
            throw;
        }
    }

    private async Task<PhrasePackageImportResult> ImportCoreAsync(SqliteConnection connection, PhrasePackageImportPlan plan, CancellationToken cancellationToken)
    {
        var traceId = Guid.NewGuid(); var started = Stopwatch.GetTimestamp();
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var phraseById = plan.Package.Phrases.ToDictionary(x => x.Id);
            var localCategoryIds = new Dictionary<Guid, Guid>();
            foreach (var mapping in plan.CategoryMappings)
            {
                localCategoryIds[mapping.PackageCategoryId] = mapping.TargetCategoryId;
                if (!mapping.Create) continue;
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO categories(id,parent_id,name,normalized_name,sort_order,version,created_at_utc,updated_at_utc) VALUES($id,$parent,$name,$normalized,$sort,1,$created,$updated);";
                var normalized = NormalizeName(mapping.Name); var now = _clock.GetUtcNow().ToString("O");
                command.Parameters.AddWithValue("$id", DbId(mapping.TargetCategoryId)); command.Parameters.AddWithValue("$parent", mapping.ParentTargetCategoryId.HasValue ? DbId(mapping.ParentTargetCategoryId.Value) : DBNull.Value);
                command.Parameters.AddWithValue("$name", normalized.Display); command.Parameters.AddWithValue("$normalized", normalized.Normalized); command.Parameters.AddWithValue("$sort", mapping.SortOrder);
                command.Parameters.AddWithValue("$created", now); command.Parameters.AddWithValue("$updated", now); await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var imported = 0; var skipped = 0;
            foreach (var decision in plan.PhraseDecisions)
            {
                if (!decision.ShouldImport) { skipped++; continue; }
                var phrase = phraseById[decision.PackagePhraseId]; var categoryId = localCategoryIds[phrase.CategoryId];
                if (await IsDuplicateAsync(connection, transaction, phrase.Title.Trim(), phrase.Body, cancellationToken)) { skipped++; continue; }
                var phraseId = Guid.NewGuid(); var now = _clock.GetUtcNow().ToString("O");
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO phrases(id,title,batch_separator,category_id,shortcut_mode,shortcut_display,shortcut_normalized,usage_count,version,created_at_utc,updated_at_utc,color_key,sort_order) VALUES($id,$title,$separator,$category,'None',NULL,NULL,0,1,$created,$updated,'default',$sort);";
                    command.Parameters.AddWithValue("$id", DbId(phraseId)); command.Parameters.AddWithValue("$title", phrase.Title.Trim()); command.Parameters.AddWithValue("$separator", phrase.Body.BatchSeparator);
                    command.Parameters.AddWithValue("$category", DbId(categoryId)); command.Parameters.AddWithValue("$created", now); command.Parameters.AddWithValue("$updated", now); command.Parameters.AddWithValue("$sort", phrase.SortOrder);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                for (var index = 0; index < phrase.Body.Segments.Length; index++)
                {
                    var segment = phrase.Body.Segments[index]; await using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "INSERT INTO phrase_segments(segment_id,phrase_id,segment_kind,text_content,media_asset_id,sort_order) VALUES($segment,$phrase,$kind,$text,$media,$sort);";
                    command.Parameters.AddWithValue("$segment", DbId(Guid.NewGuid())); command.Parameters.AddWithValue("$phrase", DbId(phraseId)); command.Parameters.AddWithValue("$kind", segment.Kind.ToString());
                    command.Parameters.AddWithValue("$text", segment.Text is null ? DBNull.Value : segment.Text); command.Parameters.AddWithValue("$media", segment.Image is null ? DBNull.Value : DbId(segment.Image.AssetId)); command.Parameters.AddWithValue("$sort", index);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                imported++;
            }
            await transaction.CommitAsync(cancellationToken); Log(traceId, "PACKAGE_IMPORT_OK", started);
            return new PhrasePackageImportResult(true, plan.CategoryMappings.Count(x => x.Create), imported, skipped, "PACKAGE_IMPORT_OK", "话术包导入完成。", traceId);
        }
        catch (OperationCanceledException) { await transaction.RollbackAsync(CancellationToken.None); Log(traceId, "PACKAGE_IMPORT_CANCELLED", started); throw; }
        catch (Exception ex)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            Console.Error.WriteLine($"话术包导入失败。TraceId={traceId:N}，阶段=DATABASE_TRANSACTION，结果码=PACKAGE_IMPORT_FAILED，异常类型={ex.GetType().Name}，耗时={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms。");
            return new PhrasePackageImportResult(false, 0, 0, 0, "PACKAGE_IMPORT_FAILED", "话术包导入失败，数据库未发生变更。", traceId);
        }
    }

    private static PhraseBody ReplaceImages(PhraseBody body, IReadOnlyDictionary<Guid, PhraseImageReference> map) =>
        new(body.Segments.Select(s => s.Kind == PhraseSegmentKind.Image && s.Image is not null && map.TryGetValue(s.Image.AssetId, out var image) ? s with { Image = image } : s).ToImmutableArray(), body.BatchSeparator);

    private static async Task<bool> IsDuplicateAsync(SqliteConnection connection, SqliteTransaction transaction, string title, PhraseBody body, CancellationToken cancellationToken)
    {
        if (!body.IsSingleText) return false;
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM phrases p JOIN phrase_segments ps ON ps.phrase_id=p.id WHERE p.title=$title AND p.batch_separator=$separator AND ps.sort_order=0 AND ps.segment_kind='Text' AND ps.text_content=$content AND NOT EXISTS(SELECT 1 FROM phrase_segments extra WHERE extra.phrase_id=p.id AND extra.sort_order<>0) LIMIT 1;";
        command.Parameters.AddWithValue("$title", title); command.Parameters.AddWithValue("$separator", body.BatchSeparator); command.Parameters.AddWithValue("$content", body.Segments[0].Text!);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static PhrasePackageImportResult Failure(string code, string message) => new(false, 0, 0, 0, code, message, Guid.NewGuid());
    private static string DbId(Guid id) => id.ToString("D");
    private static (string Display, string Normalized) NormalizeName(string value) { var display = string.Join(' ', value.Normalize(NormalizationForm.FormKC).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); return (display, display.ToUpperInvariant()); }
    private static void Log(Guid traceId, string code, long started) => Console.WriteLine($"话术包导入：TraceId={traceId:N}，结果码={code}，耗时={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms。");
}
