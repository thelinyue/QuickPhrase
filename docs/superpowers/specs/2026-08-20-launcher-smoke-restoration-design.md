# QuickPhrase 独立 Launcher Smoke 恢复设计

日期：2026-08-20
状态：待审核，审核通过后实施
范围：`QuickPhrase.Desktop`、Launcher smoke watchdog、Phase 1/4/5/5.1 脚本、Desktop 测试、`phase4-validation.md`

## 1. 背景

当前脚本调用 `--smoke-native-launcher` 和 `--smoke-launcher-performance`，但当前及初始已提交的 Desktop 入口都没有参数处理器。命令会进入普通托盘生命周期并长期驻留，阻塞 Phase 4、Phase 5 和正式发布门禁。

本设计恢复两个独立、隔离、自动退出的真实 WPF smoke 模式。Smoke 使用真实 `HotkeyCoordinator` 和单一真实 `LauncherWindow`，但不连接 SQLite、真实系统快捷键、真实目标、Clipboard、UIA、企业微信、微信或其他外部应用。

## 2. 已确认的性能口径

`P95 <= 120ms` 只约束后台初始化完成后的 Launcher 热呼出：

```text
HotkeyCoordinator 收到 IShortcutService.Activated
→ 记录 receivedTimestamp
→ WPF Dispatcher
→ 复用 LauncherWindow
→ Show/Activate
→ Render Dispatcher 完成
→ Input Dispatcher 完成
→ 搜索框可见、启用并获得键盘焦点
```

计时起点位于 `HotkeyCoordinator.Service_Activated` 通过可用性检查后的第一条诊断记录，不包含内存测试服务触发事件之前的注入耗时。

冷启动单独记录为“进程创建到第一次 Launcher 可交互”，不参与 120ms 门槛。

## 3. 架构选择

采用方案 A：在现有 `QuickPhrase.Desktop` EXE 中增加仅由 smoke 参数进入的早期分支。不新增第四个正式 Desktop Project，保持冻结边界：

```text
QuickPhrase.Desktop
├── QuickPhrase.Core
└── QuickPhrase.Platform.Windows
```

## 4. 启动分流

`App.OnStartup` 在创建 `ApplicationController`、单实例、托盘、数据运行时和 Platform.Windows 服务之前解析：

```text
--smoke-native-launcher
--smoke-launcher-performance
--smoke-output <absolute-directory>
```

规则：

1. 普通启动行为不变。
2. 两种模式同时出现时输出 `LAUNCHER_SMOKE_ARGUMENT_INVALID` 并退出 2。
3. `--smoke-output` 只能与 smoke 模式一起使用。
4. 未指定目录时创建 `%TEMP%\QuickPhrase-Smoke\yyyyMMdd-HHmmss-fff-<PID>\`。
5. Smoke 完成或失败后在 `finally` 中释放资源并 `Application.Shutdown(exitCode)`，禁止进入托盘生命周期。

## 5. 组件与隔离边界

新增内部组件：

```text
LauncherSmokeMode
LauncherSmokeOptions
LauncherSmokeRunner
LauncherSmokeDiagnostics
LauncherPerformanceSummary
LauncherLifecycleState
```

内存替身作为 runner 私有类型：

```text
LauncherSmokeShortcutService
LauncherSmokeSearchService
LauncherSmokeHistoryRepository
```

Smoke 禁止创建或调用：

```text
QuickPhraseDataRuntime
QuickPhraseDataOptions.ForCurrentUser
WindowsShortcutService
WindowsTargetDetector
WindowsAdapterResolver
ForegroundApplicationWatcher
TextDeliveryFactory
DeliveryQueueCoordinator
Clipboard
AutomationElement / UI Automation
```

Smoke 不读取或写入 `%LOCALAPPDATA%\QuickPhrase`、真实 SQLite、真实日志、真实剪贴板、联系人、聊天内容或外部窗口标题。

固定内存话术至少包括：

```text
【Smoke】设备信息收集
【Smoke】售后跟进
【Smoke】多行内容
```

搜索词固定为 `Smoke`，结果顺序固定。搜索历史只存在内存。

## 6. 热键链路与计时点

Smoke 不调用 Win32 `RegisterHotKey`。链路为：

```text
LauncherSmokeShortcutService.RaiseActivated
→ IShortcutService.Activated
→ HotkeyCoordinator.Service_Activated
→ 记录 receivedTimestamp
→ dispatchToUi
→ LauncherHotkeyPressed
→ 复用 LauncherWindow.Open
```

内存服务活动组合固定为 Alt+Space。Runner 必须调用真实 `HotkeyCoordinator.ConfigureAsync()` 并通过 `SetPracticeMode(true)` 启用链路，禁止直接调用窗口打开回调冒充热键。

`HotkeyCoordinator` 增加内部诊断事件：

```csharp
internal event Action<long>? LauncherActivationReceived;
```

在收到 `Activated` 且确认未暂停、已启用后立即执行：

```csharp
var receivedTimestamp = Stopwatch.GetTimestamp();
LauncherActivationReceived?.Invoke(receivedTimestamp);
```

生产 `ApplicationController` 不订阅此事件。该事件只证明 HotkeyCoordinator 收到激活，不证明 Win32 注册成功，且不替代 Platform.Windows 的 RegisterHotKey 测试。

## 7. Launcher 生命周期复用

每个 smoke 进程只创建一个 `LauncherWindow`。核心链路验证、10 次预热和 200 次采样全部复用该实例。

新增：

```csharp
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
```

`LauncherWindow` 提供：

```csharp
internal LauncherLifecycleState LifecycleState { get; private set; }
```

状态路径：

```text
Created → Activating → Visible → Interactive → Hiding → Hidden
Hidden  → Activating → Visible → Interactive → Hiding → Hidden
Created/Hidden/Interactive → Disposed
```

每次采样必须满足：

1. 开始前为 `Hidden`。
2. 激活后到达 `Interactive`。
3. 隐藏后回到 `Hidden`。
4. 200 次循环 `ReferenceEquals(initialWindow, currentWindow)` 始终为真。
5. 状态异常立即停止并记录循环编号、期望状态和实际状态。

## 8. 可交互完成条件

新增：

```csharp
internal async Task WaitForInteractiveAsync(CancellationToken cancellationToken)
```

依次等待 `DispatcherPriority.Render` 和 `DispatcherPriority.Input`，然后同时验证：

```text
LauncherWindow.IsVisible == true
QueryBox.IsVisible == true
QueryBox.IsEnabled == true
QueryBox.IsKeyboardFocusWithin == true
```

满足后状态设为 `Interactive`；否则以 `LAUNCHER_SMOKE_INPUT_NOT_READY` 失败。

## 9. Native smoke

`--smoke-native-launcher` 完整执行：

1. 创建唯一 LauncherWindow。
2. 配置内存 Alt+Space。
3. 触发 `Activated`。
4. 验证 HotkeyCoordinator 与 Dispatcher 路由。
5. 验证窗口显示并可输入。
6. 向真实 QueryBox 输入 `Smoke`。
7. 验证 ResultsList 展示固定结果。
8. 向 QueryBox 发送真实 WPF Down KeyDown。
9. 验证选择变化。
10. 发送真实 WPF Enter KeyDown。
11. 通过 Practice `LauncherInvocationContext.SelectionHandler` 接收结果。
12. 验证未进入 Delivery、Clipboard 或发送链。
13. 隐藏并验证 `Hidden`。
14. 输出 `LAUNCHER_SMOKE_PASS`，退出 0。

## 10. Performance smoke

### 10.1 冷启动

从 `Process.StartTime` 到第一次核心链路进入 `Interactive`，输出：

```text
LAUNCHER_COLD_START interactive={coldStart.TotalMilliseconds:F3}ms gate=none
```

只记录，不设门槛。

### 10.2 预热和采样

预热 10 次，不计入统计。正式采样固定 200 次。每次：

```text
Hidden
→ HotkeyCoordinator receivedTimestamp
→ Interactive
→ 记录 elapsed
→ Hiding
→ Hidden
```

终点通过 `Stopwatch.GetTimestamp()` 取得，耗时使用 `Stopwatch.GetElapsedTime(start, end)`。

每个样本写入 CSV：

```text
sample,elapsed_ms,start_state,end_state,window_instance
1,8.417,Hidden,Interactive,1
```

### 10.3 统计与门槛

使用 nearest-rank：

```text
rank = Ceiling(percentile * count)
index = Clamp(rank - 1, 0, count - 1)
```

输出：

```text
LAUNCHER_PERF count=200 warmup=10 p50={p50:F3}ms p95={p95:F3}ms p99={p99:F3}ms threshold=120ms
```

P95 `<= 120ms` 通过；超过时输出 `LAUNCHER_PERF_THRESHOLD_EXCEEDED` 并非零退出。

## 11. 诊断目录

每次运行建立：

```text
%TEMP%\QuickPhrase-Smoke\yyyyMMdd-HHmmss-fff-<PID>\
```

包含：

```text
result.json
stdout.log
stderr.log
exception.txt              # 失败时
performance-samples.csv    # performance 模式
launcher-failure.png       # 窗口已创建且失败时尽力生成
watchdog-timeout.txt       # watchdog 超时时
```

`result.json` 记录模式、UTC 时间、PID、退出码、阶段、错误码、冷启动、样本数、P50/P95/P99、门槛、窗口实例数和最终状态。

截图只用 WPF `RenderTargetBitmap` 捕获 LauncherWindow 自身客户区；禁止截取桌面或外部窗口。截图失败不得覆盖原异常。诊断只包含固定 smoke 数据。

## 12. Watchdog

新增 `scripts/invoke-launcher-smoke.ps1`，参数：

```powershell
-Mode Native|Performance
-Configuration Debug|Release
```

超时：

```text
Native      30s
Performance 60s
```

脚本直接启动已构建的 `QuickPhrase.exe`，使用 `Start-Process -WindowStyle Hidden -PassThru`，重定向 stdout/stderr，等待对应超时。超时时仅对本次返回的明确 PID 执行 `Stop-Process -Id $process.Id -Force`，写入 `watchdog-timeout.txt` 并返回稳定非零退出码。不得按进程名批量结束，也不得终止用户原有 QuickPhrase 实例。

## 13. Phase 脚本

以下脚本不再直接调用 `dotnet run -- --smoke-*`：

```text
scripts/verify-phase1.ps1
scripts/verify-phase4.ps1
scripts/verify-phase5.ps1
scripts/verify-phase51.ps1
```

统一通过 watchdog。Phase 1 可选 Native 使用 Debug；Phase 4/5/5.1 和正式发布使用 Release。

## 14. RegisterHotKey 边界

Smoke 只验证：

```text
HotkeyCoordinator → WPF Dispatcher → LauncherWindow → 搜索和键盘选择
```

不验证 Win32 RegisterHotKey、原生消息窗口、全局冲突、权限差异或真实前台目标。以上继续由 Platform.Windows 测试和 Windows 人工矩阵承担。日志和文档不得把合成激活描述成真实全局快捷键注册通过。

## 15. 测试策略

自动测试覆盖：

1. 参数解析和冲突。
2. HotkeyCoordinator receivedTimestamp 位于注入后、UI dispatch 前。
3. `LauncherLifecycleState` 和合法重复路径。
4. 单一 LauncherWindow 复用。
5. 搜索、结果、Down/Enter 选择。
6. nearest-rank P50/P95/P99。
7. P95 等于 120ms 通过，大于 120ms 失败。
8. 诊断目录、结果、CSV 和失败截图。
9. Phase 脚本全部经过 watchdog。
10. Runner 不引用禁止依赖。
11. App 在创建 ApplicationController 前分流。
12. 两个真实 smoke 命令在时限内退出且无残留 PID。

## 16. 文档更新

更新 `docs/phase4-validation.md`，明确：

- 热呼出定义和计时点。
- 预热 10 次、采样 200 次。
- P50/P95/P99 与 nearest-rank。
- P95 `<=120ms`。
- 冷启动只记录。
- 单窗口复用。
- 内存数据和合成激活。
- 不替代 Platform.Windows 的 RegisterHotKey 测试。
- 本次真实命令、实际指标和诊断目录。

不得预写或伪造性能数字。

## 17. 稳定错误码

```text
LAUNCHER_SMOKE_ARGUMENT_INVALID
LAUNCHER_SMOKE_INITIALIZATION_FAILED
LAUNCHER_SMOKE_HOTKEY_NOT_RECEIVED
LAUNCHER_SMOKE_WINDOW_NOT_VISIBLE
LAUNCHER_SMOKE_INPUT_NOT_READY
LAUNCHER_SMOKE_SEARCH_FAILED
LAUNCHER_SMOKE_KEYBOARD_SELECTION_FAILED
LAUNCHER_SMOKE_LIFECYCLE_INVALID
LAUNCHER_PERF_THRESHOLD_EXCEEDED
LAUNCHER_SMOKE_TIMEOUT
LAUNCHER_SMOKE_UNEXPECTED
```

错误输出使用中文阶段说明并包含诊断目录。

## 18. 修改边界

允许新增或修改：

```text
desktop/QuickPhrase.Desktop/App.xaml.cs
desktop/QuickPhrase.Desktop/HotkeyCoordinator.cs
desktop/QuickPhrase.Desktop/LauncherWindow.xaml.cs
desktop/QuickPhrase.Desktop/LauncherSmokeRunner.cs
scripts/invoke-launcher-smoke.ps1
scripts/verify-phase1.ps1
scripts/verify-phase4.ps1
scripts/verify-phase5.ps1
scripts/verify-phase51.ps1
tests/QuickPhrase.Desktop.Tests/LauncherSmokeTests.cs
tests/QuickPhrase.Desktop.Tests/LauncherSmokeScriptContractTests.cs
docs/phase4-validation.md
```

不得修改或暂存当前无关工作区文件：

```text
desktop/QuickPhrase.Desktop/DesignSystem/Tokens/Thickness.xaml
desktop/QuickPhrase.Desktop/Views/SettingsView.xaml
tests/QuickPhrase.Desktop.Tests/SettingsViewContractTests.cs
```

不得修改 Web 原型、Sites、安装器、数据库 schema 或投递安全链。

## 19. 成功标准

1. 两个参数由生产 EXE 明确处理。
2. Smoke 不访问真实数据或外部应用。
3. Native 30 秒内退出 0。
4. Performance 60 秒内退出 0。
5. 只创建一个 LauncherWindow，10 次预热和 200 次采样状态稳定。
6. 核心交互全部通过。
7. 输出冷启动、P50/P95/P99 和 200 条样本。
8. P95 `<=120ms`。
9. 成功或失败后无 smoke 残留进程。
10. 失败诊断包含结果、异常、样本和可用的 Launcher 截图。
11. Phase 5 门禁能继续执行到 `PHASE5_VERIFY_PASS`。
12. `phase4-validation.md` 与真实代码和真实结果一致。
