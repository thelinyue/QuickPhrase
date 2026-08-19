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
    private AppSettings _settings;
    private bool _manualOpen;
    private bool _hasPhrases;

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
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _launchOnStartup;
    [ObservableProperty] private string? _startupWarning;

    public OnboardingViewModel(
        ICommandService commands,
        AppSettings settings,
        Func<OnboardingViewModel, Task<bool>>? startPractice = null,
        Func<OnboardingViewModel, Task>? openShortcutEditor = null)
    {
        _commands = commands;
        _settings = settings;
        _startPractice = startPractice;
        _openShortcutEditor = openShortcutEditor;
        LaunchOnStartup = settings.LaunchOnStartup;
    }

    public bool IsManualOpen => _manualOpen;
    public event Action? Completed;
    public event Action? Skipped;
    public bool CanFinish => PracticeInserted || !_manualOpen;
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
        CurrentStep = manualOpen ? GetFirstIncompleteStep(skipWelcome: true) : OnboardingStep.Welcome;
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(CanFinish));
    }

    [RelayCommand]
    private void Start()
    {
        CurrentStep = GetFirstIncompleteStep(skipWelcome: false);
        ErrorMessage = null;
        OnPropertyChanged(nameof(StepTitle));
    }

    [RelayCommand]
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
            var result = await _commands.CreateCategoryAsync(new CreateCategoryCommand(Guid.NewGuid(), CategoryName.Trim()));
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error?.Message ?? "分类保存失败，请重试。";
                return;
            }
            await ReloadDataAsync();
            SelectedCategory = Categories.FirstOrDefault(c => c.Id == result.Value.Id) ?? Categories.FirstOrDefault();
            CurrentStep = HasPhraseData() ? OnboardingStep.Practice : OnboardingStep.Phrase;
            OnPropertyChanged(nameof(StepTitle));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"分类保存失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
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
                Guid.NewGuid(), PhraseTitle.Trim(), PhraseContent, SelectedCategory.Id, false, ShortcutMode.None, null));
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
        if (_startPractice is null)
        {
            PracticeOpened = true;
            PracticeSearched = true;
            return;
        }
        PracticeOpened = await _startPractice(this);
        OnPropertyChanged(nameof(CanFinish));
    }

    [RelayCommand]
    private async Task Finish()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var next = _settings with { HasCompletedOnboarding = true, OnboardingVersion = CurrentOnboardingVersion, LaunchOnStartup = LaunchOnStartup };
            var result = await _commands.UpdateSettingsAsync(next);
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error?.Message ?? "引导状态保存失败，请重试。";
                return;
            }
            _settings = result.Value;
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
            var next = _settings with { HasCompletedOnboarding = true, OnboardingVersion = CurrentOnboardingVersion };
            var result = await _commands.UpdateSettingsAsync(next);
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
    private void CloseComplete()
    {
        if (CurrentStep == OnboardingStep.Complete) Completed?.Invoke();
    }

    [RelayCommand]
    private async Task ModifyShortcut()
    {
        if (_openShortcutEditor is not null) await _openShortcutEditor(this);
    }

    public void MarkPracticeSearched() { PracticeSearched = true; OnPropertyChanged(nameof(CanFinish)); }
    public void MarkPracticeInserted(string content) { PracticeInput = content; PracticeSearched = true; PracticeInserted = true; OnPropertyChanged(nameof(CanFinish)); }

    private async Task ReloadDataAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _commands.ListCategoriesAsync(cancellationToken);
        Categories = new ObservableCollection<CategoryOption>(categories.Select(c => new CategoryOption(c.Id, c.Name, c.ParentId)));
        var top = Categories.Where(c => c.ParentId is null).ToArray();
        if (SelectedCategory is null || Categories.All(c => c.Id != SelectedCategory.Id)) SelectedCategory = top.FirstOrDefault() ?? Categories.FirstOrDefault();
        var phrases = await _commands.ListPhrasesAsync(cancellationToken);
        _hasPhrases = phrases.Count > 0;
        OnPropertyChanged(nameof(CanFinish));
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







