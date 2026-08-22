using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.Tests.Fakes;

namespace QuickPhrase.Desktop.Tests;

public class PhraseLibraryViewModelTests
{
    private static Phrase MakePhrase(string title, string content, Guid categoryId, string colorKey = "default")
        => new(Guid.NewGuid(), title, PhraseBody.FromText(content), categoryId, ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, colorKey);

    private static Category MakeCategory(out Guid id, string name = "工作", int sortOrder = 0)
    {
        id = Guid.NewGuid();
        return new Category(id, null, name, sortOrder, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(predicate(), "等待搜索结果超时。");
    }

    [Fact]
    public async Task LoadAsync_PopulatesPhrases_AndStatus()
    {
        var category = MakeCategory(out var catId);
        var p1 = MakePhrase("欢迎语", "您好，请问有什么可以帮您？", catId);
        var p2 = MakePhrase("结束语", "感谢您的咨询。", catId);
        var fake = new FakeCommandService();
        fake.Seed(new[] { p1, p2 });
        fake.Seed(new[] { category });

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();

        Assert.Equal(2, vm.Phrases.Count);
        Assert.Contains(vm.Phrases, p => p.Title == "欢迎语");
        Assert.Equal("共 2 条话术", vm.StatusMessage);
    }

    [Fact]
    public async Task LoadAsync_ShowsSubCategoryUnderExpandedTopCategory()
    {
        var topCategory = MakeCategory(out var topCategoryId, "客户", 0);
        var subCategory = new Category(Guid.NewGuid(), topCategoryId, "跟进", 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var fake = new FakeCommandService();
        fake.Seed(new[] { topCategory, subCategory });

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();

        var header = Assert.Single(vm.VisibleItems.OfType<SubHeaderItem>());
        Assert.Equal(subCategory.Id, header.Category.Id);
        Assert.Equal("跟进", header.Category.Name);
    }

    [Fact]
    public async Task ToggleSubCategory_CollapsesAndRestoresItsPhrases()
    {
        var topCategory = MakeCategory(out var topCategoryId, "客户", 0);
        var subCategory = new Category(Guid.NewGuid(), topCategoryId, "跟进", 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var phrase = MakePhrase("回访", "请问最近使用情况如何？", subCategory.Id);
        var fake = new FakeCommandService();
        fake.Seed(new[] { topCategory, subCategory });
        fake.Seed(new[] { phrase });

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();

        Assert.Contains(vm.VisibleItems.OfType<SubHeaderItem>(), item => item.Id == subCategory.Id);
        Assert.Contains(vm.VisibleItems.OfType<PhraseItemViewModel>(), item => item.Id == phrase.Id);

        vm.ToggleSubCategoryCommand.Execute(subCategory.Id);

        Assert.Contains(vm.VisibleItems.OfType<SubHeaderItem>(), item => item.Id == subCategory.Id);
        Assert.DoesNotContain(vm.VisibleItems.OfType<PhraseItemViewModel>(), item => item.Id == phrase.Id);

        vm.ToggleSubCategoryCommand.Execute(subCategory.Id);

        Assert.Contains(vm.VisibleItems.OfType<PhraseItemViewModel>(), item => item.Id == phrase.Id);
    }

    [Fact]
    public async Task Search_FiltersByTitleAndContent()
    {
        var category = MakeCategory(out var catId);
        var fake = new FakeCommandService();
        fake.Seed(new[]
        {
            MakePhrase("欢迎语", "您好", catId),
            MakePhrase("报价单", "价格如下", catId),
        });
        fake.Seed(new[] { category });

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();

        vm.SearchQuery = "报价";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Phrases.Count);
        var results = GetSearchResults(vm);
        Assert.Single(results);
        Assert.Equal("报价单", results[0].Title);
        Assert.Equal(1, GetSearchResultIndex(results[0]));
        Assert.Equal("工作", results[0].CategoryName);
        Assert.Equal("“报价” 匹配 1 条", vm.StatusMessage);

        vm.SearchQuery = "您好";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Phrases.Count);
        results = GetSearchResults(vm);
        Assert.Single(results);
        Assert.Equal("欢迎语", results[0].Title);
        Assert.Equal("“您好” 匹配 1 条", vm.StatusMessage);
    }

    [Fact]
    public async Task SearchQueryChanged_SearchesWithoutEnter()
    {
        var category = MakeCategory(out var catId);
        var fake = new FakeCommandService();
        fake.Seed(new[] { MakePhrase("报价单", "价格如下", catId) });
        fake.Seed(new[] { category });

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();
        vm.SearchQuery = "报价";

        await WaitForAsync(() => vm.StatusMessage == "“报价” 匹配 1 条");
        Assert.Single(GetSearchResults(vm));
        Assert.Single(vm.Phrases);
        Assert.Single(vm.VisibleItems.OfType<PhraseItemViewModel>());
        Assert.Equal("报价单", ((PhraseItemViewModel)vm.VisibleItems[0]).Title);
    }

    [Fact]
    public async Task Search_ShowsMatchFromAnotherCategory()
    {
        var firstCategory = MakeCategory(out var firstId, "设备问题", 0);
        var secondCategory = MakeCategory(out var secondId, "PRO系统", 1);
        var fake = new FakeCommandService();
        fake.Seed(new[]
        {
            MakePhrase("网络连接异常", "请检查网络", firstId),
            MakePhrase("111", "111", secondId),
        });
        fake.Seed(new[] { firstCategory, secondCategory });

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();
        vm.SearchQuery = "111";

        await WaitForAsync(() => vm.StatusMessage == "“111” 匹配 1 条");
        var results = GetSearchResults(vm);
        Assert.Single(results);
        Assert.Equal("111", results[0].Title);
        Assert.Equal(secondId, results[0].CategoryId);
        Assert.Equal("PRO系统", results[0].CategoryName);
        Assert.DoesNotContain(vm.VisibleItems.OfType<PhraseItemViewModel>(), item => item.Id == results[0].Id);
    }

    [Fact]
    public async Task Search_LatestQueryWinsWhenEarlierSearchCompletesLater()
    {
        var category = MakeCategory(out var catId);
        var fake = new FakeCommandService();
        fake.Seed(new[] { MakePhrase("旧结果", "旧", catId), MakePhrase("新结果", "新", catId) });
        fake.Seed(new[] { category });

        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.BeforeSearchAsync = async (query, _) =>
        {
            if (query != "旧") return;
            firstStarted.SetResult(true);
            await releaseFirst.Task;
        };
        fake.SearchCompleted += query =>
        {
            if (query == "旧") firstCompleted.TrySetResult(true);
        };

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();
        vm.SearchQuery = "旧";
        await firstStarted.Task;

        vm.SearchQuery = "新";
        await WaitForAsync(() => vm.StatusMessage == "“新” 匹配 1 条");
        releaseFirst.SetResult(true);
        await firstCompleted.Task;
        await Task.Delay(50);

        Assert.Equal(2, vm.Phrases.Count);
        Assert.Equal("新结果", Assert.Single(GetSearchResults(vm)).Title);
        Assert.Equal("“新” 匹配 1 条", vm.StatusMessage);
    }

    [Fact]
    public async Task Search_ClearingQueryRestoresSelectedCategoryView()
    {
        var firstCategory = MakeCategory(out var firstId, "设备问题", 0);
        var secondCategory = MakeCategory(out var secondId, "PRO系统", 1);
        var firstPhrase = MakePhrase("网络连接异常", "请检查网络", firstId);
        var secondPhrase = MakePhrase("111", "111", secondId);
        var fake = new FakeCommandService();
        fake.Seed(new[] { firstPhrase, secondPhrase });
        fake.Seed(new[] { firstCategory, secondCategory });

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();
        vm.SearchQuery = "111";
        await WaitForAsync(() => vm.StatusMessage == "“111” 匹配 1 条");

        Assert.Equal(2, vm.Phrases.Count);
        Assert.Single(GetSearchResults(vm));

        vm.SearchQuery = string.Empty;
        await WaitForAsync(() => vm.StatusMessage == "共 2 条话术");

        Assert.Empty(GetSearchResults(vm));
        var visible = vm.VisibleItems.OfType<PhraseItemViewModel>().ToArray();
        Assert.Single(visible);
        Assert.Equal(firstPhrase.Id, visible[0].Id);
    }
    [Fact]
    public async Task RefreshFromPhrase_WhenMovedToAnotherTopCategory_UpdatesVisibleItemsAndCategoryCounts()
    {
        var sourceCategory = MakeCategory(out var sourceCategoryId, "售前", 0);
        var targetCategory = MakeCategory(out var targetCategoryId, "售后", 1);
        var phrase = MakePhrase("欢迎语", "您好", sourceCategoryId);
        var fake = new FakeCommandService();
        fake.Seed(new[] { phrase });
        fake.Seed(new[] { sourceCategory, targetCategory });

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();

        var movedPhrase = phrase with
        {
            CategoryId = targetCategoryId,
            Version = phrase.Version + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        vm.RefreshMovedPhrase(movedPhrase);

        Assert.DoesNotContain(vm.VisibleItems.OfType<PhraseItemViewModel>(), item => item.Id == phrase.Id);
        Assert.Equal(targetCategoryId, Assert.Single(vm.Phrases).CategoryId);
        Assert.True(vm.IsEmpty);
        Assert.Equal(0, Assert.Single(vm.Categories, category => category.Id == sourceCategoryId).Count);
        Assert.Equal(1, Assert.Single(vm.Categories, category => category.Id == targetCategoryId).Count);
        Assert.Equal("已移动到“售后”", vm.StatusMessage);

        vm.SelectCategoryCommand.Execute(targetCategoryId);
        Assert.Equal(phrase.Id, Assert.Single(vm.VisibleItems.OfType<PhraseItemViewModel>()).Id);
    }

    [Fact]
    public async Task RefreshFromPhrase_NotifiesIsEmptyWhenNewPhraseBecomesVisible()
    {
        var category = MakeCategory(out var categoryId);
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();
        Assert.True(vm.IsEmpty);

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        var phrase = MakePhrase("新话术", "新内容", categoryId);
        vm.RefreshFromPhrase(phrase);

        Assert.Contains(vm.VisibleItems.OfType<PhraseItemViewModel>(), item => item.Id == phrase.Id);
        Assert.Contains(nameof(vm.IsEmpty), changedProperties);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task RefreshFromPhraseAsync_WhenSavedCategoryIsNew_LoadsCategoryAndShowsPhrase()
    {
        var fake = new FakeCommandService();
        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();
        Assert.Empty(vm.Categories);

        var category = MakeCategory(out var categoryId, "新分类");
        var phrase = MakePhrase("新话术", "新内容", categoryId);
        fake.Seed(new[] { category });
        fake.Seed(new[] { phrase });

        await vm.RefreshFromPhraseAsync(phrase);

        Assert.Contains(vm.Categories, item => item.Id == categoryId);
        Assert.Contains(vm.VisibleItems.OfType<PhraseItemViewModel>(), item => item.Id == phrase.Id);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task RefreshFromPhraseAsync_WhenSavedCategoryIsNew_PreservesViewStateAndRefreshesSearch()
    {
        var sourceCategory = MakeCategory(out var sourceCategoryId, "已有分类");
        var subCategory = new Category(Guid.NewGuid(), sourceCategoryId, "已折叠分类", 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var existingPhrase = MakePhrase("已有话术", "已有内容", subCategory.Id);
        var fake = new FakeCommandService();
        fake.Seed(new[] { sourceCategory, subCategory });
        fake.Seed(new[] { existingPhrase });

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();
        vm.SelectCategoryCommand.Execute(sourceCategoryId);
        vm.ToggleSubCategoryCommand.Execute(subCategory.Id);
        vm.SearchQuery = "新话术";
        await WaitForAsync(() => !vm.IsSearchBusy && vm.IsSearchResultEmpty);

        var newCategory = MakeCategory(out var newCategoryId, "新增分类", 1);
        var newPhrase = MakePhrase("新话术", "新内容", newCategoryId);
        fake.Seed(new[] { newCategory });
        fake.Seed(new[] { newPhrase });

        await vm.RefreshFromPhraseAsync(newPhrase);

        Assert.Equal(sourceCategoryId, vm.SelectedCategoryId);
        Assert.False(Assert.Single(vm.Categories, item => item.Id == subCategory.Id).IsExpanded);
        Assert.Contains(GetSearchResults(vm), item => item.Id == newPhrase.Id);
    }

    [Fact]
    public async Task RefreshMovedPhrase_PreservesSearchResultAndUpdatesCategoryName()
    {
        var sourceCategory = MakeCategory(out var sourceCategoryId, "售前", 0);
        var targetCategory = MakeCategory(out var targetCategoryId, "售后", 1);
        var phrase = MakePhrase("报价说明", "报价详情", sourceCategoryId);
        var fake = new FakeCommandService();
        fake.Seed(new[] { phrase });
        fake.Seed(new[] { sourceCategory, targetCategory });

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();
        vm.SearchQuery = "报价";
        await WaitForAsync(() => vm.StatusMessage == "“报价” 匹配 1 条");

        vm.RefreshMovedPhrase(phrase with
        {
            CategoryId = targetCategoryId,
            Version = phrase.Version + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        var visible = Assert.Single(GetSearchResults(vm));
        Assert.Equal(targetCategoryId, visible.CategoryId);
        Assert.Equal("售后", visible.CategoryName);
        Assert.Equal("已移动到“售后”", vm.StatusMessage);
    }
    [Fact]
    public async Task Delete_RemovesFromList_AndService()
    {
        var category = MakeCategory(out var catId);
        var p = MakePhrase("欢迎语", "您好", catId);
        var fake = new FakeCommandService();
        fake.Seed(new[] { p });
        fake.Seed(new[] { category });

        var vm = new PhraseLibraryViewModel(fake);
        await vm.LoadAsync();
        var item = vm.Phrases[0];
        await vm.DeleteCommand.ExecuteAsync(item);

        Assert.Empty(vm.Phrases);
        Assert.Equal("已删除", vm.StatusMessage);
        Assert.Null(await fake.GetPhraseAsync(p.Id));
    }

    private static IReadOnlyList<PhraseItemViewModel> GetSearchResults(PhraseLibraryViewModel vm)
    {
        var property = vm.GetType().GetProperty("SearchResults");
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<IReadOnlyList<PhraseItemViewModel>>(property!.GetValue(vm));
    }

    private static int GetSearchResultIndex(PhraseItemViewModel item)
    {
        var property = item.GetType().GetProperty("SearchResultIndex");
        Assert.NotNull(property);
        return Assert.IsType<int>(property!.GetValue(item));
    }
}
