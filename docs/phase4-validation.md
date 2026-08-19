# QuickPhrase Phase 4 验证记录

状态：`PHASE4_VERIFY_PASS`

本阶段实现 Native Launcher、会话内全局快捷键协调、首次引导、托盘菜单、正式管理界面 IPC 接入，以及 WebView2 故障隔离。文本插入、发送、剪贴板和目标识别仍是明确标注的模拟行为，真实 Windows 投递留给 Phase 5。

## 验证环境

- 主要平台：Windows 11 x64
- SDK：`.NET 10`（由 `global.json` 固定）
- WebView2：稳定版 Evergreen Runtime，Desktop 包版本 `1.0.4078.44`
- Node/npm：使用工作区现有 React/Vite 工具链

## 已执行命令

| 命令 | 结果 |
| --- | --- |
| `dotnet build QuickPhrase.sln --no-restore` | 通过，零警告 |
| `dotnet build QuickPhrase.sln -c Release --no-restore` | 通过，零警告 |
| `dotnet test QuickPhrase.sln --no-build` | 通过，35/35 |
| `dotnet test QuickPhrase.sln -c Release --no-build` | 通过 |
| `npm run build` | 通过，生成 `dist/client`、`dist/server` 和 Sites hosting 产物 |
| `npm run test:sites` | 通过 |
| `node scripts/qa.mjs` | 通过，中文搜索、多结果、零结果、降级、首次使用、编辑器、设置和窄屏检查通过 |
| `dotnet run --no-build -c Release --project desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -- --smoke-native-launcher` | 通过，退出码 0 |
| `dotnet run --no-build -c Release --project desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -- --smoke-launcher-performance` | 通过 |
| `dotnet run --no-build -c Release --project desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -- --smoke-webview-lifecycle` | 通过，退出码 0 |

## 性能证据

Launcher Release 热呼出 smoke：预热 10 次后执行 200 次，计时从呼出请求到首个可见渲染帧。

```text
LAUNCHER_PERF count=200 p50=8.417ms p95=13.287ms p99=17.137ms
```

P95 低于 120ms 门槛。性能报告写入 `%TEMP%\QuickPhrase-launcher-perf.txt`。此前独立 Release smoke 也通过（P95 12.978ms）。

## 关键验收结论

- Native Launcher 为纯 WPF 窗口，不创建或依赖 WebView2；管理窗口关闭后仍可呼出。
- 搜索直接调用 Core 内存 `ISearchService`，不在 Launcher 查询 SQLite。
- `Alt + Space`、话术快捷键、暂停/恢复和冲突状态由 Desktop/Platform.Windows 协调；Phase 4 不注册真实目标投递能力。
- `Enter` 和 `Ctrl + Enter` 的反馈明确为模拟操作，绝不宣称已经写入或发送到目标应用。
- WebView2 的 IPC 使用版本化 camelCase 协议；资源缺失、页面失败、Runtime 不可用和进程异常都显示原生中文故障面板，不注销 Launcher 或托盘能力。
- 管理窗口关闭时解除 WebView2 事件订阅并释放 Controller；生命周期 smoke 以 `BrowserProcessExited` 为主要退出信号。
- 首次引导由原生 WPF 承载，“试一下”不依赖 WebView2。

## 数据安全说明

本轮没有修改已执行的 `001_initial.sql` 迁移内容。若本地数据库已应用迁移 1，启动时会继续使用原校验和；不会删除、重建或覆盖用户数据库。`HasCompletedOnboarding` 作为现有设置 JSON 的可选字段向后兼容旧数据。

## 下一阶段

`Phase 5 — Windows Integration 与安全投递`
