# Prototype Instructions

仓库中的 `src/`、`prototype/`、Sites 构建脚本和相关测试属于独立的 Web 原型/展示链路。原型链路可以按自身说明运行，但**不属于 QuickPhrase 正式产品架构，也不作为正式 WPF 界面、布局或交互的参考**。

- 保留 `.openai/hosting.json`、`worker/index.js`、`scripts/prepare-sites-build.mjs` 和 `tests/sites-worker.test.mjs`，不得为了纯 WPF 改造而删除或破坏它们。
- 任何正式产品 UI、交互、尺寸和行为判断，必须以 `desktop/QuickPhrase.Desktop` 当前实际 WPF XAML、ViewModel 和代码为准。
- 不要把原型中的 Web、React、WebView、假桌面外壳或调试控件带入生产项目。

## QuickPhrase 首发图文话术与分批发送架构基线

QuickPhrase 正式技术路线固定为：`.NET 10 LTS + Pure WPF + Win32/UIA + SQLite + Core 内存搜索`。

正式产品是单进程、进程内调用的 WPF 桌面应用，不使用 WebView2、React 管理页、ManagementIpc、ManagementBridge、IPC DTO、协议版本、requestId 或网页桥接层。

### Architecture Constitution

1. Core 不知道 Windows。
2. Desktop 的 View、ViewModel 和 Command 不直接依赖 Platform.Windows 具体实现。
3. Launcher 不经过任何 Web 或 IPC 层。
4. 搜索只访问 Core 内存索引，不查询 SQLite。
5. UI Automation 不运行在 WPF UI Thread。
6. 显式发送默认不可信；无用户授权的自动发送禁止。
7. Target 必须在动作执行前以及批次每一段执行前重新验证。
8. 第三方应用能力必须通过运行时能力检测验证，不依赖客户端版本号准入。
9. 降级失败不允许演变成误发送。
10. 原型文件与生产 WPF 项目物理、依赖和验收边界分离。
11. 闪念是唯一投递入口；话术库只负责内容管理。

最高安全原则：宁可不能发送，也不能发错窗口、发错内容或重复发送。

### Project Boundary

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

Core 禁止引用 WPF、WebView2、Win32、UI Automation、SQLite 和 PinyinM.NET。Platform.Windows 承载 UIA Worker、Clipboard Transaction、SQLite Write Queue、Hotkeys、Target Detection、Adapter 和媒体存储；Desktop 承载 WPF 生命周期、单实例、托盘、Launcher、Views、ViewModels、Commands 和 Composition Root。

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

### Core Phrase Boundary

Core 直接以 `Phrase.Body: PhraseBody` 表达首发正文，不保留旧 `Phrase.Content` 事实源或纯文本兼容层：

```text
PhraseBody
├── Segments: ImmutableArray<PhraseSegment>
└── BatchSeparator: string

PhraseSegment
├── Id
├── Kind: Text | Image
├── Text: string?
└── Image: PhraseImageReference?
```

`ImmutableArray` 顺序就是编辑、预览和发送顺序。每段只能是一段非空文字或一张有效图片；Core 不保存文件路径、WPF 图片类型、图片文件名、EXIF、OCR 或图片二进制。

首发约束为：每条话术至少一个有效段，最多 20 段、10 张图片，全部文字合计最多 4000 字，标题 0–80 字（标题允许为空）。每条话术独立保存 `BatchSeparator`，默认 `---`；分隔符长度 1–32 字且不能仅为空白，只有独占一行、去除行首尾空格后完全匹配时才拆分。连续、开头或结尾分隔符产生空段时必须报错。CSV 模板表头固定为“一级分类、二级分类、话术标题、话术内容”，CSV 和 `.qphrase` 话术包导入均允许标题为空。

搜索索引使用标题、分类名称和所有文字段按顺序拼接的文本；首发模型不包含标签系统。不索引图片文件名、路径、二进制、EXIF、尺寸，也不做 OCR、图片识别或 AI 描述。图片-only 话术依靠标题和分类名称搜索。搜索过程只访问 Core 内存索引。

### Core Target Boundary

Core 只保存平台无关的 `DeliveryTarget`：`ApplicationId`、`ApplicationKind`、`AdapterId`、`DisplayName`、`RuntimeKey` 和 `CapturedAtUtc`。

HWND、PID、WindowThreadId、ProcessStartTimeUtc、ProcessName、AutomationElement 和 FocusElementIdentity 只存在于 Platform.Windows 的 `WindowsTargetIdentity` / `WindowsTargetContext`。两者通过 `RuntimeKey` 关联，Core 不泄漏 Win32 或 UIA 类型。

### Product and Delivery Boundary

- 话术库只负责图文话术创建、编辑、排序、删除和企业只读详情，不提供插入、发送或投递快捷键。
- 闪念始终只允许明确选择一条话术。
- 空查询最多展示 5 条常用/最近话术，默认不选择；关键词搜索可默认选择第一项。
- 单段纯文字：`Enter`/双击安全插入，目标不可验证时安全复制；`Ctrl+Enter` 进入现有显式发送流程。
- 多段或含图片：`Enter`/双击直接按段插入；`Ctrl+Enter` 直接按段插入并发送，不打开分批预览或确认窗口。
- 两种模式都沿用文本话术的明确选择、目标重校验和失败即停语义；`Ctrl+Enter` 仍属于显式发送动作。

用户选择话术后隐藏闪念并恢复目标焦点，随后按段顺序执行：

```text
RevalidateTarget
→ DetectSegmentCapabilities
→ PrepareClipboardPayload
→ InsertSegment
→ VerifySegmentInsert
→ [InsertOnly: RecordSegmentResult]
→ [InsertAndSend: RevalidateBeforeSend → TriggerSendOnce → RecordSegmentResult]
→ AdapterStabilityWait
→ NextSegment
```

每段执行前都重新验证目标。Adapter 根据粘贴完成、目标、前台窗口、焦点/Caret 指纹稳定性决定何时进入下一段，不提供用户可配置的固定间隔。任一段失败、`Unknown` 或能力不支持时立即停止，不自动重试，不执行后续段，也不提供“继续剩余段”。

`BatchDeliveryResult` 记录总段数、已完成段数、失败段索引、逐段结果和 TraceId。部分成功必须明确显示“已完成 X/N 段，第 Y 段停止”。分批插入完整完成声明 `Inserted`；分批发送完整完成只声明 `SendTriggered`，不得声称目标应用最终已发送；UsageCount 和搜索历史只在分批完整成功后更新一次。

### Adapter Capabilities

首发能力字段固定为：

```text
InsertText
VerifyTextInsert
InsertImage
VerifyImageInsert
TriggerSend
VerifySend
```

能力状态为 `Verified`、`Unverified`、`Unsupported`。客户端版本号只进入脱敏诊断 Trace，不参与准入或降级。

- Generic Adapter：文字能力按运行时规则检测；`InsertImage`、`VerifyImageInsert`、`TriggerSend`、`VerifySend` 默认为 `Unsupported`。
- 企业微信 Adapter：`InsertText = Verified`、`VerifyTextInsert = Verified`、`TriggerSend = Verified`、`VerifySend = Unsupported`。
- 企业微信图片人工矩阵已通过，`InsertImage = Verified`、`VerifyImageInsert = Verified`；不得使用企业微信版本号作为图片准入门槛。
- `VerifyTextInsert` 和未来的 `VerifyImageInsert` 只验证投递动作完整执行以及目标、前台窗口、焦点/Caret 指纹稳定，不读取聊天正文、不截图、不识别聊天区内容。
- `SendTriggered` 仅表示发送快捷操作已完整执行；只有 Adapter 能确认目标应用最终发送结果时才可使用 `Sent`。

文字和图片剪贴板事务都必须复用独立 STA Worker、保存原剪贴板、写入规范化 Payload、投递前重校验、一次 `Ctrl+V`、剪贴板序列检查和尽力恢复。日志和 Trace 禁止记录正文、图片二进制、路径、文件名或缩略图。

### Persistence, Media and Package

SQLite 是事实源，由 Platform.Windows 单写者、外键、busy timeout 和 WAL 管理。正式 schema v1 使用 `phrases`、`phrase_segments` 和 `media_assets` 表保存话术头、有序段和媒体元数据；只有 DB Commit 成功后才更新 Core 内存搜索索引。

产品尚未正式发布，不实现旧数据库、旧 `Phrase.Content` 或旧 `.qphrase` 包兼容。检测到现有开发库 schema 不一致时，必须关闭连接，备份数据库及 WAL/SHM/journal 到带时间戳的开发备份目录，然后重建新 schema v1；旧开发话术不迁移。重建失败时停止启动且不删除备份。首发基线后结构变化改用事务 migration。

图片导入后复制到 `%LOCALAPPDATA%\QuickPhrase\Media\`，以 AssetId 命名。SQLite 只保存内部媒体标识、MIME、字节数、宽高和创建时间，不保存原始绝对路径。支持 PNG、JPEG，以及导入时转为 PNG 的 BMP；单图最大 10 MB、20 MP。导入必须完整解码并重新编码，移除 EXIF 和其他非必要元数据。

`.qphrase` 首发包直接使用图文格式，包含 manifest、话术与有序段 JSON、`media/` 图片；不兼容旧开发期纯文字包。导入必须验证路径穿越、条目和引用完整性、扩展名与实际格式、单文件大小、总解压大小和媒体数量。CSV 每行只创建一个纯文字段，不支持图片或多段。

首阶段图文能力只支持个人话术。企业同步继续使用纯文字契约，收到的企业正文映射为单文字段；企业话术只读，个人图文话术不得上传到 QuickPhrase Hub。

### Logging and Comments

关键类补充中文设计注释；用户可见错误和日志使用清晰中文，并包含 TraceId、阶段、结果码和耗时。日志禁止记录话术标题和正文、剪贴板、图片内容、图片路径或文件名、输入框文字、聊天内容、联系人和客户资料。

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

MainWindow 当前为 `1200×760`，最小 `900×560`；话术库、图文段编辑器、设置、分类/移动/导航确认对话框和 Launcher 的布局以现有 XAML 和实际行为为准。不要基于 Floating Workspace、演示壁纸、假 Windows 桌面、任务栏或原型调试控件进行重构。

### Release Boundary

- `QuickPhrase.Desktop.csproj` 不得引用 WebView2、React、JavaScript runtime、wwwroot 或网页资源。
- `dotnet build QuickPhrase.sln` 不得触发 npm/node。
- 发布目录不得包含 React bundle、HTML/JS/CSS 网页资源或 WebView2 Runtime 安装器。
- `src/` 等原型链路可以保留，但不得被三个正式桌面 Project 引用。
- 安装器保持当前用户、纯 WPF、自包含安装方式；数据库、媒体、备份和日志在卸载后保留。
- 企业微信图片投递已通过 Windows 11 人工矩阵，当前能力可保持 `Verified`；若验收结论失效，必须立即恢复 `Unsupported`，不得使用代码存在或模拟测试通过替代真实矩阵结论。

插件、AI、团队图片同步、普通文件附件、视频、动画图片、OCR、图片编辑、截图、云媒体、浏览器扩展、跨平台、后台发送、失败续传和自动更新不属于本首发基线；个人图文话术与安全分批发送已经属于首发正式架构。
