using QuickPhrase.Core;

namespace QuickPhrase.Desktop.Tests;

public sealed class HotkeyCoordinatorTests
{
    private static readonly ShortcutChord AltSpace = new(ShortcutModifiers.Alt, ShortcutKey.Space);
    private static readonly ShortcutChord CtrlSpace = new(ShortcutModifiers.Ctrl, ShortcutKey.Space);

    [Fact]
    public async Task ConfigureAsync_StagesAndCommitsConfiguredChord_ThenHonorsInactiveScope()
    {
        var service = new FakeShortcutService();
        await using var coordinator = new HotkeyCoordinator(service, action => action());

        await coordinator.ConfigureAsync(CreateSettings(CtrlSpace));

        Assert.Equal([CtrlSpace], service.StagedChords);
        Assert.Single(service.CommittedTokens);
        Assert.Equal(CtrlSpace, service.ActiveChord);
        Assert.False(service.IsEnabled);
        Assert.False(coordinator.LauncherAvailable);
        Assert.Null(coordinator.LauncherErrorCode);
    }

    [Fact]
    public async Task ConfigureAsync_WithAlreadyActiveChord_DoesNotStageDuplicateRegistration()
    {
        var service = new FakeShortcutService();
        await using var coordinator = new HotkeyCoordinator(service, action => action());
        await coordinator.ConfigureAsync(CreateSettings(AltSpace));
        service.StagedChords.Clear();
        service.CommittedTokens.Clear();

        await coordinator.ConfigureAsync(CreateSettings(AltSpace));

        Assert.Empty(service.StagedChords);
        Assert.Empty(service.CommittedTokens);
    }
    [Fact]
    public async Task ScopeVisiblePracticeAndPause_OnlyControlRegistrationAvailability()
    {
        var service = new FakeShortcutService();
        await using var coordinator = new HotkeyCoordinator(service, action => action());
        await coordinator.ConfigureAsync(CreateSettings(AltSpace));

        coordinator.SetLauncherScopeActive(true, "WXWork");
        Assert.True(service.IsEnabled);
        Assert.True(coordinator.LauncherAvailable);

        coordinator.SetPaused(true);
        Assert.False(service.IsEnabled);
        Assert.False(coordinator.LauncherAvailable);

        coordinator.SetLauncherVisible(true);
        Assert.False(service.IsEnabled);

        coordinator.SetPaused(false);
        Assert.True(service.IsEnabled);

        coordinator.SetLauncherScopeActive(false, null);
        coordinator.SetLauncherVisible(false);
        coordinator.SetPracticeMode(true);
        Assert.True(service.IsEnabled);
    }

    [Fact]
    public async Task Activated_AlwaysUsesInjectedUiDispatcher()
    {
        var service = new FakeShortcutService();
        Action? pendingUiAction = null;
        await using var coordinator = new HotkeyCoordinator(service, action => pendingUiAction = action);
        await coordinator.ConfigureAsync(CreateSettings(AltSpace));
        coordinator.SetPracticeMode(true);
        var pressed = 0;
        coordinator.LauncherHotkeyPressed += () => pressed++;

        service.RaiseActivated();

        Assert.Equal(0, pressed);
        Assert.NotNull(pendingUiAction);
        pendingUiAction!();
        Assert.Equal(1, pressed);
    }

    [Fact]
    public async Task ConfigureAsync_WhenStageConflicts_KeepsUnavailableAndReportsStableCode()
    {
        var service = new FakeShortcutService
        {
            StageResult = ShortcutStageResult.Failure("HOTKEY_CONFLICT", "快捷键已被其他程序占用。"),
        };
        await using var coordinator = new HotkeyCoordinator(service, action => action());

        await coordinator.ConfigureAsync(CreateSettings(AltSpace));

        Assert.False(coordinator.LauncherAvailable);
        Assert.Equal("HOTKEY_CONFLICT", coordinator.LauncherErrorCode);
        Assert.Empty(service.CommittedTokens);
    }

    [Fact]
    public async Task ConfigureAsync_WhenCommitIsCancelled_RollsBackThenPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new FakeShortcutService { BeforeCommit = cancellation.Cancel };
        await using var coordinator = new HotkeyCoordinator(service, action => action());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.ConfigureAsync(CreateSettings(CtrlSpace), cancellation.Token));

        Assert.False(coordinator.LauncherAvailable);
        Assert.Single(service.RollbackTokens);
        Assert.False(service.ObservedRollbackToken.CanBeCanceled);
    }

    [Fact]
    public async Task ConfigureAsync_WhenCommitIsCancelledAndRollbackFails_ReturnsRollbackErrorInsteadOfCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new FakeShortcutService
        {
            BeforeCommit = cancellation.Cancel,
            RollbackException = new InvalidOperationException("模拟回滚异常。"),
        };
        await using var coordinator = new HotkeyCoordinator(service, action => action());

        await coordinator.ConfigureAsync(CreateSettings(CtrlSpace), cancellation.Token);

        Assert.Equal("HOTKEY_ROLLBACK_FAILED", coordinator.LauncherErrorCode);
        Assert.False(coordinator.LauncherAvailable);
        Assert.Single(service.RollbackTokens);
    }

    [Fact]
    public async Task ConfigureAsync_WhenCommitFails_RollsBackWithIndependentToken()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new FakeShortcutService
        {
            CommitResult = ShortcutApplyResult.Failure("HOTKEY_UNREGISTER_FAILED", "无法释放原快捷键。"),
        };
        await using var coordinator = new HotkeyCoordinator(service, action => action());

        await coordinator.ConfigureAsync(CreateSettings(CtrlSpace), cancellation.Token);

        Assert.Equal("HOTKEY_UNREGISTER_FAILED", coordinator.LauncherErrorCode);
        Assert.False(coordinator.LauncherAvailable);
        Assert.Single(service.RollbackTokens);
        Assert.False(service.ObservedRollbackToken.CanBeCanceled);
    }

    [Fact]
    public async Task CommitAsync_WhenSuccessful_UpdatesConfiguredStateAndRestoresScopePolicy()
    {
        var service = new FakeShortcutService();
        await using var coordinator = new HotkeyCoordinator(service, action => action());
        await coordinator.ConfigureAsync(CreateSettings(AltSpace));
        coordinator.SetLauncherScopeActive(true, "WXWork");

        var stage = await coordinator.StageAsync(CtrlSpace);
        var apply = await coordinator.CommitAsync(stage.Token);

        Assert.True(apply.IsSuccess);
        Assert.Equal(CtrlSpace, service.ActiveChord);
        Assert.True(service.IsEnabled);
        Assert.True(coordinator.LauncherAvailable);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenStageConflicts_DoesNotWriteSettings()
    {
        var service = new FakeShortcutService
        {
            ActiveChord = AltSpace,
            StageResult = ShortcutStageResult.Failure("HOTKEY_CONFLICT", "快捷键已被其他程序占用。"),
        };
        await using var coordinator = new HotkeyCoordinator(service, action => action());
        var saveCalls = 0;

        var result = await coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) =>
            {
                saveCalls++;
                return Task.FromResult(RepositoryResult<AppSettings>.Success(settings));
            });

        Assert.False(result.IsSuccess);
        Assert.Equal("HOTKEY_CONFLICT", result.Error?.Code);
        Assert.Equal(0, saveCalls);
        Assert.Empty(service.RollbackTokens);
        Assert.Equal(AltSpace, service.ActiveChord);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenSaveFails_RollsBackStagedRegistration()
    {
        var service = new FakeShortcutService { ActiveChord = AltSpace };
        await using var coordinator = new HotkeyCoordinator(service, action => action());

        var result = await coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) => Task.FromResult(
                RepositoryResult<AppSettings>.Failure(new DataError("SETTINGS_SAVE_FAILED", "模拟保存失败。"))));

        Assert.False(result.IsSuccess);
        Assert.Single(service.RollbackTokens);
        Assert.Empty(service.CommittedTokens);
        Assert.Equal(AltSpace, service.ActiveChord);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenSaveIsCancelled_RollsBackThenPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new FakeShortcutService { ActiveChord = AltSpace };
        await using var coordinator = new HotkeyCoordinator(service, action => action());

        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            },
            cancellation.Token));

        Assert.Single(service.RollbackTokens);
        Assert.False(service.ObservedRollbackToken.CanBeCanceled);
        Assert.Equal(AltSpace, service.ActiveChord);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenSaveFailsAfterCancellation_RollsBackWithIndependentToken()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new FakeShortcutService { ActiveChord = AltSpace };
        await using var coordinator = new HotkeyCoordinator(service, action => action());

        var result = await coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) =>
            {
                cancellation.Cancel();
                return Task.FromResult(RepositoryResult<AppSettings>.Failure(
                    new DataError("SETTINGS_SAVE_FAILED", "模拟保存失败。")));
            },
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("SETTINGS_SAVE_FAILED", result.Error?.Code);
        Assert.Single(service.RollbackTokens);
        Assert.False(service.ObservedRollbackToken.CanBeCanceled);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenCommitThrows_RollsBackAndRestoresOldSettings()
    {
        var service = new FakeShortcutService
        {
            ActiveChord = AltSpace,
            CommitException = new InvalidOperationException("模拟提交异常。"),
        };
        await using var coordinator = new HotkeyCoordinator(service, action => action());
        var writes = new List<(AppSettings Settings, long ExpectedVersion, CancellationToken Token)>();

        var result = await coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) =>
            {
                writes.Add((settings, expectedVersion, cancellationToken));
                var version = writes.Count == 1 ? 2 : 3;
                return Task.FromResult(RepositoryResult<AppSettings>.Success(settings with { Version = version }));
            });

        Assert.False(result.IsSuccess);
        Assert.Equal("HOTKEY_COMMIT_EXCEPTION", result.Error?.Code);
        Assert.Contains("TraceId", result.Error?.Message, StringComparison.Ordinal);
        Assert.Single(service.RollbackTokens);
        Assert.Equal(2, writes.Count);
        Assert.Equal(AltSpace, writes[1].Settings.LauncherShortcut);
        Assert.False(writes[1].Token.CanBeCanceled);
        Assert.Equal(AltSpace, service.ActiveChord);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenCommitIsCancelled_CompensatesThenPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new FakeShortcutService
        {
            ActiveChord = AltSpace,
            BeforeCommit = cancellation.Cancel,
        };
        await using var coordinator = new HotkeyCoordinator(service, action => action());
        var writes = new List<(AppSettings Settings, CancellationToken Token)>();

        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) =>
            {
                writes.Add((settings, cancellationToken));
                return Task.FromResult(RepositoryResult<AppSettings>.Success(settings with { Version = writes.Count + 1 }));
            },
            cancellation.Token));

        Assert.Single(service.RollbackTokens);
        Assert.False(service.ObservedRollbackToken.CanBeCanceled);
        Assert.Equal(2, writes.Count);
        Assert.False(writes[1].Token.CanBeCanceled);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenCommitIsCancelledAndRollbackFails_ReturnsCompensationErrorWithRestoredSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new FakeShortcutService
        {
            ActiveChord = AltSpace,
            BeforeCommit = cancellation.Cancel,
            RollbackException = new InvalidOperationException("模拟回滚异常。"),
        };
        await using var coordinator = new HotkeyCoordinator(service, action => action());
        var writes = new List<AppSettings>();

        var result = await coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) =>
            {
                writes.Add(settings);
                return Task.FromResult(RepositoryResult<AppSettings>.Success(settings with { Version = writes.Count + 1 }));
            },
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("HOTKEY_ROLLBACK_FAILED", result.Error?.Code);
        Assert.NotNull(result.Value);
        Assert.Equal(3, result.Value.Version);
        Assert.Equal(AltSpace, result.Value.LauncherShortcut);
        Assert.Equal(2, writes.Count);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenRollbackFails_ReturnsConsistencyErrorWithTraceId()
    {
        var service = new FakeShortcutService
        {
            ActiveChord = AltSpace,
            RollbackException = new InvalidOperationException("模拟回滚异常。"),
        };
        await using var coordinator = new HotkeyCoordinator(service, action => action());

        var result = await coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) => Task.FromResult(
                RepositoryResult<AppSettings>.Failure(new DataError("SETTINGS_SAVE_FAILED", "模拟保存失败。"))));

        Assert.False(result.IsSuccess);
        Assert.Equal("HOTKEY_ROLLBACK_FAILED", result.Error?.Code);
        Assert.Contains("TraceId", result.Error?.Message, StringComparison.Ordinal);
        Assert.Single(service.RollbackTokens);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenSaveThrows_RollsBackWithIndependentToken()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new FakeShortcutService { ActiveChord = AltSpace };
        await using var coordinator = new HotkeyCoordinator(service, action => action());

        var result = await coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) =>
            {
                cancellation.Cancel();
                throw new InvalidOperationException("模拟保存异常。");
            },
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("SETTINGS_SAVE_FAILED", result.Error?.Code);
        Assert.Contains("TraceId", result.Error?.Message, StringComparison.Ordinal);
        Assert.Single(service.RollbackTokens);
        Assert.False(service.ObservedRollbackToken.CanBeCanceled);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenCommitAndRollbackFail_StillRestoresOldSettings()
    {
        var service = new FakeShortcutService
        {
            ActiveChord = AltSpace,
            CommitResult = ShortcutApplyResult.Failure("HOTKEY_UNREGISTER_FAILED", "无法释放原快捷键。"),
            RollbackException = new InvalidOperationException("模拟回滚异常。"),
        };
        await using var coordinator = new HotkeyCoordinator(service, action => action());
        var writes = new List<AppSettings>();

        var result = await coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) =>
            {
                writes.Add(settings);
                return Task.FromResult(RepositoryResult<AppSettings>.Success(
                    settings with { Version = writes.Count + 1 }));
            });

        Assert.False(result.IsSuccess);
        Assert.Equal("HOTKEY_ROLLBACK_FAILED", result.Error?.Code);
        Assert.Contains("TraceId", result.Error?.Message, StringComparison.Ordinal);
        Assert.Equal(2, writes.Count);
        Assert.Equal(AltSpace, writes[1].LauncherShortcut);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenRestoreFails_ReturnsRestoreFailureWithTraceId()
    {
        var service = new FakeShortcutService
        {
            ActiveChord = AltSpace,
            CommitResult = ShortcutApplyResult.Failure("HOTKEY_UNREGISTER_FAILED", "无法释放原快捷键。"),
        };
        await using var coordinator = new HotkeyCoordinator(service, action => action());
        var writeCount = 0;

        var result = await coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) =>
            {
                writeCount++;
                return Task.FromResult(writeCount == 1
                    ? RepositoryResult<AppSettings>.Success(settings with { Version = 2 })
                    : RepositoryResult<AppSettings>.Failure(
                        new DataError("SETTINGS_RESTORE_WRITE_FAILED", "模拟恢复失败。")));
            });

        Assert.False(result.IsSuccess);
        Assert.Equal("HOTKEY_SETTINGS_RESTORE_FAILED", result.Error?.Code);
        Assert.Contains("TraceId", result.Error?.Message, StringComparison.Ordinal);
        Assert.Equal(2, writeCount);
        Assert.Single(service.RollbackTokens);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenRollbackAndRestoreFail_ReturnsCompensationFailure()
    {
        var service = new FakeShortcutService
        {
            ActiveChord = AltSpace,
            CommitResult = ShortcutApplyResult.Failure("HOTKEY_UNREGISTER_FAILED", "无法释放原快捷键。"),
            RollbackException = new InvalidOperationException("模拟回滚异常。"),
        };
        await using var coordinator = new HotkeyCoordinator(service, action => action());
        var writeCount = 0;

        var result = await coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) =>
            {
                writeCount++;
                return Task.FromResult(writeCount == 1
                    ? RepositoryResult<AppSettings>.Success(settings with { Version = 2 })
                    : RepositoryResult<AppSettings>.Failure(
                        new DataError("SETTINGS_RESTORE_WRITE_FAILED", "模拟恢复失败。")));
            });

        Assert.False(result.IsSuccess);
        Assert.Equal("HOTKEY_COMPENSATION_FAILED", result.Error?.Code);
        Assert.Contains("TraceId", result.Error?.Message, StringComparison.Ordinal);
        Assert.Equal(2, writeCount);
        Assert.Single(service.RollbackTokens);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenSaveAndCommitSucceed_UsesPersistedValue()
    {
        var service = new FakeShortcutService { ActiveChord = AltSpace };
        await using var coordinator = new HotkeyCoordinator(service, action => action());
        var saved = CreateSettings(CtrlSpace) with { Version = 2 };

        var result = await coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) => Task.FromResult(RepositoryResult<AppSettings>.Success(saved)));

        Assert.True(result.IsSuccess);
        Assert.Same(saved, result.Value);
        Assert.Equal(CtrlSpace, service.ActiveChord);
        Assert.Single(service.CommittedTokens);
        Assert.Empty(service.RollbackTokens);
    }

    [Fact]
    public async Task ApplyShortcutChangeAsync_WhenCommitFails_RestoresPersistedOldChord()
    {
        var service = new FakeShortcutService
        {
            ActiveChord = AltSpace,
            CommitResult = ShortcutApplyResult.Failure("HOTKEY_UNREGISTER_FAILED", "无法释放原快捷键。"),
        };
        await using var coordinator = new HotkeyCoordinator(service, action => action());
        var writes = new List<(AppSettings Settings, long ExpectedVersion)>();

        var result = await coordinator.ApplyShortcutChangeAsync(
            CreateSettings(AltSpace),
            CreateSettings(CtrlSpace),
            (settings, expectedVersion, cancellationToken) =>
            {
                writes.Add((settings, expectedVersion));
                var version = writes.Count == 1 ? 2 : 3;
                return Task.FromResult(RepositoryResult<AppSettings>.Success(settings with { Version = version }));
            });

        Assert.False(result.IsSuccess);
        Assert.Equal("HOTKEY_UNREGISTER_FAILED", result.Error?.Code);
        Assert.NotNull(result.Value);
        Assert.Equal(3, result.Value.Version);
        Assert.Equal(AltSpace, result.Value.LauncherShortcut);
        Assert.Equal(2, writes.Count);
        Assert.Equal(CtrlSpace, writes[0].Settings.LauncherShortcut);
        Assert.Equal(AltSpace, writes[1].Settings.LauncherShortcut);
        Assert.Equal(2, writes[1].ExpectedVersion);
        Assert.Single(service.RollbackTokens);
        Assert.Equal(AltSpace, service.ActiveChord);
    }

    private static AppSettings CreateSettings(ShortcutChord chord) =>
        new(1, false, false, true, chord, false, true);

    private sealed class FakeShortcutService : IShortcutService
    {
        private ShortcutChord? stagedChord;
        private ShortcutStageToken stagedToken;

        public event EventHandler? Activated;

        public ShortcutChord ActiveChord { get; set; }

        public bool IsEnabled { get; private set; } = true;

        public List<ShortcutChord> StagedChords { get; } = [];

        public List<ShortcutStageToken> CommittedTokens { get; } = [];

        public List<ShortcutStageToken> RollbackTokens { get; } = [];

        public ShortcutStageResult? StageResult { get; init; }

        public ShortcutApplyResult? CommitResult { get; init; }

        public Exception? CommitException { get; init; }

        public Exception? RollbackException { get; init; }

        public Action? BeforeCommit { get; init; }

        public CancellationToken ObservedRollbackToken { get; private set; }

        public Task<ShortcutStageResult> StageAsync(ShortcutChord chord, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StagedChords.Add(chord);
            if (StageResult is { } configuredResult)
                return Task.FromResult(configuredResult);

            stagedChord = chord;
            stagedToken = ShortcutStageToken.Create();
            return Task.FromResult(ShortcutStageResult.Success(stagedToken));
        }

        public Task<ShortcutApplyResult> CommitAsync(ShortcutStageToken token, CancellationToken cancellationToken = default)
        {
            BeforeCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            CommittedTokens.Add(token);
            if (CommitException is not null)
                throw CommitException;
            if (CommitResult is { } configuredResult)
                return Task.FromResult(configuredResult);
            if (stagedChord is null || token != stagedToken)
                return Task.FromResult(ShortcutApplyResult.Failure("HOTKEY_STAGE_NOT_FOUND", "找不到待提交的快捷键。"));

            ActiveChord = stagedChord.Value;
            stagedChord = null;
            stagedToken = default;
            return Task.FromResult(ShortcutApplyResult.Success());
        }

        public Task RollbackAsync(ShortcutStageToken token, CancellationToken cancellationToken = default)
        {
            ObservedRollbackToken = cancellationToken;
            RollbackTokens.Add(token);
            cancellationToken.ThrowIfCancellationRequested();
            if (RollbackException is not null)
                throw RollbackException;
            if (token == stagedToken)
            {
                stagedChord = null;
                stagedToken = default;
            }
            return Task.CompletedTask;
        }

        public void SetEnabled(bool enabled) => IsEnabled = enabled;

        public void RaiseActivated() => Activated?.Invoke(this, EventArgs.Empty);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
