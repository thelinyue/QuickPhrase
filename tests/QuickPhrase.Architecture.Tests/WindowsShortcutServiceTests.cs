using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

/// <summary>
/// 锁定 Windows 全局快捷键服务的键码映射、双注册暂存和启停语义。
/// 测试使用最小原生宿主替身，避免把系统当前占用的快捷键当成稳定测试前提。
/// </summary>
public sealed class WindowsShortcutServiceTests
{
    public static TheoryData<ShortcutKey, uint> SupportedVirtualKeys => new()
    {
        { ShortcutKey.Space, 0x20 },
        { ShortcutKey.A, 0x41 },
        { ShortcutKey.B, 0x42 },
        { ShortcutKey.C, 0x43 },
        { ShortcutKey.D, 0x44 },
        { ShortcutKey.E, 0x45 },
        { ShortcutKey.F, 0x46 },
        { ShortcutKey.G, 0x47 },
        { ShortcutKey.H, 0x48 },
        { ShortcutKey.I, 0x49 },
        { ShortcutKey.J, 0x4A },
        { ShortcutKey.K, 0x4B },
        { ShortcutKey.L, 0x4C },
        { ShortcutKey.M, 0x4D },
        { ShortcutKey.N, 0x4E },
        { ShortcutKey.O, 0x4F },
        { ShortcutKey.P, 0x50 },
        { ShortcutKey.Q, 0x51 },
        { ShortcutKey.R, 0x52 },
        { ShortcutKey.S, 0x53 },
        { ShortcutKey.T, 0x54 },
        { ShortcutKey.U, 0x55 },
        { ShortcutKey.V, 0x56 },
        { ShortcutKey.W, 0x57 },
        { ShortcutKey.X, 0x58 },
        { ShortcutKey.Y, 0x59 },
        { ShortcutKey.Z, 0x5A },
        { ShortcutKey.Digit0, 0x30 },
        { ShortcutKey.Digit1, 0x31 },
        { ShortcutKey.Digit2, 0x32 },
        { ShortcutKey.Digit3, 0x33 },
        { ShortcutKey.Digit4, 0x34 },
        { ShortcutKey.Digit5, 0x35 },
        { ShortcutKey.Digit6, 0x36 },
        { ShortcutKey.Digit7, 0x37 },
        { ShortcutKey.Digit8, 0x38 },
        { ShortcutKey.Digit9, 0x39 },
        { ShortcutKey.F1, 0x70 },
        { ShortcutKey.F2, 0x71 },
        { ShortcutKey.F3, 0x72 },
        { ShortcutKey.F4, 0x73 },
        { ShortcutKey.F5, 0x74 },
        { ShortcutKey.F6, 0x75 },
        { ShortcutKey.F7, 0x76 },
        { ShortcutKey.F8, 0x77 },
        { ShortcutKey.F9, 0x78 },
        { ShortcutKey.F10, 0x79 },
        { ShortcutKey.F11, 0x7A },
        { ShortcutKey.F12, 0x7B },
    };

    [Theory]
    [MemberData(nameof(SupportedVirtualKeys))]
    public void EveryCoreShortcutKeyMapsOneWayToExpectedVirtualKey(ShortcutKey key, uint expectedVirtualKey)
    {
        Assert.True(WindowsShortcutKeyMapper.TryGetVirtualKey(key, out var actualVirtualKey));
        Assert.Equal(expectedVirtualKey, actualVirtualKey);
    }

    [Fact]
    public void UnknownCoreShortcutKeyDoesNotMapToWindows()
    {
        Assert.False(WindowsShortcutKeyMapper.TryGetVirtualKey((ShortcutKey)50, out _));
    }

    [Fact]
    public void ModifierFlagsMapToRegisterHotKeyFlags()
    {
        var modifiers = ShortcutModifiers.Ctrl | ShortcutModifiers.Alt | ShortcutModifiers.Shift | ShortcutModifiers.Win;

        Assert.Equal(0x000Fu, WindowsShortcutKeyMapper.GetNativeModifiers(modifiers));
    }

    [Fact]
    public void ServiceImplementsPlatformAgnosticShortcutContract()
    {
        Assert.Contains(typeof(IShortcutService), typeof(WindowsShortcutService).GetInterfaces());
    }

    [Fact]
    public async Task NativeHostOwnsARealMessageOnlyWindowOnADedicatedThread()
    {
        var callerThreadId = Environment.CurrentManagedThreadId;
        await using var host = new WindowsShortcutNativeHost();

        Assert.NotEqual(callerThreadId, host.ManagedThreadId);
        Assert.NotEqual(IntPtr.Zero, host.WindowHandle);
        Assert.True(host.IsMessageOnlyWindow);
    }

    [Fact]
    public async Task StageConflictKeepsOldRegistrationAndActiveChord()
    {
        await using var host = new FakeNativeHost();
        await using var service = new WindowsShortcutService(host);
        var oldChord = new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space);
        await ActivateAsync(service, oldChord);
        var oldRegistration = Assert.Single(host.Registrations);
        host.NextRegisterResult = WindowsShortcutNativeResult.Failure(1409);

        var result = await service.StageAsync(new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space));

        Assert.False(result.IsSuccess);
        Assert.True(result.Token.IsEmpty);
        Assert.Equal("HOTKEY_CONFLICT", result.ErrorCode);
        Assert.Equal("快捷键已被系统或其他应用占用。", result.ErrorMessage);
        Assert.Equal(oldChord, service.ActiveChord);
        Assert.Single(host.Registrations);
        Assert.Equal(oldRegistration, Assert.Single(host.Registrations));
        Assert.DoesNotContain(host.Operations, operation => operation.StartsWith("unregister:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StageUsesSpareIdAndIgnoresActivationUntilCommit()
    {
        await using var host = new FakeNativeHost();
        await using var service = new WindowsShortcutService(host);
        var oldChord = new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space);
        await ActivateAsync(service, oldChord);
        var activeId = Assert.Single(host.Registrations).Key;
        var activationCount = 0;
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Activated += (_, _) =>
        {
            Interlocked.Increment(ref activationCount);
            activated.TrySetResult();
        };

        var staged = await service.StageAsync(new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space));
        var stagedId = Assert.Single(host.Registrations.Keys, id => id != activeId);
        host.RaiseHotkey(stagedId);
        host.RaiseHotkey(activeId);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(staged.IsSuccess);
        Assert.NotEqual(activeId, stagedId);
        Assert.Contains(activeId, new[] { WindowsShortcutService.PrimaryRegistrationId, WindowsShortcutService.SecondaryRegistrationId });
        Assert.Contains(stagedId, new[] { WindowsShortcutService.PrimaryRegistrationId, WindowsShortcutService.SecondaryRegistrationId });
        Assert.Equal(1, activationCount);
        Assert.Equal(oldChord, service.ActiveChord);
    }

    [Fact]
    public async Task CommitUnregistersOldRegistrationThenSwitchesActiveChord()
    {
        await using var host = new FakeNativeHost();
        await using var service = new WindowsShortcutService(host);
        var oldChord = new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space);
        var newChord = new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space);
        await ActivateAsync(service, oldChord);
        var oldId = Assert.Single(host.Registrations).Key;
        var staged = await service.StageAsync(newChord);
        var operationCountBeforeCommit = host.Operations.Count;

        var result = await service.CommitAsync(staged.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(newChord, service.ActiveChord);
        Assert.Single(host.Registrations);
        Assert.DoesNotContain(oldId, host.Registrations.Keys);
        Assert.Equal($"unregister:{oldId}", Assert.Single(host.Operations.Skip(operationCountBeforeCommit)));
    }

    [Fact]
    public async Task FailedCommitKeepsOldChordAndBothRegistrations()
    {
        await using var host = new FakeNativeHost();
        await using var service = new WindowsShortcutService(host);
        var oldChord = new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space);
        var newChord = new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space);
        await ActivateAsync(service, oldChord);
        var oldId = Assert.Single(host.Registrations).Key;
        var staged = await service.StageAsync(newChord);
        host.FailUnregisterId = oldId;

        var result = await service.CommitAsync(staged.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("HOTKEY_UNREGISTER_FAILED", result.ErrorCode);
        Assert.Equal("无法释放原快捷键，已保留原有快捷键。", result.ErrorMessage);
        Assert.Equal(oldChord, service.ActiveChord);
        Assert.Equal(2, host.Registrations.Count);
    }

    [Fact]
    public async Task RollbackReleasesStagedRegistrationAndKeepsOldChord()
    {
        await using var host = new FakeNativeHost();
        await using var service = new WindowsShortcutService(host);
        var oldChord = new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space);
        await ActivateAsync(service, oldChord);
        var oldId = Assert.Single(host.Registrations).Key;
        var staged = await service.StageAsync(new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space));
        var stagedId = Assert.Single(host.Registrations.Keys, id => id != oldId);

        await service.RollbackAsync(staged.Token);

        Assert.Equal(oldChord, service.ActiveChord);
        Assert.Single(host.Registrations);
        Assert.Contains(oldId, host.Registrations.Keys);
        Assert.DoesNotContain(stagedId, host.Registrations.Keys);
    }

    [Fact]
    public async Task SetEnabledUnregistersAndRestoresTheActiveChord()
    {
        await using var host = new FakeNativeHost();
        await using var service = new WindowsShortcutService(host);
        var chord = new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space);
        await ActivateAsync(service, chord);
        var activeId = Assert.Single(host.Registrations).Key;
        var activationCount = 0;
        EventArgs? receivedArgs = null;
        object? receivedSender = null;
        service.Activated += (sender, args) =>
        {
            activationCount++;
            receivedSender = sender;
            receivedArgs = args;
        };

        service.SetEnabled(false);
        host.RaiseHotkey(activeId);
        Assert.Empty(host.Registrations);
        Assert.Equal(chord, service.ActiveChord);
        Assert.Equal(0, activationCount);

        service.SetEnabled(true);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Activated += (_, _) => activated.TrySetResult();
        host.RaiseHotkey(activeId);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(host.Registrations);
        Assert.Equal(chord, service.ActiveChord);
        Assert.Equal(1, activationCount);
        Assert.Same(service, receivedSender);
        Assert.Same(EventArgs.Empty, receivedArgs);
    }



    [Fact]
    public async Task SetEnabledDuringPendingStageTogglesOnlyActiveRegistrationAndKeepsCandidateReserved()
    {
        await using var host = new FakeNativeHost();
        await using var service = new WindowsShortcutService(host);
        var oldChord = new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space);
        var newChord = new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space);
        await ActivateAsync(service, oldChord);
        var activeId = Assert.Single(host.Registrations).Key;
        var staged = await service.StageAsync(newChord);
        Assert.True(staged.IsSuccess, $"暂存失败：{staged.ErrorCode} {staged.ErrorMessage}");
        var candidateId = Assert.Single(host.Registrations.Keys, id => id != activeId);

        service.SetEnabled(false);

        var disabledRegistration = Assert.Single(host.Registrations);
        Assert.Equal(candidateId, disabledRegistration.Key);
        Assert.Equal(oldChord, service.ActiveChord);

        service.SetEnabled(true);

        Assert.Equal(2, host.Registrations.Count);
        Assert.Contains(activeId, host.Registrations.Keys);
        Assert.Contains(candidateId, host.Registrations.Keys);
        await service.RollbackAsync(staged.Token);
        Assert.Single(host.Registrations);
        Assert.Contains(activeId, host.Registrations.Keys);
        Assert.Equal(oldChord, service.ActiveChord);
    }

    [Fact]
    public async Task StageWhileDisabledStillChecksNativeConflict()
    {
        await using var host = new FakeNativeHost();
        await using var service = new WindowsShortcutService(host);
        var oldChord = new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space);
        await ActivateAsync(service, oldChord);
        service.SetEnabled(false);
        host.NextRegisterResult = WindowsShortcutNativeResult.Failure(1409);

        var result = await service.StageAsync(new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space));

        Assert.False(result.IsSuccess);
        Assert.Equal("HOTKEY_CONFLICT", result.ErrorCode);
        Assert.Equal(oldChord, service.ActiveChord);
        Assert.Empty(host.Registrations);
    }

    [Fact]
    public async Task CommitWhileDisabledSwitchesChordWithoutKeepingNativeRegistration()
    {
        await using var host = new FakeNativeHost();
        await using var service = new WindowsShortcutService(host);
        var oldChord = new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space);
        var newChord = new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space);
        await ActivateAsync(service, oldChord);
        service.SetEnabled(false);

        var staged = await service.StageAsync(newChord);
        Assert.True(staged.IsSuccess, $"暂存失败：{staged.ErrorCode} {staged.ErrorMessage}");
        Assert.Single(host.Registrations);
        var committed = await service.CommitAsync(staged.Token);

        Assert.True(committed.IsSuccess, $"提交失败：{committed.ErrorCode} {committed.ErrorMessage}");
        Assert.Equal(newChord, service.ActiveChord);
        Assert.Empty(host.Registrations);

        service.SetEnabled(true);
        var restored = Assert.Single(host.Registrations);
        Assert.Equal(WindowsShortcutKeyMapper.GetNativeModifiers(newChord.Modifiers), restored.Value.Modifiers);
        Assert.True(WindowsShortcutKeyMapper.TryGetVirtualKey(newChord.Key, out var expectedVirtualKey));
        Assert.Equal(expectedVirtualKey, restored.Value.VirtualKey);
    }

    [Fact]
    public async Task StageDoesNotDeadlockWhenNativeRegistrationObservesAnActiveHotkey()
    {
        await using var host = new ControllableAsyncNativeHost();
        await using var service = new WindowsShortcutService(host);
        await ActivateAsync(service, new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space));
        var activeId = Assert.Single(host.Registrations).Key;
        host.RaiseHotkeyBeforeNextRegisterReturns = activeId;

        var stageTask = Task.Run(async () =>
            await service.StageAsync(new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space)));
        var result = await stageTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.IsSuccess, $"暂存不应被热键回调阻塞：{result.ErrorCode} {result.ErrorMessage}");
    }

    [Fact]
    public async Task ConcurrentDisposeCallsWaitForTheSameNativeClose()
    {
        await using var host = new ControllableAsyncNativeHost(blockDispose: true);
        var service = new WindowsShortcutService(host);

        var firstDispose = service.DisposeAsync().AsTask();
        await host.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondDispose = service.DisposeAsync().AsTask();

        Assert.False(secondDispose.IsCompleted);
        host.AllowDispose();
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, host.DisposeCallCount);
    }

    [Fact]
    public async Task DisposeWaitsForInFlightStageBeforeClosingNativeHost()
    {
        await using var host = new ControllableAsyncNativeHost();
        var service = new WindowsShortcutService(host);
        host.BlockNextRegister();

        var stageTask = Task.Run(async () =>
            await service.StageAsync(new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space)));
        await host.RegisterStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposeTask = service.DisposeAsync().AsTask();

        Assert.False(disposeTask.IsCompleted);
        host.AllowRegister();
        var stageResult = await stageTask.WaitAsync(TimeSpan.FromSeconds(2));
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stageResult.IsSuccess);
        Assert.Equal(1, host.DisposeCallCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.StageAsync(new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space)));
    }

    [Fact]
    public async Task ActivatedRunsOnABackgroundThread()
    {
        await using var host = new ControllableAsyncNativeHost();
        await using var service = new WindowsShortcutService(host);
        await ActivateAsync(service, new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space));
        var activeId = Assert.Single(host.Registrations).Key;
        var activatedThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Activated += (_, _) => activatedThread.TrySetResult(Environment.CurrentManagedThreadId);

        await host.RaiseHotkeyAsync(activeId).WaitAsync(TimeSpan.FromSeconds(2));
        var actualThreadId = await activatedThread.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(host.ManagedThreadId, actualThreadId);
    }

    [Fact]
    public async Task ThrowingActivatedSubscriberDoesNotBlockLaterSubscribers()
    {
        await using var host = new ControllableAsyncNativeHost();
        await using var service = new WindowsShortcutService(host);
        await ActivateAsync(service, new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space));
        var activeId = Assert.Single(host.Registrations).Key;
        var laterSubscriber = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Activated += (_, _) => throw new InvalidOperationException("测试订阅者异常");
        service.Activated += (_, _) => laterSubscriber.TrySetResult();

        await host.RaiseHotkeyAsync(activeId).WaitAsync(TimeSpan.FromSeconds(2));
        await laterSubscriber.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }


    [Fact]
    public async Task NativeHostWorkPostFailureFailsInvocationWithoutHanging()
    {
        var poster = new ControllableMessagePoster
        {
            FailMessage = WindowsShortcutNativeHost.WorkMessage,
            FailureErrorCode = 5,
        };
        await using var host = new WindowsShortcutNativeHost(poster, TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<Win32Exception>(async () =>
            await Task.Run(() => host.Unregister(WindowsShortcutService.PrimaryRegistrationId))
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(5, exception.NativeErrorCode);
    }

    [Fact]
    public async Task NativeHostDisposeCannotOvertakeAnAlreadyQueuedInvocation()
    {
        var poster = new ControllableMessagePoster
        {
            BlockMessage = WindowsShortcutNativeHost.WorkMessage,
        };
        var host = new WindowsShortcutNativeHost(poster, TimeSpan.FromSeconds(1));

        var invocation = Task.Run(() => host.Unregister(WindowsShortcutService.PrimaryRegistrationId));
        await poster.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var dispose = Task.Run(async () => await host.DisposeAsync());

        Assert.False(dispose.IsCompleted);
        poster.ReleaseBlockedPost();
        var invocationFailure = await Record.ExceptionAsync(async () =>
            await invocation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(
            invocationFailure is null or ObjectDisposedException,
            $"已入队调用必须成功或以关闭异常结束，实际：{invocationFailure?.GetType().Name}");
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task NativeHostConcurrentDisposeCallsWaitForOneCloseSequence()
    {
        var poster = new ControllableMessagePoster
        {
            BlockMessage = WindowsShortcutNativeHost.CloseMessage,
        };
        var host = new WindowsShortcutNativeHost(poster, TimeSpan.FromSeconds(1));

        var firstDispose = Task.Run(async () => await host.DisposeAsync());
        await poster.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondDispose = Task.Run(async () => await host.DisposeAsync());

        Assert.False(secondDispose.IsCompleted);
        poster.ReleaseBlockedPost();
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, poster.GetPostCount(WindowsShortcutNativeHost.CloseMessage));
    }

    [Fact]
    public void NativeHostStartupTimeoutStopsTheMessageThreadBeforeThrowing()
    {
        Thread? messageThread = null;

        Assert.Throws<TimeoutException>(() =>
            _ = new WindowsShortcutNativeHost(
                WindowsShortcutMessagePoster.Instance,
                TimeSpan.FromMilliseconds(100),
                beforeReady: cancellationToken =>
                {
                    messageThread = Thread.CurrentThread;
                    cancellationToken.WaitHandle.WaitOne();
                    cancellationToken.ThrowIfCancellationRequested();
                }));

        Assert.NotNull(messageThread);
        Assert.False(messageThread.IsAlive);
    }

    [Fact]
    public async Task NativeHostStartupTimeoutTransfersBlockedThreadToObservableCleanupOwner()
    {
        using var releaseBeforeReady = new ManualResetEventSlim(initialState: false);
        Thread? messageThread = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            Assert.Throws<TimeoutException>(() =>
                _ = new WindowsShortcutNativeHost(
                    WindowsShortcutMessagePoster.Instance,
                    TimeSpan.FromMilliseconds(75),
                    beforeReady: _ =>
                    {
                        messageThread = Thread.CurrentThread;
                        releaseBeforeReady.Wait();
                    }));
            stopwatch.Stop();

            Assert.NotNull(messageThread);
            Assert.True(messageThread.IsAlive, "受控初始化尚未释放，消息线程应仍处于存活状态。此断言用于稳定复现旧实现的泄漏窗口。");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "构造启动超时必须保持有界返回。");

            Assert.True(
                WindowsShortcutNativeHost.TryGetStartupFailureCleanup(messageThread, out var cleanup),
                "构造返回时仍存活的线程必须由可观测的后台清理对象持有。");

            releaseBeforeReady.Set();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(messageThread.IsAlive);
            Assert.False(WindowsShortcutNativeHost.TryGetStartupFailureCleanup(messageThread, out _));
        }
        finally
        {
            releaseBeforeReady.Set();
            if (messageThread is { IsAlive: true })
                Assert.True(messageThread.Join(TimeSpan.FromSeconds(2)), "测试清理未能结束受控消息线程。");
        }
    }

    [Fact]
    public void NativeHostStartupFailureStopsTheMessageThreadBeforeThrowing()
    {
        Thread? messageThread = null;
        var expected = new InvalidOperationException("测试原生宿主启动失败");

        var actual = Assert.Throws<InvalidOperationException>(() =>
            _ = new WindowsShortcutNativeHost(
                WindowsShortcutMessagePoster.Instance,
                TimeSpan.FromSeconds(1),
                beforeReady: _ =>
                {
                    messageThread = Thread.CurrentThread;
                    throw expected;
                }));

        Assert.Same(expected, actual);
        Assert.NotNull(messageThread);
        Assert.False(messageThread.IsAlive);
    }

    [Fact]
    public async Task NativeHostClosePostFailureUsesThreadQuitAndSharesOneCloseSequence()
    {
        var poster = new ControllableMessagePoster
        {
            BlockMessage = WindowsShortcutNativeHost.WorkMessage,
            FailMessage = WindowsShortcutNativeHost.CloseMessage,
            FailureErrorCode = 5,
        };
        var host = new WindowsShortcutNativeHost(poster, TimeSpan.FromSeconds(1));
        var invocation = Task.Run(() => host.Unregister(WindowsShortcutService.PrimaryRegistrationId));
        await poster.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var firstDispose = Task.Run(async () => await host.DisposeAsync());
        Assert.False(firstDispose.IsCompleted);
        poster.ReleaseBlockedPost();
        var invocationFailure = await Record.ExceptionAsync(async () =>
            await invocation.WaitAsync(TimeSpan.FromSeconds(2)));
        var secondDispose = host.DisposeAsync().AsTask();

        Assert.True(
            invocationFailure is null or ObjectDisposedException,
            $"已入队调用必须成功或以关闭异常结束，实际：{invocationFailure?.GetType().Name}");
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, poster.GetPostCount(WindowsShortcutNativeHost.CloseMessage));
        Assert.Equal(1, poster.GetThreadPostCount(WindowsShortcutNativeHost.QuitMessage));
    }

    [Fact]
    public async Task NativeWorkQueueFailureCompletesEveryPendingInvocation()
    {
        var queue = new WindowsShortcutWorkQueue();
        var first = queue.Enqueue(() => 1);
        var second = queue.Enqueue(() => 2);
        var failure = new InvalidOperationException("测试消息线程异常");

        queue.FailPending(failure);

        var firstFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        var secondFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await second.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(failure, firstFailure);
        Assert.Same(failure, secondFailure);
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(() => 3));
    }

    private static async Task ActivateAsync(WindowsShortcutService service, ShortcutChord chord)
    {
        var staged = await service.StageAsync(chord);
        Assert.True(staged.IsSuccess, $"暂存失败：{staged.ErrorCode} {staged.ErrorMessage}");
        var committed = await service.CommitAsync(staged.Token);
        Assert.True(committed.IsSuccess, $"提交失败：{committed.ErrorCode} {committed.ErrorMessage}");
    }



    private sealed class ControllableMessagePoster : IWindowsShortcutMessagePoster
    {
        private readonly IWindowsShortcutMessagePoster inner = WindowsShortcutMessagePoster.Instance;
        private readonly ConcurrentDictionary<uint, int> postCounts = new();
        private readonly ConcurrentDictionary<uint, int> threadPostCounts = new();
        private readonly ManualResetEventSlim release = new(initialState: false);

        public uint? BlockMessage { get; init; }

        public uint? FailMessage { get; init; }

        public int FailureErrorCode { get; init; } = 5;

        public TaskCompletionSource Blocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int GetPostCount(uint message) => postCounts.TryGetValue(message, out var count) ? count : 0;

        public int GetThreadPostCount(uint message) =>
            threadPostCounts.TryGetValue(message, out var count) ? count : 0;

        public void ReleaseBlockedPost() => release.Set();

        public bool TryPost(IntPtr windowHandle, uint message, out int errorCode)
        {
            postCounts.AddOrUpdate(message, 1, static (_, count) => count + 1);
            if (BlockMessage == message)
            {
                Blocked.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(2)))
                {
                    errorCode = 1460;
                    return false;
                }
            }

            if (FailMessage == message)
            {
                errorCode = FailureErrorCode;
                return false;
            }

            return inner.TryPost(windowHandle, message, out errorCode);
        }

        public bool TryPostThread(uint threadId, uint message, out int errorCode)
        {
            threadPostCounts.AddOrUpdate(message, 1, static (_, count) => count + 1);
            return WindowsShortcutNativeHost.TryPostNativeThreadMessage(threadId, message, out errorCode);
        }
    }

    /// <summary>
    /// 在独立后台线程串行执行原生调用，并提供受控阻塞点，用于稳定复现消息回调与关闭竞态。
    /// 所有等待都有上限，测试失败时不会遗留永久挂起的前台线程。
    /// </summary>
    private sealed class ControllableAsyncNativeHost : IWindowsShortcutNativeHost
    {
        private readonly BlockingCollection<Action> workItems = new();
        private readonly Thread worker;
        private readonly TaskCompletionSource workerExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim registerRelease = new(initialState: true);
        private int blockNextRegister;
        private int disposed;

        public ControllableAsyncNativeHost(bool blockDispose = false)
        {
            if (!blockDispose)
                disposeRelease.TrySetResult();

            worker = new Thread(Run)
            {
                IsBackground = true,
                Name = "QuickPhrase 快捷键测试原生线程",
            };
            worker.Start();
        }

        public event Action<int>? HotkeyPressed;

        public ConcurrentDictionary<int, (uint Modifiers, uint VirtualKey)> Registrations { get; } = new();

        public TaskCompletionSource RegisterStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int? RaiseHotkeyBeforeNextRegisterReturns { get; set; }

        public int DisposeCallCount { get; private set; }

        public int ManagedThreadId { get; private set; }

        public IntPtr WindowHandle => new(2);

        public bool IsMessageOnlyWindow => true;

        public void BlockNextRegister()
        {
            registerRelease.Reset();
            Interlocked.Exchange(ref blockNextRegister, 1);
        }

        public void AllowRegister() => registerRelease.Set();

        public void AllowDispose() => disposeRelease.TrySetResult();

        public WindowsShortcutNativeResult Register(int id, uint modifiers, uint virtualKey)
        {
            return Invoke(() =>
            {
                if (Interlocked.Exchange(ref blockNextRegister, 0) != 0)
                {
                    RegisterStarted.TrySetResult();
                    if (!registerRelease.Wait(TimeSpan.FromSeconds(2)))
                        return WindowsShortcutNativeResult.Failure(1460);
                }

                if (RaiseHotkeyBeforeNextRegisterReturns is { } hotkeyId)
                {
                    RaiseHotkeyBeforeNextRegisterReturns = null;
                    using var callbackCompleted = new ManualResetEventSlim();
                    var callbackThread = new Thread(() =>
                    {
                        try
                        {
                            HotkeyPressed?.Invoke(hotkeyId);
                        }
                        finally
                        {
                            callbackCompleted.Set();
                        }
                    })
                    {
                        IsBackground = true,
                        Name = "QuickPhrase 快捷键测试回调线程",
                    };
                    callbackThread.Start();
                    if (!callbackCompleted.Wait(TimeSpan.FromMilliseconds(500)))
                        return WindowsShortcutNativeResult.Failure(1460);
                }

                return Registrations.TryAdd(id, (modifiers, virtualKey))
                    ? WindowsShortcutNativeResult.Success()
                    : WindowsShortcutNativeResult.Failure(1409);
            });
        }

        public WindowsShortcutNativeResult Unregister(int id)
        {
            return Invoke(() =>
            {
                Registrations.TryRemove(id, out _);
                return WindowsShortcutNativeResult.Success();
            });
        }

        public Task RaiseHotkeyAsync(int id)
        {
            return InvokeAsync(() => HotkeyPressed?.Invoke(id));
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            DisposeStarted.TrySetResult();
            await disposeRelease.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            workItems.CompleteAdding();
            await workerExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
            registerRelease.Dispose();
            workItems.Dispose();
        }

        private T Invoke<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            workItems.Add(() =>
            {
                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }

        private Task InvokeAsync(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            workItems.Add(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            return completion.Task;
        }

        private void Run()
        {
            ManagedThreadId = Environment.CurrentManagedThreadId;
            try
            {
                foreach (var workItem in workItems.GetConsumingEnumerable())
                    workItem();
            }
            finally
            {
                workerExited.TrySetResult();
            }
        }
    }

    private sealed class FakeNativeHost : IWindowsShortcutNativeHost
    {
        public event Action<int>? HotkeyPressed;

        public Dictionary<int, (uint Modifiers, uint VirtualKey)> Registrations { get; } = [];

        public List<string> Operations { get; } = [];

        public WindowsShortcutNativeResult? NextRegisterResult { get; set; }

        public int? FailUnregisterId { get; set; }

        public int ManagedThreadId => Environment.CurrentManagedThreadId;

        public IntPtr WindowHandle => new(1);

        public bool IsMessageOnlyWindow => true;

        public WindowsShortcutNativeResult Register(int id, uint modifiers, uint virtualKey)
        {
            Operations.Add($"register:{id}");
            if (NextRegisterResult is { } result)
            {
                NextRegisterResult = null;
                return result;
            }

            if (!Registrations.TryAdd(id, (modifiers, virtualKey)))
                return WindowsShortcutNativeResult.Failure(1409);

            return WindowsShortcutNativeResult.Success();
        }

        public WindowsShortcutNativeResult Unregister(int id)
        {
            Operations.Add($"unregister:{id}");
            if (FailUnregisterId == id)
                return WindowsShortcutNativeResult.Failure(5);

            Registrations.Remove(id);
            return WindowsShortcutNativeResult.Success();
        }

        public void RaiseHotkey(int id) => HotkeyPressed?.Invoke(id);

        public ValueTask DisposeAsync()
        {
            Registrations.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
