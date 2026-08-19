using System.Windows.Interop;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Desktop;

/// <summary>
/// Desktop 侧热键协调器，只负责应用级 Launcher 快捷键（默认 Alt + Space）。
/// 当前版本不注册、不维护、不触发任何话术级快捷键；数据库中的历史字段仍由 Core/迁移保留。
/// </summary>
internal sealed class HotkeyCoordinator : IDisposable
{
    private const int LauncherId = 1;
    private readonly HwndSource _messageWindow;
    private readonly WindowsHotkeyService _service;
    private AppSettings? _settings;
    private bool _launcherConfigured;
    private bool _launcherScopeActive;
    private bool _launcherVisible;
    private bool _paused;
    private bool _practiceMode;
    private bool _disposed;
    private string? _activeAdapterId;

    public HotkeyCoordinator()
    {
        var parameters = new HwndSourceParameters("QuickPhrase.Hotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080,
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(HandleMessage);
        _service = new WindowsHotkeyService(_messageWindow.Handle);
    }

    public bool IsPaused => _paused;
    public bool LauncherAvailable { get; private set; }
    public string? LauncherErrorCode { get; private set; }
    public object StatusSnapshot => new
    {
        configured = _launcherConfigured,
        registered = LauncherAvailable,
        conflict = LauncherErrorCode == "HOTKEY_CONFLICT",
        activeAdapterId = _activeAdapterId,
        launcher = new
        {
            available = LauncherAvailable,
            configured = _launcherConfigured,
            registered = LauncherAvailable,
            conflict = LauncherErrorCode == "HOTKEY_CONFLICT",
            errorCode = LauncherErrorCode,
            activeAdapterId = _activeAdapterId,
        },
        paused = _paused,
    };

    public event Action? LauncherHotkeyPressed;
    public event Action? StatusChanged;

    /// <summary>应用设置变化时，仅重新配置全局 Launcher 快捷键。</summary>
    public void Configure(AppSettings settings)
    {
        _settings = settings;
        _service.UnregisterAll();
        LauncherAvailable = false;
        LauncherErrorCode = null;
        _launcherConfigured = WindowsHotkeyChord.TryParse(settings.LauncherShortcutNormalized, out _);
        if (!_launcherConfigured) LauncherErrorCode = "VALIDATION_FAILED";
        ReconcileLauncherRegistration();
        StatusChanged?.Invoke();
    }

    /// <summary>根据前台 Adapter 更新 Launcher 热键的注册范围。</summary>
    public void SetLauncherScopeActive(bool active, string? adapterId)
    {
        if (_launcherScopeActive == active && string.Equals(_activeAdapterId, adapterId, StringComparison.OrdinalIgnoreCase)) return;
        _launcherScopeActive = active;
        _activeAdapterId = active ? adapterId : null;
        ReconcileLauncherRegistration();
        StatusChanged?.Invoke();
    }

    /// <summary>Launcher 显示期间保留热键，以便 Alt + Space 继续执行关闭动作。</summary>
    public void SetLauncherVisible(bool visible)
    {
        if (_launcherVisible == visible) return;
        _launcherVisible = visible;
        ReconcileLauncherRegistration();
        StatusChanged?.Invoke();
    }

    /// <summary>练习模式使用同一个全局快捷键，但不要求当前前台存在可投递 Adapter。</summary>
    public void SetPracticeMode(bool active)
    {
        if (_practiceMode == active) return;
        _practiceMode = active;
        ReconcileLauncherRegistration();
        StatusChanged?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (_settings is not null) Configure(_settings);
        else ReconcileLauncherRegistration();
        StatusChanged?.Invoke();
    }

    private void ReconcileLauncherRegistration()
    {
        _service.Unregister(LauncherId);
        LauncherAvailable = false;
        if (_paused || !_launcherConfigured || (!_launcherScopeActive && !_launcherVisible && !_practiceMode)) return;
        if (!_service.TryRegister(LauncherId, ParseLauncherChord(), out var errorCode))
        {
            LauncherErrorCode = errorCode ?? "HOTKEY_CONFLICT";
            return;
        }
        LauncherErrorCode = null;
        LauncherAvailable = true;
    }

    private WindowsHotkeyChord ParseLauncherChord()
    {
        WindowsHotkeyChord.TryParse(_settings?.LauncherShortcutNormalized, out var chord);
        return chord;
    }

    private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312 || _paused) return IntPtr.Zero;
        if (wParam.ToInt32() == LauncherId) LauncherHotkeyPressed?.Invoke();
        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.Dispose();
        _messageWindow.RemoveHook(HandleMessage);
        _messageWindow.Dispose();
    }
}



