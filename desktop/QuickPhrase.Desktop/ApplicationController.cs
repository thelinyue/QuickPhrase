using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.Onboarding;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Desktop;

/// <summary>
/// Desktop 组合根：集中管理数据运行时、主窗口、独立设置窗口、闪念、托盘和退出顺序。
/// 设置窗口与主窗口在同一进程内以非模态方式打开，不切换主窗口内容，也不阻塞主界面的后续操作。
/// </summary>
internal sealed class ApplicationController : IAsyncDisposable
{
    private readonly SingleInstanceCoordinator _singleInstance;
    private readonly HotkeyCoordinator _hotkeys;
    private readonly QuickPhraseDataOptions _dataOptions;
    private readonly WindowsTargetDetector _targetDetector;
    private readonly WindowsAdapterResolver _adapterResolver;
    private readonly ForegroundApplicationWatcher _foregroundWatcher;
    private readonly ITextDeliveryStateMachine _delivery;
    private readonly DeliveryQueueCoordinator _deliveryQueue;
    private readonly UsageUpdateQueue _usageUpdates;
    private readonly DeliveryTraceWriter _traceWriter;
    private readonly WindowsStartupRegistration _startupRegistration;
    private const string ApplicationIconResourceUri =
        "pack://application:,,,/QuickPhrase;component/Assets/quickphrase.ico";
    private QuickPhraseDataRuntime? _dataRuntime;
    private SearchHistoryCoordinator? _searchHistory;
    private ICommandService? _commands;
    private AppSettings? _settings;
    private Forms.NotifyIcon? _tray;
    // NotifyIcon 依赖托盘图标流的生命周期，必须保持到托盘销毁完成。
    private Icon? _trayIcon;
    private Stream? _trayIconStream;
    private MainWindow? _management;
    private SettingsWindow? _settingsWindow;
    private LauncherWindow? _launcher;
    private DeliveryTarget? _lastExternalTarget;
    private OnboardingCoordinator? _onboarding;
    private bool _onboardingHandled;
    private bool _suppressManagementCloseExit;
    private bool _hotkeyConflictNotified;
    private string? _startupWarning;

    public ApplicationController()
    {
        _singleInstance = new SingleInstanceCoordinator();
        _dataOptions = QuickPhraseDataOptions.ForCurrentUser();
        _hotkeys = new HotkeyCoordinator(new WindowsShortcutService(), DispatchToUi);
        _targetDetector = new WindowsTargetDetector();
        _adapterResolver = new WindowsAdapterResolver(
            targetValidator: target => _targetDetector.Validate(target, requireForeground: false).IsValid);
        _foregroundWatcher = new ForegroundApplicationWatcher();
        _traceWriter = new DeliveryTraceWriter(Path.Combine(_dataOptions.RootPath, "Logs"));
        _startupRegistration = new WindowsStartupRegistration();
        _usageUpdates = new UsageUpdateQueue(RecordUsageCoreAsync);
        _delivery = TextDeliveryFactory.Create(_targetDetector, _adapterResolver, RecordUsageAsync, _traceWriter.Write);
        _deliveryQueue = new DeliveryQueueCoordinator(_delivery);
        _deliveryQueue.StatusChanged += status => DispatchToUi(() => _launcher?.SetQueueStatus(status));
        _deliveryQueue.ItemFailed += result => DispatchToUi(() => ShowDeliveryNotification(result, Forms.ToolTipIcon.Warning));
        _deliveryQueue.ItemCompleted += OnDeliveryCompleted;
        _deliveryQueue.BatchCompleted += summary => DispatchToUi(() =>
        {
            if (summary.CompletedCount + summary.FailedCount + summary.CancelledCount > 1)
                _tray?.ShowBalloonTip(1800, "闪语", $"连续话术处理完成：成功 {summary.CompletedCount} 条，失败 {summary.FailedCount} 条，取消 {summary.CancelledCount} 条。", Forms.ToolTipIcon.Info);
        });
        _hotkeys.LauncherHotkeyPressed += ToggleLauncherFromHotkey;
        _hotkeys.StatusChanged += OnHotkeyStatusChanged;
        _foregroundWatcher.Changed += OnForegroundChanged;
    }

    public async Task InitializeDataAsync(CancellationToken cancellationToken = default)
    {
        _dataRuntime = await QuickPhraseDataRuntime.OpenAsync(_dataOptions, cancellationToken);
        _searchHistory = new SearchHistoryCoordinator(_dataRuntime.SearchHistory);
        await _searchHistory.InitializeAsync(cancellationToken);
        _commands = new CommandService(
            _dataRuntime.Phrases,
            _dataRuntime.Search,
            _dataRuntime.Categories,
            _dataRuntime.Settings,
            InsertPhraseFromManagementAsync,
            ApplySettingsAsync,
            _dataRuntime);

        _settings = await _dataRuntime.Settings.LoadAsync(cancellationToken);
        try
        {
            _startupRegistration.SetEnabled(
                _settings.LaunchOnStartup,
                _settings.LaunchOnStartup ? GetStartupExecutablePath() : null);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"开机启动状态校准失败：{exception.Message}");
        }
        await _hotkeys.ConfigureAsync(_settings, cancellationToken);
        UpdateLauncherScope();
    }

    public bool TryBecomePrimary() => _singleInstance.TryBecomePrimary();

    public Task<string> CreateUpgradeBackupAsync(string reason, CancellationToken cancellationToken = default) =>
        QuickPhraseDataRuntime.CreateBackupOnlyAsync(_dataOptions, reason, cancellationToken);

    public void StartActivationServer()
    {
        _singleInstance.StartServer(message =>
        {
            DispatchToUi(() =>
            {
                if (string.Equals(message, "shutdown-for-upgrade", StringComparison.OrdinalIgnoreCase))
                {
                    _suppressManagementCloseExit = true;
                    System.Windows.Application.Current?.Shutdown();
                    return;
                }

                OpenManagement();
            });
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 鍒涘缓绯荤粺鎵樼洏鍥炬爣锛屽苟涓庣獥鍙ｆ爣棰樻爮銆佷富鐣岄潰鍝佺墝浣嶇粺涓€浣跨敤鍚屼竴浠藉唴锟?ICO锟?    /// 璧勬簮鍔犺浇澶辫触鏃朵笉鍥為€€锟?Windows 绯荤粺榛樿鍥炬爣锛岄伩鍏嶅啀娆℃樉绀洪敊璇浛浠ｅ浘鏍囷拷?    /// </summary>
    public void StartTray()
    {
        if (_tray is not null) return;

        Forms.NotifyIcon? tray = null;
        Forms.ContextMenuStrip? menu = null;
        Stream? iconStream = null;
        Icon? icon = null;
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri(ApplicationIconResourceUri, UriKind.Absolute));
            if (resource is null)
                throw new InvalidOperationException($"鎵句笉鍒板唴宓屽浘鏍囪祫婧愶細{ApplicationIconResourceUri}");

            iconStream = resource.Stream;
            icon = new Icon(iconStream);
            menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开话术库", null, (_, _) => OpenManagement());
            menu.Items.Add("打开闪念", null, (_, _) => OpenLauncher(captureTarget: false));
            menu.Items.Add("新建话术", null, (_, _) => OpenManagement("editor"));
            menu.Items.Add("暂停快捷键", null, (_, _) => _hotkeys.SetPaused(!_hotkeys.IsPaused));
            menu.Items.Add("设置", null, (_, _) => OpenSettings());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => System.Windows.Application.Current.Shutdown());

            tray = new Forms.NotifyIcon
            {
                Icon = icon,
                Visible = true,
                Text = "闂",
                ContextMenuStrip = menu,
            };
            tray.DoubleClick += (_, _) => OpenManagement();

            _trayIconStream = iconStream;
            _trayIcon = icon;
            _tray = tray;
        }
        catch (Exception exception)
        {
            tray?.Dispose();
            menu?.Dispose();
            icon?.Dispose();
            iconStream?.Dispose();
            Console.Error.WriteLine($"托盘图标初始化失败：资源加载或 NotifyIcon 创建失败。{exception.Message}");
            throw new InvalidOperationException("托盘图标初始化失败，应用未使用默认替代图标。", exception);
        }
    }

    public void OpenManagement(string? scene = null)
    {
        if (string.Equals(scene, "settings", StringComparison.OrdinalIgnoreCase))
        {
            OpenSettings();
            return;
        }

        if (_management is { IsVisible: true })
        {
            _management.Activate();
            if (scene is not null) _management.NavigateTo(scene);
            return;
        }

        if (_commands is null) return;
        if (_searchHistory is null) return;
        _management = new MainWindow(_commands, _searchHistory, scene ?? "library");
        _management.SettingsRequested += (_, _) => OpenSettings();
        _management.Closed += (_, _) => OnManagementClosed();
        _management.Show();
        if (scene is not null) _management.NavigateTo(scene);
    }

    /// <summary>
    /// 鎵撳紑鎴栨縺娲诲敮涓€鐨勯潪妯℃€佽缃獥鍙ｃ€傜獥鍙ｄ笌涓荤獥鍙ｅ悓杩涚▼锛岃缃姞杞藉拰淇濆瓨涓嶉樆濉炰富绐楀彛锟?    /// </summary>
    public void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        if (_commands is null) return;
        _settingsWindow = new SettingsWindow(_commands);
        _settingsWindow.RestartOnboardingRequested += SettingsWindow_RestartOnboardingRequested;
        _settingsWindow.Closed += (_, _) =>
        {
            if (_settingsWindow is not null)
                _settingsWindow.RestartOnboardingRequested -= SettingsWindow_RestartOnboardingRequested;
            _settingsWindow = null;
            RequestShutdownIfNoProductWindows();
        };
        _settingsWindow.Show();
    }

    private void SettingsWindow_RestartOnboardingRequested(object? sender, EventArgs e)
    {
        OpenOnboarding(manualOpen: true);
    }

    /// <summary>
    /// 涓荤獥鍙ｅ叧闂悗锛屽鏋滆缃獥鍙ｄ粛鐒跺彲瑙侊紝鍏堜繚鐣欒繘绋嬪拰璁剧疆绐楀彛锟?    /// 鍙湁鏈€鍚庝竴涓骇鍝佺獥鍙ｄ篃鍏抽棴鏃讹紝鎵嶆寜鈥滃叧闂悗鐣欏湪鎵樼洏鈥濊缃喅瀹氭槸鍚﹂€€鍑猴拷?    /// </summary>
    private void OnManagementClosed()
    {
        _management = null;
        var suppressExit = _suppressManagementCloseExit;
        _suppressManagementCloseExit = false;
        RequestShutdownIfNoProductWindows(suppressExit);
    }

    /// <summary>
    /// 缁熶竴澶勭悊浜у搧绐楀彛鍏抽棴鍚庣殑閫€鍑哄垽鏂紝閬垮厤鍏抽棴璇濇湳搴撴椂璇€€鍑虹嫭绔嬭缃獥鍙ｏ拷?    /// </summary>
    private void RequestShutdownIfNoProductWindows(bool suppressExit = false)
    {
        if (suppressExit || _settings is not { StayInTrayOnClose: false }) return;
        if (_management is { IsVisible: true } || _settingsWindow is { IsVisible: true }) return;
        System.Windows.Application.Current?.Shutdown();
    }

    public void OpenLauncher(string initialQuery = "", DeliveryTarget? target = null, bool captureTarget = true, Guid? phraseId = null, LauncherInvocationContext? invocationContext = null)
    {
        if (_dataRuntime is null) return;
        if (_launcher is { IsVisible: true })
        {
            if (invocationContext is not null)
                _launcher.Open(initialQuery, target, phraseId, _adapterResolver.GetStatus(target), invocationContext);
            _launcher.Activate();
            return;
        }

        if (_searchHistory is null) return;
        _launcher ??= new LauncherWindow(_dataRuntime.Search, _searchHistory);
        _launcher.DeliveryRequested -= OnDeliveryRequested;
        _launcher.DeliveryRequested += OnDeliveryRequested;
        _launcher.CreatePhraseRequested -= OnCreatePhraseRequested;
        _launcher.CreatePhraseRequested += OnCreatePhraseRequested;
        _launcher.Hidden -= OnLauncherHidden;
        _launcher.Hidden += OnLauncherHidden;
        _launcher.Closed -= OnLauncherClosed;
        _launcher.Closed += OnLauncherClosed;

        var resolvedTarget = captureTarget ? target ?? _targetDetector.CaptureForeground() : target;
        if (resolvedTarget is not null) _lastExternalTarget = resolvedTarget;
        var status = _adapterResolver.GetStatus(resolvedTarget);
        _hotkeys.SetLauncherVisible(true);
        _launcher.Open(initialQuery, resolvedTarget, phraseId, status, invocationContext);
    }

    public bool ShouldShowOnboarding => _settings is { HasCompletedOnboarding: false };
    public bool StartMinimized => _settings?.StartMinimized == true;

    public void OpenOnboarding(bool manualOpen = false)
    {
        if (_commands is null || _settings is null) return;
        if (_onboarding is not null)
        {
            _ = _onboarding.OpenAsync(manualOpen);
            return;
        }

        // 每次重新打开都使用最新设置快照，避免设置窗口保存后向导仍读取旧的开机启动状态。
        _onboarding = new OnboardingCoordinator(
            _commands,
            _settings,
            startPractice: StartOnboardingPracticeAsync,
            editShortcut: EditOnboardingShortcutAsync,
            startupWarningProvider: () => _startupWarning,
            stopPractice: StopOnboardingPractice);
        var coordinator = _onboarding;
        coordinator.Closed += () =>
        {
            if (ReferenceEquals(_onboarding, coordinator)) _onboarding = null;
        };
        coordinator.Completed -= OnboardingCompleted;
        coordinator.Completed += OnboardingCompleted;
        _ = coordinator.OpenAsync(manualOpen);
    }

    private void OnboardingCompleted()
    {
        _onboarding = null;
        // 手动引导可能在设置窗口仍打开时更新了设置版本；刷新设置基线，保留用户尚未保存的控件修改。
        _ = _settingsWindow?.ViewModel.RefreshBaseAsync();
        OpenManagement();
    }

    private Task<bool> StartOnboardingPracticeAsync(OnboardingViewModel viewModel)
    {
        if (_dataRuntime is null) return Task.FromResult(false);
        var context = new LauncherInvocationContext(LauncherInvocationMode.Practice,
            phrase =>
            {
                viewModel.MarkPracticeInserted(phrase.Content);
                return Task.FromResult(true);
            },
            (query, status) => viewModel.MarkPracticeSearched(status));
        _hotkeys.SetPracticeMode(true);
        OpenLauncher(captureTarget: false, invocationContext: context);
        if (!_hotkeys.LauncherAvailable)
        {
            _launcher?.HideLauncher();
            _hotkeys.SetPracticeMode(false);
            viewModel.SetPracticeError("闪念快捷键注册失败，可能与其他程序冲突。请修改快捷键后重试。");
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    private async Task EditOnboardingShortcutAsync(OnboardingViewModel viewModel)
    {
        if (_commands is null || _settings is null)
            return;

        var current = await _commands.GetSettingsAsync();
        RepositoryResult<AppSettings>? appliedResult = null;

        async Task<RepositoryResult<AppSettings>> ApplyShortcutAsync(
            ShortcutChord chord,
            CancellationToken cancellationToken)
        {
            RepositoryResult<AppSettings> result = RepositoryResult<AppSettings>.Failure(
                new DataError("SETTINGS_SAVE_FAILED", "快捷键保存失败，请重试。"));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                result = await ApplySettingsAsync(
                    current with { LauncherShortcut = chord },
                    cancellationToken);
                if (result.IsSuccess ||
                    !string.Equals(result.Error?.Code, "VERSION_CONFLICT", StringComparison.OrdinalIgnoreCase) ||
                    attempt == 1)
                {
                    break;
                }

                current = await _commands.GetSettingsAsync(cancellationToken);
            }

            if (result.IsSuccess)
                appliedResult = result;
            return result;
        }

        var owner = System.Windows.Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window is OnboardingWindow);
        var dialog = new HotkeyCaptureDialog(current.LauncherShortcut, ApplyShortcutAsync)
        {
            Owner = owner,
        };

        if (dialog.ShowDialog() == true && appliedResult?.Value is not null)
            viewModel.ApplySettingsSnapshot(appliedResult.Value);
    }

    private void StopOnboardingPractice()
    {
        if (_launcher?.IsPracticeMode == true) _launcher.HideLauncher();
        else _hotkeys.SetPracticeMode(false);
    }

    public async ValueTask DisposeAsync()
    {
        _suppressManagementCloseExit = true;
        _onboarding?.Close();
        _settingsWindow?.Close();
        _management?.Close();
        _launcher?.DisposeLauncher();
        _tray?.Dispose();
        _tray = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayIconStream?.Dispose();
        _trayIconStream = null;
        await _deliveryQueue.DisposeAsync();
        await _hotkeys.DisposeAsync();
        _foregroundWatcher.Dispose();
        _adapterResolver.Dispose();
        await _usageUpdates.DisposeAsync();
        (_delivery as IDisposable)?.Dispose();
        if (_dataRuntime is not null) await _dataRuntime.DisposeAsync();
        await _singleInstance.DisposeAsync();
    }

    private void OnDeliveryRequested(Phrase phrase, bool sendRequested, DeliveryTarget? target, string? query) =>
        _ = QueueOrDeliverPhraseAsync(phrase, target, sendRequested, query);
    private void OnCreatePhraseRequested(string seed)
    {
        OpenManagement("editor");
        if (!string.IsNullOrWhiteSpace(seed))
            _tray?.ShowBalloonTip(1600, "闪语", $"已打开新话术编辑器，可继续填写“{seed}”。", Forms.ToolTipIcon.Info);
    }

    private void OnLauncherClosed(object? sender, EventArgs e) => _hotkeys.SetLauncherVisible(false);

    private async Task<bool> InsertPhraseFromManagementAsync(Guid phraseId, CancellationToken cancellationToken)
    {
        if (_commands is null) return false;
        var phrase = await _commands.GetPhraseAsync(phraseId, cancellationToken);
        if (phrase is null) return false;
        var result = await QueueOrDeliverPhraseAsync(phrase, _lastExternalTarget, sendRequested: false, query: null);
        return result?.IsSuccess == true && result.Inserted;
    }


    private async Task<DeliveryResult?> QueueOrDeliverPhraseAsync(Phrase phrase, DeliveryTarget? target, bool sendRequested, string? query)
    {
        var settings = _settings ?? new AppSettings(1, false, false, true, new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space), false, true)
        {
            LauncherEnabledAdapters = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["WXWork"] = true },
        };
        var adapter = target is null ? null : _adapterResolver.Resolve(target);
        var canQueue = adapter is not null && DeliveryQueuePolicy.CanQueue(adapter.Profile);
        var request = new DeliveryRequest(phrase, target, sendRequested, settings.AutoSend, settings.ClipboardCompatibilityMode,
            canQueue ? TargetChangeBehavior.Cancel : TargetChangeBehavior.CopyOnly);
        if (canQueue)
        {
            var ticket = _deliveryQueue.TryEnqueue(request, Guid.NewGuid(), query);
            if (!ticket.Accepted)
            {
                ShowDeliveryNotification(new DeliveryResult(DeliveryStatus.Failed, DeliveryEffect.None, DeliveryStage.NotStarted,
                    DeliveryConfidence.Confirmed, ticket.Code, "连续话术队列已满，当前话术未投递。", false, Guid.NewGuid()), Forms.ToolTipIcon.Warning);
                return null;
            }
            return ticket.Completion is null ? null : await ticket.Completion.ConfigureAwait(true);
        }
        return await DeliverSingleAsync(request, query);
    }

    private async Task<DeliveryResult?> DeliverSingleAsync(DeliveryRequest request, string? query)
    {
        try
        {
            var result = await _delivery.DeliverAsync(request, CancellationToken.None);
            if (result.Status is DeliveryStatus.Failed or DeliveryStatus.Unknown)
                ShowDeliveryNotification(result, Forms.ToolTipIcon.Warning);
            else if (result.Status == DeliveryStatus.Unsupported)
                ShowDeliveryNotification(result, Forms.ToolTipIcon.Info);
            await RecordSearchHistoryIfSuccessfulAsync(result, query);
            return result;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"璇濇湳鎶曢€掑け璐ワ細{exception.Message}");
            _tray?.ShowBalloonTip(2200, "闪语", "话术投递失败，未自动重试。", Forms.ToolTipIcon.Warning);
            return null;
        }
    }

    private void OnDeliveryCompleted(DeliveryResult result, string? query)
    {
        DispatchToUi(() => _ = RecordSearchHistoryIfSuccessfulAsync(result, query));
    }

    private async Task RecordSearchHistoryIfSuccessfulAsync(DeliveryResult result, string? query)
    {
        if (_searchHistory is null || !result.IsSuccess || !result.Inserted || string.IsNullOrWhiteSpace(query)) return;
        await _searchHistory.RecordAsync(query);
    }
    private void ShowDeliveryNotification(DeliveryResult result, Forms.ToolTipIcon icon)
    {
        _tray?.ShowBalloonTip(2200, "闂", result.Message, icon);
    }

    /// <summary>
    /// 应用设置时，快捷键严格按 Stage → SQLite Save → Commit 执行。
    /// 开机启动属于独立的 Windows 外部副作用：主事务失败时恢复旧注册表值，
    /// 但启动项本身同步失败仍只作为可读警告，不阻断其他设置保存。
    /// </summary>
    private async Task<RepositoryResult<AppSettings>> ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_dataRuntime is null || _settings is null)
            return RepositoryResult<AppSettings>.Failure(new DataError("DATA_UNAVAILABLE", "本地数据运行时尚未就绪"));

        var current = _settings;
        var previousCommand = _startupRegistration.GetCommand();
        _startupWarning = null;
        try
        {
            // 启动项是 Windows 外部副作用。注册失败只记录可读警告，不能阻止设置和引导状态落库。
            try
            {
                _startupRegistration.SetEnabled(settings.LaunchOnStartup,
                    settings.LaunchOnStartup ? GetStartupExecutablePath() : null);
            }
            catch (Exception exception)
            {
                _startupWarning = $"开机启动设置未能同步：{exception.Message}。引导仍会完成，你可以稍后在设置中重试。";
                Console.Error.WriteLine($"开机启动设置同步失败。阶段：STARTUP_REGISTRATION；结果码：STARTUP_SYNC_FAILED；异常类型：{exception.GetType().Name}");
            }

            var result = await _hotkeys.ApplyShortcutChangeAsync(
                current,
                settings,
                _dataRuntime.Settings.SaveAsync,
                cancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                // 快捷键 Commit 失败后，补偿写回会产生新的设置版本；即使业务结果失败，
                // 也必须同步该快照，否则本次会话后续保存会持续触发 VERSION_CONFLICT。
                if (result.Value is not null)
                    _settings = result.Value;
                try { _startupRegistration.SetRawCommand(previousCommand); } catch { }
                return result;
            }

            _settings = result.Value;
            UpdateLauncherScope();
            return result;
        }
        catch (OperationCanceledException)
        {
            try { _startupRegistration.SetRawCommand(previousCommand); } catch { }
            throw;
        }
        catch (Exception exception)
        {
            try { _startupRegistration.SetRawCommand(previousCommand); } catch { }
            var traceId = Guid.NewGuid();
            Console.Error.WriteLine($"设置保存失败。阶段：APPLY_SETTINGS；结果码：SETTINGS_SAVE_FAILED；TraceId：{traceId}；异常类型：{exception.GetType().Name}");
            return RepositoryResult<AppSettings>.Failure(new DataError(
                "SETTINGS_SAVE_FAILED",
                $"设置保存失败，请重试。TraceId：{traceId}"));
        }
    }
    private static string GetStartupExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前开发运行方式没有可注册的 QuickPhrase.exe；请使用发布版程序。");
        return path;
    }

    private async void CompleteOnboarding(bool openLauncher)
    {
        if (_onboardingHandled || _dataRuntime is null || _settings is null) return;
        _onboardingHandled = true;
        var result = await _dataRuntime.Settings.SaveAsync(_settings with { HasCompletedOnboarding = true }, _settings.Version);
        if (result.IsSuccess && result.Value is not null) _settings = result.Value;
        else Console.Error.WriteLine($"首次使用状态保存失败：{result.Error?.Message ?? "未知错误"}");
        _onboarding?.Close();
        _onboarding = null;
        if (openLauncher) OpenLauncher("", captureTarget: false); else OpenManagement();
    }

    private void ToggleLauncherFromHotkey()
    {
        if (_launcher?.IsLauncherVisible == true)
        {
            _launcher.HideLauncher();
            return;
        }

        // 首次引导的练习页使用真实闪念窗口，但必须走 Practice 上下文，
        // 这样 Alt + Space 只把选择结果回传给向导，不会进入正式投递链。
        if (_onboarding?.ViewModel is { CurrentStep: OnboardingStep.Practice } onboardingViewModel)
        {
            _ = onboardingViewModel.BeginPracticeCommand.ExecuteAsync(null);
            return;
        }

        var target = _targetDetector.CaptureForeground();
        var adapter = target is null ? null : _adapterResolver.Resolve(target);
        if (adapter is null || _settings is null || !LauncherEligibilityPolicy.CanOpen(adapter.AdapterId, _settings.LauncherEnabledAdapters)) return;
        OpenLauncher(target: target);
    }

    private void OnHotkeyStatusChanged()
    {
        if (_hotkeys.LauncherErrorCode == "HOTKEY_CONFLICT")
        {
            if (!_hotkeyConflictNotified && _tray is not null)
            {
                _hotkeyConflictNotified = true;
                _tray.ShowBalloonTip(3000, "闪语", "全局快捷键 Alt + Space 被其他程序占用，已临时释放。请在设置中更换快捷键或关闭冲突程序。", Forms.ToolTipIcon.Warning);
            }
        }
        else _hotkeyConflictNotified = false;
    }

    private void OnForegroundChanged() => DispatchToUi(UpdateLauncherScope);

    private void OnLauncherHidden()
    {
        _hotkeys.SetLauncherVisible(false);
        _hotkeys.SetPracticeMode(false);
        UpdateLauncherScope();
    }

    private void UpdateLauncherScope()
    {
        if (_settings is null)
        {
            _hotkeys.SetLauncherScopeActive(false, null);
            return;
        }
        var target = _targetDetector.CaptureForeground();
        var adapter = target is null ? null : _adapterResolver.Resolve(target);
        var active = adapter is not null && LauncherEligibilityPolicy.CanOpen(adapter.AdapterId, _settings.LauncherEnabledAdapters);
        if (active) _lastExternalTarget = target;
        _hotkeys.SetLauncherScopeActive(active, active ? adapter!.AdapterId : null);
    }

    private void DispatchToUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private Task RecordUsageAsync(Guid phraseId, CancellationToken cancellationToken) => _usageUpdates.EnqueueAsync(phraseId, cancellationToken);

    private async Task RecordUsageCoreAsync(Guid phraseId, CancellationToken cancellationToken)
    {
        if (_dataRuntime is null) return;
        await _dataRuntime.Phrases.IncrementUsageAsync(phraseId, DateTimeOffset.UtcNow, cancellationToken);
    }
}
