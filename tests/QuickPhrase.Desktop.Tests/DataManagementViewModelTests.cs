using System;
using System.Linq;
using System.Threading.Tasks;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Tests.Fakes;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

public sealed class DataManagementViewModelTests
{
    [Fact]
    public async Task LoadImport_DefaultsAllCategoriesAndBuildsPlan()
    {
        var fake = new FakeCommandService();
        var root = Category("root", "客户", null, 0);
        var child = Category("child", "设备", root.Id, 0);
        fake.Seed([root, child]);
        var packageCategory = new PhrasePackageCategory(Guid.NewGuid(), "设备", null, 0);
        var package = Package([packageCategory], [new PhrasePackagePhrase(Guid.NewGuid(), "恢复", "正文", packageCategory.Id, 0)]);
        fake.NextPackageDocument = package;
        var vm = new DataManagementViewModel(fake);

        var import = await vm.LoadImportAsync("sample.qphrase");

        Assert.NotNull(import);
        Assert.All(import!.Categories, item => Assert.True(item.IsSelected));
        Assert.Equal(1, import.NewPhraseCount);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Import_RebuildPlanAfterCategoryDeselection_ExcludesItsPhrases()
    {
        var fake = new FakeCommandService();
        var first = new PhrasePackageCategory(Guid.NewGuid(), "第一类", null, 0);
        var second = new PhrasePackageCategory(Guid.NewGuid(), "第二类", null, 1);
        fake.NextPackageDocument = Package(
            [first, second],
            [new PhrasePackagePhrase(Guid.NewGuid(), "一", "正文一", first.Id, 0), new PhrasePackagePhrase(Guid.NewGuid(), "二", "正文二", second.Id, 0)]);
        var import = await new DataManagementViewModel(fake).LoadImportAsync("sample.qphrase");

        import!.Categories.Single(item => item.Category.Id == second.Id).IsSelected = false;
        await import.RebuildPlanAsync();

        Assert.Equal(1, import.NewPhraseCount);
        Assert.Single(import.Plan.PhraseDecisions, decision => decision.ShouldImport);
        Assert.DoesNotContain(import.Plan.SelectedCategoryIds, id => id == second.Id);
    }

    [Fact]
    public async Task Export_RejectsEmptySelection_AndUsesDefaultName()
    {
        var fake = new FakeCommandService();
        var category = Category("category", "客户", null, 0);
        fake.Seed([category]);
        var vm = await new DataManagementViewModel(fake).LoadExportAsync();

        Assert.NotNull(vm);
        Assert.Equal("我的话术包", vm!.Name);
        vm.Scope = PhrasePackageExportScope.Categories;
        var error = vm.ValidateSelection();

        Assert.NotNull(error);
        Assert.Contains("选择", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmImport_ReportsResultAndClearsBusyState()
    {
        var fake = new FakeCommandService
        {
            ImportResult = new PhrasePackageImportResult(true, 1, 2, 3, "PACKAGE_IMPORT_OK", "话术包导入完成。", Guid.NewGuid()),
        };
        var category = new PhrasePackageCategory(Guid.NewGuid(), "客户", null, 0);
        fake.NextPackageDocument = Package([category], []);
        var data = new DataManagementViewModel(fake);
        var import = await data.LoadImportAsync("sample.qphrase");

        var result = await data.ConfirmImportAsync(import!);

        Assert.NotNull(result);
        Assert.False(data.IsBusy);
        Assert.Equal(1, result!.NewCategoryCount);
        Assert.NotNull(fake.LastImportedPlan);
    }

    private static PhrasePackageDocument Package(PhrasePackageCategory[] categories, PhrasePackagePhrase[] phrases) =>
        new(new PhrasePackageManifest(PhrasePackageFormat.Format, 1, Guid.NewGuid(), "包", DateTimeOffset.UtcNow, phrases.Length, categories.Length), categories, phrases);

    private static Category Category(string key, string name, Guid? parentId, int sortOrder) =>
        new(GuidUtility(key), parentId, name, sortOrder, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static Guid GuidUtility(string key)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
