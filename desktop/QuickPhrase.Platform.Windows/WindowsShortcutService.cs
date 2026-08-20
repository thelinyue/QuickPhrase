using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 将 Core 的稳定按键枚举单向映射为 Win32 Virtual Key。
/// 这里刻意不提供反向映射，避免把 Windows 键码带回 Core 或持久化配置。
/// </summary>
internal static class WindowsShortcutKeyMapper
{
    public static bool TryGetVirtualKey(ShortcutKey key, out uint virtualKey)
    {
        virtualKey = key switch
        {
            ShortcutKey.Space => 0x20,
            ShortcutKey.A => 0x41,
            ShortcutKey.B => 0x42,
            ShortcutKey.C => 0x43,
            ShortcutKey.D => 0x44,
            ShortcutKey.E => 0x45,
            ShortcutKey.F => 0x46,
            ShortcutKey.G => 0x47,
            ShortcutKey.H => 0x48,
            ShortcutKey.I => 0x49,
            ShortcutKey.J => 0x4A,
            ShortcutKey.K => 0x4B,
            ShortcutKey.L => 0x4C,
            ShortcutKey.M => 0x4D,
            ShortcutKey.N => 0x4E,
            ShortcutKey.O => 0x4F,
            ShortcutKey.P => 0x50,
            ShortcutKey.Q => 0x51,
            ShortcutKey.R => 0x52,
            ShortcutKey.S => 0x53,
            ShortcutKey.T => 0x54,
            ShortcutKey.U => 0x55,
            ShortcutKey.V => 0x56,
            ShortcutKey.W => 0x57,
            ShortcutKey.X => 0x58,
            ShortcutKey.Y => 0x59,
            ShortcutKey.Z => 0x5A,
            ShortcutKey.Digit0 => 0x30,
            ShortcutKey.Digit1 => 0x31,
            ShortcutKey.Digit2 => 0x32,
            ShortcutKey.Digit3 => 0x33,
            ShortcutKey.Digit4 => 0x34,
            ShortcutKey.Digit5 => 0x35,
            ShortcutKey.Digit6 => 0x36,
            ShortcutKey.Digit7 => 0x37,
            ShortcutKey.Digit8 => 0x38,
            ShortcutKey.Digit9 => 0x39,
            ShortcutKey.F1 => 0x70,
            ShortcutKey.F2 => 0x71,
            ShortcutKey.F3 => 0x72,
            ShortcutKey.F4 => 0x73,
            ShortcutKey.F5 => 0x74,
            ShortcutKey.F6 => 0x75,
            ShortcutKey.F7 => 0x76,
            ShortcutKey.F8 => 0x77,
            ShortcutKey.F9 => 0x78,
            ShortcutKey.F10 => 0x79,
            ShortcutKey.F11 => 0x7A,
            ShortcutKey.F12 => 0x7B,
            _ => 0,
        };

        return virtualKey != 0;
    }

    public static uint GetNativeModifiers(ShortcutModifiers modifiers)
    {
        uint nativeModifiers = 0;
        if ((modifiers & ShortcutModifiers.Alt) != 0)
            nativeModifiers |= 0x0001;
        if ((modifiers & ShortcutModifiers.Ctrl) != 0)
            nativeModifiers |= 0x0002;
        if ((modifiers & ShortcutModifiers.Shift) != 0)
            nativeModifiers |= 0x0004;
        if ((modifiers & ShortcutModifiers.Win) != 0)
            nativeModifiers |= 0x0008;
        return nativeModifiers;
    }
}

/// <summary>Win32 注册调用的最小结果，只保留成功状态和系统错误码。</summary>
internal readonly record struct WindowsShortcutNativeResult(bool IsSuccess, int ErrorCode)
{
    public static WindowsShortcutNativeResult Success() => new(true, 0);

    public static WindowsShortcutNativeResult Failure(int errorCode) => new(false, errorCode);
}

/// <summary>
/// 隔离原生消息窗口的最小边界。生产实现拥有独立消息线程；测试替身只验证服务状态机，
/// 从而不依赖测试机器当前有哪些系统快捷键被占用。
/// </summary>
internal interface IWindowsShortcutNativeHost : IAsyncDisposable
{
    event Action<int>? HotkeyPressed;

    int ManagedThreadId { get; }

    IntPtr WindowHandle { get; }

    bool IsMessageOnlyWindow { get; }

    WindowsShortcutNativeResult Register(int id, uint modifiers, uint virtualKey);

    WindowsShortcutNativeResult Unregister(int id);
}

/// <summary>
/// Windows 全局快捷键服务。服务使用两个固定注册 ID：当前快捷键占用一个 ID，Stage 在备用 ID
/// 上先完成系统注册；只有 Commit 成功释放旧注册后才切换 ActiveChord。这样系统冲突、保存失败
/// 或用户取消都不会破坏仍在工作的旧快捷键。
/// </summary>
public sealed class WindowsShortcutService : IShortcutService
{
    internal const int PrimaryRegistrationId = 0x5150;
    internal const int SecondaryRegistrationId = 0x5151;

    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object disposeSync = new();
    private readonly IWindowsShortcutNativeHost nativeHost;
    private ShortcutChord activeChord;
    private int? activeRegistrationId;
    private StagedRegistration? stagedRegistration;
    private bool activeRegistrationIsRegistered;
    private bool enabled = true;
    private ActivationSnapshot activationSnapshot = ActivationSnapshot.Empty;
    private Task? disposeTask;
    private int disposeState;

    public WindowsShortcutService()
        : this(new WindowsShortcutNativeHost())
    {
    }

    internal WindowsShortcutService(IWindowsShortcutNativeHost nativeHost)
    {
        this.nativeHost = nativeHost ?? throw new ArgumentNullException(nameof(nativeHost));
        this.nativeHost.HotkeyPressed += OnHotkeyPressed;
        PublishActivationSnapshot(disposed: false);
    }

    /// <summary>
    /// 快捷键激活事件固定从后台线程触发，不承诺 WPF Dispatcher 上下文。
    /// Desktop 协调器必须显式切换到 Dispatcher 后再访问窗口或其他 DispatcherObject。
    /// </summary>
    public event EventHandler? Activated;

    public ShortcutChord ActiveChord => Volatile.Read(ref activationSnapshot).Chord;

    public async Task<ShortcutStageResult> StageAsync(
        ShortcutChord chord,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ShortcutChordValidator.Validate(chord);
        if (!validation.IsValid)
        {
            return ShortcutStageResult.Failure(
                validation.ErrorCode!,
                validation.ErrorMessage!);
        }

        if (!WindowsShortcutKeyMapper.TryGetVirtualKey(chord.Key, out var virtualKey))
        {
            return ShortcutStageResult.Failure(
                ShortcutValidationErrorCodes.KeyUnsupported,
                "快捷键包含不支持的普通按键。");
        }

        ThrowIfDisposeRequested();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposeRequested();
            // Stage 即使在 Launcher Scope 暂停时也必须尝试原生注册，以便设置事务仍能检测系统冲突。
            // 暂存注册不会触发 Activated；Commit 在 disabled 状态下会立即释放它。
            if (stagedRegistration is not null)
            {
                return ShortcutStageResult.Failure(
                    "HOTKEY_STAGE_PENDING",
                    "已有待处理的快捷键，请先提交或取消。");
            }

            var registrationId = activeRegistrationId == PrimaryRegistrationId
                ? SecondaryRegistrationId
                : PrimaryRegistrationId;
            var nativeResult = nativeHost.Register(
                registrationId,
                WindowsShortcutKeyMapper.GetNativeModifiers(chord.Modifiers),
                virtualKey);

            if (!nativeResult.IsSuccess)
            {
                var errorCode = nativeResult.ErrorCode == ErrorHotkeyAlreadyRegistered
                    ? "HOTKEY_CONFLICT"
                    : "HOTKEY_REGISTER_FAILED";
                TraceFailure("Stage", errorCode, nativeResult.ErrorCode);
                return nativeResult.ErrorCode == ErrorHotkeyAlreadyRegistered
                    ? ShortcutStageResult.Failure(errorCode, "快捷键已被系统或其他应用占用。")
                    : ShortcutStageResult.Failure(errorCode, "无法注册新的全局快捷键，请稍后重试。");
            }

            var token = ShortcutStageToken.Create();
            stagedRegistration = new StagedRegistration(token, chord, registrationId);
            return ShortcutStageResult.Success(token);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ShortcutApplyResult> CommitAsync(
        ShortcutStageToken token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposeRequested();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposeRequested();
            if (stagedRegistration is null || stagedRegistration.Token != token)
            {
                return ShortcutApplyResult.Failure(
                    "HOTKEY_STAGE_NOT_FOUND",
                    "找不到待提交的快捷键，请重新设置。");
            }

            if (!enabled)
            {
                // disabled 仅代表 Launcher 当前不响应全局热键，不应阻断设置保存。
                // Stage 已完成冲突检测；Commit 先释放候选注册，再把新 Chord 作为未注册的活动配置保存。
                var unregisterStagedResult = nativeHost.Unregister(stagedRegistration.RegistrationId);
                if (!unregisterStagedResult.IsSuccess)
                {
                    TraceFailure("Commit.Disabled", "HOTKEY_UNREGISTER_FAILED", unregisterStagedResult.ErrorCode);
                    return ShortcutApplyResult.Failure(
                        "HOTKEY_UNREGISTER_FAILED",
                        "无法停用新的快捷键，已保留原有快捷键。");
                }

                activeChord = stagedRegistration.Chord;
                activeRegistrationId = stagedRegistration.RegistrationId;
                activeRegistrationIsRegistered = false;
                stagedRegistration = null;
                PublishActivationSnapshot(disposed: false);
                return ShortcutApplyResult.Success();
            }

            if (activeRegistrationId is { } oldRegistrationId && activeRegistrationIsRegistered)
            {
                var unregisterResult = nativeHost.Unregister(oldRegistrationId);
                if (!unregisterResult.IsSuccess)
                {
                    TraceFailure("Commit", "HOTKEY_UNREGISTER_FAILED", unregisterResult.ErrorCode);
                    return ShortcutApplyResult.Failure(
                        "HOTKEY_UNREGISTER_FAILED",
                        "无法释放原快捷键，已保留原有快捷键。");
                }
            }

            activeChord = stagedRegistration.Chord;
            activeRegistrationId = stagedRegistration.RegistrationId;
            activeRegistrationIsRegistered = true;
            stagedRegistration = null;
            PublishActivationSnapshot(disposed: false);
            return ShortcutApplyResult.Success();
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task RollbackAsync(
        ShortcutStageToken token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposeRequested();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposeRequested();
            if (stagedRegistration is null || stagedRegistration.Token != token)
                return;

            var unregisterResult = nativeHost.Unregister(stagedRegistration.RegistrationId);
            if (!unregisterResult.IsSuccess)
            {
                TraceFailure("Rollback", "HOTKEY_ROLLBACK_UNREGISTER_FAILED", unregisterResult.ErrorCode);
                throw new InvalidOperationException("无法释放暂存的快捷键，请稍后重试。");
            }

            stagedRegistration = null;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void SetEnabled(bool enabled)
    {
        ThrowIfDisposeRequested();
        operationGate.Wait();
        try
        {
            ThrowIfDisposeRequested();
            if (this.enabled == enabled)
                return;

            if (!enabled)
            {
                if (activeRegistrationId is { } activeId && activeRegistrationIsRegistered)
                {
                    var unregisterResult = nativeHost.Unregister(activeId);
                    if (!unregisterResult.IsSuccess)
                    {
                        TraceFailure("SetEnabled.Disable", "HOTKEY_DISABLE_UNREGISTER_FAILED", unregisterResult.ErrorCode);
                        throw new InvalidOperationException("无法暂停全局快捷键，原快捷键仍保持启用。");
                    }

                    activeRegistrationIsRegistered = false;
                }

                this.enabled = false;
                PublishActivationSnapshot(disposed: false);
                return;
            }

            if (activeRegistrationId is { } registrationId)
            {
                if (!WindowsShortcutKeyMapper.TryGetVirtualKey(activeChord.Key, out var virtualKey))
                    throw new InvalidOperationException("无法恢复全局快捷键：按键映射无效。");

                var registerResult = nativeHost.Register(
                    registrationId,
                    WindowsShortcutKeyMapper.GetNativeModifiers(activeChord.Modifiers),
                    virtualKey);
                if (!registerResult.IsSuccess)
                {
                    TraceFailure("SetEnabled.Enable", "HOTKEY_ENABLE_REGISTER_FAILED", registerResult.ErrorCode);
                    throw new InvalidOperationException("无法恢复全局快捷键，快捷键可能已被其他应用占用。");
                }

                activeRegistrationIsRegistered = true;
            }

            this.enabled = true;
            PublishActivationSnapshot(disposed: false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeSync)
        {
            if (disposeTask is null)
            {
                Interlocked.CompareExchange(ref disposeState, 1, 0);
                disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            nativeHost.HotkeyPressed -= OnHotkeyPressed;

            if (stagedRegistration is { } staged)
            {
                try
                {
                    var result = nativeHost.Unregister(staged.RegistrationId);
                    if (!result.IsSuccess)
                        TraceFailure("Dispose.Staged", "HOTKEY_DISPOSE_UNREGISTER_FAILED", result.ErrorCode);
                }
                catch (Exception exception)
                {
                    TraceFailure("Dispose.Staged", "HOTKEY_DISPOSE_UNREGISTER_FAILED", 0, exception);
                }

                stagedRegistration = null;
            }

            if (activeRegistrationId is { } activeId && activeRegistrationIsRegistered)
            {
                try
                {
                    var result = nativeHost.Unregister(activeId);
                    if (!result.IsSuccess)
                        TraceFailure("Dispose.Active", "HOTKEY_DISPOSE_UNREGISTER_FAILED", result.ErrorCode);
                }
                catch (Exception exception)
                {
                    TraceFailure("Dispose.Active", "HOTKEY_DISPOSE_UNREGISTER_FAILED", 0, exception);
                }

                activeRegistrationIsRegistered = false;
            }

            enabled = false;
            PublishActivationSnapshot(disposed: true);
        }
        finally
        {
            operationGate.Release();
        }

        try
        {
            await nativeHost.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref disposeState, 2);
            GC.SuppressFinalize(this);
        }
    }

    private void OnHotkeyPressed(int registrationId)
    {
        var snapshot = Volatile.Read(ref activationSnapshot);
        if (!snapshot.ShouldActivate(registrationId))
            return;

        // 原生消息线程只负责确认匿名注册 ID。真正的公开事件转交线程池，避免订阅者阻塞
        // HWND_MESSAGE 消息循环；Desktop 收到事件后仍必须切换到 WPF Dispatcher。
        ThreadPool.QueueUserWorkItem(
            static state => state.Service.RaiseActivated(state.RegistrationId),
            (Service: this, RegistrationId: registrationId),
            preferLocal: false);
    }

    private void RaiseActivated(int registrationId)
    {
        var snapshot = Volatile.Read(ref activationSnapshot);
        if (!snapshot.ShouldActivate(registrationId))
            return;

        var handlers = Activated;
        if (handlers is null)
            return;

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                TraceFailure("Activated", "HOTKEY_ACTIVATION_HANDLER_FAILED", 0, exception);
            }
        }
    }

    private void PublishActivationSnapshot(bool disposed)
    {
        Volatile.Write(
            ref activationSnapshot,
            new ActivationSnapshot(
                activeChord,
                activeRegistrationId,
                activeRegistrationIsRegistered,
                enabled,
                disposed));
    }

    private void ThrowIfDisposeRequested()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
    }

    private static void TraceFailure(string stage, string errorCode, int nativeErrorCode, Exception? exception = null)
    {
        var traceId = Guid.NewGuid().ToString("N");
        Trace.TraceError(
            "全局快捷键操作失败。阶段：{0}；结果码：{1}；NativeErrorCode：{2}；TraceId：{3}；异常类型：{4}",
            stage,
            errorCode,
            nativeErrorCode,
            traceId,
            exception?.GetType().Name ?? "无");
    }

    private sealed record StagedRegistration(
        ShortcutStageToken Token,
        ShortcutChord Chord,
        int RegistrationId);

    private sealed record ActivationSnapshot(
        ShortcutChord Chord,
        int? RegistrationId,
        bool IsRegistered,
        bool IsEnabled,
        bool IsDisposed)
    {
        public static ActivationSnapshot Empty { get; } =
            new(default, null, IsRegistered: false, IsEnabled: true, IsDisposed: false);

        public bool ShouldActivate(int registrationId) =>
            !IsDisposed
            && IsEnabled
            && IsRegistered
            && RegistrationId == registrationId;
    }
}

/// <summary>测试与生产共享的消息投递边界，用于显式处理 PostMessage 失败。</summary>
internal interface IWindowsShortcutMessagePoster
{
    bool TryPost(IntPtr windowHandle, uint message, out int errorCode);

    bool TryPostThread(uint threadId, uint message, out int errorCode);
}

/// <summary>生产消息投递器，只返回成功状态和 Win32 错误码，不记录消息参数。</summary>
internal sealed class WindowsShortcutMessagePoster : IWindowsShortcutMessagePoster
{
    private WindowsShortcutMessagePoster()
    {
    }

    public static WindowsShortcutMessagePoster Instance { get; } = new();

    public bool TryPost(IntPtr windowHandle, uint message, out int errorCode) =>
        WindowsShortcutNativeHost.TryPostNativeMessage(windowHandle, message, out errorCode);

    public bool TryPostThread(uint threadId, uint message, out int errorCode) =>
        WindowsShortcutNativeHost.TryPostNativeThreadMessage(threadId, message, out errorCode);
}

/// <summary>消息线程待执行工作的非泛型边界，便于关闭或异常时统一失败所有等待者。</summary>
internal interface IWindowsShortcutWorkItem
{
    void Execute();

    void Fail(Exception exception);
}

/// <summary>承载一次同步原生调用及其 completion，保证结果只会完成一次。</summary>
internal sealed class WindowsShortcutWorkItem<T> : IWindowsShortcutWorkItem
{
    private readonly Func<T> action;
    private readonly TaskCompletionSource<T> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WindowsShortcutWorkItem(Func<T> action)
    {
        this.action = action;
    }

    public Task<T> Completion => completion.Task;

    public void Execute()
    {
        try
        {
            completion.TrySetResult(action());
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    public void Fail(Exception exception) => completion.TrySetException(exception);
}

/// <summary>
/// 原生消息线程工作队列。关闭或消息循环异常时会停止接收新工作，并显式失败全部待处理 completion，
/// 避免调用方在消息窗口已经退出后仍无限等待。
/// </summary>
internal sealed class WindowsShortcutWorkQueue
{
    private readonly object syncRoot = new();
    private readonly Queue<IWindowsShortcutWorkItem> pending = new();
    private Exception? terminalFailure;

    public WindowsShortcutWorkItem<T> Enqueue<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (syncRoot)
        {
            if (terminalFailure is not null)
                ExceptionDispatchInfo.Capture(terminalFailure).Throw();

            var item = new WindowsShortcutWorkItem<T>(action);
            pending.Enqueue(item);
            return item;
        }
    }

    public bool TryDequeue(out IWindowsShortcutWorkItem? item)
    {
        lock (syncRoot)
        {
            if (pending.Count == 0)
            {
                item = null;
                return false;
            }

            item = pending.Dequeue();
            return true;
        }
    }

    public void FailPending(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        List<IWindowsShortcutWorkItem> items;
        lock (syncRoot)
        {
            terminalFailure ??= exception;
            items = [.. pending];
            pending.Clear();
        }

        foreach (var item in items)
            item.Fail(terminalFailure);
    }
}

/// <summary>
/// RegisterHotKey 的原生宿主。它不借用 WPF UI 线程，而是在独立 MTA 线程创建 HWND_MESSAGE
/// 消息窗口并运行 GetMessage 循环；对外只上报匿名注册 ID，不泄漏 HWND、Virtual Key 或消息结构。
/// 所有 Invoke、关闭和线程异常都通过同一生命周期协议完成或失败对应 completion。
/// </summary>
internal sealed class WindowsShortcutNativeHost : IWindowsShortcutNativeHost
{
    internal const uint WorkMessage = 0x8001;
    internal const uint CloseMessage = 0x0010;

    private const uint HotkeyMessage = 0x0312;
    private const uint DestroyMessage = 0x0002;
    internal const uint QuitMessage = 0x0012;
    private static readonly IntPtr MessageOnlyWindowParent = new(-3);
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<Thread, StartupFailureCleanupOwnership>
        StartupFailureCleanups = new();

    private readonly object lifecycleSync = new();
    private readonly WindowsShortcutWorkQueue workItems = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource threadExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread messageThread;
    private readonly string windowClassName = $"QuickPhrase.Shortcut.{Guid.NewGuid():N}";
    private readonly WindowProcedure windowProcedure;
    private readonly IWindowsShortcutMessagePoster messagePoster;
    private readonly TimeSpan operationTimeout;
    private readonly Action<CancellationToken>? beforeReady;
    private readonly CancellationTokenSource startupCancellation = new();
    private TaskCompletionSource? disposeCompletion;
    private IntPtr windowHandle;
    private int managedThreadId;
    private uint nativeThreadId;
    private bool closing;
    private bool startupCancellationOwnershipTransferred;

    public WindowsShortcutNativeHost()
        : this(WindowsShortcutMessagePoster.Instance, DefaultOperationTimeout)
    {
    }

    internal WindowsShortcutNativeHost(
        IWindowsShortcutMessagePoster messagePoster,
        TimeSpan operationTimeout,
        Action<CancellationToken>? beforeReady = null)
    {
        this.messagePoster = messagePoster ?? throw new ArgumentNullException(nameof(messagePoster));
        if (operationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        this.operationTimeout = operationTimeout;
        this.beforeReady = beforeReady;

        windowProcedure = WindowProc;
        messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "QuickPhrase 全局快捷键消息线程",
        };
        messageThread.SetApartmentState(ApartmentState.MTA);
        messageThread.Start();
        try
        {
            ready.Task.WaitAsync(operationTimeout).GetAwaiter().GetResult();
        }
        catch (Exception startupException)
        {
            StopAfterStartupFailure(startupException);
            throw;
        }
        finally
        {
            if (!startupCancellationOwnershipTransferred)
                startupCancellation.Dispose();
        }
    }

    public event Action<int>? HotkeyPressed;

    public int ManagedThreadId => managedThreadId;

    public IntPtr WindowHandle => windowHandle;

    public bool IsMessageOnlyWindow =>
        windowHandle != IntPtr.Zero
        && NativeMethods.FindWindowEx(MessageOnlyWindowParent, IntPtr.Zero, windowClassName, null) == windowHandle;

    public WindowsShortcutNativeResult Register(int id, uint modifiers, uint virtualKey)
    {
        return Invoke(() => NativeMethods.RegisterHotKey(windowHandle, id, modifiers, virtualKey)
            ? WindowsShortcutNativeResult.Success()
            : WindowsShortcutNativeResult.Failure(Marshal.GetLastWin32Error()));
    }

    public WindowsShortcutNativeResult Unregister(int id)
    {
        return Invoke(() => NativeMethods.UnregisterHotKey(windowHandle, id)
            ? WindowsShortcutNativeResult.Success()
            : WindowsShortcutNativeResult.Failure(Marshal.GetLastWin32Error()));
    }

    public ValueTask DisposeAsync()
    {
        Task completionTask;
        var shouldStart = false;
        lock (lifecycleSync)
        {
            if (disposeCompletion is null)
            {
                closing = true;
                workItems.FailPending(new ObjectDisposedException(nameof(WindowsShortcutNativeHost)));
                disposeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                shouldStart = true;
            }

            completionTask = disposeCompletion.Task;
        }

        if (shouldStart)
        {
            ThreadPool.QueueUserWorkItem(
                static state => _ = state.Host.DisposeCoreAsync(state.Completion),
                (Host: this, Completion: disposeCompletion!),
                preferLocal: false);
        }

        return new ValueTask(completionTask);
    }

    private T Invoke<T>(Func<T> action)
    {
        if (Environment.CurrentManagedThreadId == managedThreadId)
        {
            lock (lifecycleSync)
            {
                ObjectDisposedException.ThrowIf(closing, this);
            }

            return action();
        }

        WindowsShortcutWorkItem<T> workItem;
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            workItem = workItems.Enqueue(action);
            if (!messagePoster.TryPost(windowHandle, WorkMessage, out var nativeErrorCode))
            {
                var exception = new Win32Exception(nativeErrorCode, "无法通知全局快捷键消息线程。");
                closing = true;
                workItems.FailPending(exception);
                TraceNativeFailure("Invoke.Post", "HOTKEY_MESSAGE_POST_FAILED", nativeErrorCode, exception);
                throw exception;
            }
        }

        try
        {
            return workItem.Completion.WaitAsync(operationTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException exception)
        {
            lock (lifecycleSync)
            {
                closing = true;
                workItems.FailPending(exception);
            }

            TraceNativeFailure("Invoke.Wait", "HOTKEY_MESSAGE_TIMEOUT", 0, exception);
            throw new TimeoutException("全局快捷键消息线程响应超时。", exception);
        }
    }

    /// <summary>
    /// 构造阶段失败时调用方拿不到宿主实例，因此先尝试在限定时间内完成关闭。
    /// 若任意初始化步骤不响应取消，构造仍需有界返回；此时把宿主、取消源和退出任务
    /// 转交给静态清理所有者，直到消息线程真实退出，避免形成无人持有的后台线程。
    /// </summary>
    private void StopAfterStartupFailure(Exception startupException)
    {
        lock (lifecycleSync)
        {
            closing = true;
            workItems.FailPending(startupException);
        }

        startupCancellation.Cancel();
        RequestStartupThreadExit();

        if (!WaitForStartupThreadExit())
        {
            RequestStartupThreadExit(forceThreadQuit: true);
            if (!WaitForStartupThreadExit())
            {
                TransferStartupFailureCleanupOwnership(startupException);
                return;
            }
        }

        if (Environment.CurrentManagedThreadId != managedThreadId
            && !messageThread.Join(operationTimeout))
        {
            TransferStartupFailureCleanupOwnership(startupException);
        }
    }

    private bool WaitForStartupThreadExit()
    {
        try
        {
            threadExited.Task.WaitAsync(operationTimeout).GetAwaiter().GetResult();
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void RequestStartupThreadExit(bool forceThreadQuit = false)
    {
        var handle = windowHandle;
        if (!forceThreadQuit && handle != IntPtr.Zero)
        {
            if (messagePoster.TryPost(handle, CloseMessage, out _))
                return;
        }

        if (nativeThreadId != 0)
            messagePoster.TryPostThread(nativeThreadId, QuitMessage, out _);
    }

    private void TransferStartupFailureCleanupOwnership(Exception startupException)
    {
        var ownership = new StartupFailureCleanupOwnership(this);
        StartupFailureCleanups[messageThread] = ownership;
        startupCancellationOwnershipTransferred = true;
        TraceNativeFailure(
            "Startup.CleanupDeferred",
            "HOTKEY_STARTUP_CLEANUP_DEFERRED",
            GetNativeErrorCode(startupException),
            startupException);
        ownership.Start();
    }

    /// <summary>
    /// 仅供生命周期测试和诊断观察延迟清理所有权；不公开任何快捷键或原生键码信息。
    /// </summary>
    internal static bool TryGetStartupFailureCleanup(Thread thread, out Task cleanupTask)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (StartupFailureCleanups.TryGetValue(thread, out var ownership))
        {
            cleanupTask = ownership.Completion;
            return true;
        }

        cleanupTask = Task.CompletedTask;
        return false;
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            var handle = windowHandle;
            var closeErrorCode = 0;
            var closePosted = handle == IntPtr.Zero
                || messagePoster.TryPost(handle, CloseMessage, out closeErrorCode);
            if (!closePosted)
            {
                TraceNativeFailure("Dispose.PostClose", "HOTKEY_CLOSE_POST_FAILED", closeErrorCode);
                if (nativeThreadId == 0
                    || !messagePoster.TryPostThread(nativeThreadId, QuitMessage, out _))
                {
                    var fallbackError = Marshal.GetLastWin32Error();
                    throw new Win32Exception(
                        fallbackError,
                        "无法关闭全局快捷键消息线程。");
                }
            }

            try
            {
                await threadExited.Task.WaitAsync(operationTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (nativeThreadId != 0)
                    messagePoster.TryPostThread(nativeThreadId, QuitMessage, out _);
                await threadExited.Task.WaitAsync(operationTimeout).ConfigureAwait(false);
            }

            if (Environment.CurrentManagedThreadId != managedThreadId
                && !messageThread.Join(operationTimeout))
            {
                throw new TimeoutException("全局快捷键消息线程未在限定时间内退出。");
            }

            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            workItems.FailPending(exception);
            TraceNativeFailure("Dispose", "HOTKEY_NATIVE_DISPOSE_FAILED", 0, exception);
            completion.TrySetException(exception);
        }
    }

    private void MessageLoop()
    {
        managedThreadId = Environment.CurrentManagedThreadId;
        nativeThreadId = NativeMethods.GetCurrentThreadId();
        var moduleHandle = IntPtr.Zero;
        ushort classAtom = 0;

        try
        {
            startupCancellation.Token.ThrowIfCancellationRequested();
            beforeReady?.Invoke(startupCancellation.Token);
            startupCancellation.Token.ThrowIfCancellationRequested();

            moduleHandle = NativeMethods.GetModuleHandle(null);
            startupCancellation.Token.ThrowIfCancellationRequested();

            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(windowProcedure),
                Instance = moduleHandle,
                ClassName = windowClassName,
            };

            startupCancellation.Token.ThrowIfCancellationRequested();
            classAtom = NativeMethods.RegisterClassEx(ref windowClass);
            if (classAtom == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册全局快捷键消息窗口类。");
            startupCancellation.Token.ThrowIfCancellationRequested();

            windowHandle = NativeMethods.CreateWindowEx(
                0,
                windowClassName,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                MessageOnlyWindowParent,
                IntPtr.Zero,
                moduleHandle,
                IntPtr.Zero);
            if (windowHandle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建全局快捷键消息窗口。");
            startupCancellation.Token.ThrowIfCancellationRequested();

            PublishReady();

            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == 0)
                    break;
                if (result == -1)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "全局快捷键消息循环读取失败。");

                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested)
        {
            ready.TrySetCanceled(startupCancellation.Token);
        }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
            workItems.FailPending(exception);
            lock (lifecycleSync)
                closing = true;
            TraceNativeFailure("MessageLoop", "HOTKEY_MESSAGE_LOOP_FAILED", GetNativeErrorCode(exception), exception);
        }
        finally
        {
            var handle = windowHandle;
            if (handle != IntPtr.Zero)
                NativeMethods.DestroyWindow(handle);
            windowHandle = IntPtr.Zero;
            if (classAtom != 0)
                NativeMethods.UnregisterClass(windowClassName, moduleHandle);
            workItems.FailPending(new ObjectDisposedException(nameof(WindowsShortcutNativeHost)));
            threadExited.TrySetResult();
        }
    }

    private void PublishReady()
    {
        lock (lifecycleSync)
        {
            startupCancellation.Token.ThrowIfCancellationRequested();
            if (closing)
                throw new OperationCanceledException(startupCancellation.Token);
            ready.TrySetResult();
        }
    }

    /// <summary>
    /// 延迟清理对象由静态表强引用：即使构造函数已经抛出，宿主和取消源仍有明确所有者。
    /// 它不强行终止线程，只等待受阻初始化最终返回，再完成正常 finally 清理。
    /// </summary>
    private sealed class StartupFailureCleanupOwnership
    {
        private readonly WindowsShortcutNativeHost host;
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StartupFailureCleanupOwnership(WindowsShortcutNativeHost host)
        {
            this.host = host;
        }

        public Task Completion => completion.Task;

        public void Start() => _ = ObserveExitAsync();

        private async Task ObserveExitAsync()
        {
            Exception? cleanupException = null;
            try
            {
                await host.threadExited.Task.ConfigureAwait(false);
                if (Environment.CurrentManagedThreadId != host.managedThreadId
                    && host.messageThread.IsAlive)
                {
                    host.messageThread.Join();
                }
            }
            catch (Exception exception)
            {
                cleanupException = exception;
                TraceNativeFailure(
                    "Startup.CleanupObserver",
                    "HOTKEY_STARTUP_CLEANUP_FAILED",
                    GetNativeErrorCode(exception),
                    exception);
            }
            finally
            {
                host.startupCancellation.Dispose();
                StartupFailureCleanups.TryRemove(host.messageThread, out _);
                if (cleanupException is null)
                    completion.TrySetResult();
                else
                    completion.TrySetException(cleanupException);
            }
        }
    }

    private IntPtr WindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WorkMessage:
                while (workItems.TryDequeue(out var workItem))
                    workItem!.Execute();
                return IntPtr.Zero;

            case HotkeyMessage:
                try
                {
                    HotkeyPressed?.Invoke(unchecked((int)wParam.ToInt64()));
                }
                catch (Exception exception)
                {
                    TraceNativeFailure("WindowProc.Hotkey", "HOTKEY_NATIVE_CALLBACK_FAILED", 0, exception);
                }
                return IntPtr.Zero;

            case CloseMessage:
                NativeMethods.DestroyWindow(handle);
                return IntPtr.Zero;

            case DestroyMessage:
                windowHandle = IntPtr.Zero;
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return NativeMethods.DefWindowProc(handle, message, wParam, lParam);
        }
    }

    internal static bool TryPostNativeMessage(IntPtr handle, uint message, out int errorCode)
    {
        if (NativeMethods.PostMessage(handle, message, IntPtr.Zero, IntPtr.Zero))
        {
            errorCode = 0;
            return true;
        }

        errorCode = Marshal.GetLastWin32Error();
        return false;
    }

    private static int GetNativeErrorCode(Exception exception) =>
        exception is Win32Exception win32Exception ? win32Exception.NativeErrorCode : 0;

    private static void TraceNativeFailure(
        string stage,
        string errorCode,
        int nativeErrorCode,
        Exception? exception = null)
    {
        var traceId = Guid.NewGuid().ToString("N");
        Trace.TraceError(
            "全局快捷键原生宿主失败。阶段：{0}；结果码：{1}；NativeErrorCode：{2}；TraceId：{3}；异常类型：{4}",
            stage,
            errorCode,
            nativeErrorCode,
            traceId,
            exception?.GetType().Name ?? "无");
    }

    internal static bool TryPostNativeThreadMessage(uint threadId, uint message, out int errorCode)
    {
        if (NativeMethods.PostThreadMessage(threadId, message, IntPtr.Zero, IntPtr.Zero))
        {
            errorCode = 0;
            return true;
        }

        errorCode = Marshal.GetLastWin32Error();
        return false;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr BackgroundBrush;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr WindowHandle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern ushort RegisterClassEx(ref WindowClass windowClass);

        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        internal static extern IntPtr DefWindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetMessage(out NativeMessage message, IntPtr windowHandle, uint minimumMessage, uint maximumMessage);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        internal static extern IntPtr DispatchMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        internal static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

        [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);
    }
}
