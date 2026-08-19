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
    private static Phrase MakePhrase(string title, string content, Guid categoryId, bool favorite = false, string colorKey = "default")
        => new(Guid.NewGuid(), title, content, categoryId, favorite, ShortcutMode.None, null,
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
        var p1 = MakePhrase("欢迎语", "您好，请问有什么可以帮您？", catId, favorite: true);
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

        Assert.Single(vm.Phrases);
        Assert.Equal("报价单", vm.Phrases[0].Title);
        Assert.Equal("“报价” 匹配 1 条", vm.StatusMessage);

        vm.SearchQuery = "您好";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Single(vm.Phrases);
        Assert.Equal("欢迎语", vm.Phrases[0].Title);
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
        var visible = vm.VisibleItems.OfType<PhraseItemViewModel>().ToArray();
        Assert.Single(visible);
        Assert.Equal("111", visible[0].Title);
        Assert.Equal(secondId, visible[0].CategoryId);
        Assert.DoesNotContain(vm.VisibleItems, item => item is SubHeaderItem);
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

        Assert.Equal("新结果", Assert.Single(vm.Phrases).Title);
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

        vm.SearchQuery = string.Empty;
        await WaitForAsync(() => vm.StatusMessage == "共 2 条话术");

        var visible = vm.VisibleItems.OfType<PhraseItemViewModel>().ToArray();
        Assert.Single(visible);
        Assert.Equal(firstPhrase.Id, visible[0].Id);
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
}

