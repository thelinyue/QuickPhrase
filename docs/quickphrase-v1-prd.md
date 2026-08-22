# QuickPhrase 首发产品需求文档

状态：与“QuickPhrase 首发图文话术与分批发送架构基线”对齐
产品名称：闪语（QuickPhrase）
正式技术：`.NET 10 + Pure WPF + Win32/UIA + SQLite + Core 内存搜索`

> 正式产品以当前实际 WPF 界面和交互为准。仓库中的 Web/React 原型仅是独立展示资产，不作为本 PRD 的视觉或架构依据。

## 1. 产品定义

闪语是 Windows 本地快捷话术工具。用户在话术库管理个人图文话术，通过 `Alt + Space` 呼出 WPF 闪念，明确选择一条话术，再将其安全插入或按确认后的顺序分批发送到当前目标应用。

核心安全目标：宁可不能发送，也不能发错窗口、发错内容或重复发送。

## 2. 首发范围

### P0

- WPF MainWindow：当前实际话术库、图文段编辑器、设置和导航确认流程。
- WPF 闪念：`Alt + Space` 呼出、内存搜索、方向键、Enter、Esc、单击选择、双击预览或安全插入。
- 个人话术：标题、分类、排序、稳定 ColorKey、快捷键，以及有序文字段和图片段；首发不建立标签系统。
- 每条话术独立文字分隔符；支持拆分预览、空段错误和确认后生成多个文字段。
- 多段或含图片话术的整批预览、整批确认、逐段重校验、失败即停和批次结果。
- SQLite 本地事实源、开发库备份重建、单写者、图文 schema v1、媒体库和 Core 内存搜索索引。
- 图文 `.qphrase` 导入导出；CSV 每行创建一个纯文字段。
- 企业微信目标重校验、文字 Clipboard Transaction、投递 Trace 和安全降级。
- 单实例、托盘、开机启动、当前用户安装以及数据库、媒体、备份和日志卸载保留。

### 明确不做

插件、AI、团队图片同步、普通文件附件、视频、动画图片、OCR、图片编辑、截图、云媒体、浏览器扩展、跨平台、后台目标发送、失败续传和自动更新不属于首发范围。

企业微信图片投递在 Windows 11 人工矩阵通过前保持 `Unsupported`；代码、模拟测试或客户端版本号不能替代真实人工矩阵。

## 3. 正式 UI 与入口

当前实际界面是唯一参考：

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

MainWindow 为 `1200×760`，最小 `900×560`。话术库、编辑器、设置、分类对话框、移动对话框、未保存导航确认、闪念和整批预览以当前 XAML、ViewModel 和代码为准。

### 话术库

话术库只负责管理，不提供插入、发送、直接发送或投递快捷键。列表行显示标题、第一段文字摘要、内容构成和企业只读标识。普通话术双击或按 `Enter` 打开编辑器；企业话术打开只读详情。

编辑器以有序段列表管理文字段和图片段，支持添加、删除、拖拽排序、上移、下移和固定几何缩略图。每段只能是一段文字或一张图片，文字和图片可以任意交错。

### 闪念

闪念是唯一投递入口，一次只能选择一条话术。空查询最多展示 5 条常用/最近话术但不默认选择；关键词搜索结果可以默认选择第一项。

- 单段纯文字：`Enter`/双击安全插入；`Ctrl+Enter` 进入显式发送流程。
- 多段或含图片：`Enter`/双击只打开整批预览；`Ctrl+Enter` 打开整批发送确认。
- 多段或含图片每次都必须确认整批发送，快捷发送设置不能跳过本次确认。

整批预览显示消息总数、段序号、段类型、文字摘要、图片缩略图与尺寸、最终顺序，以及当前目标的文字、图片和发送能力。

## 4. 数据模型与验证

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
```

首发不保留旧 `Phrase.Content` 或纯文本兼容层。验证规则：

- 标题 1–80 字。
- 每条话术至少一个有效段，最多 20 段、10 张图片。
- 文字段不能为空，全部文字合计最多 4000 字。
- 图片段必须引用有效媒体资产。
- 段数组顺序就是编辑、预览和发送顺序。
- Core 不保存原文件路径、WPF 图片类型、EXIF、OCR 或图片二进制。

每条话术独立保存 `BatchSeparator`，默认 `---`，长度 1–32 个字符且不能仅为空白。分隔符按普通文本匹配，必须独占一行并在去除该行首尾空格后完全匹配。连续、开头或结尾分隔符产生空段时显示字段级错误。

## 5. 搜索规则

查询进入 Core 后执行 Unicode 规范化、大小写归一、空白清理和全角半角归一。索引和匹配内容包括标题、分类名称、标题/正文拼音以及全部文字段按顺序拼接的文本；首发不建立标签索引。

图片话术可通过标题、分类名称和文字段搜索；图片-only 话术依赖标题和分类名称。搜索不索引图片文件名、路径、二进制、EXIF 或尺寸，不增加 OCR、图片识别或 AI 描述。

同分时按 `usageCount`、`lastUsedAt`、`updatedAt` 和标题稳定排序。搜索只访问 Core 内存快照，不查询 SQLite；DB Commit 后才更新索引。

## 6. 投递规则

### Target

Core 使用平台无关的 `DeliveryTarget`。HWND、PID、WindowThreadId、ProcessStartTimeUtc、ProcessName 和 UIA 上下文只存在于 Platform.Windows。目标由闪念呼出时捕获，但每次动作和批次每一段执行前必须重新验证。

### 单段纯文字

`Enter` 执行安全插入。目标变化、能力未验证、UIA 失败或 Clipboard 失败时停止动作；仅插入可按安全策略降级为复制提示。

`Ctrl+Enter` 表示通用显式发送意图。发送必须满足目标有效、文字插入及验证能力可用、发送前 Target 再次验证通过和用户授权。显式发送能力不支持时不得先插入再降级。

### 多段或含图片

整批确认后隐藏闪念、恢复目标焦点，并按正文段顺序执行：

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

Adapter 根据粘贴完成、目标身份、前台窗口和焦点/Caret 指纹稳定性决定下一段时机；不提供用户可配置的固定间隔。

任一段失败、`Unknown`、`Unsupported` 或结果不确定时立即停止，不执行后续段，不自动重试，不提供“继续剩余段”。部分成功必须明确显示“已完成 X/N 段，第 Y 段停止”。

批次完整完成只声明 `SendTriggered`，不声称目标应用最终已发送。UsageCount 和搜索历史只在完整批次后更新一次；部分成功或 `Unknown` 不更新。

## 7. Adapter 能力矩阵

正式能力字段为：

```text
InsertText
VerifyTextInsert
InsertImage
VerifyImageInsert
TriggerSend
VerifySend
```

能力状态为 `Verified`、`Unverified`、`Unsupported`。客户端版本号仅作为可空诊断信息，不参与能力或降级判断。

| 能力 | Generic Adapter | 企业微信 Adapter |
|---|---|---|
| InsertText | 运行时检测 | Verified |
| VerifyTextInsert | 运行时检测 | Verified |
| InsertImage | Unsupported | Unsupported，图片人工矩阵通过前不开放 |
| VerifyImageInsert | Unsupported | Unsupported，图片人工矩阵通过前不开放 |
| TriggerSend | Unsupported | Verified |
| VerifySend | Unsupported | Unsupported，只返回 `SendTriggered` |

文字和图片都使用受保护 Clipboard + 一次 `Ctrl+V`。插入验证只确认动作完整执行以及目标、前台窗口和焦点/Caret 指纹稳定，不读取聊天正文、不截图、不识别聊天区内容。

## 8. SQLite、开发库与媒体

正式 schema v1 使用 `phrases`、`phrase_segments` 和 `media_assets`。`phrases` 不保存旧单字段正文事实源；同一话术内使用唯一排序约束保证段顺序稳定。只有 DB Commit 成功后才更新 Core 内存搜索索引。

产品尚未正式发布，不转换旧开发数据。检测到开发库 schema 不一致时，先关闭连接并备份数据库及 WAL/SHM/journal 到带时间戳目录，再重建 schema v1；旧开发话术不迁移。重建失败时停止启动且保留备份。首发后的结构变化使用事务 migration。

图片导入后复制到 `%LOCALAPPDATA%\QuickPhrase\Media\`，使用 AssetId 命名。支持 PNG、JPEG 和导入时转 PNG 的 BMP；单图最大 10 MB、20 MP。导入必须完整解码和重新编码，移除 EXIF 与其他非必要元数据。损坏图片、伪造扩展名、尺寸超限或无法解码时显示中文错误。

原始图片移动或删除后，应用媒体副本仍可使用。数据库、媒体、备份和日志在卸载后保留。

## 9. 导入导出与企业边界

`.qphrase` 直接使用首发图文格式，包含 manifest、话术与有序段 JSON、`media/` 图片，不兼容旧开发期纯文字包。导入必须验证路径穿越、条目与引用完整性、扩展名、实际图片格式、单文件大小、总解压大小和媒体数量。

CSV 每行只创建一个纯文字段，不支持图片和多段。

首阶段图片和分批发送只支持个人话术。企业同步继续使用纯文字契约，收到的企业正文映射为单文字段；企业话术只读，个人图文话术不得上传到 QuickPhrase Hub。

## 10. 错误、日志与可访问性

用户可见错误使用中文；日志包含 TraceId、阶段、结果码和耗时，但不得记录话术标题和正文、剪贴板、图片内容、图片路径或文件名、输入框、聊天、联系人或客户资料。

图片加载失败同时显示图标和中文错误文字。图文段新增、删除、上移和下移可通过键盘完成。图片 Automation Name 包含段序号和尺寸，例如“图片，第 2 段，1920 × 1080”。

## 11. 性能、平台与发布边界

- 主要平台：Windows 11 x64。
- Windows 10 22H2：`UNVERIFIED / NOT SUPPORTED IN FIRST RELEASE`。
- Launcher 热呼出 P95 ≤ 120ms。
- 一万条话术搜索 P95 ≤ 50ms。
- 稳定空闲五分钟平均 CPU ≤ 0.1%。
- 稳定空闲五分钟不产生周期性持久化写入。
- 发布目录为纯 WPF 自包含产物，不包含网页资源或 WebView2 Runtime 安装器。
- `dotnet build QuickPhrase.sln` 不触发 npm/node。

企业微信图片投递只有在 Windows 11 人工矩阵通过后才能从 `Unsupported` 调整为 `Verified`；在此之前不得声明首发图片发送能力已完成真实客户端验收。
