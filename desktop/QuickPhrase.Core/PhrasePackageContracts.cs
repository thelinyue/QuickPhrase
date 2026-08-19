using System.Collections.Immutable;
using System.Text;

namespace QuickPhrase.Core;

/// <summary>话术包 V1 的固定格式标识与安全上限，避免把本机数据库细节带出应用。</summary>
public static class PhrasePackageFormat
{
    public const string Format = "QuickPhrase.PhrasePackage";
    public const int Version = 1;
    public const int MaxPhraseCount = 10_000;
    public const int MaxCategoryCount = 1_000;
    public const int MaxNameLength = 80;
}

public sealed record PhrasePackageManifest(
    string Format,
    int FormatVersion,
    Guid PackageId,
    string Name,
    DateTimeOffset CreatedAt,
    int PhraseCount,
    int CategoryCount);

public sealed record PhrasePackageCategory(Guid Id, string Name, Guid? ParentId, int SortOrder);

public sealed record PhrasePackagePhrase(Guid Id, string Title, string Content, Guid CategoryId, int SortOrder);

public sealed record PhrasePackageDocument(
    PhrasePackageManifest Manifest,
    IReadOnlyList<PhrasePackageCategory> Categories,
    IReadOnlyList<PhrasePackagePhrase> Phrases);

public enum PhrasePackageExportScope
{
    All,
    Categories,
    Phrases,
}

public sealed record PhrasePackageExportSelection(
    PhrasePackageExportScope Scope,
    string Name,
    IReadOnlySet<Guid> CategoryIds,
    IReadOnlySet<Guid> PhraseIds);

public sealed record PhrasePackageLocalSnapshot(
    IReadOnlyList<Category> Categories,
    IReadOnlyList<Phrase> Phrases);

public sealed record PhrasePackageCategoryMapping(
    Guid PackageCategoryId,
    Guid TargetCategoryId,
    Guid? ExistingCategoryId,
    Guid? ParentTargetCategoryId,
    string Name,
    int SortOrder,
    bool Create,
    bool StructuralAncestor);

public sealed record PhrasePackagePhraseDecision(
    Guid PackagePhraseId,
    Guid TargetCategoryId,
    bool ShouldImport,
    bool IsDuplicate,
    Guid? ExistingPhraseId);

/// <summary>导入预览的不可变结果。实际写库前 Desktop 只修改 SelectedCategoryIds 并重新规划。</summary>
public sealed record PhrasePackageImportPlan(
    PhrasePackageDocument Package,
    IReadOnlySet<Guid> SelectedCategoryIds,
    IReadOnlySet<Guid> StructuralCategoryIds,
    IReadOnlyList<PhrasePackageCategoryMapping> CategoryMappings,
    IReadOnlyList<PhrasePackagePhraseDecision> PhraseDecisions,
    int NewCategoryCount,
    int NewPhraseCount,
    int SkippedDuplicateCount);

public static class PhrasePackagePlanner
{
    /// <summary>验证包的结构、关系和业务字段；不依赖文件系统或数据库。</summary>
    public static IReadOnlyList<string> Validate(PhrasePackageDocument document)
    {
        var errors = new List<string>();
        if (document is null) return ["话术包内容为空。"];
        var manifest = document.Manifest;
        if (manifest.Format != PhrasePackageFormat.Format) errors.Add("话术包格式不受支持。");
        if (manifest.FormatVersion != PhrasePackageFormat.Version) errors.Add("话术包版本不受支持。");
        if (manifest.PackageId == Guid.Empty) errors.Add("话术包标识无效。");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Trim().Length > PhrasePackageFormat.MaxNameLength) errors.Add("话术包名称不能为空且不能超过 80 个字。");
        if (document.Categories.Count > PhrasePackageFormat.MaxCategoryCount) errors.Add("话术包分类数量超过 1000 个上限。");
        if (document.Phrases.Count > PhrasePackageFormat.MaxPhraseCount) errors.Add("话术包话术数量超过 10000 条上限。");
        if (manifest.CategoryCount != document.Categories.Count || manifest.PhraseCount != document.Phrases.Count) errors.Add("话术包清单数量与数据不一致。");

        var categoryIds = document.Categories.Select(x => x.Id).ToArray();
        var phraseIds = document.Phrases.Select(x => x.Id).ToArray();
        AddDuplicateErrors(categoryIds, "分类");
        AddDuplicateErrors(phraseIds, "话术");
        if (categoryIds.Intersect(phraseIds).Any()) errors.Add("分类和话术不能共用同一个包内标识。");

        var categories = document.Categories.ToDictionary(x => x.Id);
        foreach (var category in document.Categories)
        {
            if (category.Id == Guid.Empty) errors.Add("分类标识无效。");
            if (string.IsNullOrWhiteSpace(category.Name) || category.Name.Trim().Length > PhrasePackageFormat.MaxNameLength) errors.Add("分类名称不能为空且不能超过 80 个字。");
            if (category.ParentId == category.Id || (category.ParentId.HasValue && !categories.ContainsKey(category.ParentId.Value))) errors.Add("话术包包含非法的分类父级关系。");
        }
        foreach (var phrase in document.Phrases)
        {
            if (phrase.Id == Guid.Empty) errors.Add("话术标识无效。");
            if (string.IsNullOrWhiteSpace(phrase.Title) || phrase.Title.Trim().Length > 80) errors.Add("话术标题不能为空且不能超过 80 个字。");
            if (string.IsNullOrEmpty(phrase.Content) || phrase.Content.Length > 4000) errors.Add("话术正文不能为空且不能超过 4000 个字。");
            if (!categories.ContainsKey(phrase.CategoryId)) errors.Add("话术引用了不存在的分类。");
        }
        foreach (var category in document.Categories)
        {
            var seen = new HashSet<Guid>();
            var cursor = category;
            while (cursor.ParentId.HasValue)
            {
                if (!seen.Add(cursor.Id) || !categories.TryGetValue(cursor.ParentId.Value, out cursor!))
                {
                    errors.Add("话术包包含环形的分类父级关系。");
                    break;
                }
            }
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();

        void AddDuplicateErrors(IEnumerable<Guid> ids, string label)
        {
            if (ids.Any(id => id == Guid.Empty)) return;
            if (ids.Count() != ids.Distinct().Count()) errors.Add($"话术包包含重复的{label}标识。");
        }
    }

    /// <summary>按范围生成导出闭包，并为包内对象重新分配 Guid，避免暴露本机数据库标识。</summary>
    public static PhrasePackageDocument BuildExportDocument(PhrasePackageLocalSnapshot snapshot, PhrasePackageExportSelection selection, DateTimeOffset createdAtUtc, Guid? packageId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selection);
        var categories = snapshot.Categories.ToDictionary(x => x.Id);
        var phrases = snapshot.Phrases.ToDictionary(x => x.Id);
        var selectedCategoryIds = selection.CategoryIds.Where(categories.ContainsKey).ToHashSet();
        var selectedPhraseIds = selection.PhraseIds.Where(phrases.ContainsKey).ToHashSet();
        if (selection.Scope == PhrasePackageExportScope.All)
        {
            selectedCategoryIds = categories.Keys.ToHashSet();
            selectedPhraseIds = phrases.Keys.ToHashSet();
        }
        else if (selection.Scope == PhrasePackageExportScope.Categories)
        {
            var descendants = categories.Values.Where(x => IsWithinSelectedCategory(x.Id, selectedCategoryIds, categories)).Select(x => x.Id);
            selectedCategoryIds = descendants.ToHashSet();
            selectedPhraseIds = snapshot.Phrases.Where(x => selectedCategoryIds.Contains(x.CategoryId)).Select(x => x.Id).ToHashSet();
        }

        // 话术范围只导出选中的话术，但必须带上这些话术所属分类及其祖先，确保导入后树关系完整。
        if (selection.Scope == PhrasePackageExportScope.Phrases)
            selectedCategoryIds = selectedCategoryIds.Union(snapshot.Phrases.Where(x => selectedPhraseIds.Contains(x.Id)).Select(x => x.CategoryId)).ToHashSet();

        var closure = AddAncestors(selectedCategoryIds, categories);
        var categoryMap = closure.ToDictionary(id => id, _ => Guid.NewGuid());
        var packageCategories = closure
            .Select(id => categories[id])
            .OrderBy(x => Depth(x.Id, categories))
            .ThenBy(x => x.SortOrder)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new PhrasePackageCategory(categoryMap[x.Id], x.Name, x.ParentId.HasValue ? categoryMap[x.ParentId.Value] : null, x.SortOrder))
            .ToArray();
        var packagePhrases = snapshot.Phrases
            .Where(x => selectedPhraseIds.Contains(x.Id))
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Title, StringComparer.Ordinal)
            .Select(x => new PhrasePackagePhrase(Guid.NewGuid(), x.Title, x.Content, categoryMap[x.CategoryId], x.SortOrder))
            .ToArray();
        var manifest = new PhrasePackageManifest(PhrasePackageFormat.Format, PhrasePackageFormat.Version, packageId ?? Guid.NewGuid(), string.IsNullOrWhiteSpace(selection.Name) ? "话术包" : selection.Name.Trim(), createdAtUtc, packagePhrases.Length, packageCategories.Length);
        var document = new PhrasePackageDocument(manifest, packageCategories, packagePhrases);
        var errors = Validate(document);
        if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors), nameof(selection));
        return document;
    }

    /// <summary>根据本地快照生成导入预览；结构性祖先分类只用于维持树结构，不自动带入祖先话术。</summary>
    public static PhrasePackageImportPlan BuildImportPlan(PhrasePackageDocument package, PhrasePackageLocalSnapshot local, IReadOnlySet<Guid>? selectedCategoryIds = null)
    {
        var errors = Validate(package);
        if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors), nameof(package));
        var packageCategories = package.Categories.ToDictionary(x => x.Id);
        var localCategories = local.Categories.ToArray();
        var localByName = localCategories.GroupBy(x => NormalizeName(x.Name), StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.OrderBy(c => c.Id).First(), StringComparer.Ordinal);
        var selected = (selectedCategoryIds is null ? packageCategories.Keys : selectedCategoryIds.Where(packageCategories.ContainsKey)).ToHashSet();
        var structural = AddAncestors(selected, packageCategories);
        var categoryMappings = new List<PhrasePackageCategoryMapping>();
        var targetByPackage = new Dictionary<Guid, Guid>();
        foreach (var category in package.Categories.OrderBy(x => Depth(x.Id, packageCategories)).ThenBy(x => x.SortOrder))
        {
            if (!structural.Contains(category.Id)) continue;
            var nameKey = NormalizeName(category.Name);
            var existing = localByName.TryGetValue(nameKey, out var localCategory) ? localCategory : null;
            Guid targetId;
            if (existing is not null) targetId = existing.Id;
            else targetId = Guid.NewGuid();
            targetByPackage[category.Id] = targetId;
            Guid? parentTarget = category.ParentId.HasValue && targetByPackage.TryGetValue(category.ParentId.Value, out var parent) ? parent : null;
            categoryMappings.Add(new PhrasePackageCategoryMapping(category.Id, targetId, existing?.Id, parentTarget, category.Name.Trim(), category.SortOrder, existing is null, !selected.Contains(category.Id)));
            if (existing is null) localByName[nameKey] = new Category(targetId, parentTarget, category.Name.Trim(), category.SortOrder, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }
        var localDuplicates = local.Phrases.GroupBy(x => (x.Title.Trim(), x.Content), StringTupleComparer.Instance).ToDictionary(x => x.Key, x => x.First());
        var decisions = package.Phrases
            .Where(x => selected.Contains(x.CategoryId))
            .Select(x =>
            {
                var duplicate = localDuplicates.TryGetValue((x.Title.Trim(), x.Content), out var existing);
                return new PhrasePackagePhraseDecision(x.Id, targetByPackage[x.CategoryId], !duplicate, duplicate, existing?.Id);
            }).ToArray();
        return new PhrasePackageImportPlan(package, selected, structural.Except(selected).ToHashSet(), categoryMappings, decisions, categoryMappings.Count(x => x.Create), decisions.Count(x => x.ShouldImport), decisions.Count(x => x.IsDuplicate));
    }

    private static HashSet<Guid> AddAncestors(IEnumerable<Guid> selected, IReadOnlyDictionary<Guid, Category> categories)
    {
        var result = selected.ToHashSet();
        foreach (var id in selected.ToArray())
        {
            var cursor = id;
            var visited = new HashSet<Guid>();
            while (categories.TryGetValue(cursor, out var category) && category.ParentId.HasValue && visited.Add(cursor))
            {
                result.Add(category.ParentId.Value);
                cursor = category.ParentId.Value;
            }
        }
        return result;
    }
    private static HashSet<Guid> AddAncestors(IEnumerable<Guid> selected, IReadOnlyDictionary<Guid, PhrasePackageCategory> categories)
    {
        var result = selected.ToHashSet();
        foreach (var id in selected.ToArray())
        {
            var cursor = id;
            var visited = new HashSet<Guid>();
            while (categories.TryGetValue(cursor, out var category) && category.ParentId.HasValue && visited.Add(cursor))
            {
                result.Add(category.ParentId.Value);
                cursor = category.ParentId.Value;
            }
        }
        return result;
    }

    private static bool IsWithinSelectedCategory(Guid id, IReadOnlySet<Guid> selected, IReadOnlyDictionary<Guid, Category> categories)
    {
        var cursor = id;
        var visited = new HashSet<Guid>();
        while (visited.Add(cursor) && categories.TryGetValue(cursor, out var category))
        {
            if (selected.Contains(cursor)) return true;
            if (!category.ParentId.HasValue) return false;
            cursor = category.ParentId.Value;
        }
        return false;
    }

    private static int Depth(Guid id, IReadOnlyDictionary<Guid, Category> categories)
    {
        var depth = 1;
        var cursor = id;
        var visited = new HashSet<Guid>();
        while (categories.TryGetValue(cursor, out var category) && category.ParentId.HasValue && visited.Add(cursor)) { depth++; cursor = category.ParentId.Value; }
        return depth;
    }

    private static int Depth(Guid id, IReadOnlyDictionary<Guid, PhrasePackageCategory> categories)
    {
        var depth = 1;
        var cursor = id;
        var visited = new HashSet<Guid>();
        while (categories.TryGetValue(cursor, out var category) && category.ParentId.HasValue && visited.Add(cursor)) { depth++; cursor = category.ParentId.Value; }
        return depth;
    }

    private static string NormalizeName(string value) => string.Join(' ', value.Normalize(NormalizationForm.FormKC).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private sealed class StringTupleComparer : IEqualityComparer<(string Title, string Content)>
    {
        public static StringTupleComparer Instance { get; } = new();
        public bool Equals((string Title, string Content) x, (string Title, string Content) y) => string.Equals(x.Title, y.Title, StringComparison.Ordinal) && string.Equals(x.Content, y.Content, StringComparison.Ordinal);
        public int GetHashCode((string Title, string Content) value) => HashCode.Combine(value.Title, value.Content);
    }
}

