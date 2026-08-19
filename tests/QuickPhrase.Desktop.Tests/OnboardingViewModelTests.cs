using QuickPhrase.Core;
using QuickPhrase.Desktop.Onboarding;
using QuickPhrase.Desktop.Tests.Fakes;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 首次使用向导状态恢复与提交行为的回归测试。
/// 测试只使用进程内命令替身，确保向导不会依赖 Windows 平台实现或外部应用。
/// </summary>
public sealed class OnboardingViewModelTests
{
    [Fact]
    public async Task EmptyData_StartsAtWelcome_ThenMovesToCategory()
    {
        var fake = new FakeCommandService();
        var vm = CreateViewModel(fake);

        await vm.InitializeAsync();

        Assert.Equal(OnboardingStep.Welcome, vm.CurrentStep);
        vm.StartCommand.Execute(null);
        Assert.Equal(OnboardingStep.Category, vm.CurrentStep);
    }

    [Fact]
    public async Task AutomaticRestore_WithCategoryButNoPhrase_SkipsWelcome()
    {
        var category = RootCategory("客户沟通");
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });
        var vm = CreateViewModel(fake);

        await vm.InitializeAsync();

        Assert.Equal(OnboardingStep.Phrase, vm.CurrentStep);
        Assert.Equal(category.Id, vm.SelectedCategory!.Id);
    }

    [Fact]
    public async Task AutomaticRestore_WithCategoryAndPhrase_StartsPractice()
    {
        var category = RootCategory("客户沟通");
        var phrase = new Phrase(Guid.NewGuid(), "欢迎语", "您好", category.Id, false, ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });
        fake.Seed(new[] { phrase });
        var vm = CreateViewModel(fake);

        await vm.InitializeAsync();

        Assert.Equal(OnboardingStep.Practice, vm.CurrentStep);
        Assert.Equal(category.Id, vm.SelectedCategory!.Id);
    }
    [Fact]
    public async Task ManualOpen_WithCategoryButNoPhrase_MovesToPhrase()
    {
        var category = RootCategory("客户沟通");
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });
        var vm = CreateViewModel(fake);

        await vm.InitializeAsync(manualOpen: true);

        Assert.Equal(OnboardingStep.Phrase, vm.CurrentStep);
        Assert.Equal(category.Id, vm.SelectedCategory!.Id);
    }

    [Fact]
    public async Task ManualOpen_WithCategoryAndPhrase_MovesToPractice()
    {
        var category = RootCategory("客户沟通");
        var phrase = new Phrase(Guid.NewGuid(), "欢迎语", "您好", category.Id, false, ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });
        fake.Seed(new[] { phrase });
        var vm = CreateViewModel(fake);

        await vm.InitializeAsync(manualOpen: true);

        Assert.Equal(OnboardingStep.Practice, vm.CurrentStep);
        Assert.Equal(category.Id, vm.SelectedCategory!.Id);
    }

    [Fact]
    public async Task Skip_DoesNotCreateBusinessData_ButMarksOnboardingHandled()
    {
        var fake = new FakeCommandService();
        var vm = CreateViewModel(fake);

        await vm.InitializeAsync();
        await vm.SkipCommand.ExecuteAsync(null);

        Assert.Empty(await fake.ListCategoriesAsync());
        Assert.Empty(await fake.ListPhrasesAsync());
        Assert.Equal(OnboardingStep.Complete, vm.CurrentStep);
        Assert.True((await fake.GetSettingsAsync()).HasCompletedOnboarding);
        Assert.Equal(OnboardingViewModel.CurrentOnboardingVersion, (await fake.GetSettingsAsync()).OnboardingVersion);
    }

    [Fact]
    public async Task CompletePage_PersistsStartupChoiceBeforeClosing()
    {
        var category = RootCategory("客户沟通");
        var phrase = new Phrase(Guid.NewGuid(), "欢迎语", "您好", category.Id, false, ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });
        fake.Seed(new[] { phrase });
        var vm = CreateViewModel(fake);
        var completed = false;
        vm.Completed += () => completed = true;

        await vm.InitializeAsync(manualOpen: true);
        vm.MarkPracticeInserted("您好");
        await vm.FinishCommand.ExecuteAsync(null);
        vm.LaunchOnStartup = true;
        await vm.CloseCompleteCommand.ExecuteAsync(null);

        Assert.True(completed);
        Assert.True((await fake.GetSettingsAsync()).LaunchOnStartup);
    }

    [Fact]
    public async Task Finish_RequiresPracticeInsertion_ThenPersistsVersionOne()
    {
        var fake = new FakeCommandService();
        var vm = CreateViewModel(fake);
        await vm.InitializeAsync(manualOpen: true);

        Assert.False(vm.CanFinish);
        vm.MarkPracticeInserted("练习结果");
        Assert.True(vm.CanFinish);

        await vm.FinishCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingStep.Complete, vm.CurrentStep);
        var settings = await fake.GetSettingsAsync();
        Assert.True(settings.HasCompletedOnboarding);
        Assert.Equal(1, settings.OnboardingVersion);
    }


    [Fact]
    public async Task ApplySettingsSnapshot_OnComplete_PreservesPendingStartupChoice()
    {
        var fake = new FakeCommandService();
        var vm = CreateViewModel(fake);
        await vm.InitializeAsync(manualOpen: true);
        vm.MarkPracticeInserted("练习结果");
        await vm.FinishCommand.ExecuteAsync(null);
        vm.LaunchOnStartup = true;

        vm.ApplySettingsSnapshot((await fake.GetSettingsAsync()) with { LaunchOnStartup = false });

        Assert.True(vm.LaunchOnStartup);
    }

    [Fact]
    public async Task Finish_ReloadsAndRetriesAfterSettingsVersionConflict()
    {
        var fake = new FakeCommandService { ReturnSettingsConflictOnce = true };
        var vm = CreateViewModel(fake);
        await vm.InitializeAsync(manualOpen: true);
        vm.MarkPracticeInserted("练习结果");

        await vm.FinishCommand.ExecuteAsync(null);

        var settings = await fake.GetSettingsAsync();
        Assert.Equal(OnboardingStep.Complete, vm.CurrentStep);
        Assert.True(settings.HasCompletedOnboarding);
        Assert.True(settings.StartMinimized);
        Assert.Equal(2, fake.SettingsUpdateCalls);
    }

    [Fact]
    public async Task PracticeSearch_DirtyIndexRemainsUsableWithWarning()
    {
        var fake = new FakeCommandService();
        var vm = CreateViewModel(fake);
        await vm.InitializeAsync(manualOpen: true);

        vm.MarkPracticeSearched(new SearchIndexStatus(SearchIndexState.Dirty, 2, "SEARCH_INDEX_DIRTY", "当前使用降级搜索。"));

        Assert.True(vm.PracticeSearched);
        Assert.Equal("当前使用降级搜索。", vm.ErrorMessage);
    }

    [Fact]
    public async Task PracticeSearch_RebuildingIndexDoesNotMarkSearchComplete()
    {
        var fake = new FakeCommandService();
        var vm = CreateViewModel(fake);
        await vm.InitializeAsync(manualOpen: true);

        vm.MarkPracticeSearched(new SearchIndexStatus(SearchIndexState.Rebuilding, 2, "SEARCH_INDEX_REBUILDING", "搜索索引正在重建。"));

        Assert.False(vm.PracticeSearched);
        Assert.Equal("搜索索引正在重建。", vm.ErrorMessage);
    }

    private static OnboardingViewModel CreateViewModel(FakeCommandService fake) =>
        new(fake, new AppSettings(1, false, false, true, "Alt + Space", "Alt+Space", false, true));

    private static Category RootCategory(string name) =>
        new(Guid.NewGuid(), null, name, 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
