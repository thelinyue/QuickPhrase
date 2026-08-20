using System.Diagnostics;
using System.Runtime.ExceptionServices;
using QuickPhrase.Core;

namespace QuickPhrase.Desktop;

/// <summary>
/// Desktop 侧应用级快捷键协调器。它只依赖 Core 的 <see cref="IShortcutService"/>，负责 Launcher
/// 的作用域、暂停、练习模式和 UI 激活编排；Win32 注册、消息窗口和虚拟键映射全部留在 Platform.Windows。
/// </summary>
internal sealed class HotkeyCoordinator : IDisposable, IAsyncDisposable
{
    private readonly IShortcutService service;
    private readonly Action<Action> dispatchToUi;
    private bool launcherConfigured;
    private bool launcherScopeActive;
    private bool launcherVisible;
    private bool paused;
    private bool practiceMode;
    private bool disposed;
    private int launcherRegistrationReconcileState;
    private string? activeAdapterId;

    public HotkeyCoordinator(IShortcutService service, Action<Action> dispatchToUi)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        this.service.Activated += Service_Activated;
    }

    public bool IsPaused => paused;

    public bool LauncherAvailable { get; private set; }

    public string? LauncherErrorCode { get; private set; }

    public object StatusSnapshot => new
    {
        configured = launcherConfigured,
        registered = LauncherAvailable,
        conflict = LauncherErrorCode == "HOTKEY_CONFLICT",
        activeAdapterId,
        launcher = new
        {
            available = LauncherAvailable,
            configured = launcherConfigured,
            registered = LauncherAvailable,
            conflict = LauncherErrorCode == "HOTKEY_CONFLICT",
            errorCode = LauncherErrorCode,
            activeAdapterId,
        },
        paused,
    };

    /// <summary>
    /// 记录协调器收到快捷键激活的单调时钟时间戳，供隔离 Launcher smoke 计算热呼出耗时；
    /// 该事件不表达 Win32 RegisterHotKey 已通过，也不参与正式产品编排。
    /// </summary>
    internal event Action<long>? LauncherActivationReceived;

    public event Action? LauncherHotkeyPressed;

    public event Action? StatusChanged;

    /// <summary>
    /// 启动时把持久化的结构化快捷键注册为活动快捷键。注册冲突不会伪装成配置成功，
    /// 也不会让后续 Launcher Scope 错误地显示为可用。
    /// </summary>
    public async Task ConfigureAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(settings);

        if (ShortcutChordValidator.Validate(service.ActiveChord).IsValid
            && service.ActiveChord == settings.LauncherShortcut)
        {
            launcherConfigured = true;
            LauncherErrorCode = null;
            ReconcileLauncherRegistration();
            StatusChanged?.Invoke();
            return;
        }

        var stage = await service.StageAsync(settings.LauncherShortcut, cancellationToken).ConfigureAwait(false);
        if (!stage.IsSuccess)
        {
            launcherConfigured = false;
            LauncherErrorCode = stage.ErrorCode ?? "HOTKEY_REGISTER_FAILED";
            ReconcileLauncherRegistration();
            StatusChanged?.Invoke();
            return;
        }

        ShortcutApplyResult apply;
        try
        {
            apply = await service.CommitAsync(stage.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var rollbackError = await TryRollbackAsync(stage.Token, "CONFIGURE_COMMIT_CANCELLED").ConfigureAwait(false);
            launcherConfigured = false;
            LauncherErrorCode = rollbackError?.Code;
            ReconcileLauncherRegistration();
            StatusChanged?.Invoke();
            if (rollbackError is null)
                throw;
            return;
        }
        catch (Exception exception)
        {
            var rollbackError = await TryRollbackAsync(stage.Token, "CONFIGURE_COMMIT_EXCEPTION").ConfigureAwait(false);
            launcherConfigured = false;
            LauncherErrorCode = rollbackError?.Code ?? "HOTKEY_COMMIT_EXCEPTION";
            TraceCommitException(exception, "CONFIGURE_COMMIT");
            ReconcileLauncherRegistration();
            StatusChanged?.Invoke();
            return;
        }

        if (!apply.IsSuccess)
        {
            var rollbackError = await TryRollbackAsync(stage.Token, "CONFIGURE_COMMIT_FAILED").ConfigureAwait(false);
            launcherConfigured = false;
            LauncherErrorCode = rollbackError?.Code ?? apply.ErrorCode ?? "HOTKEY_COMMIT_FAILED";
            ReconcileLauncherRegistration();
            StatusChanged?.Invoke();
            return;
        }

        launcherConfigured = true;
        LauncherErrorCode = null;
        ReconcileLauncherRegistration();
        StatusChanged?.Invoke();
    }

    /// <summary>暂存候选快捷键，只代理 Core 契约，不写设置。</summary>
    public Task<ShortcutStageResult> StageAsync(
        ShortcutChord chord,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return service.StageAsync(chord, cancellationToken);
    }

    /// <summary>提交已持久化成功的候选快捷键，并恢复当前 Launcher Scope 的启停策略。</summary>
    public async Task<ShortcutApplyResult> CommitAsync(
        ShortcutStageToken token,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = await service.CommitAsync(token, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            launcherConfigured = true;
            LauncherErrorCode = null;
        }
        else
        {
            LauncherErrorCode = result.ErrorCode ?? "HOTKEY_COMMIT_FAILED";
        }

        ReconcileLauncherRegistration();
        StatusChanged?.Invoke();
        return result;
    }

    public Task RollbackAsync(
        ShortcutStageToken token,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return service.RollbackAsync(token, cancellationToken);
    }

    /// <summary>
    /// 按 Stage → SQLite Save → Commit 顺序应用快捷键。保存失败会释放候选注册；Commit 失败会
    /// 回滚候选注册并补偿恢复旧设置，避免数据库与当前系统注册在应用重启前后出现不一致。
    /// </summary>
    public async Task<RepositoryResult<AppSettings>> ApplyShortcutChangeAsync(
        AppSettings current,
        AppSettings proposed,
        Func<AppSettings, long, CancellationToken, Task<RepositoryResult<AppSettings>>> saveAsync,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(saveAsync);

        if (current.LauncherShortcut == proposed.LauncherShortcut)
            return await saveAsync(proposed, current.Version, cancellationToken).ConfigureAwait(false);

        var stage = await StageAsync(proposed.LauncherShortcut, cancellationToken).ConfigureAwait(false);
        if (!stage.IsSuccess)
        {
            return RepositoryResult<AppSettings>.Failure(new DataError(
                stage.ErrorCode ?? "HOTKEY_STAGE_FAILED",
                stage.ErrorMessage ?? "快捷键暂存失败，请重试。"));
        }

        RepositoryResult<AppSettings> saved;
        try
        {
            saved = await saveAsync(proposed, current.Version, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var rollbackError = await TryRollbackAsync(stage.Token, "SAVE_CANCELLED").ConfigureAwait(false);
            if (rollbackError is not null)
                return RepositoryResult<AppSettings>.Failure(rollbackError);
            throw;
        }
        catch (Exception exception)
        {
            var rollbackError = await TryRollbackAsync(stage.Token, "SAVE_EXCEPTION").ConfigureAwait(false);
            if (rollbackError is not null)
                return RepositoryResult<AppSettings>.Failure(rollbackError);

            return RepositoryResult<AppSettings>.Failure(CreateOperationError(
                "SETTINGS_SAVE_FAILED",
                "设置保存失败，候选快捷键已释放，请重试。",
                "SAVE_SETTINGS",
                exception));
        }

        if (!saved.IsSuccess || saved.Value is null)
        {
            var rollbackError = await TryRollbackAsync(stage.Token, "SAVE_FAILED").ConfigureAwait(false);
            return rollbackError is null
                ? saved
                : RepositoryResult<AppSettings>.Failure(rollbackError);
        }

        ShortcutApplyResult? apply = null;
        Exception? commitException = null;
        try
        {
            apply = await CommitAsync(stage.Token, cancellationToken).ConfigureAwait(false);
            if (apply is { IsSuccess: true })
                return saved;
        }
        catch (Exception exception)
        {
            commitException = exception;
        }

        // 从这里开始属于一致性补偿：即使用户已取消，也必须尽力释放候选注册并恢复旧设置。
        var rollbackFailure = await TryRollbackAsync(
            stage.Token,
            commitException is null ? "COMMIT_FAILED" : "COMMIT_EXCEPTION").ConfigureAwait(false);
        var restore = await TryRestoreSettingsAsync(
            current,
            saved.Value.Version,
            saveAsync).ConfigureAwait(false);

        if (rollbackFailure is not null && restore.Error is not null)
        {
            return RepositoryResult<AppSettings>.Failure(CreateConsistencyError(
                "HOTKEY_COMPENSATION_FAILED",
                "快捷键切换失败，候选快捷键释放和旧设置恢复均未完成。请重启应用后检查快捷键状态。",
                "ROLLBACK_AND_RESTORE"));
        }

        if (rollbackFailure is not null)
            return new RepositoryResult<AppSettings>(restore.Settings, rollbackFailure, null);
        if (restore.Error is not null)
            return RepositoryResult<AppSettings>.Failure(restore.Error);

        if (commitException is OperationCanceledException cancellationException)
            ExceptionDispatchInfo.Capture(cancellationException).Throw();

        if (commitException is not null)
        {
            return new RepositoryResult<AppSettings>(
                restore.Settings,
                CreateOperationError(
                    "HOTKEY_COMMIT_EXCEPTION",
                    "快捷键提交发生异常，已恢复原快捷键。",
                    "COMMIT_HOTKEY",
                    commitException),
                null);
        }

        return new RepositoryResult<AppSettings>(
            restore.Settings,
            new DataError(
                apply?.ErrorCode ?? "HOTKEY_COMMIT_FAILED",
                apply?.ErrorMessage ?? "快捷键切换失败，已恢复原快捷键。"),
            null);
    }

    private async Task<DataError?> TryRollbackAsync(ShortcutStageToken token, string stageCode)
    {
        try
        {
            await service.RollbackAsync(token, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return CreateOperationError(
                "HOTKEY_ROLLBACK_FAILED",
                "释放候选快捷键失败。请重启应用后再修改快捷键。",
                stageCode,
                exception);
        }
    }

    private static async Task<(AppSettings? Settings, DataError? Error)> TryRestoreSettingsAsync(
        AppSettings current,
        long persistedVersion,
        Func<AppSettings, long, CancellationToken, Task<RepositoryResult<AppSettings>>> saveAsync)
    {
        try
        {
            var restoreCandidate = current with { Version = persistedVersion };
            var restored = await saveAsync(restoreCandidate, persistedVersion, CancellationToken.None).ConfigureAwait(false);
            if (restored.IsSuccess && restored.Value is not null)
                return (restored.Value, null);

            return (null, CreateConsistencyError(
                "HOTKEY_SETTINGS_RESTORE_FAILED",
                "快捷键切换失败，且旧设置恢复失败。请重启应用后检查快捷键状态。",
                restored.Error?.Code ?? "RESTORE_SETTINGS"));
        }
        catch (Exception exception)
        {
            return (null, CreateOperationError(
                "HOTKEY_SETTINGS_RESTORE_FAILED",
                "快捷键切换失败，且旧设置恢复失败。请重启应用后检查快捷键状态。",
                "RESTORE_SETTINGS",
                exception));
        }
    }

    private static DataError CreateOperationError(
        string code,
        string message,
        string stageCode,
        Exception exception)
    {
        var traceId = Guid.NewGuid();
        Trace.TraceError(
            "快捷键操作失败。阶段：{0}；结果码：{1}；TraceId：{2}；异常类型：{3}",
            stageCode,
            code,
            traceId,
            exception.GetType().Name);
        return new DataError(code, $"{message}TraceId：{traceId}");
    }

    private static DataError CreateConsistencyError(string code, string message, string stageCode)
    {
        var traceId = Guid.NewGuid();
        Trace.TraceError(
            "快捷键一致性补偿失败。阶段：{0}；结果码：{1}；TraceId：{2}",
            stageCode,
            code,
            traceId);
        return new DataError(code, $"{message}TraceId：{traceId}");
    }

    private static void TraceCommitException(Exception exception, string stageCode)
    {
        var traceId = Guid.NewGuid();
        Trace.TraceError(
            "快捷键提交异常。阶段：{0}；结果码：{1}；TraceId：{2}；异常类型：{3}",
            stageCode,
            "HOTKEY_COMMIT_EXCEPTION",
            traceId,
            exception.GetType().Name);
    }

    /// <summary>根据前台 Adapter 更新 Launcher 热键的注册范围。</summary>
    public void SetLauncherScopeActive(bool active, string? adapterId)
    {
        if (launcherScopeActive == active && string.Equals(activeAdapterId, adapterId, StringComparison.OrdinalIgnoreCase))
            return;

        launcherScopeActive = active;
        activeAdapterId = active ? adapterId : null;
        ReconcileLauncherRegistration();
        StatusChanged?.Invoke();
    }

    /// <summary>Launcher 显示期间保留热键，以便同一快捷键继续执行关闭动作。</summary>
    public void SetLauncherVisible(bool visible)
    {
        if (launcherVisible == visible) return;
        launcherVisible = visible;
        ReconcileLauncherRegistration();
        StatusChanged?.Invoke();
    }

    /// <summary>练习模式使用同一个全局快捷键，但不要求当前前台存在可投递 Adapter。</summary>
    public void SetPracticeMode(bool active)
    {
        if (practiceMode == active) return;
        practiceMode = active;
        ReconcileLauncherRegistration();
        StatusChanged?.Invoke();
    }

    public void SetPaused(bool value)
    {
        if (paused == value) return;
        paused = value;
        ReconcileLauncherRegistration();
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// 合并 Launcher 注册状态变化，禁止在同步 SetEnabled 等待期间再次进入同一状态切换。
    /// WinEventHook 可能在 WPF Dispatcher 等待原生消息线程时同步回调前台变化；若直接重入，
    /// WindowsShortcutService 会再次等待同一 SemaphoreSlim，导致整个 UI 线程死锁。
    /// 状态 0 表示空闲，1 表示正在应用，2 表示应用期间又出现了更新；外层完成后继续应用最新状态。
    /// </summary>
    private void ReconcileLauncherRegistration()
    {
        if (disposed) return;
        if (Interlocked.CompareExchange(ref launcherRegistrationReconcileState, 1, 0) != 0)
        {
            Interlocked.Exchange(ref launcherRegistrationReconcileState, 2);
            return;
        }

        try
        {
            while (!disposed)
            {
                var shouldEnable = launcherConfigured
                    && !paused
                    && (launcherScopeActive || launcherVisible || practiceMode);
                try
                {
                    service.SetEnabled(shouldEnable);
                    LauncherAvailable = shouldEnable;
                    if (!shouldEnable && LauncherErrorCode is not "HOTKEY_CONFLICT")
                        LauncherErrorCode = launcherConfigured ? null : LauncherErrorCode;
                }
                catch (Exception exception)
                {
                    LauncherAvailable = false;
                    LauncherErrorCode = "HOTKEY_REGISTRATION_FAILED";
                    var traceId = Guid.NewGuid();
                    Trace.TraceError(
                        "全局快捷键状态切换失败。阶段：SET_ENABLED；结果码：HOTKEY_REGISTRATION_FAILED；TraceId：{0}；异常类型：{1}",
                        traceId,
                        exception.GetType().Name);
                }

                if (Interlocked.CompareExchange(ref launcherRegistrationReconcileState, 0, 1) == 1)
                    return;

                _ = Interlocked.CompareExchange(ref launcherRegistrationReconcileState, 1, 2);
            }
        }
        finally
        {
            Interlocked.Exchange(ref launcherRegistrationReconcileState, 0);
        }
    }
    /// <summary>
    /// IShortcutService 明确允许从后台原生消息线程触发 Activated；所有 WPF UI 编排必须经过注入的调度器。
    /// </summary>
    private void Service_Activated(object? sender, EventArgs e)
    {
        if (disposed || paused || !LauncherAvailable) return;
        var receivedTimestamp = Stopwatch.GetTimestamp();
        LauncherActivationReceived?.Invoke(receivedTimestamp);
        dispatchToUi(() =>
        {
            if (!disposed && !paused && LauncherAvailable)
                LauncherHotkeyPressed?.Invoke();
        });
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        service.Activated -= Service_Activated;
        LauncherAvailable = false;
        await service.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

