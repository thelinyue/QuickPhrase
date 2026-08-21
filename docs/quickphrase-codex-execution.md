# QuickPhrase Codex 开发执行文档

状态：Architecture v1.1 已冻结为纯 WPF；Phase 6 Windows 11 发布基础设施已完成。
当前下一步：从不可变标签 `v0.0.1` 触发未签名正式发布工作流，完成最终资产、SHA-256 和 GitHub Release 核验。

## 执行纪律

- 正式产品只使用 `QuickPhrase.Desktop`、`QuickPhrase.Platform.Windows`、`QuickPhrase.Core` 三个桌面 Project。
- Core 不引用 Windows、WPF、UIA、SQLite 或 PinyinM.NET；Desktop 通过 Core 契约调用 Platform.Windows 能力。
- 正式产品是单进程纯 WPF，不引入 WebView、React 管理页、ManagementIpc、ManagementBridge、IPC DTO、协议版本或 requestId。
- 当前实际 WPF XAML、ViewModel 和交互是唯一 UI 参考；Web 原型/Sites 只保留为独立展示资产。
- 任何第三方应用能力默认 `Unverified`；必须通过运行时能力检测后才能执行，客户端版本号只用于脱敏诊断，不作为准入条件。
- 任何不确定的插入、目标变化、剪贴板状态或发送结果都不得自动重试。
- 保留 `.openai/hosting.json`、`worker/index.js`、Sites 脚本和 Sites 测试，不删除原型构建链。

## Phase 0：纯 WPF 边界审计

### 目标

确认生产代码已经完全脱离旧 Web/IPC 技术链，同时不破坏独立原型链。

### 验收标准

- `QuickPhrase.Desktop.csproj` 无 WebView2、React、JavaScript runtime、wwwroot 或网页资源引用。
- `dotnet build QuickPhrase.sln` 不触发 npm/node。
- `desktop/` 生产源码无 ManagementIpc、ManagementBridge、ManagementRequest、ManagementResponse、protocolVersion、requestId。
- WPF ViewModel 无 Web/Bridge DTO。
- 主界面、话术库、编辑器、设置和 Launcher 全部由当前 WPF XAML 实现。
- `src/` 等 Prototype 与生产 csproj 无依赖关系。
- 最终 `artifacts/release/0.0.1/publish` 不含 HTML、JS、CSS bundle 或 WebView2 Runtime 安装器。

### 当前结果

- 生产项目边界测试已覆盖三项目引用方向、Core 平台隔离、Desktop 纯 WPF 和 Bridge 残留。
- 原型目录和 Sites 构建链保留，不作为生产 UI 依据。
- 当前 `publish/` 无网页文件；`installers/`、`prerequisites/`、`previous-*` 和 `publish-generated/` 中的旧产物只作审计留档，不得分发，需重新生成纯 WPF 安装器。
- 发布清单和哈希文件已改为只描述当前纯 WPF EXE，并标记安装器需要重建。

## Phase 1：架构骨架

### 已完成

- 锁定 .NET 10 SDK、三个桌面 Project 和解决方案。
- 建立严格项目引用方向和架构回归测试。
- 建立 WPF Shell、当前实际 MainWindow、Launcher、Tray 生命周期和单实例入口。
- Desktop 通过进程内 Core Contract 调用应用服务；不再通过 Web/IPC 桥接。

结果：`PHASE1_VERIFY_PASS`。

## Phase 2：数据与 SQLite

### 已完成

- categories、phrases、tags、phrase_tags、settings 和 schema migrations。
- 事务化 migration、升级前轻量备份和回滚。
- bounded `IDatabaseWriteQueue` 与 single SQLite writer。
- `foreign_keys=ON`、`busy_timeout=5000`、WAL、`synchronous=NORMAL`。
- Repository、分类/标签 CRUD、设置存储和快捷键规范化。

数据库是唯一权威来源；写入失败不会更新搜索索引。结果：`PHASE2_VERIFY_PASS`，证据见 [phase2-validation.md](phase2-validation.md)。

## Phase 3：搜索引擎

### 已完成

- 不可变内存搜索快照。
- 中文、标题、标签、拼音首字母、全拼、正文和有限模糊匹配。
- DB Commit 成功后才更新索引；索引异常时保留快照并可重建。
- 搜索过程不访问 SQLite。

10,000 条语料 Release 性能：P50 1.547ms、P95 8.729ms、P99 14.396ms。结果：`PHASE3_VERIFY_PASS`，证据见 [phase3-validation.md](phase3-validation.md)。

## Phase 4：WPF Launcher 与生命周期

### 已完成

- 当前 WPF Launcher 的呼出、关闭、焦点、尺寸、键盘导航和安全复制/插入流程。
- 全局 `Alt + Space`、话术快捷键、单实例、托盘、暂停快捷键和首次引导。
- Launcher 不经过管理窗口，不依赖 Web 原型，不依赖网络资源。
- WPF 窗口生命周期和导航确认由 Desktop 直接管理。

结果：`PHASE4_VERIFY_PASS`。后续新增 UI 必须以当前实际 WPF 界面为准，不以旧原型图为准。

## Phase 5：Windows Integration 与投递安全

### 已完成/进行中

- Core 使用平台无关 `DeliveryTarget`；HWND、PID、WindowThreadId、ProcessStartTimeUtc、ProcessName 和 UIA 上下文只在 Platform.Windows 保存。
- 实现 Target 捕获、动作前重校验、MTA UIA Worker、Clipboard Transaction、序列号恢复保护、投递并发闸门、脱敏 DeliveryTrace。
- `DeliveryResult` 已正交化为 Status、Effect、Stage、Confidence、ErrorCode、Message、Retryable、TraceId。
- 企业微信不设置版本门禁：InsertText Verified、VerifyInsert Verified、SendText Verified、VerifySend Unsupported；版本号仅进入脱敏诊断 Trace。
- `Enter` 为 `InsertOnly`，`Ctrl+Enter` 为通用 `InsertAndSend`；Launcher 手势与目标发送协议分离。企业微信固定使用受保护 Clipboard + `Ctrl+V` 插入，并在发送前重校验后按当前已验收配置注入一次 `Enter`。未知结果不自动重试，不开放后台目标发送或无用户授权自动发送。

强制安全测试包括 HWND 复用、PID/线程/启动时间变化、Launcher 后切换窗口、插入验证不确定、插入与发送之间切换窗口、发送后验证不确定、剪贴板新复制内容、UIA 超时/取消/UIPI 和运行时能力不满足时的安全拒绝。

结果：`PHASE5_VERIFY_PASS`。当前主流版本企业微信人工矩阵已由发布负责人明确确认通过；证据见 [phase5-validation.md](phase5-validation.md)。

## Phase 5.1：连续投递与启动性能

### 已完成

- 连续投递队列只接受 `InsertOnly`，按运行时能力提供 1 条执行 + 4 条等待的 FIFO；`InsertAndSend` 永不进入队列。
- 启动路径、投递并发闸门和脱敏日志基础设施。
- 不再记录或依赖旧 management bundle、system.ready 或 WebView 生命周期。

结果：`PHASE5_1_INFRA_PASS`，证据见 [phase5.1-validation.md](phase5.1-validation.md)。

## Phase 6：Windows 11 Release

### 已完成/待发布

- `.NET 10 win-x64` self-contained、非裁剪构建。
- 按用户安装的纯 WPF Inno Setup EXE。
- 开机启动、单实例、托盘、升级备份、卸载保留数据和重装流程。
- 正式版目标目录：`artifacts/release/0.0.1`；正式资产当前未附带 Authenticode 签名，并公开 `SHA256SUMS.txt`。
- 中文产品名：`闪语`；程序集、安装目录、AppId 和数据目录保持 `QuickPhrase` 以兼容升级。
- 发布清单不得包含 WebView2 Runtime、WebView2 前置安装器或网页 bundle。

主要平台为 Windows 11 x64；Windows 10 22H2 为 `UNVERIFIED / NOT SUPPORTED IN V0.0.1`。

当前结果：企业微信人工矩阵与 Windows 11 x64 安装/升级/启动/卸载保留数据矩阵均已由发布负责人明确确认通过。未签名稳定资产通过版本、哈希、纯 WPF 发布边界和 GitHub Release 核验后，才写入 `PHASE6_VERIFY_PASS_WIN11`。证据见 [phase6-validation.md](phase6-validation.md)。

## 统一验证命令

```text
dotnet build QuickPhrase.sln --no-restore
dotnet test QuickPhrase.sln --no-build --no-restore
npm run build
npm run test:sites
```

其中 npm 命令只用于独立 Prototype/Sites 链路，不是正式 WPF 构建依赖。涉及 Windows 真实能力的阶段必须增加 Windows 11 人工验收矩阵；模拟通过不得替代真实第三方客户端运行时能力验收。

## V1 冻结后的 Backlog

插件、AI、团队共享、云同步、文件/图片话术、浏览器扩展、跨平台、后台发送和自动更新均进入 V2 Backlog，不修改 Architecture v1.1 的项目边界和安全原则。

## Frozen Status

**QuickPhrase Architecture v1.1 — FROZEN**

- Phase 0：**AUDITED / PURE WPF BOUNDARY**
- Phase 1：**COMPLETED**
- Phase 2：**COMPLETED**
- Phase 3：**COMPLETED**
- Phase 4：**COMPLETED**
- Phase 5：**VERIFY PASS**
- Phase 5.1：**INFRASTRUCTURE PASS**
- Phase 6：**MANUAL MATRIX PASS / SIGNING PENDING**
