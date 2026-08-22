using QuickPhrase.Core;

namespace QuickPhrase.Architecture.Tests;

public sealed class PhrasePackagePlannerTests
{
    [Fact]
    public void ValidateRejectsInvalidFormatCountsRelationsAndLimits()
    {
        var categoryId = Guid.NewGuid();
        var document = new PhrasePackageDocument(
            new PhrasePackageManifest("other", 2, Guid.NewGuid(), "包", default, 2, 1, 0),
            [
                new PhrasePackageCategory(categoryId, "根", categoryId, -1),
                new PhrasePackageCategory(categoryId, "重复", null, 0),
            ],
            [new PhrasePackagePhrase(Guid.NewGuid(), new string('标', 81), PhraseBody.FromText("正文"), Guid.NewGuid(), -1),
             new PhrasePackagePhrase(Guid.NewGuid(), "标题", PhraseBody.FromText(""), categoryId, 0)], []);

        var errors = PhrasePackagePlanner.Validate(document);

        Assert.Contains(errors, error => error.Contains("格式"));
        Assert.Contains(errors, error => error.Contains("版本"));
        Assert.Contains(errors, error => error.Contains("创建时间"));
        Assert.Contains(errors, error => error.Contains("重复的分类"));
        Assert.Contains(errors, error => error.Contains("排序"));
        Assert.Contains(errors, error => error.Contains("父级"));
        Assert.Contains(errors, error => error.Contains("标题"));
        Assert.Contains(errors, error => error.Contains("正文"));
    }

    [Fact]
    public void ValidateRejectsNullDataCollections()
    {
        var document = new PhrasePackageDocument(
            new PhrasePackageManifest(PhrasePackageFormat.Format, 1, Guid.NewGuid(), "包", DateTimeOffset.UtcNow, 0, 0, 0),
            null!,
            null!,
            null!);

        var errors = PhrasePackagePlanner.Validate(document);

        Assert.Contains(errors, error => error.Contains("分类数据"));
        Assert.Contains(errors, error => error.Contains("话术数据"));
        Assert.Contains(errors, error => error.Contains("媒体数据"));
    }
    [Fact]
    public void ExportByPhraseAddsCategoryAncestorsButOnlySelectedPhrases()
    {
        var root = Category("root", "客户", null, 0);
        var child = Category("child", "设备", root.Id, 1);
        var selected = Phrase("selected", "恢复", "请备份。", child.Id, 1);
        var omitted = Phrase("omitted", "不要导出", "正文", child.Id, 2);
        var snapshot = new PhrasePackageLocalSnapshot([root, child], [selected, omitted]);

        var document = PhrasePackagePlanner.BuildExportDocument(
            snapshot,
            new PhrasePackageExportSelection(PhrasePackageExportScope.Phrases, "设备包", [], [selected.Id]),
            DateTimeOffset.UtcNow);

        Assert.Equal(2, document.Categories.Count);
        Assert.Single(document.Phrases);
        Assert.Equal("恢复", document.Phrases[0].Title);
        Assert.NotEqual(root.Id, document.Categories.Single(x => x.Name == "客户").Id);
        Assert.NotEqual(child.Id, document.Phrases[0].CategoryId);
        Assert.Equal(document.Categories.Single(x => x.Name == "设备").Id, document.Phrases[0].CategoryId);
    }

    [Fact]
    public void ExportByCategoryIncludesDescendantsAndAllTheirPhrasesInOneDocument()
    {
        var root = Category("root", "客户", null, 0);
        var child = Category("child", "设备", root.Id, 1);
        var phrase = Phrase("phrase", "恢复", "正文", child.Id, 0);

        var document = PhrasePackagePlanner.BuildExportDocument(
            new PhrasePackageLocalSnapshot([root, child], [phrase]),
            new PhrasePackageExportSelection(PhrasePackageExportScope.Categories, "分类包", [root.Id], []),
            DateTimeOffset.UtcNow);

        Assert.Equal(2, document.Categories.Count);
        Assert.Single(document.Phrases);
        Assert.Equal(document.Categories.Single(x => x.Name == "设备").Id, document.Phrases[0].CategoryId);
    }


    [Fact]
    public void ExportAllExcludesEnterprisePhrasesAndEnterpriseCategoriesIncludingEmptyOnes()
    {
        var personalCategory = Category("personal", "个人分类", null, 0);
        var enterpriseCategory = Category("enterprise", "敏感企业分类", null, 1) with { Scope = PhraseScope.Enterprise };
        var personalPhrase = Phrase("personal-phrase", "个人话术", "正文", personalCategory.Id, 0);
        var enterprisePhrase = Phrase("enterprise-phrase", "企业话术", "企业正文", enterpriseCategory.Id, 0) with { Scope = PhraseScope.Enterprise };

        var document = PhrasePackagePlanner.BuildExportDocument(
            new PhrasePackageLocalSnapshot([personalCategory, enterpriseCategory], [personalPhrase, enterprisePhrase]),
            new PhrasePackageExportSelection(PhrasePackageExportScope.All, "个人导出", [], []),
            DateTimeOffset.UtcNow);

        Assert.Single(document.Categories);
        Assert.Equal("个人分类", document.Categories[0].Name);
        Assert.DoesNotContain(document.Categories, category => category.Name == "敏感企业分类");
        Assert.Single(document.Phrases);
        Assert.Equal("个人话术", document.Phrases[0].Title);
    }

    [Fact]
    public void ImportPlanReusesNormalizedCategoriesSeparatesAncestorsAndDeduplicatesPackagePhrases()
    {
        var root = Category("local-root", "客户", null, 9);
        var local = new PhrasePackageLocalSnapshot([root], [Phrase("local", " 已有 ", "同正文", root.Id, 0)]);
        var packageRootId = Guid.NewGuid();
        var packageChildId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var package = new PhrasePackageDocument(
            new PhrasePackageManifest(PhrasePackageFormat.Format, 1, Guid.NewGuid(), "包", DateTimeOffset.UtcNow, 2, 2, 0),
            [new PhrasePackageCategory(packageRootId, " 客户 ", null, 1), new PhrasePackageCategory(packageChildId, "设备", packageRootId, 2)],
            [new PhrasePackagePhrase(firstId, "新话术", PhraseBody.FromText("正文"), packageChildId, 0), new PhrasePackagePhrase(secondId, "新话术", PhraseBody.FromText("正文"), packageChildId, 1)], []);

        var plan = PhrasePackagePlanner.BuildImportPlan(package, local, new HashSet<Guid> { packageChildId });

        Assert.Equal(new[] { packageChildId }, plan.SelectedCategoryIds);
        Assert.Contains(packageRootId, plan.StructuralCategoryIds);
        Assert.Equal(2, plan.CategoryMappings.Count);
        Assert.False(plan.CategoryMappings.Single(x => x.PackageCategoryId == packageRootId).Create);
        Assert.True(plan.CategoryMappings.Single(x => x.PackageCategoryId == packageRootId).StructuralAncestor);
        Assert.Equal(1, plan.NewPhraseCount);
        Assert.Equal(1, plan.SkippedDuplicateCount);
        Assert.Equal(2, plan.PhraseDecisions.Count);
        Assert.Contains(plan.PhraseDecisions, decision => decision.PackagePhraseId == secondId && decision.IsDuplicate);
    }

    [Fact]
    public void ImportPlanKeepsSameTitleWithDifferentContent()
    {
        var category = Category("local", "客户", null, 0);
        var packageCategory = new PhrasePackageCategory(Guid.NewGuid(), "客户", null, 0);
        var package = new PhrasePackageDocument(
            new PhrasePackageManifest(PhrasePackageFormat.Format, 1, Guid.NewGuid(), "包", DateTimeOffset.UtcNow, 2, 1, 0),
            [packageCategory],
            [new PhrasePackagePhrase(Guid.NewGuid(), "同标题", PhraseBody.FromText("正文一"), packageCategory.Id, 0), new PhrasePackagePhrase(Guid.NewGuid(), "同标题", PhraseBody.FromText("正文二"), packageCategory.Id, 1)], []);

        var plan = PhrasePackagePlanner.BuildImportPlan(package, new PhrasePackageLocalSnapshot([category], []));

        Assert.Equal(2, plan.NewPhraseCount);
        Assert.Equal(0, plan.SkippedDuplicateCount);
    }

    private static Category Category(string key, string name, Guid? parentId, int sortOrder) =>
        new(StableId(key), parentId, name, sortOrder, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static Phrase Phrase(string key, string title, string content, Guid categoryId, int sortOrder) =>
        new(StableId(key), title, PhraseBody.FromText(content), categoryId, ShortcutMode.None, null, 0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "default", sortOrder);

    private static Guid StableId(string key) => GuidUtility.Create(Guid.Empty, key);

    private static class GuidUtility
    {
        public static Guid Create(Guid namespaceId, string name)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(namespaceId + name));
            return new Guid(bytes.AsSpan(0, 16));
        }
    }
}
