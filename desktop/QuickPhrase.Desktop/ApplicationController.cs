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
/// 鏄惧紡 composition root锛氶泦涓鐞嗘暟鎹繍琛屾椂銆佷富绐楀彛銆佺嫭绔嬭缃獥鍙ｃ€丯ative Launcher銆佹墭鐩樺拰閫€鍑洪『搴忥拷?/// 璁剧疆绐楀彛閲囩敤鍚岃繘绋嬮潪妯℃€佹柟寮忔墦寮€锛屼笉鍒囨崲 MainWindow 鍐呭锛屼篃涓嶉樆濉炰富鐣岄潰鐨勫悗缁搷浣滐拷?/// </summary>
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

    public ApplicationController()
    {
        _singleInstance = new SingleInstanceCoordinator();
        _dataOptions = QuickPhraseDataOptions.ForCurrentUser();
        _hotkeys = new HotkeyCoordinator();
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
            Console.Error.WriteLine($"寮€鏈哄惎鍔ㄧ姸鎬佹牎鍑嗗け璐ワ細{exception.Message}");
        }
        _hotkeys.Configure(_settings);
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
            menu.Items.Add("快速搜索", null, (_, _) => OpenLauncher(captureTarget: false));
            menu.Items.Add("鏂板缓璇濇湳", null, (_, _) => OpenManagement("editor"));
            menu.Items.Add("暂停快捷键", null, (_, _) => _hotkeys.SetPaused(!_hotkeys.IsPaused));
            menu.Items.Add("璁剧疆", null, (_, _) => OpenSettings());
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
        var settingsWindow = new SettingsWindow(_commands);
        _settingsWindow = settingsWindow;
        settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            RequestShutdownIfNoProductWindows();
        };
        settingsWindow.Show();
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
        _launcher.Open(initialQuery, resolvedTarget, phraseId, status);
    }

    public bool ShouldShowOnboarding => _settings is { HasCompletedOnboarding: false };
    public bool StartMinimized => _settings?.StartMinimized == true;

    public void OpenOnboarding(bool manualOpen = false)
    {
        if (_commands is null || _settings is null) return;
        _onboarding ??= new OnboardingCoordinator(
            _commands,
            _settings,
            startPractice: StartOnboardingPracticeAsync,
            editShortcut: EditOnboardingShortcutAsync);
        _ = _onboarding.OpenAsync(manualOpen);
    }

    private Task<bool> StartOnboardingPracticeAsync(OnboardingViewModel viewModel)
    {
        if (_dataRuntime is null) return Task.FromResult(false);
        var context = new LauncherInvocationContext(LauncherInvocationMode.Practice,
            phrase =>
            {
                viewModel.MarkPracticeInserted(phrase.Content);
                return Task.FromResult(true);
            });
        _hotkeys.SetPracticeMode(true);
        OpenLauncher(captureTarget: false, invocationContext: context);
        viewModel.MarkPracticeSearched();
        return Task.FromResult(true);
    }

    private Task EditOnboardingShortcutAsync(OnboardingViewModel viewModel)
    {
        if (_settings is null) return Task.CompletedTask;
        var owner = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w is OnboardingWindow);
        var dialog = new HotkeyCaptureDialog(_settings.LauncherShortcutDisplay) { Owner = owner };
        if (dialog.ShowDialog() == true)
        {
            var next = _settings with { LauncherShortcutDisplay = dialog.Display, LauncherShortcutNormalized = dialog.Normalized };
            _ = ApplySettingsAsync(next, CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _suppressManagementCloseExit = true;
        _onboarding?.Close();
        _settingsWindow?.Close();
        _management?.Close();
        _onboarding?.Close();
        _launcher?.DisposeLauncher();
        _tray?.Dispose();
        _tray = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayIconStream?.Dispose();
        _trayIconStream = null;
        await _deliveryQueue.DisposeAsync();
        _hotkeys.Dispose();
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
        var settings = _settings ?? new AppSettings(1, false, false, true, "Alt + Space", "Alt+Space", false, true)
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

    private async Task ApplySettingsHotkeysAsync(AppSettings settings)
    {
        _settings = settings;
        if (_dataRuntime is null) return;
        _hotkeys.Configure(settings);
        UpdateLauncherScope();
    }

    private async Task<RepositoryResult<AppSettings>> ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_dataRuntime is null)
            return RepositoryResult<AppSettings>.Failure(new DataError("DATA_UNAVAILABLE", "本地数据运行时尚未就绪"));

        var previousCommand = _startupRegistration.GetCommand();
        try
        {
            _startupRegistration.SetEnabled(settings.LaunchOnStartup,
                settings.LaunchOnStartup ? GetStartupExecutablePath() : null);
            var result = await _dataRuntime.Settings.SaveAsync(settings, settings.Version, cancellationToken);
            if (!result.IsSuccess)
            {
                _startupRegistration.SetRawCommand(previousCommand);
                return result;
            }

            if (result.Value is not null)
            {
                _settings = result.Value;
                await ApplySettingsHotkeysAsync(result.Value);
            }
            return result;
        }
        catch (Exception exception)
        {
            try { _startupRegistration.SetRawCommand(previousCommand); } catch { }
            return RepositoryResult<AppSettings>.Failure(new DataError("STARTUP_REGISTRATION_FAILED", $"寮€鏈哄惎鍔ㄨ缃け璐ワ細{exception.Message}"));
        }
    }

    private static string GetStartupExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("褰撳墠寮€鍙戣繍琛屾柟寮忔病鏈夊彲娉ㄥ唽鐨?QuickPhrase.exe锛涜浣跨敤鍙戝竷鐗堢▼搴忋€?");
        return path;
    }

    private async void CompleteOnboarding(bool openLauncher)
    {
        if (_onboardingHandled || _dataRuntime is null || _settings is null) return;
        _onboardingHandled = true;
        var result = await _dataRuntime.Settings.SaveAsync(_settings with { HasCompletedOnboarding = true }, _settings.Version);
        if (result.IsSuccess && result.Value is not null) _settings = result.Value;
        else Console.Error.WriteLine($"棣栨浣跨敤鐘舵€佷繚瀛樺け璐ワ細{result.Error?.Message ?? "鏈煡閿欒"}");
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

















