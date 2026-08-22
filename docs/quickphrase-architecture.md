# QuickPhrase 首发图文话术与分批发送架构基线

状态：首次正式发布基线
目标运行时：`.NET 10 LTS`
正式 UI：当前实际 Pure WPF 界面
主要验证平台：Windows 11 x64
兼容测试平台：Windows 10 22H2 x64（首发未承诺支持）

> 本文只描述正式 QuickPhrase Windows 产品。仓库中的 Web/React 原型和 Sites 构建链是独立资产，不是生产架构，也不是当前 UI 的参考。

## 1. Architecture Constitution

1. Core 不知道 Windows。
2. Desktop 的 View、ViewModel 和 Command 不直接依赖 Platform.Windows 具体实现。
3. Launcher 不经过 Web、IPC 或序列化桥接层。
4. 搜索不查询 SQLite，只查询 Core 内存索引。
5. UI Automation 不运行在 WPF UI Thread。
6. 显式发送默认不可信；无用户授权的自动发送禁止。
7. Target 必须在动作执行前以及批次每一段执行前重新验证。
8. 第三方应用能力必须通过运行时能力检测验证，不依赖客户端版本号准入。
9. 降级失败不允许演变成误发送。
10. 原型与生产代码必须保持物理、依赖和验收隔离。
11. 闪念是唯一投递入口；话术库只负责图文话术管理。

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

负责领域模型、应用服务、内存搜索排序、投递契约和结果模型。禁止引用 WPF、WebView2、Win32、UI Automation、SQLite 和 PinyinM.NET，也不保存文件路径或 WPF 图片类型。

### QuickPhrase.Platform.Windows

负责 Windows 目标捕获与重校验、UIA MTA Worker、文字与图片 Clipboard Transaction、Win32 输入、SQLite 单写者、开发库重建、正式 migration、媒体存储、Adapter 和平台日志。

### QuickPhrase.Desktop

负责 WPF 生命周期、单实例、托盘、窗口、绑定、命令和依赖注入组合。只有 `App.xaml.cs`、Bootstrap/Composition 和必要的 Shell/Launcher 编排可以引用 Platform.Windows 具体类型。Views、ViewModels 和 Commands 依赖 Core 接口或 Desktop 自身抽象。

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

所有正式管理和投递功能均为单进程、进程内调用。不存在 ManagementIpc、ManagementRequest、ManagementResponse、ManagementBridge、协议版本、requestId、JSON Bridge 或网页消息转换。

## 4. 正式 WPF 与产品入口

当前实际界面是唯一 UI 参考：

```text
desktop/QuickPhrase.Desktop/MainWindow.xaml
├── TitleBar
└── ContentRegion
    ├── Views/LibraryView.xaml
    ├── Views/EditorView.xaml
    └── Views/SettingsView.xaml

desktop/QuickPhrase.Desktop/LauncherWindow.xaml
desktop/QuickPhrase.Desktop/BatchPreviewWindow.xaml
```

MainWindow 当前为 `1200×760`，最小 `900×560`。话术库、图文段编辑器、设置、分类/移动/导航确认对话框、Launcher 和整批预览的真实布局与行为以现有 XAML、ViewModel 和代码为准。

产品入口固定为：

- 话术库只负责个人图文话术管理和企业话术只读详情，不提供插入、发送、直接发送或投递快捷键。
- 闪念是唯一投递入口，始终只允许选择一条话术。
- 空查询展示最多 5 条常用/最近话术，但默认不选择；关键词搜索结果可以默认选择第一项。
- 单段纯文字话术保持原有 `Enter`/双击安全插入、`Ctrl+Enter` 显式发送语义。
- 多段或含图片话术的 `Enter`/双击只打开整批预览；`Ctrl+Enter` 打开整批发送确认。
- 多段或含图片必须每次确认整批发送；快捷发送设置不得跳过该确认。

## 5. Core 图文话术模型

旧 `Phrase.Content` 和纯文本兼容层不属于首发正式模型。Core 直接使用：

```text
Phrase
├── Id
├── Title
├── Body: PhraseBody
├── CategoryId
├── Shortcut
├── UsageCount
├── LastUsedAtUtc
├── Version
├── ColorKey
├── SortOrder
└── Scope

PhraseBody
├── Segments: ImmutableArray<PhraseSegment>
└── BatchSeparator: string

PhraseSegment
├── Id
├── Kind: Text | Image
├── Text: string?
└── Image: PhraseImageReference?

PhraseImageReference
├── AssetId
├── MimeType
├── ByteLength
├── PixelWidth
└── PixelHeight
```

不变量：

- `ImmutableArray` 的顺序就是编辑、预览和发送顺序。
- 每条话术至少一个有效段，最多 20 段、10 张图片。
- 每段只能是一段非空文字或一张引用有效媒体资产的图片。
- 全部文字段合计最多 4000 字；标题 0–80 字，允许为空。
- Core 不保存文件路径、图片文件名、WPF 图片类型、图片二进制、EXIF、OCR 或 AI 描述。

每条话术独立保存文字分隔符，默认 `---`：

- 长度 1–32 个字符，不能仅包含空白。
- 按普通文本匹配，不支持正则。
- 必须独占一行，去除该行首尾空格后完全匹配才生效。
- 连续、开头或结尾分隔符产生空段时返回字段级错误，不静默删除。
- 分隔符只用于把粘贴的文字拆成多个文字段；图片始终作为独立图片段插入。

## 6. 搜索边界

Core 内存索引使用标题、分类名称和所有文字段按顺序拼接的文本。首发模型不包含标签系统；图片话术可以通过标题、分类名称和文字段搜索，图片-only 话术依赖标题和分类名称。

搜索不索引图片文件名、路径、二进制、EXIF 或尺寸，不增加 OCR、图片识别或 AI 描述。SQLite 是事实源，但搜索过程不访问 SQLite；只有 DB Commit 成功后才更新内存索引。

## 7. Core Target Boundary

Core 只公开平台无关的 `DeliveryTarget`：

```text
ApplicationId
ApplicationKind
AdapterId
DisplayName
RuntimeKey
CapturedAtUtc
```

HWND、PID、WindowThreadId、ProcessStartTimeUtc、ProcessName、AutomationElement 和 FocusElementIdentity 只存在于 Platform.Windows 的 `WindowsTargetIdentity` / `WindowsTargetContext`。Platform.Windows 通过 `RuntimeKey` 将运行时上下文映射回 Core 目标。

## 8. 单段与批次投递

### 单段纯文字

单段纯文字继续复用安全投递状态机：目标捕获与重校验、能力检测、受保护剪贴板插入、插入验证、发送前重校验、最多一次发送触发和结果记录。

目标不可验证时，`InsertOnly` 可以安全降级为复制提示。显式发送能力不支持时不得先插入再降级；不确定结果不得自动重试。

### 多段或含图片

整批确认后隐藏闪念、恢复目标应用焦点，再按 `PhraseBody.Segments` 顺序处理。每段固定执行链为：

```text
RevalidateTarget
→ DetectSegmentCapabilities
→ PrepareClipboardPayload
→ InsertSegment
→ VerifySegmentInsert
→ RevalidateBeforeSend
→ TriggerSendOnce
→ RecordSegmentResult
→ AdapterStabilityWait
→ NextSegment
```

Adapter 根据粘贴完成、目标身份、前台窗口和焦点/Caret 指纹稳定性决定何时进入下一段，不提供用户可配置的固定秒数间隔。

任一段出现 `Failed`、`Unknown`、`Unsupported` 或结果不确定时：

- 立即停止，不执行后续段。
- 不自动重试，不重复粘贴或重复发送。
- 不提供“继续剩余段”。
- 已触发发送的前置段无法回滚，必须显示“已完成 X/N 段，第 Y 段停止”。

批次结果使用：

```text
BatchDeliveryResult
├── Status
├── Effect
├── TotalSegments
├── CompletedSegments
├── FailedSegmentIndex
├── SegmentResults
└── TraceId
```

每段结果继续包含状态、效果、阶段、可信度、错误码和耗时。UsageCount 和搜索历史只在整批完成后更新一次；部分成功或 `Unknown` 不更新。全批完成只声明 `SendTriggered`，不得声称目标应用最终已发送。

## 9. Adapter 六字段能力

Adapter 能力状态为 `Verified`、`Unverified`、`Unsupported`。正式能力字段固定为：

```text
InsertText
VerifyTextInsert
InsertImage
VerifyImageInsert
TriggerSend
VerifySend
```

Profile 描述 Adapter 身份、实现版本和运行时能力，不包含客户端版本准入范围：

```text
AdapterId
ProcessName
ProfileVersion
InsertTextStatus
VerifyTextInsertStatus
InsertImageStatus
VerifyImageInsertStatus
TriggerSendStatus
VerifySendStatus
FallbackMode
VerifiedAt
DetectedProductVersion (nullable, diagnostics only)
```

企业微信兼容目标为当前主流版本。版本号缺失、读取失败或变化都不得阻止运行时能力检测；版本号只写入不包含用户内容的诊断 Trace。

| 能力 | Generic Adapter | 企业微信 Adapter | 语义 |
|---|---|---|---|
| InsertText | 运行时检测 | Verified | 受保护 Clipboard + `Ctrl+V` |
| VerifyTextInsert | 运行时检测 | Verified | 验证动作完整且目标、前台窗口、焦点/Caret 指纹稳定，不读取正文 |
| InsertImage | Unsupported | Unsupported | Windows 11 企业微信图片人工矩阵通过前禁止图片投递 |
| VerifyImageInsert | Unsupported | Unsupported | 不截图、不读取聊天区、不识别图片是否出现在正文区 |
| TriggerSend | Unsupported | Verified | 用户显式确认后，发送前重校验并注入一次当前目标协议按键 |
| VerifySend | Unsupported | Unsupported | 不确认目标应用最终发送结果，只返回 `SendTriggered` |

`Ctrl+Enter` 是 Launcher 的通用显式发送意图，不绑定企业微信。企业微信图片能力不得使用客户端版本号作为准入门槛；人工矩阵通过前必须保持 `Unsupported`。

## 10. Windows 线程与剪贴板

- WPF Dispatcher 固定为 STA。
- UIA 使用长生命周期、无窗口、COM MTA Worker。
- UIA 查找、控制模式、事件订阅和解绑只能通过 Worker 的有界队列执行。
- UIA Worker 不得阻塞 WPF、Launcher 或全局快捷键消息循环。
- 文字和图片 Clipboard Transaction 使用独立 STA Worker 执行短时操作。

文字与图片事务共享安全边界：

1. 保存原 DataObject 和初始剪贴板序列号。
2. 写入规范化文字或图片 Payload，记录 QuickPhrase 产生的序列号。
3. 投递前重新验证目标。
4. 激活目标并注入一次 `Ctrl+V`。
5. 检查投递动作、目标、前台窗口和焦点/Caret 指纹稳定性。
6. 只有剪贴板未被第三方修改时才尽力恢复原内容。

图片二进制、路径、文件名和缩略图不得进入 Trace 或日志。Clipboard busy 使用有限短退避；失败返回中文错误并停止可能误投递的后续动作。

## 11. SQLite、开发库重建与媒体一致性

正式 schema v1 直接定义图文有序段：

```text
phrases 1 ── N phrase_segments
phrase_segments 0..1 ── 1 media_assets
```

`phrases` 保存话术头和 `batch_separator`，不保存旧单字段正文事实源。`phrase_segments` 保存段类型、文字内容、媒体引用和稳定排序；`media_assets` 保存内部媒体标识、MIME、字节数、宽高和创建时间。外键和同一话术内唯一排序约束保证引用与顺序一致。

SQLite 由 Platform.Windows 单写者、外键、`busy_timeout=5000`、WAL 和 `synchronous=NORMAL` 管理：

```text
Core Application Service
→ Platform.Windows Bounded Write Queue
→ Single SQLite Writer
→ DB Commit
→ Publish Domain Change
→ Core In-memory Search Index
```

产品尚未正式发布，不实现旧数据库或旧正文数据转换。检测到现有开发库 schema 与首发基线不一致时：

1. 关闭数据库连接。
2. 将数据库及 WAL/SHM/journal 复制到带时间戳的开发备份目录。
3. 重建正式 schema v1。
4. 记录中文日志，明确旧开发数据未迁移和备份位置。

重建失败时停止启动且不删除备份。首发基线之后的结构变化必须使用事务 migration。任何数据库写入只有 Commit 成功后才能更新 Core 内存索引。

媒体导入采用临时文件、完整解码与规范化、数据库提交、正式文件原子替换的顺序。DB 保存失败时删除本次临时文件；删除或更新话术后只清理已无引用的媒体。清理失败不回滚已提交数据库修改，记录中文错误并在后续启动执行安全的孤儿媒体清理。

## 12. 图片媒体库

应用管理目录为：

```text
%LOCALAPPDATA%\QuickPhrase\Media\
```

文件使用 AssetId 命名，不依赖原文件路径。SQLite 和日志不保存原文件名或绝对路径。用户移动或删除原图不影响应用媒体副本。

支持 PNG、JPEG 和 BMP；BMP 导入时转换为 PNG。不支持 GIF 动画、SVG、WebP、视频和普通文件附件。单张图片最大 10 MB、20 MP。导入必须完整解码并重新编码，移除 EXIF 和其他非必要元数据；损坏图片、伪造扩展名、尺寸超限或无法解码时显示中文错误。

## 13. 导入、导出与企业边界

`.qphrase` 直接使用首发图文格式，不兼容旧开发期纯文字包：

```text
manifest.json
话术与有序段 JSON
media/图片文件
```

导入必须验证路径穿越、重复或未知条目、扩展名、实际图片格式、单文件大小、总解压大小、媒体数量、Manifest 数量和 JSON/媒体引用完整性。CSV 每行只创建一个纯文字段，不支持图片和多段。

首阶段图文能力只支持个人话术。企业同步继续使用纯文字契约，收到的企业正文映射为单文字段；企业话术只读。个人图文话术不得上传到 QuickPhrase Hub。

## 14. Observability

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

禁止记录话术标题和正文、剪贴板、图片二进制、图片路径或文件名、输入框文字、聊天内容、联系人名称、客户资料和 UIA 读取文本。关键类补充中文设计注释；日志和用户可见错误使用清晰中文。

## 15. 性能、发布与原型隔离

- `.NET 10 win-x64` self-contained、非裁剪构建。
- Launcher 热呼出 P95 ≤ 120ms。
- 一万条话术搜索 P95 ≤ 50ms。
- 稳定空闲五分钟平均 CPU ≤ 0.1%。
- 稳定空闲五分钟内不产生周期性持久化写入。
- 发布目录不得包含 HTML、JavaScript、CSS Web bundle 或 WebView2 Runtime 安装器。
- 使用按用户安装的纯 WPF Inno Setup EXE；数据库、媒体、备份和日志卸载后保留。

仓库中的 `src/`、`prototype/`、`package.json`、Vite、worker 和 Sites 测试可以保留，用于独立设计/展示验证；它们不得被正式桌面 Project 引用，也不能作为生产 UI 参考。`dotnet build QuickPhrase.sln` 不得触发 npm/node。

## 16. 首发基线状态

- `.NET 10 + Pure WPF + Win32/UIA + SQLite + Core 内存搜索` 技术路线不变。
- 三项目边界、依赖方向、Core 平台隔离和内存搜索边界不变。
- 个人 `PhraseBody` 图文话术、独立分隔符、闪念唯一投递、整批确认、逐段重校验、失败即停和图文 `.qphrase` 属于首发正式基线。
- 企业同步首阶段保持纯文字单段；个人图文话术不上传 Hub。
- 企业微信图片投递在 Windows 11 人工矩阵通过前保持 `Unsupported`，不得声明图片能力已验收。
- 插件、AI、团队图片同步、普通文件附件、视频、动画图片、OCR、图片编辑、截图、云媒体、浏览器扩展、跨平台、后台发送、失败续传和自动更新不属于首发范围。

**QuickPhrase 首发图文话术与分批发送架构基线**
