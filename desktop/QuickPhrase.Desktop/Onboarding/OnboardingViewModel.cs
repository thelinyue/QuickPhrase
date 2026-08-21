using System.Collections.ObjectModel;
using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop.Onboarding;

/// <summary>
/// 首次使用向导的状态机。它只根据真实分类、话术和设置结果推进，不把当前步骤写入数据库，
/// 因而中途关闭后可以用事实数据自然恢复，也不会重复生成示例数据。
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    public const int CurrentOnboardingVersion = 1;
    private readonly ICommandService _commands;
    private readonly Func<OnboardingViewModel, Task<bool>>? _startPractice;
    private readonly Func<OnboardingViewModel, Task>? _openShortcutEditor;
    private readonly Func<string?>? _startupWarningProvider;
    private AppSettings _settings;
    private bool _manualOpen;
    private bool _hasPhrases;
    private Guid? _createdCategoryId;
    private bool _launchOnStartupDirty;
    private bool _suppressLaunchOnStartupTracking;

    [ObservableProperty] private OnboardingStep _currentStep;
    [ObservableProperty] private string _categoryName = "";
    [ObservableProperty] private string _phraseTitle = "";
    [ObservableProperty] private string _phraseContent = "";
    [ObservableProperty] private CategoryOption? _selectedCategory;
    [ObservableProperty] private ObservableCollection<CategoryOption> _categories = new();
    [ObservableProperty] private string _practiceInput = "";
    [ObservableProperty] private bool _practiceOpened;
    [ObservableProperty] private bool _practiceSearched;
    [ObservableProperty] private bool _practiceInserted;
    [ObservableProperty] private bool _practiceStarting;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _launchOnStartup;
    [ObservableProperty] private string? _startupWarning;

    public OnboardingViewModel(
        ICommandService commands,
        AppSettings settings,
        Func<OnboardingViewModel, Task<bool>>? startPractice = null,
        Func<OnboardingViewModel, Task>? openShortcutEditor = null,
        Func<string?>? startupWarningProvider = null)
    {
        _commands = commands;
        _settings = settings;
        _startPractice = startPractice;
        _openShortcutEditor = openShortcutEditor;
        _startupWarningProvider = startupWarningProvider;
        _launchOnStartup = settings.LaunchOnStartup;
    }

    public bool IsManualOpen => _manualOpen;
    public int StepNumber => (int)CurrentStep + 1;
    public bool CanGoBack => CurrentStep is > OnboardingStep.Welcome and < OnboardingStep.Complete;
    public bool HasSingleCategory => Categories.Count == 1;
    public bool HasMultipleCategories => Categories.Count > 1;
    public string SelectedCategoryDisplayName => SelectedCategory?.Name ?? "请选择分类";
    public event Action? Completed;
    public event Action? Skipped;
    public event Action? PracticeStopRequested;
    /// <summary>练习页允许继续，避免用户因快捷键或外部窗口状态被首次引导卡死。</summary>
    public bool CanFinish => CurrentStep == OnboardingStep.Practice && !IsBusy;
    public bool CanCreateCategory => CurrentStep == OnboardingStep.Category && !IsBusy && !string.IsNullOrWhiteSpace(CategoryName);
    public bool CanSavePhrase => CurrentStep == OnboardingStep.Phrase
        && !IsBusy
        && !string.IsNullOrWhiteSpace(PhraseTitle)
        && !string.IsNullOrWhiteSpace(PhraseContent)
        && SelectedCategory is not null;
    public string PracticeHint => PracticeInserted
        ? "练习已完成，可以继续。"
        : "可以先体验一次；也可以稍后在话术库中再次体验。";
    public string PracticeOpenedStatus => PracticeStarting
        ? "进行中 · 打开闪念"
        : PracticeOpened ? "已完成 · 打开闪念" : "待完成 · 打开闪念";
    public string PracticeSearchedStatus => PracticeSearched ? "已完成 · 找到刚才的话术" : "待完成 · 找到刚才的话术";
    public string PracticeInsertedStatus => PracticeInserted ? "已完成 · 按 Enter 选择话术" : "待完成 · 按 Enter 选择话术";

    partial void OnCategoryNameChanged(string value) => NotifyFormCommandsChanged();
    partial void OnPhraseTitleChanged(string value) => NotifyFormCommandsChanged();
    partial void OnPhraseContentChanged(string value) => NotifyFormCommandsChanged();
    partial void OnSelectedCategoryChanged(CategoryOption? value)
    {
        OnPropertyChanged(nameof(SelectedCategoryDisplayName));
        NotifyFormCommandsChanged();
    }
    partial void OnCategoriesChanged(ObservableCollection<CategoryOption> value)
    {
        OnPropertyChanged(nameof(HasSingleCategory));
        OnPropertyChanged(nameof(HasMultipleCategories));
        OnPropertyChanged(nameof(SelectedCategoryDisplayName));
        NotifyFormCommandsChanged();
    }
    partial void OnIsBusyChanged(bool value) => NotifyFormCommandsChanged();
    partial void OnLaunchOnStartupChanged(bool value)
    {
        if (!_suppressLaunchOnStartupTracking) _launchOnStartupDirty = true;
    }

    partial void OnCurrentStepChanged(OnboardingStep value)
    {
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepNumber));
        OnPropertyChanged(nameof(CanFinish));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanCreateCategory));
        OnPropertyChanged(nameof(CanSavePhrase));
    }

    partial void OnPracticeStartingChanged(bool value) => OnPropertyChanged(nameof(PracticeOpenedStatus));

    partial void OnPracticeOpenedChanged(bool value)
    {
        OnPropertyChanged(nameof(PracticeOpenedStatus));
        OnPropertyChanged(nameof(PracticeHint));
    }

    partial void OnPracticeSearchedChanged(bool value)
    {
        OnPropertyChanged(nameof(PracticeSearchedStatus));
        OnPropertyChanged(nameof(PracticeHint));
    }

    partial void OnPracticeInsertedChanged(bool value)
    {
        OnPropertyChanged(nameof(PracticeInsertedStatus));
        OnPropertyChanged(nameof(PracticeHint));
        OnPropertyChanged(nameof(CanFinish));
    }

    public string StepTitle => CurrentStep switch
    {
        OnboardingStep.Welcome => "欢迎使用闪语",
        OnboardingStep.Category => "创建第一个一级分类",
        OnboardingStep.Phrase => "创建第一条话术",
        OnboardingStep.Practice => "体验闪念",
        OnboardingStep.Complete => "准备完成",
        _ => "使用引导",
    };

    public async Task InitializeAsync(bool manualOpen = false, CancellationToken cancellationToken = default)
    {
        _manualOpen = manualOpen;
        await ReloadDataAsync(cancellationToken);
        // 从设置页重新打开时先展示欢迎页；已有数据会在用户点击“开始设置”后自然跳过，
        // 因而既能完整重温引导，也不会重复创建分类或话术。
        CurrentStep = manualOpen ? OnboardingStep.Welcome : GetFirstIncompleteStep(skipWelcome: false);
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(CanFinish));
    }

    [RelayCommand]
    private void Start()
    {
        // 欢迎页的“开始设置”应直接进入第一个未完成的数据步骤，避免空数据再次停留在欢迎页。
        CurrentStep = GetFirstIncompleteStep(skipWelcome: true);
        ErrorMessage = null;
        OnPropertyChanged(nameof(StepTitle));
    }

    [RelayCommand(CanExecute = nameof(CanCreateCategory))]
    private async Task CreateCategoryAndContinue()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (string.IsNullOrWhiteSpace(CategoryName))
            {
                ErrorMessage = "请输入分类名称。";
                return;
            }
            var normalizedName = CategoryName.Trim();
            var existingCreatedCategory = _createdCategoryId is Guid createdId
                ? Categories.FirstOrDefault(category => category.Id == createdId &&
                    string.Equals(category.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
                : null;
            if (existingCreatedCategory is not null)
            {
                SelectedCategory = existingCreatedCategory;
                CurrentStep = OnboardingStep.Phrase;
                OnPropertyChanged(nameof(StepTitle));
                return;
            }

            var result = await _commands.CreateCategoryAsync(new CreateCategoryCommand(Guid.NewGuid(), normalizedName));
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error?.Message ?? "分类保存失败，请重试。";
                return;
            }
            await ReloadDataAsync();
            SelectedCategory = Categories.FirstOrDefault(c => c.Id == result.Value.Id) ?? Categories.FirstOrDefault();
            _createdCategoryId = result.Value.Id;
            CategoryName = normalizedName;
            CurrentStep = HasPhraseData() ? OnboardingStep.Practice : OnboardingStep.Phrase;
            OnPropertyChanged(nameof(StepTitle));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"分类保存失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanSavePhrase))]
    private async Task SavePhraseAndContinue()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (string.IsNullOrWhiteSpace(PhraseTitle)) { ErrorMessage = "请输入话术标题。"; return; }
            if (string.IsNullOrWhiteSpace(PhraseContent)) { ErrorMessage = "请输入话术正文。"; return; }
            if (SelectedCategory is null || SelectedCategory.Id == Guid.Empty) { ErrorMessage = "请选择一个分类。"; return; }
            var result = await _commands.CreatePhraseAsync(new CreatePhraseCommand(
                Guid.NewGuid(), PhraseTitle.Trim(), PhraseContent, SelectedCategory.Id, ShortcutMode.None, null));
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error?.Message ?? "话术保存失败，请重试。";
                return;
            }
            CurrentStep = OnboardingStep.Practice;
            OnPropertyChanged(nameof(StepTitle));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"话术保存失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task BeginPractice()
    {
        ErrorMessage = null;
        PracticeStarting = true;
        try
        {
            if (_startPractice is null)
            {
                PracticeOpened = true;
                PracticeSearched = true;
                return;
            }
            PracticeOpened = await _startPractice(this);
            OnPropertyChanged(nameof(CanFinish));
        }
        finally
        {
            PracticeStarting = false;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack) return;
        if (CurrentStep == OnboardingStep.Practice) PracticeStopRequested?.Invoke();
        CurrentStep = (OnboardingStep)((int)CurrentStep - 1);
        ErrorMessage = null;
        OnPropertyChanged(nameof(StepTitle));
    }

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private async Task Finish()
    {
        if (IsBusy) return;
        PracticeStopRequested?.Invoke();
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await SaveOnboardingSettingsAsync(settings => settings with
            {
                HasCompletedOnboarding = true,
                OnboardingVersion = CurrentOnboardingVersion,
                LaunchOnStartup = LaunchOnStartup,
            });
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error?.Message ?? "引导状态保存失败，请重试。";
                return;
            }
            _settings = result.Value;
            _launchOnStartupDirty = false;
            StartupWarning = _startupWarningProvider?.Invoke();
            CurrentStep = OnboardingStep.Complete;
            OnPropertyChanged(nameof(StepTitle));
        }
        catch (Exception ex) { ErrorMessage = $"引导状态保存失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task Skip()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await SaveOnboardingSettingsAsync(settings => settings with
            {
                HasCompletedOnboarding = true,
                OnboardingVersion = CurrentOnboardingVersion,
            });
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error?.Message ?? "跳过引导失败，请重试。";
                return;
            }
            _settings = result.Value;
            CurrentStep = OnboardingStep.Complete;
            OnPropertyChanged(nameof(StepTitle));
            Skipped?.Invoke();
        }
        catch (Exception ex) { ErrorMessage = $"跳过引导失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task Retry()
    {
        ErrorMessage = null;
        await ReloadDataAsync();
        CurrentStep = GetFirstIncompleteStep(skipWelcome: true);
        OnPropertyChanged(nameof(StepTitle));
    }

    [RelayCommand]
    private async Task CloseComplete()
    {
        if (CurrentStep != OnboardingStep.Complete || IsBusy) return;

        // 启动项复选框只在完成页显示，因此不能只依赖进入完成页前的保存。
        // 这里再次提交当前设置，确保用户在完成页做出的选择也会同步到 Windows 启动项。
        if (_launchOnStartupDirty)
        {
            IsBusy = true;
            ErrorMessage = null;
            try
            {
                var result = await SaveOnboardingSettingsAsync(settings => settings with
                {
                    HasCompletedOnboarding = true,
                    OnboardingVersion = CurrentOnboardingVersion,
                    LaunchOnStartup = LaunchOnStartup,
                });
                if (!result.IsSuccess || result.Value is null)
                {
                    ErrorMessage = result.Error?.Message ?? "启动项设置保存失败，请重试。";
                    return;
                }

                _settings = result.Value;
                _launchOnStartupDirty = false;
                StartupWarning = _startupWarningProvider?.Invoke();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"启动项设置保存失败：{ex.Message}";
                return;
            }
            finally
            {
                IsBusy = false;
            }
        }

        Completed?.Invoke();
    }

    [RelayCommand]
    private async Task ModifyShortcut()
    {
        if (_openShortcutEditor is not null) await _openShortcutEditor(this);
    }

    /// <summary>
    /// 同步快捷键保存后的最新设置快照，避免后续完成/跳过操作使用过期版本号。
    /// 完成页的启动项是尚未提交的页面状态，不能被快捷键保存返回的旧快照覆盖。
    /// </summary>
    public void ApplySettingsSnapshot(AppSettings settings)
    {
        var pendingLaunchOnStartup = LaunchOnStartup;
        var preservePendingChoice = CurrentStep == OnboardingStep.Complete;
        _settings = settings;
        _suppressLaunchOnStartupTracking = true;
        LaunchOnStartup = preservePendingChoice ? pendingLaunchOnStartup : settings.LaunchOnStartup;
        _suppressLaunchOnStartupTracking = false;
        if (!preservePendingChoice) _launchOnStartupDirty = false;
    }

    /// <summary>把快捷键保存失败转换为向导中的中文错误，而不是静默丢失。</summary>
    public void SetShortcutError(string message) => ErrorMessage = message;

    /// <summary>初始化读取失败时保留窗口并显示可理解的错误，允许用户稍后重试。</summary>
    public void SetInitializationError(string message) => ErrorMessage = message;

    public void MarkPracticeSearched() { PracticeSearched = true; OnPropertyChanged(nameof(CanFinish)); }

    /// <summary>
    /// 将 Core 搜索索引状态映射到向导状态。Dirty 仍是可用的降级搜索，Rebuilding 则必须等待后重试。
    /// </summary>
    public void MarkPracticeSearched(SearchIndexStatus status)
    {
        if (status.State == SearchIndexState.Rebuilding)
        {
            ErrorMessage = status.Message ?? "搜索索引正在重建，请稍后重试。";
            PracticeSearched = false;
            OnPropertyChanged(nameof(CanFinish));
            return;
        }

        PracticeSearched = true;
        ErrorMessage = status.State == SearchIndexState.Dirty
            ? status.Message ?? "搜索索引暂不可用，当前已降级为中文搜索。"
            : null;
        OnPropertyChanged(nameof(CanFinish));
    }
    public void SetPracticeError(string message)
    {
        ErrorMessage = message;
        PracticeOpened = false;
        OnPropertyChanged(nameof(CanFinish));
    }
    public void MarkPracticeInserted(string content) { PracticeInput = content; PracticeSearched = true; PracticeInserted = true; OnPropertyChanged(nameof(CanFinish)); }

    /// <summary>
    /// 在写入前读取最新设置，并在乐观版本冲突时重新合并并重试一次。
    /// 这样设置窗口与向导并行保存时，向导只覆盖自己负责的字段，不会丢失其他设置。
    /// </summary>
    private async Task<RepositoryResult<AppSettings>> SaveOnboardingSettingsAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default)
    {
        var latest = await _commands.GetSettingsAsync(cancellationToken);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await _commands.UpdateSettingsAsync(update(latest), cancellationToken);
            if (result.IsSuccess && result.Value is not null)
            {
                _settings = result.Value;
                return result;
            }

            if (!string.Equals(result.Error?.Code, "VERSION_CONFLICT", StringComparison.OrdinalIgnoreCase) || attempt == 1)
                return result;

            latest = await _commands.GetSettingsAsync(cancellationToken);
        }

        return RepositoryResult<AppSettings>.Failure(new DataError("VERSION_CONFLICT", "设置版本冲突，请重试。"));
    }

    private async Task ReloadDataAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _commands.ListCategoriesAsync(cancellationToken);
        Categories = new ObservableCollection<CategoryOption>(categories
            .Where(c => c.ParentId is null)
            .Select(c => new CategoryOption(c.Id, c.Name, c.ParentId)));
        if (SelectedCategory is null || Categories.All(c => c.Id != SelectedCategory.Id)) SelectedCategory = Categories.FirstOrDefault();
        var phrases = await _commands.ListPhrasesAsync(cancellationToken);
        _hasPhrases = phrases.Count > 0;
        OnPropertyChanged(nameof(CanFinish));
    }

    private void NotifyFormCommandsChanged()
    {
        CreateCategoryAndContinueCommand.NotifyCanExecuteChanged();
        SavePhraseAndContinueCommand.NotifyCanExecuteChanged();
        FinishCommand.NotifyCanExecuteChanged();
    }

    private OnboardingStep GetFirstIncompleteStep(bool skipWelcome)
    {
        if (!Categories.Any(c => c.ParentId is null)) return skipWelcome ? OnboardingStep.Category : OnboardingStep.Welcome;
        if (!_hasPhrases) return OnboardingStep.Phrase;
        return OnboardingStep.Practice;
    }

    private bool HasPhraseData() => _hasPhrases;
}

/// <summary>向导下拉框使用的轻量分类项，避免把 Platform.Windows 类型泄漏到 UI。</summary>
public sealed record CategoryOption(Guid Id, string Name, Guid? ParentId);
