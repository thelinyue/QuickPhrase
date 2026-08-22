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
    public async Task CategoryForm_DisablesSubmitUntilNameIsProvided_AndTrimsBeforeSaving()
    {
        var fake = new FakeCommandService();
        var vm = CreateViewModel(fake);
        await vm.InitializeAsync();
        vm.StartCommand.Execute(null);

        Assert.False(vm.CreateCategoryAndContinueCommand.CanExecute(null));

        vm.CategoryName = "  客户沟通  ";

        Assert.True(vm.CreateCategoryAndContinueCommand.CanExecute(null));
        await vm.CreateCategoryAndContinueCommand.ExecuteAsync(null);

        var category = Assert.Single(await fake.ListCategoriesAsync());
        Assert.Equal("客户沟通", category.Name);
    }

    [Fact]
    public async Task BackNavigation_PreservesFormState_AndDoesNotDuplicateCreatedCategory()
    {
        var fake = new FakeCommandService();
        var vm = CreateViewModel(fake);
        await vm.InitializeAsync();
        vm.StartCommand.Execute(null);
        vm.CategoryName = "客户沟通";
        await vm.CreateCategoryAndContinueCommand.ExecuteAsync(null);

        vm.PhraseTitle = "问题已收到";
        vm.PhraseContent = "您好，问题已经收到。";
        vm.BackCommand.Execute(null);

        Assert.Equal(OnboardingStep.Category, vm.CurrentStep);
        Assert.Equal("客户沟通", vm.CategoryName);
        await vm.CreateCategoryAndContinueCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingStep.Phrase, vm.CurrentStep);
        Assert.Single(await fake.ListCategoriesAsync());
        Assert.Equal("问题已收到", vm.PhraseTitle);
        Assert.Equal("您好，问题已经收到。", vm.PhraseContent);
    }

    [Fact]
    public async Task PhraseForm_UsesCreatedCategoryAndExposesCategoryDisplayMode()
    {
        var category = RootCategory("客户沟通");
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });
        var vm = CreateViewModel(fake);

        await vm.InitializeAsync();

        Assert.Equal(OnboardingStep.Phrase, vm.CurrentStep);
        Assert.Equal(category.Id, vm.SelectedCategory!.Id);
        Assert.True(vm.HasSingleCategory);
        Assert.False(vm.HasMultipleCategories);
        Assert.False(vm.SavePhraseAndContinueCommand.CanExecute(null));

        vm.PhraseTitle = "问题已收到";
        vm.PhraseContent = "您好，问题已经收到。";

        Assert.True(vm.SavePhraseAndContinueCommand.CanExecute(null));
    }

    [Fact]
    public async Task PhraseForm_WithMultipleRootCategories_ExposesSelectionMode()
    {
        var first = RootCategory("客户沟通");
        var second = RootCategory("项目协作");
        var fake = new FakeCommandService();
        fake.Seed(new[] { first, second });
        var vm = CreateViewModel(fake);

        await vm.InitializeAsync();

        Assert.Equal(OnboardingStep.Phrase, vm.CurrentStep);
        Assert.False(vm.HasSingleCategory);
        Assert.True(vm.HasMultipleCategories);
        Assert.Equal(first.Id, vm.SelectedCategory!.Id);
    }

    [Fact]
    public async Task BeginPracticeCommand_MarksLauncherOpenedWhenPracticeStarts()
    {
        var category = RootCategory("客户沟通");
        var phrase = new Phrase(Guid.NewGuid(), "欢迎语", PhraseBody.FromText("您好"), category.Id, ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });
        fake.Seed(new[] { phrase });
        var vm = CreateViewModel(fake, _ => Task.FromResult(true));

        await vm.InitializeAsync(manualOpen: true);
        vm.StartCommand.Execute(null);
        await vm.BeginPracticeCommand.ExecuteAsync(null);

        Assert.True(vm.PracticeOpened);
        Assert.Equal("已完成 · 打开闪念", vm.PracticeOpenedStatus);
        Assert.True(vm.CanFinish);
    }

    [Fact]
    public async Task PracticeStatus_UsesProductLanguageInsteadOfBooleanDebugValues()
    {
        var fake = new FakeCommandService();
        var vm = CreateViewModel(fake);
        await vm.InitializeAsync(manualOpen: true);

        Assert.Contains("待完成", vm.PracticeOpenedStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("True", vm.PracticeOpenedStatus, StringComparison.OrdinalIgnoreCase);
        vm.MarkPracticeInserted("练习结果");
        Assert.Contains("已完成", vm.PracticeInsertedStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("True", vm.PracticeInsertedStatus, StringComparison.OrdinalIgnoreCase);
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
        var phrase = new Phrase(Guid.NewGuid(), "欢迎语", PhraseBody.FromText("您好"), category.Id, ShortcutMode.None, null,
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
    public async Task ManualOpen_WithEmptyData_StartsAtWelcome()
    {
        var vm = CreateViewModel(new FakeCommandService());

        await vm.InitializeAsync(manualOpen: true);

        Assert.Equal(OnboardingStep.Welcome, vm.CurrentStep);
    }

    [Fact]
    public async Task ManualOpen_WithCategoryButNoPhrase_StartsAtWelcome()
    {
        var category = RootCategory("客户沟通");
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });
        var vm = CreateViewModel(fake);

        await vm.InitializeAsync(manualOpen: true);

        Assert.Equal(OnboardingStep.Welcome, vm.CurrentStep);
        Assert.Equal(category.Id, vm.SelectedCategory!.Id);
    }

    [Fact]
    public async Task ManualOpen_WithCategoryAndPhrase_StartsAtWelcome_ThenMovesToPractice()
    {
        var category = RootCategory("客户沟通");
        var phrase = new Phrase(Guid.NewGuid(), "欢迎语", PhraseBody.FromText("您好"), category.Id, ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });
        fake.Seed(new[] { phrase });
        var vm = CreateViewModel(fake);

        await vm.InitializeAsync(manualOpen: true);

        Assert.Equal(OnboardingStep.Welcome, vm.CurrentStep);
        Assert.Equal(category.Id, vm.SelectedCategory!.Id);

        vm.StartCommand.Execute(null);

        Assert.Equal(OnboardingStep.Practice, vm.CurrentStep);
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
        var phrase = new Phrase(Guid.NewGuid(), "欢迎语", PhraseBody.FromText("您好"), category.Id, ShortcutMode.None, null,
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
    public async Task Finish_AllowsContinuingWithoutPractice_ThenPersistsVersionOne()
    {
        var category = RootCategory("客户沟通");
        var phrase = new Phrase(Guid.NewGuid(), "欢迎语", PhraseBody.FromText("您好"), category.Id, ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });
        fake.Seed(new[] { phrase });
        var vm = CreateViewModel(fake);
        await vm.InitializeAsync(manualOpen: true);
        vm.StartCommand.Execute(null);

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

    private static OnboardingViewModel CreateViewModel(
        FakeCommandService fake,
        Func<OnboardingViewModel, Task<bool>>? startPractice = null) =>
        new(fake, new AppSettings(1, false, false, true, new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space), false, true), startPractice);

    private static Category RootCategory(string name) =>
        new(Guid.NewGuid(), null, name, 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
