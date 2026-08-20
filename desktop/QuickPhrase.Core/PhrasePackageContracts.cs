using System.Text;

namespace QuickPhrase.Core;

/// <summary>话术包 V1 的固定格式标识和边界，确保导出的内容只包含可导入的话术数据。</summary>
public static class PhrasePackageFormat
{
    public const string Format = "QuickPhrase.PhrasePackage";
    public const int Version = 1;
    public const int MaxPhraseCount = 10_000;
    public const int MaxCategoryCount = 1_000;
    public const int MaxNameLength = 80;
    public const int MaxTitleLength = 80;
    public const int MaxContentLength = 4_000;
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
    IReadOnlySet<Guid> PhraseIds)
{
    /// <summary>允许调用方使用集合表达式构造选择集合，同时在契约内部保持去重集合语义。</summary>
    public PhrasePackageExportSelection(PhrasePackageExportScope scope, string name, IEnumerable<Guid> categoryIds, IEnumerable<Guid> phraseIds)
        : this(scope, name, categoryIds.ToHashSet(), phraseIds.ToHashSet())
    {
    }
}

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

/// <summary>导入预览的不可变结果。用户调整分类选择后，Desktop 应重新生成此计划再提交。</summary>
public sealed record PhrasePackageImportPlan(
    PhrasePackageDocument Package,
    IReadOnlySet<Guid> SelectedCategoryIds,
    IReadOnlySet<Guid> StructuralCategoryIds,
    IReadOnlyList<PhrasePackageCategoryMapping> CategoryMappings,
    IReadOnlyList<PhrasePackagePhraseDecision> PhraseDecisions,
    int NewCategoryCount,
    int NewPhraseCount,
    int SkippedDuplicateCount);

/// <summary>
/// 话术包领域规划器。这里仅处理格式、关系和导入闭包，不接触文件、数据库或 WPF 类型。
/// </summary>
public static class PhrasePackagePlanner
{
    /// <summary>验证清单、数量、字段边界以及分类树关系。</summary>
    public static IReadOnlyList<string> Validate(PhrasePackageDocument? document)
    {
        if (document is null) return ["话术包内容为空。"];

        var errors = new List<string>();
        var manifest = document.Manifest;
        if (document.Categories is null) errors.Add("话术包分类数据为空。");
        if (document.Phrases is null) errors.Add("话术包话术数据为空。");
        var categories = document.Categories ?? Array.Empty<PhrasePackageCategory>();
        var phrases = document.Phrases ?? Array.Empty<PhrasePackagePhrase>();

        if (manifest is null)
        {
            errors.Add("话术包清单为空。");
        }
        else
        {
            if (!string.Equals(manifest.Format, PhrasePackageFormat.Format, StringComparison.Ordinal))
                errors.Add("话术包格式不受支持。");
            if (manifest.FormatVersion != PhrasePackageFormat.Version)
                errors.Add("话术包版本不受支持。");
            if (manifest.PackageId == Guid.Empty)
                errors.Add("话术包标识无效。");
            if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Trim().Length > PhrasePackageFormat.MaxNameLength)
                errors.Add("话术包名称不能为空且不能超过 80 个字。");
            if (manifest.CreatedAt == default)
                errors.Add("话术包创建时间无效。");
            if (manifest.CategoryCount != categories.Count || manifest.PhraseCount != phrases.Count)
                errors.Add("话术包清单数量与数据不一致。");
        }

        if (categories.Count > PhrasePackageFormat.MaxCategoryCount)
            errors.Add("话术包分类数量超过 1000 个上限。");
        if (phrases.Count > PhrasePackageFormat.MaxPhraseCount)
            errors.Add("话术包话术数量超过 10000 条上限。");

        var categoryIds = categories.Select(x => x.Id).ToArray();
        var phraseIds = phrases.Select(x => x.Id).ToArray();
        AddDuplicateErrors(categoryIds, "分类", errors);
        AddDuplicateErrors(phraseIds, "话术", errors);
        if (categoryIds.Intersect(phraseIds).Any())
            errors.Add("分类和话术不能共用同一个包内标识。");

        var categoryById = categories
            .GroupBy(x => x.Id)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var category in categories)
        {
            if (category.Id == Guid.Empty) errors.Add("分类标识无效。");
            if (string.IsNullOrWhiteSpace(category.Name) || category.Name.Trim().Length > PhrasePackageFormat.MaxNameLength)
                errors.Add("分类名称不能为空且不能超过 80 个字。");
            if (category.SortOrder < 0)
                errors.Add("分类排序不能为负数。");
            if (category.ParentId == category.Id || (category.ParentId.HasValue && !categoryById.ContainsKey(category.ParentId.Value)))
                errors.Add("话术包包含非法的分类父级关系。");
        }

        if (categories
            .GroupBy(category => (category.ParentId, Name: NormalizeName(category.Name)))
            .Any(group => group.Count() > 1))
            errors.Add("话术包包含同一父分类下的重名分类。");

        foreach (var phrase in phrases)
        {
            if (phrase.Id == Guid.Empty) errors.Add("话术标识无效。");
            if (string.IsNullOrWhiteSpace(phrase.Title) || phrase.Title.Trim().Length > PhrasePackageFormat.MaxTitleLength)
                errors.Add("话术标题不能为空且不能超过 80 个字。");
            if (string.IsNullOrEmpty(phrase.Content) || phrase.Content.Length > PhrasePackageFormat.MaxContentLength)
                errors.Add("话术正文不能为空且不能超过 4000 个字。");
            if (phrase.SortOrder < 0)
                errors.Add("话术排序不能为负数。");
            if (!categoryById.ContainsKey(phrase.CategoryId))
                errors.Add("话术引用了不存在的分类。");
        }

        foreach (var category in categories)
        {
            var visited = new HashSet<Guid>();
            var cursor = category;
            while (cursor.ParentId.HasValue)
            {
                if (!visited.Add(cursor.Id) || !categoryById.TryGetValue(cursor.ParentId.Value, out cursor!))
                {
                    errors.Add("话术包包含环形的分类父级关系。");
                    break;
                }
            }
        }

        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>按范围生成一个文件对应的导出闭包，并用新的包内标识隔离本机主键。</summary>
    public static PhrasePackageDocument BuildExportDocument(
        PhrasePackageLocalSnapshot snapshot,
        PhrasePackageExportSelection selection,
        DateTimeOffset createdAtUtc,
        Guid? packageId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selection);

        var localCategories = snapshot.Categories ?? Array.Empty<Category>();
        var localPhrases = snapshot.Phrases ?? Array.Empty<Phrase>();
        var categories = localCategories.ToDictionary(x => x.Id);
        var phrases = localPhrases.ToDictionary(x => x.Id);
        IEnumerable<Guid> requestedCategoryIds = selection.CategoryIds ?? new HashSet<Guid>();
        IEnumerable<Guid> requestedPhraseIds = selection.PhraseIds ?? new HashSet<Guid>();
        var selectedCategoryIds = requestedCategoryIds.Where(categories.ContainsKey).ToHashSet();
        var selectedPhraseIds = requestedPhraseIds.Where(phrases.ContainsKey).ToHashSet();

        if (selection.Scope == PhrasePackageExportScope.All)
        {
            selectedCategoryIds = categories.Keys.ToHashSet();
            selectedPhraseIds = phrases.Keys.ToHashSet();
        }
        else if (selection.Scope == PhrasePackageExportScope.Categories)
        {
            selectedCategoryIds = categories.Values
                .Where(category => IsWithinSelectedCategory(category.Id, selectedCategoryIds, categories))
                .Select(category => category.Id)
                .ToHashSet();
            selectedPhraseIds = localPhrases
                .Where(phrase => selectedCategoryIds.Contains(phrase.CategoryId))
                .Select(phrase => phrase.Id)
                .ToHashSet();
        }
        else
        {
            selectedCategoryIds = selectedCategoryIds
                .Union(localPhrases.Where(phrase => selectedPhraseIds.Contains(phrase.Id)).Select(phrase => phrase.CategoryId))
                .ToHashSet();
        }

        var closure = AddAncestors(selectedCategoryIds, categories);
        var categoryMap = closure.ToDictionary(id => id, _ => Guid.NewGuid());
        var packageCategories = closure
            .Select(id => categories[id])
            .OrderBy(category => Depth(category.Id, categories))
            .ThenBy(category => category.SortOrder)
            .ThenBy(category => category.Name, StringComparer.Ordinal)
            .Select(category => new PhrasePackageCategory(
                categoryMap[category.Id],
                category.Name.Trim(),
                category.ParentId.HasValue ? categoryMap[category.ParentId.Value] : null,
                category.SortOrder))
            .ToArray();
        var packagePhrases = localPhrases
            .Where(phrase => selectedPhraseIds.Contains(phrase.Id))
            .OrderBy(phrase => phrase.SortOrder)
            .ThenBy(phrase => phrase.Title, StringComparer.Ordinal)
            .Select(phrase => new PhrasePackagePhrase(
                Guid.NewGuid(),
                phrase.Title.Trim(),
                phrase.Content,
                categoryMap[phrase.CategoryId],
                phrase.SortOrder))
            .ToArray();

        var manifest = new PhrasePackageManifest(
            PhrasePackageFormat.Format,
            PhrasePackageFormat.Version,
            packageId ?? Guid.NewGuid(),
            string.IsNullOrWhiteSpace(selection.Name) ? "话术包" : selection.Name.Trim(),
            createdAtUtc,
            packagePhrases.Length,
            packageCategories.Length);
        var document = new PhrasePackageDocument(manifest, packageCategories, packagePhrases);
        var errors = Validate(document);
        if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors), nameof(selection));
        return document;
    }

    /// <summary>根据本地快照生成导入预览；结构性祖先只用于补树，不带入祖先分类中的其他话术。</summary>
    public static PhrasePackageImportPlan BuildImportPlan(
        PhrasePackageDocument package,
        PhrasePackageLocalSnapshot local,
        IReadOnlySet<Guid>? selectedCategoryIds = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(local);
        var errors = Validate(package);
        if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors), nameof(package));

        var packageCategories = package.Categories.ToDictionary(x => x.Id);
        IEnumerable<Guid> requestedCategoryIds = selectedCategoryIds is null ? packageCategories.Keys.ToArray() : selectedCategoryIds;
        var selected = requestedCategoryIds
            .Where(packageCategories.ContainsKey)
            .ToHashSet();
        var structural = AddAncestors(selected, packageCategories);
        var localByName = local.Categories
            .GroupBy(category => (category.ParentId, Name: NormalizeName(category.Name)))
            .ToDictionary(group => group.Key, group => group.OrderBy(category => category.Id).First());
        var categoryMappings = new List<PhrasePackageCategoryMapping>();
        var targetByPackage = new Dictionary<Guid, Guid>();

        foreach (var category in package.Categories
                     .Where(category => structural.Contains(category.Id))
                     .OrderBy(category => Depth(category.Id, packageCategories))
                     .ThenBy(category => category.SortOrder)
                     .ThenBy(category => category.Name, StringComparer.Ordinal))
        {
            var parentTarget = category.ParentId.HasValue && targetByPackage.TryGetValue(category.ParentId.Value, out var parent)
                ? parent
                : (Guid?)null;
            var nameKey = NormalizeName(category.Name);
            var existing = localByName.TryGetValue((parentTarget, nameKey), out var localCategory) ? localCategory : null;
            var targetId = existing?.Id ?? Guid.NewGuid();
            targetByPackage[category.Id] = targetId;
            categoryMappings.Add(new PhrasePackageCategoryMapping(
                category.Id,
                targetId,
                existing?.Id,
                parentTarget,
                category.Name.Trim(),
                category.SortOrder,
                existing is null,
                !selected.Contains(category.Id)));
            if (existing is null)
                localByName[(parentTarget, nameKey)] = new Category(targetId, parentTarget, category.Name.Trim(), category.SortOrder, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }

        var localDuplicates = local.Phrases
            .GroupBy(phrase => (Title: phrase.Title.Trim(), phrase.Content), StringTupleComparer.Instance)
            .ToDictionary(group => group.Key, group => group.First(), StringTupleComparer.Instance);
        var seenPackagePhrases = new HashSet<(string Title, string Content)>(StringTupleComparer.Instance);
        var decisions = new List<PhrasePackagePhraseDecision>();
        foreach (var phrase in package.Phrases.Where(phrase => selected.Contains(phrase.CategoryId)))
        {
            var key = (phrase.Title.Trim(), phrase.Content);
            var duplicate = localDuplicates.TryGetValue(key, out var existing) || !seenPackagePhrases.Add(key);
            decisions.Add(new PhrasePackagePhraseDecision(
                phrase.Id,
                targetByPackage[phrase.CategoryId],
                !duplicate,
                duplicate,
                existing?.Id));
        }

        return new PhrasePackageImportPlan(
            package,
            selected,
            structural.Except(selected).ToHashSet(),
            categoryMappings,
            decisions,
            categoryMappings.Count(mapping => mapping.Create),
            decisions.Count(decision => decision.ShouldImport),
            decisions.Count(decision => decision.IsDuplicate));
    }

    private static void AddDuplicateErrors(IEnumerable<Guid> ids, string label, ICollection<string> errors)
    {
        var values = ids.ToArray();
        if (values.Any(id => id == Guid.Empty)) return;
        if (values.Length != values.Distinct().Count()) errors.Add($"话术包包含重复的{label}标识。");
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
        while (categories.TryGetValue(cursor, out var category) && category.ParentId.HasValue && visited.Add(cursor))
        {
            depth++;
            cursor = category.ParentId.Value;
        }
        return depth;
    }

    private static int Depth(Guid id, IReadOnlyDictionary<Guid, PhrasePackageCategory> categories)
    {
        var depth = 1;
        var cursor = id;
        var visited = new HashSet<Guid>();
        while (categories.TryGetValue(cursor, out var category) && category.ParentId.HasValue && visited.Add(cursor))
        {
            depth++;
            cursor = category.ParentId.Value;
        }
        return depth;
    }

    private static string NormalizeName(string value) =>
        string.Join(' ', value.Normalize(NormalizationForm.FormKC).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private sealed class StringTupleComparer : IEqualityComparer<(string Title, string Content)>
    {
        public static StringTupleComparer Instance { get; } = new();
        public bool Equals((string Title, string Content) x, (string Title, string Content) y) =>
            string.Equals(x.Title, y.Title, StringComparison.Ordinal) && string.Equals(x.Content, y.Content, StringComparison.Ordinal);
        public int GetHashCode((string Title, string Content) value) => HashCode.Combine(value.Title, value.Content);
    }
}
