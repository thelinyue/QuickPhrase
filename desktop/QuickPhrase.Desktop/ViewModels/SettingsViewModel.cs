using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>设置页可选择的快捷键预设。Custom 只表示合法组合不属于两个内置预设。</summary>
public enum LauncherShortcutPreset
{
    Recommended,
    Alternate,
    Custom,
}

/// <summary>
/// 设置页视图模型（通用 / 快捷键 / 发送行为 / 应用适配）。
/// 普通设置变化通过 ICommandService 串行即时落库；快捷键候选使用显式异步提交，
/// 只有 Desktop 编排层完成 Stage、SQLite Save、Commit 后才更新当前展示。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private static readonly ShortcutChord RecommendedShortcut = new(ShortcutModifiers.Alt, ShortcutKey.Space);
    private static readonly ShortcutChord AlternateShortcut = new(ShortcutModifiers.Ctrl, ShortcutKey.Space);

    private readonly ICommandService _commands;
    private readonly object _applyGate = new();
    private Task _applyChain = Task.CompletedTask;
    private bool _isLoading;
    private AppSettings _base = new(0, false, false, true, RecommendedShortcut, false, false);
    private Dictionary<string, bool> _baseAdapters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>设置页末尾的数据管理入口，内部仍通过 ICommandService 访问话术包服务。</summary>
    public DataManagementViewModel DataManagement { get; }

    [ObservableProperty] private bool _launchOnStartup;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _stayInTrayOnClose;
    [ObservableProperty] private ShortcutChord _launcherShortcut = RecommendedShortcut;
    [ObservableProperty] private bool _autoSend;
    [ObservableProperty] private bool _clipboardCompatibilityMode;
    [ObservableProperty] private ObservableCollection<AdapterToggleItem> _adapters = new();
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isBusy;

    public LauncherShortcutPreset LauncherShortcutPreset => InferShortcutPreset(LauncherShortcut);

    /// <summary>
    /// 请求重新打开首次使用向导。
    /// 这里只发布 UI 编排事件，不删除数据、不修改设置，也不依赖 Windows 平台实现；
    /// 由 ApplicationController 订阅后负责调用 OnboardingCoordinator.OpenAsync(manualOpen: true)。
    /// </summary>
    public event EventHandler? RestartOnboardingRequested;

    public SettingsViewModel(ICommandService commands)
    {
        _commands = commands;
        DataManagement = new DataManagementViewModel(commands);
    }

    public async Task LoadAsync()
    {
        _isLoading = true;
        try
        {
            var settings = await _commands.GetSettingsAsync();
            _base = settings;
            _baseAdapters = new Dictionary<string, bool>(settings.LauncherEnabledAdapters, StringComparer.OrdinalIgnoreCase);
            LaunchOnStartup = settings.LaunchOnStartup;
            StartMinimized = settings.StartMinimized;
            StayInTrayOnClose = settings.StayInTrayOnClose;
            LauncherShortcut = settings.LauncherShortcut;
            AutoSend = settings.AutoSend;
            ClipboardCompatibilityMode = settings.ClipboardCompatibilityMode;
            ReplaceAdapters(settings.LauncherEnabledAdapters);
            ErrorMessage = null;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>
    /// 仅刷新持久化基线，不覆盖当前控件值。
    /// 向导在设置窗口仍打开时更新设置版本后，使用该方法避免后续即时保存因乐观并发版本过期而失败。
    /// </summary>
    public async Task RefreshBaseAsync()
    {
        var settings = await _commands.GetSettingsAsync();
        _base = settings;
        _baseAdapters = new Dictionary<string, bool>(settings.LauncherEnabledAdapters, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>等待当前已排队的即时保存完成，供关闭流程和单元测试使用。</summary>
    public async Task ApplyPendingChangesAsync()
    {
        Task pending;
        lock (_applyGate)
        {
            pending = _applyChain;
        }

        await pending;
    }

    /// <summary>
    /// 提交结构化快捷键候选。失败时保留当前快捷键，让捕获弹窗继续显示错误并允许重试。
    /// </summary>
    public async Task<RepositoryResult<AppSettings>> ApplyLauncherShortcutAsync(
        ShortcutChord chord,
        CancellationToken cancellationToken = default)
    {
        var validation = ShortcutChordValidator.Validate(chord);
        if (!validation.IsValid)
        {
            ErrorMessage = validation.ErrorMessage;
            return RepositoryResult<AppSettings>.Failure(new DataError(
                validation.ErrorCode ?? "SHORTCUT_INVALID",
                validation.ErrorMessage ?? "快捷键无效。"));
        }

        await ApplyPendingChangesAsync();
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _commands.UpdateSettingsAsync(BuildCandidate(chord), cancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error?.Message ?? "快捷键保存失败，请重试。";
                return result;
            }

            _base = result.Value;
            _baseAdapters = new Dictionary<string, bool>(result.Value.LauncherEnabledAdapters, StringComparer.OrdinalIgnoreCase);
            LauncherShortcut = result.Value.LauncherShortcut;
            return result;
        }
        catch (Exception exception)
        {
            var traceId = Guid.NewGuid();
            System.Diagnostics.Trace.TraceError(
                "快捷键设置失败。阶段：APPLY_SHORTCUT；结果码：SHORTCUT_APPLY_FAILED；TraceId：{0}；异常类型：{1}",
                traceId,
                exception.GetType().Name);
            ErrorMessage = $"快捷键保存失败，请重试。TraceId：{traceId}";
            return RepositoryResult<AppSettings>.Failure(new DataError("SHORTCUT_APPLY_FAILED", ErrorMessage));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public static LauncherShortcutPreset InferShortcutPreset(ShortcutChord chord) =>
        chord == RecommendedShortcut
            ? LauncherShortcutPreset.Recommended
            : chord == AlternateShortcut
                ? LauncherShortcutPreset.Alternate
                : LauncherShortcutPreset.Custom;

    private void ReplaceAdapters(IReadOnlyDictionary<string, bool> adapters)
    {
        foreach (var adapter in Adapters)
            adapter.PropertyChanged -= AdapterToggleItem_PropertyChanged;

        Adapters = new ObservableCollection<AdapterToggleItem>(
            adapters.Select(kv => new AdapterToggleItem(kv.Key, kv.Value)));

        foreach (var adapter in Adapters)
            adapter.PropertyChanged += AdapterToggleItem_PropertyChanged;
    }

    private void AdapterToggleItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AdapterToggleItem.Enabled))
            QueueApply();
    }

    private void QueueApply()
    {
        if (_isLoading) return;

        lock (_applyGate)
        {
            // 每个任务在前一个提交完成后重新读取当前控件状态，
            // 因此快速连续操作最终会以最新状态覆盖旧快照，而不会乱序写入。
            _applyChain = ApplyAfterAsync(_applyChain);
        }
    }

    private async Task ApplyAfterAsync(Task previous)
    {
        try { await previous; } catch { }
        await ApplyCurrentSettingsAsync();
    }

    private AppSettings BuildCandidate(ShortcutChord chord)
    {
        var adapters = Adapters.ToDictionary(a => a.Id, a => a.Enabled, StringComparer.OrdinalIgnoreCase);
        return _base with
        {
            LaunchOnStartup = LaunchOnStartup,
            StartMinimized = StartMinimized,
            StayInTrayOnClose = StayInTrayOnClose,
            LauncherShortcut = chord,
            AutoSend = AutoSend,
            ClipboardCompatibilityMode = ClipboardCompatibilityMode,
            LauncherEnabledAdapters = adapters,
        };
    }

    private async Task ApplyCurrentSettingsAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _commands.UpdateSettingsAsync(BuildCandidate(LauncherShortcut));
            if (result.IsSuccess && result.Value is not null)
            {
                _base = result.Value;
                _baseAdapters = new Dictionary<string, bool>(result.Value.LauncherEnabledAdapters, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                ErrorMessage = result.Error?.Message ?? "设置保存失败。";
            }
        }
        catch (Exception exception)
        {
            var traceId = Guid.NewGuid();
            System.Diagnostics.Trace.TraceError(
                "设置保存失败。阶段：APPLY_SETTINGS；结果码：SETTINGS_SAVE_FAILED；TraceId：{0}；异常类型：{1}",
                traceId,
                exception.GetType().Name);
            ErrorMessage = $"设置保存失败，请重试。TraceId：{traceId}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnLauncherShortcutChanged(ShortcutChord value) =>
        OnPropertyChanged(nameof(LauncherShortcutPreset));

    partial void OnLaunchOnStartupChanged(bool value) => QueueApply();
    partial void OnStartMinimizedChanged(bool value) => QueueApply();
    partial void OnStayInTrayOnCloseChanged(bool value) => QueueApply();
    partial void OnAutoSendChanged(bool value) => QueueApply();
    partial void OnClipboardCompatibilityModeChanged(bool value) => QueueApply();

    /// <summary>
    /// 发出重新开始使用引导的请求。向导是否打开以及打开后的数据恢复由应用编排层决定。
    /// </summary>
    [RelayCommand]
    private void RestartOnboarding() => RestartOnboardingRequested?.Invoke(this, EventArgs.Empty);
}
