using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>设置页视图模型（通用 / 快捷键 / 发送行为 / 应用适配）。保存经 ICommandService，不直接碰 SQLite。</summary>
public partial class SettingsViewModel : ObservableObject, INavigationGuard
{
    private readonly ICommandService _commands;

    /// <summary>设置页末尾的数据管理入口，内部仍通过 ICommandService 访问话术包服务。</summary>
    public DataManagementViewModel DataManagement { get; }
    private AppSettings _base = new(0, false, false, true, string.Empty, string.Empty, false, false);
    private Dictionary<string, bool> _baseAdapters = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool _launchOnStartup;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _stayInTrayOnClose;
    [ObservableProperty] private string _launcherShortcutDisplay = "";
    [ObservableProperty] private bool _autoSend;
    [ObservableProperty] private bool _clipboardCompatibilityMode;
    [ObservableProperty] private ObservableCollection<AdapterToggleItem> _adapters = new();
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isBusy;

    public event EventHandler<AppSettings>? Saved;
    public event EventHandler? Cancelled;

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
        var s = await _commands.GetSettingsAsync();
        _base = s;
        _baseAdapters = new Dictionary<string, bool>(s.LauncherEnabledAdapters, StringComparer.OrdinalIgnoreCase);
        LaunchOnStartup = s.LaunchOnStartup;
        StartMinimized = s.StartMinimized;
        StayInTrayOnClose = s.StayInTrayOnClose;
        LauncherShortcutDisplay = s.LauncherShortcutDisplay;
        AutoSend = s.AutoSend;
        ClipboardCompatibilityMode = s.ClipboardCompatibilityMode;
        Adapters = new ObservableCollection<AdapterToggleItem>(
            s.LauncherEnabledAdapters.Select(kv => new AdapterToggleItem(kv.Key, kv.Value)));
        ErrorMessage = null;
    }

    /// <summary>
    /// 仅刷新持久化基线，不覆盖当前编辑中的控件值。
    /// 向导在设置窗口仍打开时更新设置版本后，使用该方法避免后续保存因乐观并发版本过期而失败。
    /// </summary>
    public async Task RefreshBaseAsync()
    {
        var settings = await _commands.GetSettingsAsync();
        _base = settings;
        _baseAdapters = new Dictionary<string, bool>(settings.LauncherEnabledAdapters, StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    public bool HasUnsavedChanges =>
        LaunchOnStartup != _base.LaunchOnStartup ||
        StartMinimized != _base.StartMinimized ||
        StayInTrayOnClose != _base.StayInTrayOnClose ||
        AutoSend != _base.AutoSend ||
        ClipboardCompatibilityMode != _base.ClipboardCompatibilityMode ||
        LauncherShortcutDisplay != _base.LauncherShortcutDisplay ||
        Adapters.Any(a => !_baseAdapters.TryGetValue(a.Id, out var v) || v != a.Enabled);

    public void DiscardChanges()
    {
        LaunchOnStartup = _base.LaunchOnStartup;
        StartMinimized = _base.StartMinimized;
        StayInTrayOnClose = _base.StayInTrayOnClose;
        LauncherShortcutDisplay = _base.LauncherShortcutDisplay;
        AutoSend = _base.AutoSend;
        ClipboardCompatibilityMode = _base.ClipboardCompatibilityMode;
        Adapters = new ObservableCollection<AdapterToggleItem>(
            _baseAdapters.Select(kv => new AdapterToggleItem(kv.Key, kv.Value)));
        ErrorMessage = null;
    }

    public async Task SaveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var adapters = Adapters.ToDictionary(a => a.Id, a => a.Enabled, StringComparer.OrdinalIgnoreCase);
            // 使用 with 保留引导处理状态和其他未来扩展字段，普通设置保存不能把用户重新判定为首次使用。
            var settings = _base with
            {
                LaunchOnStartup = LaunchOnStartup,
                StartMinimized = StartMinimized,
                StayInTrayOnClose = StayInTrayOnClose,
                LauncherShortcutDisplay = LauncherShortcutDisplay,
                LauncherShortcutNormalized = NormalizeShortcut(LauncherShortcutDisplay),
                AutoSend = AutoSend,
                ClipboardCompatibilityMode = ClipboardCompatibilityMode,
                LauncherEnabledAdapters = adapters,
            };
            var result = await _commands.UpdateSettingsAsync(settings);
            if (result.IsSuccess && result.Value is not null)
            {
                _base = result.Value;
                _baseAdapters = new Dictionary<string, bool>(result.Value.LauncherEnabledAdapters, StringComparer.OrdinalIgnoreCase);
                Saved?.Invoke(this, result.Value);
            }
            else
            {
                ErrorMessage = result.Error?.Message ?? "保存失败。";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Save() => await SaveAsync();

    [RelayCommand]
    private void Cancel()
    {
        DiscardChanges();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 发出重新开始使用引导的请求。向导是否打开以及打开后的数据恢复由应用编排层决定。
    /// </summary>
    [RelayCommand]
    private void RestartOnboarding() => RestartOnboardingRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>设置页仅保存展示名；归一化用于快捷键冲突比对，不依赖 Win32。</summary>
    private static string NormalizeShortcut(string display)
    {
        var parts = display.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(p => p.Length > 0)
            .Distinct()
            .ToArray();
        return string.Join("+", parts);
    }
}
