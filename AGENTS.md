# Prototype Instructions

仓库中的 `src/`、`prototype/`、Sites 构建脚本和相关测试属于独立的 Web 原型/展示链路。原型链路可以按自身说明运行，但**不属于 QuickPhrase 正式产品架构，也不作为正式 WPF 界面、布局或交互的参考**。

- 保留 `.openai/hosting.json`、`worker/index.js`、`scripts/prepare-sites-build.mjs` 和 `tests/sites-worker.test.mjs`，不得为了纯 WPF 改造而删除或破坏它们。
- 任何正式产品 UI、交互、尺寸和行为判断，必须以 `desktop/QuickPhrase.Desktop` 当前实际 WPF XAML、ViewModel 和代码为准。
- 不要把原型中的 Web、React、WebView、假桌面外壳或调试控件带入生产项目。

## Architecture v1.1 — FROZEN

QuickPhrase 正式技术路线固定为：`.NET 10 LTS + Pure WPF + Win32/UIA + SQLite + Core 内存搜索`。

正式产品是单进程、进程内调用的 WPF 桌面应用，不使用 WebView2、React 管理页、ManagementIpc、ManagementBridge、IPC DTO、协议版本、requestId 或网页桥接层。

### Architecture Constitution

1. Core 不知道 Windows。
2. Desktop 的 View、ViewModel 和 Command 不直接依赖 Platform.Windows 具体实现。
3. Launcher 不经过任何 Web 或 IPC 层。
4. 搜索只访问 Core 内存索引，不查询 SQLite。
5. UI Automation 不运行在 WPF UI Thread。
6. 显式发送默认不可信；无用户授权的自动发送禁止。
7. Target 必须在动作执行前重新验证。
8. 第三方应用能力必须通过运行时能力检测验证，不依赖客户端版本号准入。
9. 降级失败不允许演变成误发送。
10. 原型文件与生产 WPF 项目物理、依赖和验收边界分离。

最高安全原则：宁可不能发送，也不能发错窗口、发错内容或重复发送。

### Frozen Project Boundary

只保留三个正式桌面 Project：

```text
QuickPhrase.Desktop
├── QuickPhrase.Core
└── QuickPhrase.Platform.Windows
        └── QuickPhrase.Core
```

依赖方向永久固定：

```text
Desktop ───────→ Core
    │
    └──────────→ Platform.Windows ───→ Core
```

禁止：

```text
Core → Desktop
Core → Platform.Windows
Platform.Windows → Desktop
```

Core 禁止引用 WPF、WebView2、Win32、UI Automation、SQLite 和 PinyinM.NET。Platform.Windows 承载 UIA Worker、Clipboard Transaction、SQLite Write Queue、Hotkeys、Target Detection 和 Adapter；Desktop 承载 WPF 生命周期、单实例、托盘、Launcher、Views、ViewModels、Commands 和 Composition Root。

### In-process Call Chain

```text
WPF View
   ↓ Binding
ViewModel / ICommand
   ↓
Core Application Service / Contract
   ↓
Platform.Windows Implementation
```

Desktop 只有 `App.xaml.cs`、Bootstrap/Composition、必要的 Shell 编排和 Native Launcher 编排可以引用 Platform.Windows 具体类型。Views、ViewModels 和 Commands 依赖 Core 接口或 Desktop 自身抽象，不把 `WindowsClipboardService`、`WindowsTargetIdentity` 等平台类型注入 UI。

### Core Target Boundary

Core 只保存平台无关的 `DeliveryTarget`：`ApplicationId`、`ApplicationKind`、`AdapterId`、`DisplayName`、`RuntimeKey` 和 `CapturedAtUtc`。

HWND、PID、WindowThreadId、ProcessStartTimeUtc、ProcessName、AutomationElement 和 FocusElementIdentity 只存在于 Platform.Windows 的 `WindowsTargetIdentity` / `WindowsTargetContext`。两者通过 `RuntimeKey` 关联，Core 不泄漏 Win32 或 UIA 类型。

### Delivery Safety

投递固定经过：

```text
CaptureTarget → ValidateTarget → ResolveAdapter → DetectCapabilities
→ Insert → VerifyInsert → RevalidateBeforeSend
→ OptionalSend → VerifySend
```

`DeliveryResult` 使用正交字段表达 `Status`、`Effect`、`Stage`、`Confidence`、`ErrorCode`、`Message`、`Retryable` 和 `TraceId`。`SendTriggered` 只表示发送快捷操作已完整执行；仅能确认目标应用最终发送结果时才使用 `Sent`。插入或发送已经开始但结果不确定时返回 `Unknown + Unknown`，禁止自动重试。

当前企业微信兼容目标是当前主流版本，不设置版本门禁。客户端版本号仅进入脱敏诊断 Trace，不参与启动、插入、发送、排队或降级判断：

- `InsertText = Verified`
- `VerifyInsert = Verified`：只验证粘贴动作完整执行且目标、前台窗口、输入焦点/Caret 指纹保持稳定，不读取正文
- `SendText = Verified`：用户在 Launcher 中以 `Ctrl+Enter` 明确触发；该组合键只表达通用 `InsertAndSend` 意图，企业微信 Adapter 在发送前重校验后按当前目标协议注入一次 `Enter`
- `VerifySend = Unsupported`：无法确认目标应用最终发送结果，完整注入返回 `SendTriggered`，不得声称 `Sent`
- 固定使用受保护 Clipboard + `Ctrl+V` 插入
- 不开放 Unicode 直输、后台目标投递、无用户授权自动发送或失败自动重试

### Persistence and Search

SQLite 是事实源，由 Platform.Windows 单写者、事务 migration、外键、busy timeout 和 WAL 管理。只有 DB Commit 成功后才更新 Core 内存搜索索引；搜索过程不访问 SQLite。

### Logging and Comments

关键类补充中文设计注释；用户可见错误和日志使用清晰中文，并包含 TraceId、阶段、结果码和耗时。日志禁止记录话术正文、剪贴板、输入框文字、聊天内容、联系人和客户资料。

### Current WPF UI Baseline

当前实际 WPF 界面是唯一 UI 依据，不参考仓库中的旧原型图。现有界面以以下真实代码为准：

```text
desktop/QuickPhrase.Desktop/MainWindow.xaml
├── TitleBar
└── ContentRegion
    ├── Views/LibraryView.xaml
    ├── Views/EditorView.xaml
    └── Views/SettingsView.xaml

desktop/QuickPhrase.Desktop/LauncherWindow.xaml
```

MainWindow 当前为 `1200×760`，最小 `900×560`；话术库、编辑器、设置、分类/移动/导航确认对话框和 Launcher 的布局以现有 XAML 和实际行为为准。不要基于 Floating Workspace、演示壁纸、假 Windows 桌面、任务栏或原型调试控件进行重构。

### Release Boundary

- `QuickPhrase.Desktop.csproj` 不得引用 WebView2、React、JavaScript runtime、wwwroot 或网页资源。
- `dotnet build QuickPhrase.sln` 不得触发 npm/node。
- 发布目录不得包含 React bundle、HTML/JS/CSS 网页资源或 WebView2 Runtime 安装器。
- `src/` 等原型链路可以保留，但不得被三个正式桌面 Project 引用。
- 安装器保持当前用户、纯 WPF、自包含安装方式；数据、备份和日志在卸载后保留。

Phase 1–4 已完成，Phase 5 企业微信安全插入与通用显式发送代码路径、Phase 5.1 连续投递/启动性能基础设施已完成。Phase 6 Windows 11 发布基础设施状态为 `PHASE6_INFRA_PASS`；企业微信人工矩阵和 Windows 11 安装矩阵通过后才写入 `PHASE6_VERIFY_PASS_WIN11`。插件、AI、团队、文件/图片话术、浏览器扩展、跨平台、后台发送和自动更新进入 V2 Backlog，不修改 Architecture v1.1。
