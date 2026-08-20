# Launcher Smoke Restoration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 恢复独立、隔离、自动退出的真实 WPF Launcher smoke，验证 HotkeyCoordinator 到 LauncherWindow 的核心链路，并以 200 次热呼出 P95 `<= 120ms` 作为发布门槛。

**Architecture:** 在现有 Desktop EXE 中增加早期 smoke 分支，使用内存搜索、历史和快捷键服务构造唯一 LauncherWindow。真实 HotkeyCoordinator 在收到 `IShortcutService.Activated` 时记录计时起点，LauncherWindow 通过 `LauncherLifecycleState` 管理重复显示/隐藏；PowerShell watchdog 直接运行已构建 EXE，并按 Native 30 秒、Performance 60 秒超时清理。

**Tech Stack:** .NET 10、WPF、xUnit、PowerShell 5/7、`Stopwatch.GetTimestamp`、`RenderTargetBitmap`、JSON/CSV。

---

## 实施约束

- 严格 TDD：测试先失败，再做最小实现。
- 不修改或暂存：
  - `desktop/QuickPhrase.Desktop/DesignSystem/Tokens/Thickness.xaml`
  - `desktop/QuickPhrase.Desktop/Views/SettingsView.xaml`
  - `tests/QuickPhrase.Desktop.Tests/SettingsViewContractTests.cs`
- 不使用 `git add -A`、`git reset`、`git clean`。
- 不新增第四个正式 Desktop Project。
- 不连接真实 SQLite、外部应用、Clipboard、UIA 或 Win32 RegisterHotKey。
- 每个 Task 只暂存其明确列出的文件。

### Task 1: 锁定参数、统计和脚本契约

**Files:**
- Create: `tests/QuickPhrase.Desktop.Tests/LauncherSmokeTests.cs`
- Create: `tests/QuickPhrase.Desktop.Tests/LauncherSmokeScriptContractTests.cs`

- [ ] **Step 1: 写参数和百分位失败测试**

```csharp
using QuickPhrase.Desktop;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

public sealed class LauncherSmokeTests
{
    [Theory]
    [InlineData("--smoke-native-launcher", LauncherSmokeMode.Native)]
    [InlineData("--smoke-launcher-performance", LauncherSmokeMode.Performance)]
    public void Options_ParseSingleMode(string argument, LauncherSmokeMode expected)
    {
        var result = LauncherSmokeOptions.Parse([argument]);
        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Options.Mode);
    }

    [Fact]
    public void Options_RejectConflictingModes()
    {
        var result = LauncherSmokeOptions.Parse([
            "--smoke-native-launcher",
            "--smoke-launcher-performance",
        ]);
        Assert.False(result.IsSuccess);
        Assert.Equal("LAUNCHER_SMOKE_ARGUMENT_INVALID", result.ErrorCode);
    }

    [Fact]
    public void Options_IgnoreOrdinaryStartup()
    {
        var result = LauncherSmokeOptions.Parse(["--background"]);
        Assert.True(result.IsSuccess);
        Assert.Equal(LauncherSmokeMode.None, result.Options.Mode);
    }

    [Fact]
    public void PerformanceSummary_UsesNearestRank()
    {
        var samples = Enumerable.Range(1, 200)
            .Select(value => TimeSpan.FromMilliseconds(value)).ToArray();
        var summary = LauncherPerformanceSummary.Create(samples, TimeSpan.FromMilliseconds(120));
        Assert.Equal(100, summary.P50.TotalMilliseconds);
        Assert.Equal(190, summary.P95.TotalMilliseconds);
        Assert.Equal(198, summary.P99.TotalMilliseconds);
        Assert.False(summary.Passed);
    }

    [Fact]
    public void PerformanceSummary_AllowsP95EqualToThreshold()
    {
        var samples = Enumerable.Repeat(TimeSpan.FromMilliseconds(120), 200).ToArray();
        Assert.True(LauncherPerformanceSummary
            .Create(samples, TimeSpan.FromMilliseconds(120)).Passed);
    }

    [Fact]
    public void PerformanceSummary_RejectsEmptySamples() =>
        Assert.Throws<ArgumentException>(() =>
            LauncherPerformanceSummary.Create([], TimeSpan.FromMilliseconds(120)));
}
```

- [ ] **Step 2: 写脚本契约失败测试**

`LauncherSmokeScriptContractTests` 读取 `verify-phase1.ps1`、`verify-phase4.ps1`、`verify-phase5.ps1`、`verify-phase51.ps1`，断言：

```csharp
Assert.DoesNotContain("dotnet run", source, StringComparison.OrdinalIgnoreCase);
Assert.Contains("invoke-launcher-smoke.ps1", source, StringComparison.OrdinalIgnoreCase);
```

读取 watchdog，断言包含：

```text
Native = 30
Performance = 60
-WindowStyle Hidden
Stop-Process -Id $process.Id -Force
```

并提供 `FindRepositoryRoot()`，从 `AppContext.BaseDirectory` 向上查找 `QuickPhrase.sln`。

- [ ] **Step 3: 运行并确认失败**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~LauncherSmokeTests|FullyQualifiedName~LauncherSmokeScriptContractTests"
```

Expected: FAIL；模型和 watchdog 不存在，旧脚本仍直接调用 `dotnet run`。

- [ ] **Step 4: 提交测试骨架**

```powershell
git add -- tests/QuickPhrase.Desktop.Tests/LauncherSmokeTests.cs tests/QuickPhrase.Desktop.Tests/LauncherSmokeScriptContractTests.cs
git commit -m "test: 锁定 Launcher smoke 恢复契约"
```

### Task 2: 实现参数、百分位和生命周期状态

**Files:**
- Create: `desktop/QuickPhrase.Desktop/LauncherSmokeRunner.cs`
- Modify: `desktop/QuickPhrase.Desktop/LauncherWindow.xaml.cs`
- Modify: `tests/QuickPhrase.Desktop.Tests/LauncherSmokeTests.cs`

- [ ] **Step 1: 补充生命周期枚举失败测试**

```csharp
[Fact]
public void LauncherLifecycleState_ContainsStableReuseStates()
{
    Assert.Equal(new[]
    {
        "Created", "Activating", "Visible", "Interactive",
        "Hiding", "Hidden", "Disposed", "Faulted",
    }, Enum.GetNames<LauncherLifecycleState>());
}
```

- [ ] **Step 2: 运行并确认类型缺失**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~LauncherSmokeTests
```

Expected: FAIL。

- [ ] **Step 3: 实现最小模型**

在 `LauncherSmokeRunner.cs` 实现：

```csharp
internal enum LauncherSmokeMode { None, Native, Performance }

internal enum LauncherLifecycleState
{
    Created, Activating, Visible, Interactive,
    Hiding, Hidden, Disposed, Faulted,
}

internal sealed record LauncherSmokeOptions(LauncherSmokeMode Mode, string? OutputDirectory)
{
    public static LauncherSmokeParseResult Parse(IReadOnlyList<string> arguments)
    {
        var native = arguments.Contains("--smoke-native-launcher", StringComparer.OrdinalIgnoreCase);
        var performance = arguments.Contains("--smoke-launcher-performance", StringComparer.OrdinalIgnoreCase);
        if (native && performance)
            return LauncherSmokeParseResult.Failure(
                "LAUNCHER_SMOKE_ARGUMENT_INVALID", "Launcher smoke 模式不能同时指定。");

        string? output = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--smoke-output", StringComparison.OrdinalIgnoreCase)) continue;
            if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                return LauncherSmokeParseResult.Failure(
                    "LAUNCHER_SMOKE_ARGUMENT_INVALID", "--smoke-output 缺少目录参数。");
            output = Path.GetFullPath(arguments[index]);
        }

        var mode = native ? LauncherSmokeMode.Native
            : performance ? LauncherSmokeMode.Performance : LauncherSmokeMode.None;
        if (mode == LauncherSmokeMode.None && output is not null)
            return LauncherSmokeParseResult.Failure(
                "LAUNCHER_SMOKE_ARGUMENT_INVALID", "--smoke-output 只能用于 smoke。 ");
        return LauncherSmokeParseResult.Success(new(mode, output));
    }
}

internal sealed record LauncherSmokeParseResult(
    bool IsSuccess, LauncherSmokeOptions Options, string? ErrorCode, string? ErrorMessage)
{
    public static LauncherSmokeParseResult Success(LauncherSmokeOptions options) =>
        new(true, options, null, null);
    public static LauncherSmokeParseResult Failure(string code, string message) =>
        new(false, new(LauncherSmokeMode.None, null), code, message);
}

internal sealed record LauncherPerformanceSummary(
    TimeSpan P50, TimeSpan P95, TimeSpan P99, TimeSpan Threshold, bool Passed)
{
    public static LauncherPerformanceSummary Create(
        IReadOnlyCollection<TimeSpan> samples, TimeSpan threshold)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) throw new ArgumentException("性能样本不能为空。", nameof(samples));
        var ordered = samples.OrderBy(value => value).ToArray();
        TimeSpan Percentile(double value)
        {
            var rank = (int)Math.Ceiling(value * ordered.Length);
            return ordered[Math.Clamp(rank - 1, 0, ordered.Length - 1)];
        }
        var p50 = Percentile(.50);
        var p95 = Percentile(.95);
        var p99 = Percentile(.99);
        return new(p50, p95, p99, threshold, p95 <= threshold);
    }
}
```

- [ ] **Step 4: 在 LauncherWindow 跟踪状态**

增加只读内部属性，并在 `Open`、`WaitForInteractiveAsync`、`HideLauncher`、`DisposeLauncher` 精确设置状态：

```csharp
internal LauncherLifecycleState LifecycleState { get; private set; } = LauncherLifecycleState.Created;
```

增加：

```csharp
internal async Task WaitForInteractiveAsync(CancellationToken cancellationToken)
{
    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render, cancellationToken);
    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input, cancellationToken);
    if (!IsVisible || !QueryBox.IsVisible || !QueryBox.IsEnabled || !QueryBox.IsKeyboardFocusWithin)
        throw new InvalidOperationException(
            $"Launcher 未进入可输入状态。Visible={IsVisible}，QueryVisible={QueryBox.IsVisible}，Enabled={QueryBox.IsEnabled}，Focus={QueryBox.IsKeyboardFocusWithin}。");
    LifecycleState = LauncherLifecycleState.Interactive;
}
```

保留现有 `WaitForRenderAsync()`。

- [ ] **Step 5: 运行模型测试**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~LauncherSmokeTests
```

Expected: 参数、统计和枚举测试通过。

- [ ] **Step 6: 提交**

```powershell
git add -- desktop/QuickPhrase.Desktop/LauncherSmokeRunner.cs desktop/QuickPhrase.Desktop/LauncherWindow.xaml.cs tests/QuickPhrase.Desktop.Tests/LauncherSmokeTests.cs
git commit -m "feat: 建立 Launcher smoke 模型和生命周期状态"
```

### Task 3: 从 HotkeyCoordinator 暴露准确计时点

**Files:**
- Modify: `desktop/QuickPhrase.Desktop/HotkeyCoordinator.cs`
- Modify: `tests/QuickPhrase.Desktop.Tests/HotkeyCoordinatorTests.cs`

- [ ] **Step 1: 写计时顺序失败测试**

```csharp
[Fact]
public async Task Activated_ReportsCoordinatorReceiveTimestampBeforeUiDispatch()
{
    var service = new FakeShortcutService();
    Action? pendingUiAction = null;
    await using var coordinator = new HotkeyCoordinator(service, action => pendingUiAction = action);
    await coordinator.ConfigureAsync(CreateSettings(AltSpace));
    coordinator.SetPracticeMode(true);

    long received = 0;
    var pressed = false;
    coordinator.LauncherActivationReceived += timestamp => received = timestamp;
    coordinator.LauncherHotkeyPressed += () => pressed = true;

    var before = Stopwatch.GetTimestamp();
    service.RaiseActivated();
    var after = Stopwatch.GetTimestamp();

    Assert.InRange(received, before, after);
    Assert.False(pressed);
    Assert.NotNull(pendingUiAction);
    pendingUiAction();
    Assert.True(pressed);
}
```

- [ ] **Step 2: 运行并确认事件不存在**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~Activated_ReportsCoordinatorReceiveTimestampBeforeUiDispatch
```

Expected: FAIL。

- [ ] **Step 3: 实现诊断事件**

在 `HotkeyCoordinator` 增加中文注释和：

```csharp
internal event Action<long>? LauncherActivationReceived;
```

在 `Service_Activated` 通过现有可用性检查后、`dispatchToUi` 前执行：

```csharp
var receivedTimestamp = Stopwatch.GetTimestamp();
LauncherActivationReceived?.Invoke(receivedTimestamp);
```

不修改 `LauncherHotkeyPressed` 类型，不让生产 Controller 订阅。

- [ ] **Step 4: 验证并提交**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~HotkeyCoordinatorTests
git add -- desktop/QuickPhrase.Desktop/HotkeyCoordinator.cs tests/QuickPhrase.Desktop.Tests/HotkeyCoordinatorTests.cs
git commit -m "feat: 记录 Launcher 热键协调器接收时刻"
```

### Task 4: 实现隔离数据、真实交互和诊断

**Files:**
- Modify: `desktop/QuickPhrase.Desktop/LauncherSmokeRunner.cs`
- Modify: `tests/QuickPhrase.Desktop.Tests/LauncherSmokeTests.cs`

- [ ] **Step 1: 写隔离边界和诊断失败测试**

源码扫描必须拒绝：

```text
QuickPhraseDataRuntime
QuickPhraseDataOptions.ForCurrentUser
WindowsShortcutService
WindowsTargetDetector
WindowsAdapterResolver
TextDeliveryFactory
Clipboard
AutomationElement
```

并断言 runner 包含唯一 `new LauncherWindow`、循环中使用 `ReferenceEquals`。诊断测试在临时根目录创建 run directory，并在测试 `finally` 中只删除该测试专属目录。

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~LauncherSmokeTests
```

Expected: FAIL。

- [ ] **Step 3: 实现内存服务**

`LauncherSmokeSearchService : ISearchService` 使用固定分类 ID 和固定时间构造三条 Phrase：

```csharp
private static readonly Guid SmokeCategoryId = Guid.Parse("10000000-0000-0000-0000-000000000001");
private static readonly DateTimeOffset SmokeTimestamp = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

private static Phrase CreatePhrase(Guid id, string title, string content, int sortOrder) =>
    new(
        id,
        title,
        content,
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
```

固定 ID 分别使用 `...000101`、`...000102`、`...000103`。`Search(SearchRequest request)` 对空查询返回全部，对 `Smoke` 按标题返回固定结果，并构造：

```csharp
var matchKind = string.IsNullOrEmpty(query)
    ? SearchMatchKind.EmptyQuery
    : SearchMatchKind.TitleContains;
return new SearchResponse(
    selected
        .Take(request.Limit)
        .Select(phrase => new SearchResult(phrase, matchKind))
        .ToImmutableArray(),
    Status);
```

`LauncherSmokeHistoryRepository : ISearchHistoryRepository` 只操作内存。

`LauncherSmokeShortcutService : IShortcutService` 支持 Stage/Commit/Rollback/SetEnabled/RaiseActivated，不调用 Win32。

关键激活方法：

```csharp
public void RaiseActivated()
{
    if (!enabled) throw new InvalidOperationException("Launcher smoke 快捷键尚未启用。");
    Activated?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 4: 实现唯一窗口和核心交互**

Runner 只创建一次 SearchHistoryCoordinator、LauncherWindow 和 HotkeyCoordinator。配置 Alt+Space、订阅 `LauncherActivationReceived` 和 `LauncherHotkeyPressed`，通过 Practice `LauncherInvocationContext` 接收选择。

真实键盘事件：

```csharp
private static void RaiseKey(FrameworkElement target, Key key)
{
    var source = PresentationSource.FromVisual(target)
        ?? throw new InvalidOperationException("Launcher 尚未连接到 PresentationSource。");
    target.RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, source, Environment.TickCount, key)
    {
        RoutedEvent = Keyboard.KeyDownEvent,
    });
}
```

验证 QueryBox 输入、ResultsList 展示、Down 选择和 Enter Practice 回调；不得触发 Delivery。

- [ ] **Step 5: 实现诊断**

`LauncherSmokeDiagnostics` 创建 `%TEMP%\QuickPhrase-Smoke\...`，写 `result.json`、`exception.txt`、`performance-samples.csv`。失败截图用 `RenderTargetBitmap` 只捕获 LauncherWindow 客户区，截图异常追加记录但不覆盖原异常。

- [ ] **Step 6: 验证并提交**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~LauncherSmokeTests
git add -- desktop/QuickPhrase.Desktop/LauncherSmokeRunner.cs tests/QuickPhrase.Desktop.Tests/LauncherSmokeTests.cs
git commit -m "feat: 实现隔离 Launcher smoke 核心链路"
```

### Task 5: 集成 App 早期分流

**Files:**
- Modify: `desktop/QuickPhrase.Desktop/App.xaml.cs`
- Modify: `tests/QuickPhrase.Desktop.Tests/LauncherSmokeTests.cs`

- [ ] **Step 1: 写启动顺序失败测试**

读取 `App.xaml.cs`，断言 `LauncherSmokeOptions.Parse` 位于 `new ApplicationController` 之前。

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~App_HandlesSmokeBeforeApplicationControllerConstruction
```

Expected: FAIL。

- [ ] **Step 3: 集成 smoke 分支**

主题资源初始化后、Controller 创建前解析参数。解析失败退出 2；进入 smoke 后等待 runner，捕获异常输出 `LAUNCHER_SMOKE_UNEXPECTED`，在 `finally` 中 `Shutdown(exitCode)` 并 return。Runner 内部 90 秒兜底 token，外层 watchdog 使用 30/60 秒硬超时。

- [ ] **Step 4: 验证并提交**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~LauncherSmokeTests
dotnet build QuickPhrase.sln --no-restore
git add -- desktop/QuickPhrase.Desktop/App.xaml.cs tests/QuickPhrase.Desktop.Tests/LauncherSmokeTests.cs
git commit -m "feat: 接入 Launcher smoke 独立启动模式"
```

### Task 6: 实现预热、200 次采样和 P95 门槛

**Files:**
- Modify: `desktop/QuickPhrase.Desktop/LauncherSmokeRunner.cs`
- Modify: `tests/QuickPhrase.Desktop.Tests/LauncherSmokeTests.cs`

- [ ] **Step 1: 写固定样本契约失败测试**

```csharp
[Fact]
public void PerformanceContract_UsesConfirmedCountsAndThreshold()
{
    Assert.Equal(10, LauncherSmokeRunner.PerformanceWarmupCount);
    Assert.Equal(200, LauncherSmokeRunner.PerformanceSampleCount);
    Assert.Equal(TimeSpan.FromMilliseconds(120), LauncherSmokeRunner.PerformanceThreshold);
}
```

- [ ] **Step 2: 运行并确认常量不存在**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~PerformanceContract_UsesConfirmedCountsAndThreshold
```

Expected: FAIL。

- [ ] **Step 3: 实现性能循环**

定义：

```csharp
internal const int PerformanceWarmupCount = 10;
internal const int PerformanceSampleCount = 200;
internal static readonly TimeSpan PerformanceThreshold = TimeSpan.FromMilliseconds(120);
```

每次测量必须：

```csharp
private async Task<TimeSpan> MeasureHotOpenAsync(int iteration, CancellationToken cancellationToken)
{
    RequireState(LauncherLifecycleState.Hidden, iteration, "开始");
    var sameWindow = launcher;
    activationTimestamp = 0;
    activationDispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);

    shortcutService.RaiseActivated();
    await activationDispatched.Task.WaitAsync(cancellationToken);
    await launcher.WaitForInteractiveAsync(cancellationToken);

    if (!ReferenceEquals(sameWindow, launcher))
        throw CreateLifecycleError(iteration, "LauncherWindow 实例被替换。");
    RequireState(LauncherLifecycleState.Interactive, iteration, "可交互");
    if (activationTimestamp == 0)
        throw CreateSmokeError(
            "LAUNCHER_SMOKE_HOTKEY_NOT_RECEIVED",
            "HotkeyCoordinator 未记录激活时间。");

    var elapsed = Stopwatch.GetElapsedTime(
        activationTimestamp,
        Stopwatch.GetTimestamp());

    launcher.HideLauncher();
    await launcher.Dispatcher.InvokeAsync(
        () => { }, DispatcherPriority.Background, cancellationToken);
    RequireState(LauncherLifecycleState.Hidden, iteration, "隐藏完成");
    return elapsed;
}
```

`activationTimestamp` 只能由 `LauncherActivationReceived` 设置，禁止在 `RaiseActivated()` 之前启动计时。

- [ ] **Step 4: 输出冷启动和统计**

冷启动使用 `Process.StartTime` 到首次 Interactive：

```text
LAUNCHER_COLD_START interactive={coldStart.TotalMilliseconds:F3}ms gate=none
```

正式输出：

```text
LAUNCHER_PERF count=200 warmup=10 p50={summary.P50.TotalMilliseconds:F3}ms p95={summary.P95.TotalMilliseconds:F3}ms p99={summary.P99.TotalMilliseconds:F3}ms threshold=120ms
```

CSV 必须恰好 200 条数据行。P95 超限写 `LAUNCHER_PERF_THRESHOLD_EXCEEDED` 并非零退出。

- [ ] **Step 5: 验证并提交**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~LauncherSmokeTests
git add -- desktop/QuickPhrase.Desktop/LauncherSmokeRunner.cs tests/QuickPhrase.Desktop.Tests/LauncherSmokeTests.cs
git commit -m "feat: 恢复 Launcher 热呼出性能门槛"
```

### Task 7: 实现 watchdog 并替换旧调用

**Files:**
- Create: `scripts/invoke-launcher-smoke.ps1`
- Modify: `scripts/verify-phase1.ps1`
- Modify: `scripts/verify-phase4.ps1`
- Modify: `scripts/verify-phase5.ps1`
- Modify: `scripts/verify-phase51.ps1`
- Modify: `tests/QuickPhrase.Desktop.Tests/LauncherSmokeScriptContractTests.cs`

- [ ] **Step 1: 实现 watchdog**

```powershell
param(
  [ValidateSet('Native', 'Performance')]
  [string]$Mode,
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exe = [IO.Path]::GetFullPath((Join-Path $workspace "desktop\QuickPhrase.Desktop\bin\$Configuration\net10.0-windows10.0.19041.0\QuickPhrase.exe"))
if (-not (Test-Path -LiteralPath $exe)) { throw "Launcher smoke EXE 不存在：$exe" }

$timeouts = @{ Native = 30; Performance = 60 }
$argument = if ($Mode -eq 'Native') { '--smoke-native-launcher' } else { '--smoke-launcher-performance' }
$runDirectory = Join-Path $env:TEMP ("QuickPhrase-Smoke\{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff'), $PID)
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$stdout = Join-Path $runDirectory 'stdout.log'
$stderr = Join-Path $runDirectory 'stderr.log'

$process = Start-Process -FilePath $exe `
  -ArgumentList @($argument, '--smoke-output', ('"{0}"' -f $runDirectory)) `
  -WindowStyle Hidden -PassThru `
  -RedirectStandardOutput $stdout `
  -RedirectStandardError $stderr

$completed = $process.WaitForExit($timeouts[$Mode] * 1000)
if (-not $completed)
{
  Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
  $process.WaitForExit()
  "LAUNCHER_SMOKE_TIMEOUT：$Mode smoke 超过 $($timeouts[$Mode]) 秒；PID=$($process.Id)" |
    Set-Content -LiteralPath (Join-Path $runDirectory 'watchdog-timeout.txt') -Encoding utf8
  Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue
  Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue
  exit 124
}

Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue
Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue
exit $process.ExitCode
```

实施时在 Windows PowerShell 5 实测参数引号、输出重定向和 `WaitForExit`。只能终止明确 `$process.Id`。

- [ ] **Step 2: 替换 Phase 脚本**

Phase 1 可选 Native：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-launcher-smoke.ps1 -Mode Native -Configuration Debug
```

Phase 4/5/5.1 使用 Release；Performance 调用 `-Mode Performance`，Native 调用 `-Mode Native`。所有 `dotnet run ... --smoke-*` 必须消失。

- [ ] **Step 3: 运行脚本契约测试**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~LauncherSmokeScriptContractTests
```

Expected: PASS。

- [ ] **Step 4: 提交**

```powershell
git add -- scripts/invoke-launcher-smoke.ps1 scripts/verify-phase1.ps1 scripts/verify-phase4.ps1 scripts/verify-phase5.ps1 scripts/verify-phase51.ps1 tests/QuickPhrase.Desktop.Tests/LauncherSmokeScriptContractTests.cs
git commit -m "fix: 为 Launcher smoke 增加超时清理"
```

### Task 8: 真实运行两个 smoke

**Files:**
- Modify only when diagnostics prove a defect in Task 2-7 files.
- Generated: `%TEMP%\QuickPhrase-Smoke\...`

- [ ] **Step 1: Release 构建**

```powershell
dotnet build QuickPhrase.sln -c Release --no-restore
```

Expected: 0 errors；warning 如实记录。

- [ ] **Step 2: 运行 Native**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-launcher-smoke.ps1 -Mode Native -Configuration Release
```

Expected: 30 秒内输出 `LAUNCHER_SMOKE_PASS`，退出 0。

- [ ] **Step 3: 检查 Native 诊断与 PID**

最新目录至少包含 `result.json`、`stdout.log`、`stderr.log`。失败时包含 `exception.txt` 和可生成的 `launcher-failure.png`。记录本次 PID，确认退出后不存在；不得处理用户原有 QuickPhrase PID。

- [ ] **Step 4: 运行 Performance**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-launcher-smoke.ps1 -Mode Performance -Configuration Release
```

Expected: 60 秒内退出 0，并输出真实冷启动、P50/P95/P99；P95 `<=120ms`。

- [ ] **Step 5: 检查 Performance 诊断**

确认：

```text
performance-samples.csv 数据行 = 200
result.json sampleCount = 200
result.json windowInstanceCount = 1
result.json finalLifecycleState = Disposed
```

确认本次 PID 无残留。

- [ ] **Step 6: 失败时只按诊断做单一根因修复**

使用 result、exception、CSV 和 Launcher 截图。禁止放宽阈值、减少样本、跳过生命周期检查或增加与根因无关的重试。

- [ ] **Step 7: 有修正才提交**

只暂存实际修正的明确文件；首次通过时不创建空提交。

### Task 9: 更新 Phase 4 验证文档

**Files:**
- Modify: `docs/phase4-validation.md`
- Modify: `tests/QuickPhrase.Desktop.Tests/LauncherSmokeScriptContractTests.cs`

- [ ] **Step 1: 写文档契约失败测试**

```csharp
[Fact]
public void Phase4Validation_DefinesActualHotOpenMetricAndIsolationBoundary()
{
    var root = FindRepositoryRoot();
    var source = File.ReadAllText(Path.Combine(root, "docs", "phase4-validation.md"));
    Assert.Contains("HotkeyCoordinator 收到", source, StringComparison.Ordinal);
    Assert.Contains("预热 10 次", source, StringComparison.Ordinal);
    Assert.Contains("正式采样 200 次", source, StringComparison.Ordinal);
    Assert.Contains("120ms", source, StringComparison.Ordinal);
    Assert.Contains("不替代 Platform.Windows 的 RegisterHotKey 测试", source, StringComparison.Ordinal);
    Assert.Contains("冷启动", source, StringComparison.Ordinal);
    Assert.Contains("不作为发布门槛", source, StringComparison.Ordinal);
}
```

- [ ] **Step 2: 运行并确认旧文档失败**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~Phase4Validation_DefinesActualHotOpenMetricAndIsolationBoundary
```

Expected: FAIL。

- [ ] **Step 3: 用真实结果更新文档**

更新内容必须：

- 删除不存在的 WebView2 smoke 和过期纯 Web 架构描述。
- 写入真实实施日期和命令。
- 写入真实冷启动、P50/P95/P99。
- 定义 HotkeyCoordinator receivedTimestamp 到输入就绪。
- 说明预热 10、采样 200、nearest-rank、单窗口复用。
- 说明内存数据、合成激活和诊断目录。
- 包含精确句子：`不替代 Platform.Windows 的 RegisterHotKey 测试`。

不得预写或伪造性能数字。

- [ ] **Step 4: 验证并提交**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~LauncherSmokeScriptContractTests
git add -- docs/phase4-validation.md tests/QuickPhrase.Desktop.Tests/LauncherSmokeScriptContractTests.cs
git commit -m "docs: 对齐 Launcher smoke 性能口径"
```

### Task 10: Phase 5 门禁与最终验证

**Files:**
- No source changes expected.

- [ ] **Step 1: 完整测试和 Release 构建**

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore
dotnet test tests/QuickPhrase.Architecture.Tests/QuickPhrase.Architecture.Tests.csproj --no-restore
dotnet build QuickPhrase.sln -c Release --no-restore
dotnet test QuickPhrase.sln -c Release --no-build
```

Expected: 全部通过；构建 0 errors。

- [ ] **Step 2: 复跑已确认的企业微信人工门禁**

只在本次命令进程设置：

```powershell
$env:QUICKPHRASE_WECOM_ACCEPTANCE = "passed"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-phase5.ps1 -IncludeDesktopSmoke -IncludeWeComAcceptance
```

Expected:

```text
PHASE5_INFRA_PASS
PHASE5_VERIFY_PASS
```

不得永久设置用户环境变量。

- [ ] **Step 3: 验证无残留**

比较运行前后 PID，只检查本次 watchdog 返回的 smoke PID。不得终止用户原有 QuickPhrase 实例。

- [ ] **Step 4: 检查工作区边界**

```powershell
git status --short --branch
git diff --check
git diff --name-only HEAD~10..HEAD
```

确认未修改、暂存或提交用户已有三个设置页相关文件。

- [ ] **Step 5: 最终报告**

报告 Native 退出码、冷启动、P50/P95/P99、样本数、阈值、诊断目录、Phase 5 输出、构建测试结果、RegisterHotKey/真实企业微信边界和未提交文件保持情况。

## 计划自审

### Spec coverage

- 独立、隔离、自动退出：Task 4、5、7、8。
- 启动、热键、显示、搜索、结果、键盘选择：Task 3、4、8。
- 单窗口复用和生命周期：Task 2、4、6。
- 计时从 HotkeyCoordinator 收到 Activated 开始：Task 3、6。
- 预热 10、采样 200、P50/P95/P99、P95 `<=120ms`：Task 1、6、8。
- 冷启动单独记录：Task 6、8、9。
- Native 30s、Performance 60s：Task 7、8。
- `%TEMP%\QuickPhrase-Smoke\` 和截图：Task 4、7、8。
- RegisterHotKey 边界：Task 4、9、10。
- 文档真实一致：Task 9。

### Placeholder scan

不存在未决占位项、伪造指标或延后决定。内存 Phrase 的完整构造字段已在 Task 4 固定，不创建新业务契约。

### Type consistency

统一使用：

```text
LauncherSmokeMode
LauncherSmokeOptions
LauncherSmokeParseResult
LauncherPerformanceSummary
LauncherLifecycleState
LauncherSmokeRunner
LauncherSmokeDiagnostics
LauncherActivationReceived
WaitForInteractiveAsync
```

实施中不得创建同义重复类型或擅自更名。
