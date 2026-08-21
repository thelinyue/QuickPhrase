# QuickPhrase Architecture v1.1

状态：`FROZEN`  
目标运行时：`.NET 10 LTS`  
正式 UI：当前实际 WPF 界面  
主要验证平台：Windows 11 x64  
兼容测试平台：Windows 10 22H2 x64（V1 未承诺支持）

> 本文只描述正式 QuickPhrase Windows 产品。仓库中的 Web/React 原型和 Sites 构建链是独立资产，不是生产架构，也不是当前 UI 的参考。

## 1. Architecture Constitution

1. Core 不知道 Windows。
2. Desktop 的 View、ViewModel 和 Command 不直接依赖 Platform.Windows 具体实现。
3. Launcher 不经过 Web、IPC 或序列化桥接层。
4. 搜索不查询 SQLite，只查询 Core 内存索引。
5. UI Automation 不运行在 WPF UI Thread。
6. 显式发送默认不可信；无用户授权的自动发送禁止。
7. Target 必须在动作执行前重新验证。
8. 第三方应用能力必须通过运行时能力检测验证，不依赖客户端版本号准入。
9. 降级失败不允许演变成误发送。
10. 原型与生产代码必须保持物理和依赖隔离。

最高安全原则：宁可不能发送，也不能发错窗口、发错内容或重复发送。

## 2. 项目边界与依赖

正式项目只有三个：

```text
QuickPhrase.Desktop
├── QuickPhrase.Core
└── QuickPhrase.Platform.Windows
        └── QuickPhrase.Core
```

依赖方向固定为：

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

### QuickPhrase.Core

```text
Domain
Application
Search
Contracts
```

负责领域模型、应用服务、搜索排序、投递契约和结果模型。禁止引用 WPF、WebView2、Win32、UI Automation、SQLite 和 PinyinM.NET。

### QuickPhrase.Platform.Windows

```text
Automation
Clipboard
Delivery
Hotkeys
Integrations
Persistence/Sqlite
Storage
Logging
Native
```

负责 Windows 目标捕获与重校验、UIA MTA Worker、Clipboard Transaction、Win32 输入、SQLite 单写者、migration、适配器和平台日志。

### QuickPhrase.Desktop

```text
App.xaml / App.xaml.cs
Views
ViewModels
Controls / Themes / Resources
Commands
Navigation
Tray
Launcher
Bootstrap / Composition Root
```

负责 WPF 生命周期、单实例、托盘、窗口、绑定、命令和依赖注入组合。只有 `App.xaml.cs`、Bootstrap/Composition 和必要的 Shell 编排可以引用 Platform.Windows 具体类型。Views、ViewModels 和 Commands 依赖 Core 接口或 Desktop 自身抽象。

## 3. 进程内调用链

```text
WPF View
   ↓ Binding
ViewModel / ICommand
   ↓
Core Application Service / Contract
   ↓
Platform.Windows Implementation
```

所有正式管理功能均为单进程、进程内调用。不存在 ManagementIpc、ManagementRequest、ManagementResponse、ManagementBridge、协议版本、requestId、JSON 序列化、Bridge timeout 或网页消息转换。

## 4. 当前实际 WPF UI 基线

当前实际界面是唯一 UI 参考，不使用旧原型图或原型外壳进行布局决策。

```text
desktop/QuickPhrase.Desktop/MainWindow.xaml
├── TitleBar
└── ContentRegion
    ├── Views/LibraryView.xaml
    ├── Views/EditorView.xaml
    └── Views/SettingsView.xaml

desktop/QuickPhrase.Desktop/LauncherWindow.xaml
```

当前 MainWindow 为 `1200×760`，最小 `900×560`。话术库、编辑器、设置、分类/移动/导航确认对话框和 Launcher 的真实布局、文字、交互及状态，以现有 XAML、ViewModel 和代码为准。不要引入 Floating Workspace、演示壁纸、假 Windows 桌面、任务栏、Web 管理页或原型调试控件。

## 5. Core Target Boundary

Core 只公开平台无关的 `DeliveryTarget`：

```text
ApplicationId
ApplicationKind
AdapterId
DisplayName
RuntimeKey
CapturedAtUtc
```

HWND、PID、WindowThreadId、ProcessStartTimeUtc、ProcessName、AutomationElement 和 FocusElementIdentity 只存在于 Platform.Windows 的 `WindowsTargetIdentity` / `WindowsTargetContext`。Core 不得出现这些平台类型；Platform.Windows 通过 `RuntimeKey` 将运行时上下文映射回 Core 目标。

## 6. Delivery State Machine

```text
CaptureTarget
→ ValidateTarget
→ ResolveAdapter
→ DetectCapabilities
→ Insert
→ VerifyInsert
→ RevalidateBeforeSend
→ OptionalSend
→ VerifySend
→ Completed / Fallback
```

`DeliveryResult` 使用正交字段表达：

```text
Status:     Success | Failed | Cancelled | Unsupported | Unknown
Effect:     None | Inserted | SendTriggered | Sent | Unknown
Stage:      NotStarted | ValidateTarget | Insert | VerifyInsert | Send ...
Confidence:  Confirmed | Probable | Unknown
ErrorCode
Message
Retryable
TraceId
```

插入或发送已经开始但结果不确定时返回 `Unknown + Unknown`，不自动重试、不重复粘贴、不重复发送。

V1 安全门禁：

- Target 在捕获后、插入前、发送前重新验证。
- `VerifyInsert` 不确定时绝不进入 Send。
- V1 不允许后台目标发送或无用户授权自动发送。
- `InsertAndSend` 在发送能力不受支持时直接返回 `UnsupportedSend`，不插入、不发送、不降级。
- `InsertOnly` 可按安全策略降级为复制；连续投递队列只接受 `InsertOnly`。
- 失败消息必须对用户友好，并携带可追溯 TraceId。

## 7. Windows 线程模型

- WPF Dispatcher 固定为 STA。
- UIA 使用长生命周期、无窗口、COM MTA Worker。
- UIA 查找、控制模式、事件订阅和解绑只能通过 Worker 的有界队列执行。
- UIA Worker 不得阻塞 WPF、Launcher 或全局快捷键消息循环。
- Clipboard Transaction 在 WPF/Windows 约束下执行短时操作，并使用序列号判断恢复是否安全。

## 8. Clipboard Transaction

1. 保存原 DataObject 和初始剪贴板序列号。
2. 写入话术，记录 QuickPhrase 产生的序列号。
3. 激活目标并完成粘贴。
4. 粘贴后读取序列号。
5. 只有序列号未被其他操作改变时恢复旧内容。
6. 用户产生新复制内容时跳过恢复并记录脱敏 Trace。

Clipboard busy 使用有限次数的短退避重试；失败返回 `CLIPBOARD_FAILED`，不继续执行可能误投递的动作。

## 9. SQLite 与 Search Index

```text
Core Application Service
→ Platform.Windows Bounded Write Queue
→ Single SQLite Writer
→ DB Commit
→ Publish Domain Change
→ Core In-memory Search Index
```

固定启用 `foreign_keys=ON`、`busy_timeout=5000`、WAL 和 `synchronous=NORMAL`。migration 必须事务化，升级前创建轻量备份。SQLite 是事实源，内存 Search Index 是运行时加速器；搜索过程不访问 SQLite。

索引更新失败时保留最后有效快照，标记 `IndexDirty` 并从 SQLite 重建。禁止在数据库 Commit 前修改索引。

## 10. Adapter Profile

能力状态为 `Verified`、`Unverified`、`Unsupported`。Profile 描述 Adapter 身份、实现版本和运行时能力，不包含客户端版本准入范围：

```text
AdapterId
ProcessName
ProfileVersion
InsertTextStatus
VerifyInsertStatus
SendTextStatus
VerifySendStatus
FallbackMode
VerifiedAt
DetectedProductVersion (nullable, diagnostics only)
```

企业微信兼容目标为当前主流版本。任意版本号、缺失版本号或版本读取失败都不得阻止运行时能力检测；版本号只写入不包含用户内容的诊断 Trace。

| 能力 | 状态 | 语义 |
|---|---|---|
| InsertText | Verified | 受保护 Clipboard + `Ctrl+V` |
| VerifyInsert | Verified | 验证动作完整且目标、前台窗口、输入焦点/Caret 指纹稳定，不读取正文 |
| SendText | Verified | Launcher 以 `Ctrl+Enter` 明确触发；发送前重校验后由企业微信 Adapter 注入一次 `Enter` |
| VerifySend | Unsupported | 不确认目标应用最终发送结果，返回 `SendTriggered` 而非 `Sent` |

`Ctrl+Enter` 是 Launcher 的通用 `InsertAndSend` 意图，不绑定企业微信；各 Adapter 独立实现具体发送协议。企业微信不开放 Unicode 直输、后台投递、无用户授权自动发送或发送失败自动重试。

## 11. Observability

`DeliveryTrace` 只记录脱敏元数据：

```text
TraceId
Stage
AdapterId
ProfileVersion
ApplicationId
ProductVersion
ResultCode
DurationMs
TimestampUtc
```

禁止记录话术标题和正文、剪贴板、输入框文字、聊天内容、联系人名称、客户资料和 UIA 读取文本。关键类补充中文设计注释；日志和用户可见错误使用清晰中文。

## 12. Error Codes

```text
TARGET_CHANGED
TARGET_VALIDATION_FAILED
CAPABILITY_UNVERIFIED
INSERT_FAILED
INSERT_VERIFICATION_FAILED
INSERT_VERIFICATION_INCONCLUSIVE
SEND_FAILED
SEND_VERIFICATION_FAILED
SEND_VERIFICATION_INCONCLUSIVE
DELIVERY_CANCELLED
DELIVERY_TIMEOUT
CLIPBOARD_FAILED
DATABASE_BUSY
MIGRATION_FAILED
SEARCH_INDEX_DIRTY
```

正式产品不再定义或处理 IPC、WebView、Bridge、协议版本、requestId 或网页生命周期错误码。

## 13. 性能与发布

- `.NET 10 win-x64` self-contained、非裁剪构建。
- Launcher 热呼出 P95 ≤ 120ms。
- 一万条话术搜索 P95 ≤ 50ms。
- 稳定空闲五分钟平均 CPU ≤ 0.1%。
- 稳定空闲五分钟内不产生周期性持久化写入。
- 发布目录不得包含 HTML、JavaScript、CSS Web bundle 或 WebView2 Runtime 安装器。
- 使用按用户安装的纯 WPF Inno Setup EXE；数据、备份和日志卸载后保留。

## 14. 原型隔离与验收

仓库中的 `src/`、`prototype/`、`package.json`、Vite、worker 和 Sites 测试可以保留，用于独立的设计/展示验证；它们不得被正式桌面 Project 引用，也不能作为生产 UI 参考。

边界回归测试必须验证：

- 三个正式 Project 的引用方向。
- Core 无平台泄漏。
- Desktop 无 Web/Bridge/IPC 符号。
- `QuickPhrase.Desktop.csproj` 无 WebView2/React 包。
- 最终 `artifacts/release/0.0.1/publish` 无网页资源。

## 15. Frozen Status

- Phase 1–4：`COMPLETED`
- Phase 5：`VERIFY PASS / WECOM INSERT PROFILE FROZEN`
- Phase 5.1：`INFRASTRUCTURE PASS / QUEUE + STARTUP OPTIMIZATION COMPLETE`
- Phase 6：`MANUAL MATRIX PASS / SIGNING PENDING`

插件、AI、团队、文件/图片话术、浏览器扩展、跨平台、后台发送和自动更新进入 V2 Backlog。

**QuickPhrase Architecture v1.1 — FROZEN**
