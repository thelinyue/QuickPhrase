using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop.Onboarding;

/// <summary>
/// 向导窗口生命周期协调器。它负责把 WPF 窗口、向导 ViewModel 与 ApplicationController 的
/// 练习/完成回调连接起来，业务数据仍由 Core 契约和 ICommandService 保存。
/// 初始化期间允许窗口被关闭；通过 generation 和局部窗口引用，避免异步初始化完成后再次显示已关闭窗口。
/// </summary>
public sealed class OnboardingCoordinator
{
    private readonly ICommandService _commands;
    private readonly AppSettings _fallbackSettings;
    private readonly Func<string?>? _startupWarningProvider;
    private readonly Func<OnboardingViewModel, Task<bool>>? _startPractice;
    private readonly Func<OnboardingViewModel, Task>? _editShortcut;
    private readonly Action? _stopPractice;
    private OnboardingWindow? _window;
    private Task? _openingTask;
    private int _generation;
    private bool _practiceCleanupRequested;

    public OnboardingCoordinator(
        ICommandService commands,
        AppSettings settings,
        Func<OnboardingViewModel, Task<bool>>? startPractice = null,
        Func<OnboardingViewModel, Task>? editShortcut = null,
        Func<string?>? startupWarningProvider = null,
        Action? stopPractice = null)
    {
        _commands = commands;
        _fallbackSettings = settings;
        _startPractice = startPractice;
        _editShortcut = editShortcut;
        _startupWarningProvider = startupWarningProvider;
        _stopPractice = stopPractice;
    }

    public OnboardingViewModel? ViewModel => _window?.DataContext as OnboardingViewModel;
    public bool IsVisible => _window?.IsVisible == true;
    public event Action? Closed;
    public event Action? Completed;

    /// <summary>
    /// 打开或激活同一个向导实例。初始化任务共享，避免重复创建窗口，也让关闭与初始化并发时可以安全收敛。
    /// </summary>
    public Task OpenAsync(bool manualOpen = false)
    {
        if (_window is { IsVisible: true })
        {
            _window.Activate();
            return Task.CompletedTask;
        }

        if (_openingTask is not null)
        {
            _window?.Activate();
            return _openingTask;
        }

        var viewModel = new OnboardingViewModel(
            _commands,
            _fallbackSettings,
            _startPractice,
            _editShortcut,
            _startupWarningProvider);
        var window = new OnboardingWindow(viewModel);
        var generation = ++_generation;
        var closed = false;
        _practiceCleanupRequested = false;
        _window = window;

        viewModel.Completed += OnCompleted;
        viewModel.Skipped += OnSkipped;
        window.Closed += (_, _) =>
        {
            closed = true;
            StopPracticeOnce();
            if (ReferenceEquals(_window, window)) _window = null;
            Closed?.Invoke();
        };

        _openingTask = OpenCoreAsync(window, viewModel, manualOpen, generation, () => closed);
        return _openingTask;
    }

    private async Task OpenCoreAsync(
        OnboardingWindow window,
        OnboardingViewModel viewModel,
        bool manualOpen,
        int generation,
        Func<bool> isClosed)
    {
        try
        {
            // 设置窗口可能在向导初始化期间保存过设置，因此每次打开先读取最新版本，避免使用旧 Version 写回。
            try
            {
                viewModel.ApplySettingsSnapshot(await _commands.GetSettingsAsync());
            }
            catch (Exception exception)
            {
                viewModel.SetInitializationError($"引导设置加载失败：{exception.Message}");
            }

            try
            {
                await viewModel.InitializeAsync(manualOpen);
            }
            catch (Exception exception)
            {
                viewModel.SetInitializationError($"引导数据加载失败：{exception.Message}");
            }

            if (generation == _generation && ReferenceEquals(_window, window) && !isClosed() && !window.Dispatcher.HasShutdownStarted)
                window.Show();
        }
        finally
        {
            if (generation == _generation) _openingTask = null;
        }
    }

    public void Close()
    {
        var window = _window;
        _generation++;
        _openingTask = null;
        _window = null;
        StopPracticeOnce();
        if (window?.IsVisible == true) window.CloseWithoutSkipping();
    }

    private void StopPracticeOnce()
    {
        if (_practiceCleanupRequested) return;
        _practiceCleanupRequested = true;
        _stopPractice?.Invoke();
    }

    private void OnCompleted()
    {
        _window?.CloseWithoutSkipping();
        Completed?.Invoke();
    }

    private void OnSkipped()
    {
        _window?.CloseWithoutSkipping();
        Completed?.Invoke();
    }
}
