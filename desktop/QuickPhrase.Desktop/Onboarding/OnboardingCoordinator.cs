using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop.Onboarding;

/// <summary>
/// 向导窗口生命周期协调器。它负责把 WPF 窗口、向导 ViewModel 与 ApplicationController 的
/// 练习/完成回调连接起来，业务数据仍由 Core 契约和 ICommandService 保存。
/// </summary>
public sealed class OnboardingCoordinator
{
    private readonly ICommandService _commands;
    private readonly AppSettings _settings;
    private readonly Func<OnboardingViewModel, Task<bool>>? _startPractice;
    private readonly Func<OnboardingViewModel, Task>? _editShortcut;
    private OnboardingWindow? _window;

    public OnboardingCoordinator(
        ICommandService commands,
        AppSettings settings,
        Func<OnboardingViewModel, Task<bool>>? startPractice = null,
        Func<OnboardingViewModel, Task>? editShortcut = null)
    {
        _commands = commands;
        _settings = settings;
        _startPractice = startPractice;
        _editShortcut = editShortcut;
    }

    public OnboardingViewModel? ViewModel => _window?.DataContext as OnboardingViewModel;
    public bool IsVisible => _window?.IsVisible == true;
    public event Action? Closed;
    public event Action? Completed;

    public async Task OpenAsync(bool manualOpen = false)
    {
        if (_window is { IsVisible: true }) { _window.Activate(); return; }
        var vm = new OnboardingViewModel(_commands, _settings, _startPractice, _editShortcut);
        _window = new OnboardingWindow(vm);
        vm.Completed += OnCompleted;
        vm.Skipped += OnSkipped;
        _window.Closed += (_, _) =>
        {
            _window = null;
            Closed?.Invoke();
        };
        await vm.InitializeAsync(manualOpen);
        _window.Show();
    }

    public void Close()
    {
        _window?.CloseWithoutSkipping();
        _window = null;
    }

    private void OnCompleted() { _window?.CloseWithoutSkipping(); Completed?.Invoke(); }
    private void OnSkipped() { _window?.CloseWithoutSkipping(); Completed?.Invoke(); }
}
