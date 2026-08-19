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

    public SettingsViewModel(ICommandService commands) => _commands = commands;

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
            var settings = new AppSettings(
                _base.Version, LaunchOnStartup, StartMinimized, StayInTrayOnClose,
                LauncherShortcutDisplay, NormalizeShortcut(LauncherShortcutDisplay),
                AutoSend, ClipboardCompatibilityMode)
            {
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
