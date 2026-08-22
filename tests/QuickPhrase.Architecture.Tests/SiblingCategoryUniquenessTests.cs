using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

/// <summary>
/// 覆盖分类同级唯一规则：同一父级下名称唯一，不同父级下允许复用名称。
/// </summary>
public sealed class SiblingCategoryUniquenessTests
{
    [Fact]
    public async Task CreateAllowsSameChildNameUnderDifferentParentsButRejectsSiblingDuplicate()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var parentA = await CreateCategoryAsync(runtime, "父级 A");
        var parentB = await CreateCategoryAsync(runtime, "父级 B");

        var childA = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "处理", parentA.Id));
        var childB = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "处理", parentB.Id));
        var duplicate = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "  处理  ", parentA.Id));

        Assert.True(childA.IsSuccess, childA.Error?.Message);
        Assert.True(childB.IsSuccess, childB.Error?.Message);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", duplicate.Error?.Code);
        Assert.Equal(2, (await runtime.Categories.ListAsync()).Count(category => category.Name == "处理"));
    }

    [Fact]
    public async Task RenameUsesCurrentParentForUniqueness()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var parentA = await CreateCategoryAsync(runtime, "重命名父级 A");
        var parentB = await CreateCategoryAsync(runtime, "重命名父级 B");
        var childA = await CreateCategoryAsync(runtime, "旧名称", parentA.Id);
        var siblingA = await CreateCategoryAsync(runtime, "已有名称", parentA.Id);
        var childB = await CreateCategoryAsync(runtime, "跨父级名称", parentB.Id);

        var crossParentRename = await runtime.Categories.RenameAsync(
            new RenameCategoryCommand(childA.Id, childA.Version, childB.Name, childA.SortOrder));
        var siblingRename = await runtime.Categories.RenameAsync(
            new RenameCategoryCommand(childA.Id, crossParentRename.Value!.Version, siblingA.Name, childA.SortOrder));

        Assert.True(crossParentRename.IsSuccess, crossParentRename.Error?.Message);
        Assert.Equal(childB.Name, crossParentRename.Value?.Name);
        Assert.False(siblingRename.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", siblingRename.Error?.Code);
    }

    [Fact]
    public void ImportPlanMatchesSameNamedChildrenByParent()
    {
        var localRootA = Category("本地父级 A", null);
        var localRootB = Category("本地父级 B", null);
        var localChildA = Category("处理", localRootA.Id);
        var localChildB = Category("处理", localRootB.Id);
        var packageRootA = PackageCategory("本地父级 A", null);
        var packageRootB = PackageCategory("本地父级 B", null);
        var packageChildA = PackageCategory("处理", packageRootA.Id);
        var packageChildB = PackageCategory("处理", packageRootB.Id);
        var package = Package(
            [packageRootA, packageRootB, packageChildA, packageChildB],
            []);

        var plan = PhrasePackagePlanner.BuildImportPlan(
            package,
            new PhrasePackageLocalSnapshot([localRootA, localRootB, localChildA, localChildB], []));

        var mappingA = plan.CategoryMappings.Single(mapping => mapping.PackageCategoryId == packageChildA.Id);
        var mappingB = plan.CategoryMappings.Single(mapping => mapping.PackageCategoryId == packageChildB.Id);
        Assert.False(mappingA.Create);
        Assert.False(mappingB.Create);
        Assert.Equal(localChildA.Id, mappingA.TargetCategoryId);
        Assert.Equal(localChildB.Id, mappingB.TargetCategoryId);
        Assert.Equal(localRootA.Id, mappingA.ParentTargetCategoryId);
        Assert.Equal(localRootB.Id, mappingB.ParentTargetCategoryId);
    }

    [Fact]
    public async Task ImportAllowsSameNamedChildrenUnderDifferentParents()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var packageRootA = PackageCategory("导入父级 A", null);
        var packageRootB = PackageCategory("导入父级 B", null);
        var packageChildA = PackageCategory("处理", packageRootA.Id);
        var packageChildB = PackageCategory("处理", packageRootB.Id);
        var package = Package(
            [packageRootA, packageRootB, packageChildA, packageChildB],
            [
                new PhrasePackagePhrase(Guid.NewGuid(), "父级 A 话术", PhraseBody.FromText("内容 A"), packageChildA.Id, 0),
                new PhrasePackagePhrase(Guid.NewGuid(), "父级 B 话术", PhraseBody.FromText("内容 B"), packageChildB.Id, 0),
            ]);

        var plan = PhrasePackagePlanner.BuildImportPlan(package, await runtime.CaptureSnapshotAsync());
        var result = await runtime.ImportAsync(plan);
        var categories = await runtime.Categories.ListAsync();
        var importedChildren = categories.Where(category => category.Name == "处理").ToArray();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(4, result.NewCategoryCount);
        Assert.Equal(2, result.NewPhraseCount);
        Assert.Equal(2, importedChildren.Length);
        Assert.NotEqual(importedChildren[0].ParentId, importedChildren[1].ParentId);
    }

    private static async Task<Category> CreateCategoryAsync(QuickPhraseDataRuntime runtime, string name, Guid? parentId = null)
    {
        var result = await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), name, parentId));
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    private static Category Category(string name, Guid? parentId) =>
        new(Guid.NewGuid(), parentId, name, 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static PhrasePackageCategory PackageCategory(string name, Guid? parentId) =>
        new(Guid.NewGuid(), name, parentId, 0);

    private static PhrasePackageDocument Package(
        IReadOnlyList<PhrasePackageCategory> categories,
        IReadOnlyList<PhrasePackagePhrase> phrases) =>
        new(
            new PhrasePackageManifest(
                PhrasePackageFormat.Format,
                PhrasePackageFormat.Version,
                Guid.NewGuid(),
                "同级唯一测试包",
                DateTimeOffset.UtcNow,
                phrases.Count,
                categories.Count,
                0),
            categories,
            phrases,
            []);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QuickPhrase-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
