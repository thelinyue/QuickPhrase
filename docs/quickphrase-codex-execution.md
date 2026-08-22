# QuickPhrase Codex 开发执行文档

状态：正式架构已更新为“QuickPhrase 首发图文话术与分批发送基线”。
当前重点：完成首发图文数据、媒体、`.qphrase`、WPF 图文管理和安全分批发送验证；企业微信图片能力在 Windows 11 人工矩阵通过前保持 `Unsupported`。

## 执行纪律

- 正式产品只使用 `QuickPhrase.Desktop`、`QuickPhrase.Platform.Windows`、`QuickPhrase.Core` 三个桌面 Project。
- Core 不引用 Windows、WPF、UIA、SQLite 或 PinyinM.NET；Desktop 通过 Core 契约调用 Platform.Windows 能力。
- 正式产品是单进程 Pure WPF，不引入 WebView、React 管理页、ManagementIpc、ManagementBridge、IPC DTO、协议版本或 requestId。
- 当前实际 WPF XAML、ViewModel 和交互是唯一 UI 参考；Web 原型/Sites 只保留为独立展示资产。
- 搜索只访问 Core 内存索引；数据库 Commit 成功后才更新索引。
- 任何第三方应用能力默认不可信，必须通过运行时能力检测；客户端版本号只用于脱敏诊断，不作为准入条件。
- 任何不确定的插入、目标变化、剪贴板状态或发送结果都不得自动重试。
- 闪念是唯一投递入口；话术库只负责管理图文话术。
- 保留 `.openai/hosting.json`、`worker/index.js`、Sites 脚本和 Sites 测试，不删除或接入原型构建链。

## Phase 0：Pure WPF 边界

### 固定结果

- `QuickPhrase.Desktop.csproj` 无 WebView2、React、JavaScript runtime、wwwroot 或网页资源引用。
- `dotnet build QuickPhrase.sln` 不触发 npm/node。
- `desktop/` 生产源码无 ManagementIpc、ManagementBridge、ManagementRequest、ManagementResponse、protocolVersion、requestId。
- WPF ViewModel 无 Web/Bridge DTO。
- 主界面、话术库、图文段编辑器、设置、闪念和整批预览全部由当前 WPF XAML 实现。
- `src/` 等 Prototype 与生产 csproj 无依赖关系。
- 正式发布目录不含 HTML、JS、CSS bundle 或 WebView2 Runtime 安装器。

## Phase 1：架构骨架

### 固定结果

- `.NET 10 LTS`、三个桌面 Project 和解决方案边界不变。
- Core 不知道 Windows；项目引用方向保持 `Desktop → Core`、`Desktop → Platform.Windows → Core`。
- Desktop 通过进程内 Core Contract 调用应用服务，不经过 Web/IPC 桥接。
- WPF Shell、MainWindow、Launcher、Tray、单实例和 Composition Root 保持现有边界。

## Phase 2：图文数据、SQLite 与媒体

### 首发基线

- `Phrase.Content` 不再是正式事实源；Core 使用 `Phrase.Body: PhraseBody`。
- `PhraseBody` 保存有序文字段/图片段和每条话术独立的 `BatchSeparator`。
- 每条话术至少 1 段，最多 20 段、10 张图片，文字合计最多 4000 字。
- schema v1 使用 `phrases`、`phrase_segments`、`media_assets`、categories、tags、phrase_tags 和 settings。
- SQLite 固定启用外键、`busy_timeout=5000`、WAL、`synchronous=NORMAL`，并由单写者串行提交。
- 同一话术中的段排序使用唯一约束；图片段必须引用有效媒体资产。
- 只有 DB Commit 成功后才发布领域变化和更新 Core 内存搜索索引。

产品尚未正式发布，不实现旧数据库或旧正文数据转换。开发库 schema 不一致时：

1. 关闭数据库连接。
2. 备份数据库及 WAL/SHM/journal 到带时间戳目录。
3. 重建正式 schema v1。
4. 记录中文日志，明确旧开发数据未迁移及备份位置。

重建失败时停止启动且保留备份。首发后的结构变化改用事务 migration。

媒体目录固定为 `%LOCALAPPDATA%\QuickPhrase\Media\`。导入 PNG、JPEG、BMP 时必须完整解码并重新编码，BMP 转为 PNG，移除 EXIF 和非必要元数据；单图最大 10 MB、20 MP。SQLite 和日志不保存原始文件名或绝对路径。

## Phase 3：Core 内存搜索

### 首发基线

- 搜索快照保持不可变，搜索过程不访问 SQLite。
- 索引内容使用标题、分类名称和全部文字段按顺序拼接的文本；首发模型不包含标签系统。
- 图片话术可通过标题、分类名称和文字段搜索；图片-only 话术依赖标题和分类名称。
- 不索引图片文件名、路径、二进制、EXIF、尺寸，不增加 OCR、图片识别或 AI 描述。
- DB Commit 成功后才更新索引；索引异常时保留最后有效快照并从 SQLite 重建。

## Phase 4：WPF 管理与闪念

### 首发基线

- 话术库只管理内容，不保留插入、发送、直接发送或投递快捷键。
- 图文段编辑器支持添加文字段、添加图片段、删除、拖拽排序、上移、下移和图片缩略图。
- 分隔符拆分支持预览和确认；连续、开头或结尾分隔符导致空段时显示字段级错误。
- 企业话术打开只读详情；首阶段个人图文话术不得上传 QuickPhrase Hub。
- 闪念一次只能明确选择一条话术。
- 空查询最多展示 5 条常用/最近话术且默认不选择；关键词搜索结果可以默认选择第一项。
- 单段纯文字保持 `Enter` 插入、`Ctrl+Enter` 显式发送。
- 多段或含图片的 `Enter`/双击只预览，`Ctrl+Enter` 必须打开整批确认；快捷发送设置不能跳过。

## Phase 5：Adapter 与投递安全

### 六字段能力

```text
InsertText
VerifyTextInsert
InsertImage
VerifyImageInsert
TriggerSend
VerifySend
```

能力状态为 `Verified`、`Unverified`、`Unsupported`。企业微信不设置版本门禁；版本号仅进入脱敏诊断 Trace。

- Generic Adapter：文字能力按运行时规则检测；图片、发送触发和发送验证默认为 `Unsupported`。
- 企业微信：`InsertText = Verified`、`VerifyTextInsert = Verified`、`TriggerSend = Verified`、`VerifySend = Unsupported`。
- 企业微信图片人工矩阵通过前：`InsertImage = Unsupported`、`VerifyImageInsert = Unsupported`。

`VerifyTextInsert` 只确认粘贴动作完整执行以及目标、前台窗口、焦点/Caret 指纹稳定，不读取聊天正文。图片验证未来也不得通过读取聊天正文、截图或识别聊天区来实现。

### 批次状态机

整批确认后按段执行：

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

- 每段执行前重新验证目标。
- Adapter 根据粘贴、目标、前台窗口和焦点稳定性决定下一段时机；不提供固定延时配置。
- 任一段失败、`Unknown` 或能力不支持时立即停止，不执行后续段、不重试、不提供继续剩余段。
- 完整批次只声明 `SendTriggered`；不得声称目标应用最终已发送。
- UsageCount 和搜索历史只在整批完成后更新一次。

文字和图片 Clipboard Transaction 复用独立 STA Worker、原剪贴板保存、目标重校验、一次 `Ctrl+V`、剪贴板序列检查和尽力恢复。图片二进制、路径、文件名和缩略图不得进入 Trace 或日志。

## Phase 5.1：批次稳定等待与并发边界

- 现有投递并发闸门、启动性能和脱敏日志基础设施继续保留。
- 多段批次在当前段完成后调用 Adapter 稳定等待，再次验证目标和焦点后才进入下一段。
- 不使用用户可配置间隔，也不使用客户端版本号决定等待。
- 稳定性无法确认时返回 `Unknown` 并停止；目标或焦点明确变化时返回失败并停止。
- 既有连续投递队列与图文整批状态机是不同入口，不得绕过整批确认或把 `InsertAndSend` 自动排队重试。

## Phase 6：Windows 11 发布验证

### 固定发布边界

- `.NET 10 win-x64` self-contained、非裁剪构建。
- 按用户安装的 Pure WPF Inno Setup EXE。
- 开机启动、单实例、托盘、开发库备份重建、卸载保留数据库/媒体/备份/日志和重装流程。
- 发布清单不得包含 WebView2 Runtime、WebView2 前置安装器或网页 bundle。
- 主要平台为 Windows 11 x64；Windows 10 22H2 为 `UNVERIFIED / NOT SUPPORTED IN FIRST RELEASE`。

已有企业微信纯文字投递矩阵不等于图片矩阵。只有 Windows 11 企业微信图片人工矩阵实际覆盖图片粘贴、图文交错、切换聊天、切换前台窗口、已有草稿、大图片和处理延迟后，图片能力才允许从 `Unsupported` 调整。未通过前不得声明图片投递已验收。

## `.qphrase`、CSV 与企业边界

- `.qphrase` 使用首发图文格式：manifest、话术与有序段 JSON、`media/` 图片。
- 不兼容旧开发期纯文字包，不保留旧 `Content` 契约。
- 导入验证路径穿越、重复或未知条目、扩展名、实际格式、单文件大小、总解压大小、媒体数量和引用完整性。
- CSV 每行只创建一个纯文字段，不支持图片和多段。
- 企业同步继续使用纯文字契约，收到的企业正文映射为单文字段。
- 企业话术只读；个人图文话术不得上传 QuickPhrase Hub。

## 统一验证命令

```text
dotnet build QuickPhrase.sln --no-restore
dotnet test QuickPhrase.sln --no-build --no-restore
npm run build
npm run test:sites
```

npm 命令只用于独立 Prototype/Sites 链路，不是正式 WPF 构建依赖。涉及 Windows 真实能力的验证必须增加 Windows 11 人工矩阵；模拟通过不得替代真实第三方客户端运行时验收。

## 首发范围外

插件、AI、团队图片同步、普通文件附件、视频、动画图片、OCR、图片编辑、截图、云媒体、浏览器扩展、跨平台、后台发送、失败续传和自动更新不属于首发基线。个人图文话术、图文 `.qphrase` 和安全分批发送已经属于首发正式范围。

**QuickPhrase 首发图文话术与分批发送架构基线**
