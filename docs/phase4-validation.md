# QuickPhrase Phase 4 验证记录

状态：`PHASE4_VERIFY_PASS`

验证日期：2026-08-20

当前 Phase 4 正式链路为 Pure WPF Native Launcher。Launcher smoke 已恢复为独立、隔离、自动退出的 EXE 诊断模式，不使用旧 WebView2、React、IPC 或网页桥接链路。

## 验证环境

- 平台：Windows x64 桌面会话
- SDK：`.NET SDK 10.0.400`
- 配置：`Release`
- Smoke 数据：固定内存话术与内存搜索历史
- 快捷键输入：内存 `IShortcutService` 合成 Alt+Space 激活
- 窗口：单一真实 `LauncherWindow`，全程复用
- 诊断根目录：`%TEMP%\QuickPhrase-Smoke\`

## 实际执行命令

```powershell
dotnet build QuickPhrase.sln -c Release --no-restore --verbosity minimal
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-launcher-smoke.ps1 -Mode Native -Configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-launcher-smoke.ps1 -Mode Performance -Configuration Release
```

结果：

- Release 构建通过，0 警告、0 错误。
- Native smoke 在 30 秒 watchdog 内完成，退出码 0。
- Performance smoke 在 60 秒 watchdog 内完成，退出码 0。
- 两次运行结束后均未发现带 smoke 参数的 QuickPhrase 残留进程。

## Native Launcher 核心链路

Native smoke 使用真实 WPF 控件和事件路由验证：

```text
内存 Alt+Space Activated
→ HotkeyCoordinator 收到 IShortcutService.Activated
→ WPF Dispatcher
→ LauncherWindow 显示
→ Render/Input Dispatcher 完成
→ QueryBox 获得键盘焦点
→ 输入固定搜索词 Smoke
→ ResultsList 展示固定内存结果
→ WPF Down KeyDown 移动选择
→ WPF Enter KeyDown 选择话术
→ Practice SelectionHandler 接收结果
→ LauncherWindow 隐藏并回到 Hidden
```

实际输出：

```text
LAUNCHER_COLD_START interactive=592.614ms gate=none
LAUNCHER_SMOKE_PASS
```

诊断目录：

```text
C:\Users\林樾\AppData\Local\Temp\QuickPhrase-Smoke\20260820-222316-881-17220
```

## Launcher 热呼出性能定义

P95 `<= 120ms` 只约束应用已经完成初始化并驻留后台后的 Launcher 热呼出。

计时起点：

```text
HotkeyCoordinator 收到 IShortcutService.Activated，
完成可用性检查后立即记录 Stopwatch 时间戳。
```

计时不包含内存测试服务调用 `RaiseActivated()` 之前的测试注入层耗时。

计时终点必须同时满足：

```text
LauncherWindow.IsVisible == true
Render Dispatcher 已完成
Input Dispatcher 已完成
QueryBox.IsVisible == true
QueryBox.IsEnabled == true
QueryBox.IsKeyboardFocusWithin == true
LauncherLifecycleState == Interactive
```

采样方式：

- 测试前完成一次完整核心链路初始化。
- 预热 10 次，不进入统计。
- 正式采样 200 次。
- 200 次循环复用同一个 LauncherWindow。
- 每次循环必须稳定经过 `Hidden → Activating → Visible → Interactive → Hiding → Hidden`。
- 百分位使用 nearest-rank：`rank = Ceiling(percentile × count)`。

## 实际性能结果

```text
LAUNCHER_COLD_START interactive=595.787ms gate=none
LAUNCHER_PERF count=200 warmup=10 p50=43.484ms p95=67.882ms p99=76.268ms threshold=120ms
LAUNCHER_SMOKE_PASS
```

结论：

```text
P95 = 67.882ms <= 120ms
```

发布质量门槛通过。

冷启动耗时只作为当前环境诊断记录，**不作为发布门槛**，也不与 Launcher 热呼出 P95 混用。

Performance 诊断目录：

```text
C:\Users\林樾\AppData\Local\Temp\QuickPhrase-Smoke\20260820-222302-630-18440
```

`performance-samples.csv` 包含 200 条正式样本；`result.json` 记录：

```text
WarmupCount = 10
SampleCount = 200
WindowInstanceCount = 1
FinalLifecycleState = Disposed
ExitCode = 0
```

## 隔离与安全边界

Smoke 不创建或访问：

- 用户 SQLite 数据库或 `%LOCALAPPDATA%\QuickPhrase`。
- 真实企业微信、微信或其他外部应用。
- 真实前台窗口和投递目标。
- 真实剪贴板、UI Automation 或文字发送链路。
- Win32 全局快捷键注册。
- 托盘、单实例服务或正式投递队列。

固定内存话术只存在于 smoke 进程中。Enter 使用 Practice `LauncherInvocationContext`，只把固定测试话术回传给 runner，不进入真实投递。

Smoke **不替代 Platform.Windows 的 RegisterHotKey 测试**。它只验证：

```text
HotkeyCoordinator + WPF Dispatcher + LauncherWindow
```

Win32 `RegisterHotKey`、原生消息窗口、系统快捷键冲突、权限级别和真实 Windows 全局激活继续由 Platform.Windows 自动测试及 Windows 人工矩阵承担。

## Watchdog 与失败诊断

统一入口：

```text
scripts/invoke-launcher-smoke.ps1
```

超时：

- Native：30 秒。
- Performance：60 秒。

watchdog 直接启动已构建的 QuickPhrase.exe，只持有本次明确 PID；超时时只终止该 PID，不按进程名结束用户正在运行的 QuickPhrase。

每次运行在 `%TEMP%\QuickPhrase-Smoke\` 下生成独立目录，包含：

```text
result.json
stdout.log
stderr.log
performance-samples.csv（Performance）
exception.txt（失败时）
launcher-failure.png（窗口已创建且失败时）
watchdog-timeout.txt（超时时）
```

失败截图只通过 WPF `RenderTargetBitmap` 捕获 LauncherWindow 自身客户区，不截取桌面或外部应用。

## 数据安全说明

本轮 Launcher smoke 不修改数据库 schema、迁移、用户设置、用户话术、搜索历史或投递安全链。所有诊断内容只包含固定 Smoke 数据，不记录用户话术正文、联系人、聊天内容、剪贴板或真实窗口标题。

## 后续边界

Phase 5 继续负责 Windows 目标识别、受保护 Clipboard 插入、重新验证、显式发送和企业微信人工矩阵。Launcher smoke 通过不能替代企业微信真实会话验收，也不能单独写入 `PHASE6_VERIFY_PASS_WIN11`。
