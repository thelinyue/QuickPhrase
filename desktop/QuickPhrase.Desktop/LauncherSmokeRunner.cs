using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Onboarding;

namespace QuickPhrase.Desktop;

/// <summary>Launcher smoke 支持的独立运行模式；None 表示普通产品启动。</summary>
internal enum LauncherSmokeMode
{
    None,
    Native,
    Performance,
}

/// <summary>
/// Launcher 窗口可重复显示/隐藏的稳定生命周期状态。该状态不改变产品行为，
/// 只为诊断和 smoke 循环提供可验证的状态边界。
/// </summary>
internal enum LauncherLifecycleState
{
    Created,
    Activating,
    Visible,
    Interactive,
    Hiding,
    Hidden,
    Disposed,
    Faulted,
}

/// <summary>Launcher smoke 命令行选项；输出目录只允许在 smoke 模式下指定。</summary>
internal sealed record LauncherSmokeOptions(LauncherSmokeMode Mode, string? OutputDirectory)
{
    public static LauncherSmokeParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var native = arguments.Contains("--smoke-native-launcher", StringComparer.OrdinalIgnoreCase);
        var performance = arguments.Contains("--smoke-launcher-performance", StringComparer.OrdinalIgnoreCase);
        if (native && performance)
        {
            return LauncherSmokeParseResult.Failure(
                "LAUNCHER_SMOKE_ARGUMENT_INVALID",
                "Launcher smoke 模式不能同时指定。");
        }

        string? outputDirectory = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--smoke-output", StringComparison.OrdinalIgnoreCase))
                continue;
            index++;
            if (index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
            {
                return LauncherSmokeParseResult.Failure(
                    "LAUNCHER_SMOKE_ARGUMENT_INVALID",
                    "--smoke-output 缺少目录参数。");
            }
            outputDirectory = Path.GetFullPath(arguments[index]);
        }

        var mode = native
            ? LauncherSmokeMode.Native
            : performance ? LauncherSmokeMode.Performance : LauncherSmokeMode.None;
        if (mode == LauncherSmokeMode.None && outputDirectory is not null)
        {
            return LauncherSmokeParseResult.Failure(
                "LAUNCHER_SMOKE_ARGUMENT_INVALID",
                "--smoke-output 只能用于 Launcher smoke 模式。");
        }

        return LauncherSmokeParseResult.Success(new LauncherSmokeOptions(mode, outputDirectory));
    }
}

internal sealed record LauncherSmokeParseResult(
    bool IsSuccess,
    LauncherSmokeOptions Options,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static LauncherSmokeParseResult Success(LauncherSmokeOptions options) =>
        new(true, options, null, null);

    public static LauncherSmokeParseResult Failure(string code, string message) =>
        new(false, new LauncherSmokeOptions(LauncherSmokeMode.None, null), code, message);
}

/// <summary>使用 nearest-rank 计算 Launcher 热呼出性能分位值和发布门槛结果。</summary>
internal sealed record LauncherPerformanceSummary(
    TimeSpan P50,
    TimeSpan P95,
    TimeSpan P99,
    TimeSpan Threshold,
    bool Passed)
{
    public static LauncherPerformanceSummary Create(
        IReadOnlyCollection<TimeSpan> samples,
        TimeSpan threshold)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("性能样本不能为空。", nameof(samples));

        var ordered = samples.OrderBy(sample => sample).ToArray();
        TimeSpan Percentile(double percentile)
        {
            var rank = (int)Math.Ceiling(percentile * ordered.Length);
            return ordered[Math.Clamp(rank - 1, 0, ordered.Length - 1)];
        }

        var p50 = Percentile(0.50);
        var p95 = Percentile(0.95);
        var p99 = Percentile(0.99);
        return new LauncherPerformanceSummary(p50, p95, p99, threshold, p95 <= threshold);
    }
}

internal sealed record LauncherSmokeResult(
    string Mode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int ProcessId,
    int ExitCode,
    string Stage,
    string? ErrorCode,
    double? ColdStartMs,
    int WarmupCount,
    int SampleCount,
    double? P50Ms,
    double? P95Ms,
    double? P99Ms,
    double ThresholdMs,
    int WindowInstanceCount,
    string FinalLifecycleState);

/// <summary>为每次 smoke 写入独立结果、异常、样本和仅包含 Launcher 客户区的失败截图。</summary>
internal sealed class LauncherSmokeDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private LauncherSmokeDiagnostics(string runDirectory, LauncherSmokeMode mode)
    {
        RunDirectory = runDirectory;
        Mode = mode;
    }

    public string RunDirectory { get; }
    public LauncherSmokeMode Mode { get; }

    public static LauncherSmokeDiagnostics Create(string? configuredDirectory, LauncherSmokeMode mode)
    {
        var directory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(
                Path.GetTempPath(),
                "QuickPhrase-Smoke",
                $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}")
            : Path.GetFullPath(configuredDirectory);
        Directory.CreateDirectory(directory);
        return new LauncherSmokeDiagnostics(directory, mode);
    }

    public Task WriteResultAsync(LauncherSmokeResult result, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            Path.Combine(RunDirectory, "result.json"),
            JsonSerializer.Serialize(result, JsonOptions),
            Encoding.UTF8,
            cancellationToken);

    public Task WriteExceptionAsync(Exception exception, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            Path.Combine(RunDirectory, "exception.txt"),
            exception.ToString(),
            Encoding.UTF8,
            cancellationToken);

    public Task WriteSamplesAsync(
        IReadOnlyList<TimeSpan> samples,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder("sample,elapsed_ms,start_state,end_state,window_instance\r\n");
        for (var index = 0; index < samples.Count; index++)
        {
            builder.Append(index + 1).Append(',')
                .Append(samples[index].TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                .Append(",Hidden,Interactive,1\r\n");
        }
        return File.WriteAllTextAsync(
            Path.Combine(RunDirectory, "performance-samples.csv"),
            builder.ToString(),
            Encoding.UTF8,
            cancellationToken);
    }

    public void TryCaptureLauncher(LauncherWindow? launcher)
    {
        if (launcher is null || !launcher.IsVisible || launcher.ActualWidth <= 0 || launcher.ActualHeight <= 0)
            return;
        try
        {
            launcher.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(launcher);
            var width = Math.Max(1, (int)Math.Ceiling(launcher.ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(launcher.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(launcher);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(RunDirectory, "launcher-failure.png"));
            encoder.Save(stream);
        }
        catch (Exception screenshotException)
        {
            File.AppendAllText(
                Path.Combine(RunDirectory, "exception.txt"),
                $"{Environment.NewLine}{Environment.NewLine}Launcher 截图失败：{screenshotException}",
                Encoding.UTF8);
        }
    }
}

/// <summary>
/// 独立 Launcher smoke 运行器。所有数据和快捷键事件均在内存中构造，
/// 真实验证范围限定为 Application.Resources 话术色板、HotkeyCoordinator、WPF Dispatcher 与单一 LauncherWindow 生命周期。
/// </summary>
internal sealed class LauncherSmokeRunner : IAsyncDisposable
{
    internal const int PerformanceWarmupCount = 10;
    internal const int PerformanceSampleCount = 200;
    internal static readonly TimeSpan PerformanceThreshold = TimeSpan.FromMilliseconds(120);

    private static readonly Guid SmokeCategoryId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset SmokeTimestamp = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly ShortcutChord AltSpace = new(ShortcutModifiers.Alt, ShortcutKey.Space);

    private readonly LauncherSmokeOptions options;
    private readonly LauncherSmokeDiagnostics diagnostics;
    private readonly LauncherSmokeShortcutService shortcutService = new();
    private readonly SearchHistoryCoordinator searchHistory;
    private readonly HotkeyCoordinator hotkeys;
    private readonly LauncherWindow launcher;
    private readonly LauncherInvocationContext invocationContext;
    private TaskCompletionSource<bool> activationDispatched = NewSignal();
    private TaskCompletionSource<Phrase> selectedPhrase = NewPhraseSignal();
    private long activationTimestamp;
    private bool disposed;
    private int windowInstanceCount = 1;

    private LauncherSmokeRunner(LauncherSmokeOptions options)
    {
        this.options = options;
        diagnostics = LauncherSmokeDiagnostics.Create(options.OutputDirectory, options.Mode);
        searchHistory = new SearchHistoryCoordinator(new LauncherSmokeHistoryRepository());
        launcher = new LauncherWindow(new LauncherSmokeSearchService(), searchHistory, hideOnDeactivate: false);
        hotkeys = new HotkeyCoordinator(
            shortcutService,
            action => System.Windows.Application.Current.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                action));
        invocationContext = new LauncherInvocationContext(
            LauncherInvocationMode.Practice,
            phrase =>
            {
                selectedPhrase.TrySetResult(phrase);
                return Task.FromResult(true);
            });
        hotkeys.LauncherActivationReceived += timestamp => activationTimestamp = timestamp;
        hotkeys.LauncherHotkeyPressed += () =>
        {
            launcher.Open(invocationContext: invocationContext);
            activationDispatched.TrySetResult(true);
        };
    }

    public static async Task<int> RunAsync(
        LauncherSmokeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var runner = new LauncherSmokeRunner(options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        return await runner.RunCoreAsync(timeout.Token);
    }

    /// <summary>
    /// 在 smoke 的真实 Application 资源树中读取全部话术色板。
    /// 资源字典采用延迟解析；主动读取可在创建 Launcher 前发现 StaticResource 跨字典解析失败，
    /// 并把错误收敛为可诊断的 RESOURCE 阶段失败。
    /// </summary>
    private static void VerifyPhrasePaletteResources()
    {
        var resources = System.Windows.Application.Current?.Resources
            ?? throw SmokeFailure("LAUNCHER_SMOKE_RESOURCE_UNAVAILABLE", "Application.Resources 尚未初始化。", "RESOURCE");
        foreach (var key in new[]
        {
            "Brush.Phrase.Default", "Brush.Phrase.Orange", "Brush.Phrase.Blue", "Brush.Phrase.Magenta",
            "Brush.Phrase.Purple", "Brush.Phrase.Green", "Brush.Phrase.Pink", "Brush.Phrase.Teal",
            "Brush.Phrase.Tan", "Brush.Phrase.Gray",
        })
        {
            try
            {
                if (resources[key] is not SolidColorBrush)
                    throw SmokeFailure("LAUNCHER_SMOKE_RESOURCE_INVALID", $"资源 {key} 未解析为 SolidColorBrush。", "RESOURCE");
            }
            catch (LauncherSmokeException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw SmokeFailure("LAUNCHER_SMOKE_RESOURCE_INVALID", $"资源 {key} 解析失败：{exception.Message}", "RESOURCE");
            }
        }
    }

    private async Task<int> RunCoreAsync(CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        LauncherPerformanceSummary? summary = null;
        TimeSpan? coldStart = null;
        var samples = new List<TimeSpan>(PerformanceSampleCount);
        var stage = "INITIALIZE";
        string? errorCode = null;
        Exception? failure = null;
        var exitCode = 1;

        try
        {
            stage = "RESOURCE";
            VerifyPhrasePaletteResources();
            await searchHistory.InitializeAsync(cancellationToken);
            await hotkeys.ConfigureAsync(
                new AppSettings(1, false, false, true, AltSpace, false, true, true),
                cancellationToken);
            hotkeys.SetPracticeMode(true);
            if (!hotkeys.LauncherAvailable)
                throw SmokeFailure("LAUNCHER_SMOKE_INITIALIZATION_FAILED", "内存 Alt+Space 未进入可用状态。");

            stage = "FUNCTIONAL";
            await RunFunctionalProbeAsync(cancellationToken);
            coldStart = DateTimeOffset.UtcNow - new DateTimeOffset(
                Process.GetCurrentProcess().StartTime.ToUniversalTime());
            Console.WriteLine($"LAUNCHER_COLD_START interactive={coldStart.Value.TotalMilliseconds:F3}ms gate=none");

            if (options.Mode == LauncherSmokeMode.Performance)
            {
                stage = "WARMUP";
                for (var index = 0; index < PerformanceWarmupCount; index++)
                    _ = await MeasureHotOpenAsync(index + 1, cancellationToken);

                stage = "MEASURE";
                for (var index = 0; index < PerformanceSampleCount; index++)
                    samples.Add(await MeasureHotOpenAsync(index + 1, cancellationToken));

                await diagnostics.WriteSamplesAsync(samples, cancellationToken);
                summary = LauncherPerformanceSummary.Create(samples, PerformanceThreshold);
                Console.WriteLine(
                    $"LAUNCHER_PERF count={samples.Count} warmup={PerformanceWarmupCount} " +
                    $"p50={summary.P50.TotalMilliseconds:F3}ms " +
                    $"p95={summary.P95.TotalMilliseconds:F3}ms " +
                    $"p99={summary.P99.TotalMilliseconds:F3}ms " +
                    $"threshold={PerformanceThreshold.TotalMilliseconds:F0}ms");
                if (!summary.Passed)
                    throw SmokeFailure("LAUNCHER_PERF_THRESHOLD_EXCEEDED", $"Launcher 热呼出 P95 为 {summary.P95.TotalMilliseconds:F3}ms，超过 120ms。");
            }

            stage = "COMPLETE";
            Console.WriteLine("LAUNCHER_SMOKE_PASS");
            exitCode = 0;
        }
        catch (OperationCanceledException exception)
        {
            failure = exception;
            errorCode = "LAUNCHER_SMOKE_TIMEOUT";
            stage = "TIMEOUT";
        }
        catch (LauncherSmokeException exception)
        {
            failure = exception;
            errorCode = exception.ErrorCode;
            stage = exception.Stage;
        }
        catch (Exception exception)
        {
            failure = exception;
            errorCode = "LAUNCHER_SMOKE_UNEXPECTED";
            stage = "UNEXPECTED";
        }

        if (failure is not null)
        {
            launcher.MarkLifecycleFaulted();
            diagnostics.TryCaptureLauncher(launcher);
            await diagnostics.WriteExceptionAsync(failure, CancellationToken.None);
            Console.Error.WriteLine($"{errorCode}：Launcher smoke 在阶段 {stage} 失败。{failure.Message}");
            Console.Error.WriteLine($"诊断目录：{diagnostics.RunDirectory}");
        }

        await DisposeAsync();
        var result = new LauncherSmokeResult(
            options.Mode.ToString(),
            startedAtUtc,
            DateTimeOffset.UtcNow,
            Environment.ProcessId,
            exitCode,
            stage,
            errorCode,
            coldStart?.TotalMilliseconds,
            options.Mode == LauncherSmokeMode.Performance ? PerformanceWarmupCount : 0,
            samples.Count,
            summary?.P50.TotalMilliseconds,
            summary?.P95.TotalMilliseconds,
            summary?.P99.TotalMilliseconds,
            PerformanceThreshold.TotalMilliseconds,
            windowInstanceCount,
            launcher.LifecycleState.ToString());
        await diagnostics.WriteResultAsync(result, CancellationToken.None);
        Console.WriteLine($"LAUNCHER_SMOKE_DIAGNOSTICS={diagnostics.RunDirectory}");
        return exitCode;
    }

    private async Task RunFunctionalProbeAsync(CancellationToken cancellationToken)
    {
        await OpenFromHotkeyAsync(cancellationToken);
        launcher.QueryBox.Text = "Smoke";
        await launcher.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task.WaitAsync(cancellationToken);
        if (launcher.ResultsList.Items.Count < 2)
            throw SmokeFailure("LAUNCHER_SMOKE_SEARCH_FAILED", "固定搜索词未展示至少两条隔离结果。", "SEARCH");

        var before = launcher.ResultsList.SelectedIndex;
        RaiseKey(launcher.QueryBox, Key.Down);
        if (launcher.ResultsList.SelectedIndex <= before)
            throw SmokeFailure("LAUNCHER_SMOKE_KEYBOARD_SELECTION_FAILED", "Down 未移动 Launcher 结果选择。", "KEYBOARD_DOWN");

        selectedPhrase = NewPhraseSignal();
        RaiseKey(launcher.QueryBox, Key.Enter);
        var phrase = await selectedPhrase.Task.WaitAsync(cancellationToken);
        if (!phrase.Title.Contains("Smoke", StringComparison.Ordinal))
            throw SmokeFailure("LAUNCHER_SMOKE_KEYBOARD_SELECTION_FAILED", "Enter 返回了非隔离话术。", "KEYBOARD_ENTER");
        await launcher.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task.WaitAsync(cancellationToken);
        RequireState(LauncherLifecycleState.Hidden, 0, "核心链路隐藏完成");
    }

    private async Task<TimeSpan> MeasureHotOpenAsync(int iteration, CancellationToken cancellationToken)
    {
        RequireState(LauncherLifecycleState.Hidden, iteration, "开始");
        var sameWindow = launcher;
        await OpenFromHotkeyAsync(cancellationToken);
        if (!ReferenceEquals(sameWindow, launcher))
            throw SmokeFailure("LAUNCHER_SMOKE_LIFECYCLE_INVALID", $"第 {iteration} 次循环替换了 LauncherWindow。", "LIFECYCLE");
        RequireState(LauncherLifecycleState.Interactive, iteration, "可交互");
        if (activationTimestamp == 0)
            throw SmokeFailure("LAUNCHER_SMOKE_HOTKEY_NOT_RECEIVED", $"第 {iteration} 次循环未记录 HotkeyCoordinator 激活时间。", "HOTKEY");

        var elapsed = Stopwatch.GetElapsedTime(activationTimestamp, Stopwatch.GetTimestamp());
        launcher.HideLauncher();
        await launcher.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task.WaitAsync(cancellationToken);
        RequireState(LauncherLifecycleState.Hidden, iteration, "隐藏完成");
        return elapsed;
    }

    private async Task OpenFromHotkeyAsync(CancellationToken cancellationToken)
    {
        activationTimestamp = 0;
        activationDispatched = NewSignal();
        shortcutService.RaiseActivated();
        await activationDispatched.Task.WaitAsync(cancellationToken);
        await launcher.WaitForInteractiveAsync(cancellationToken);
    }

    private void RequireState(LauncherLifecycleState expected, int iteration, string stage)
    {
        if (launcher.LifecycleState != expected)
        {
            throw SmokeFailure(
                "LAUNCHER_SMOKE_LIFECYCLE_INVALID",
                $"Launcher 第 {iteration} 次循环在{stage}期望 {expected}，实际 {launcher.LifecycleState}。",
                "LIFECYCLE");
        }
    }

    private static void RaiseKey(FrameworkElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)
            ?? throw SmokeFailure("LAUNCHER_SMOKE_KEYBOARD_SELECTION_FAILED", "Launcher 尚未连接到 PresentationSource。", "KEYBOARD");
        target.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
    }

    private static LauncherSmokeException SmokeFailure(string code, string message, string stage = "RUN") =>
        new(code, stage, message);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<Phrase> NewPhraseSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        launcher.DisposeLauncher();
        await hotkeys.DisposeAsync();
    }

    private static Phrase CreatePhrase(Guid id, string title, string content, int sortOrder) =>
        new(
            id,
            title,
            PhraseBody.FromText(content),
            SmokeCategoryId,
            ShortcutMode.None,
            null,
            0,
            null,
            1,
            SmokeTimestamp,
            SmokeTimestamp,
            "default",
            sortOrder);

    private sealed class LauncherSmokeSearchService : ISearchService
    {
        private readonly Phrase[] phrases =
        [
            CreatePhrase(Guid.Parse("10000000-0000-0000-0000-000000000101"), "【Smoke】设备信息收集", "请提供设备信息。", 0),
            CreatePhrase(Guid.Parse("10000000-0000-0000-0000-000000000102"), "【Smoke】售后跟进", "这是隔离 Smoke 测试内容。", 1),
            CreatePhrase(Guid.Parse("10000000-0000-0000-0000-000000000103"), "【Smoke】多行内容", "第一行\n第二行", 2),
        ];

        public SearchIndexStatus Status { get; } = new(SearchIndexState.Ready, 3);

        public SearchResponse Search(SearchRequest request)
        {
            var query = request.Query.Trim();
            var selected = string.IsNullOrEmpty(query)
                ? phrases
                : phrases.Where(phrase => phrase.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            var matchKind = string.IsNullOrEmpty(query) ? SearchMatchKind.EmptyQuery : SearchMatchKind.TitleContains;
            return new SearchResponse(
                selected.Take(request.Limit)
                    .Select(phrase => new SearchResult(phrase, matchKind))
                    .ToImmutableArray(),
                Status);
        }
    }

    private sealed class LauncherSmokeHistoryRepository : ISearchHistoryRepository
    {
        private readonly List<SearchHistoryEntry> entries = [];

        public Task<IReadOnlyList<SearchHistoryEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchHistoryEntry>>(entries.ToArray());

        public Task<RepositoryResult<SearchHistoryEntry>> RecordAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new SearchHistoryEntry(query.Trim(), DateTimeOffset.UtcNow);
            entries.RemoveAll(item => string.Equals(item.Query, entry.Query, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, entry);
            return Task.FromResult(RepositoryResult<SearchHistoryEntry>.Success(entry));
        }

        public Task<RepositoryResult<bool>> ClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Clear();
            return Task.FromResult(RepositoryResult<bool>.Success(true));
        }
    }

    private sealed class LauncherSmokeShortcutService : IShortcutService
    {
        private ShortcutChord activeChord;
        private ShortcutChord? stagedChord;
        private ShortcutStageToken stagedToken;
        private bool enabled;

        public event EventHandler? Activated;
        public ShortcutChord ActiveChord => activeChord;

        public Task<ShortcutStageResult> StageAsync(
            ShortcutChord chord,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stagedChord = chord;
            stagedToken = ShortcutStageToken.Create();
            return Task.FromResult(ShortcutStageResult.Success(stagedToken));
        }

        public Task<ShortcutApplyResult> CommitAsync(
            ShortcutStageToken token,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stagedChord is null || token != stagedToken)
            {
                return Task.FromResult(ShortcutApplyResult.Failure(
                    "HOTKEY_STAGE_NOT_FOUND",
                    "找不到 Launcher smoke 待提交快捷键。"));
            }
            activeChord = stagedChord.Value;
            stagedChord = null;
            stagedToken = default;
            return Task.FromResult(ShortcutApplyResult.Success());
        }

        public Task RollbackAsync(
            ShortcutStageToken token,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stagedChord = null;
            stagedToken = default;
            return Task.CompletedTask;
        }

        public void SetEnabled(bool value) => enabled = value;

        public void RaiseActivated()
        {
            if (!enabled)
                throw new InvalidOperationException("Launcher smoke 快捷键尚未启用。");
            Activated?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LauncherSmokeException : Exception
    {
        public LauncherSmokeException(string errorCode, string stage, string message) : base(message)
        {
            ErrorCode = errorCode;
            Stage = stage;
        }

        public string ErrorCode { get; }
        public string Stage { get; }
    }
}
