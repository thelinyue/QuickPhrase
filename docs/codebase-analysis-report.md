# QuickPhrase（闪语）代码库结构化分析报告

> 生成时间：2026-08-18
> 分析依据：基于仓库实际源码、配置与文档的逐文件阅读。推测内容已显式标注。
> 当前项目状态（来自 `docs/phase6-validation.md`）：`PHASE6_INFRA_PASS`，最终门禁 `PHASE6_VERIFY_PASS_WIN11` 尚未写入。

---

## 0. 分析范围与方法

本报告通过实际阅读以下文件形成结论：

- **根配置**：`package.json`、`global.json`、`Directory.Build.props`、`README.md`、`QuickPhrase.sln`、`vite.config.mjs`、`vite.management.config.mjs`、`index.html`、`management.html`
- **Core 层**：`desktop/QuickPhrase.Core/`（`Contracts.cs`、`SearchService.cs`、`PhraseSearchRuntime.cs`、`ShortcutNormalizer.cs`、标记类）
- **Platform.Windows 层**：`desktop/QuickPhrase.Platform.Windows/` 全部约 22 个 `.cs` 与 3 个 Migration SQL（`001_initial`/`002_category_hierarchy`/`003_phrase_color_key`）
- **Desktop 层**：`desktop/QuickPhrase.Desktop/` 关键约 16 个 `.cs`
- **React 前端**：`src/` 全部 9 个文件
- **测试/工程化**：`tests/QuickPhrase.Architecture.Tests/ArchitectureTests.cs`、`scripts/build-release.ps1`、`.openai/hosting.json`、`docs/quickphrase-architecture.md`、`docs/phase6-validation.md`

**未深入阅读（结论中已标注不确定项）**：`tests/` 各 Phase 测试的具体断言、`design-prototype/`、`installer/QuickPhrase.iss` 全文、`docs/` 其余 Phase 验证文档、`artifacts/`（构建产物）、`worker/index.js` 逐行逻辑。

---

## 1. 项目概览

### 1.1 项目类型
Windows 11 x64 本地**快捷话术（文本片段）投递工具**，中文产品名“**闪语**”，MIT License。目标是在任意应用（V1 仅**企业微信 5.0.9.6065**）的聊天编辑区快速插入预存话术，支持分类树、标签、快捷键与搜索。无云/网络依赖，纯本地工具。

### 1.2 核心功能与业务目标
- 话术库：分类（最多三级树）、标签、纯文本正文、ColorKey、快捷键。
- 快速投递：全局快捷键 `Alt + Space` 呼出 **Native Launcher**（独立于 React，不经过 WebView2）；贴边话术库默认收起、展开承载浏览/编辑/插入。
- 安全投递：捕获目标 → 校验目标 → 解析 Adapter → 检测能力 → 插入 → 校验 →（可选）发送，最高原则“宁可不能发送，也不能发错窗口/内容或重复发送”。
- 管理界面：WebView2 承载 React 管理页（话术库/编辑器/设置三表面）。

### 1.3 技术栈及主要依赖版本
| 类别 | 技术 / 依赖 | 版本 |
| --- | --- | --- |
| 运行时 | .NET | **10 LTS**（`net10.0`，SDK `10.0.400`） |
| 语言 | C# LangVersion | **14.0**（`Nullable` 启用、`ImplicitUsings` 启用、`TreatWarningsAsErrors` 启用） |
| UI 框架 | WPF（Desktop 宿主） | .NET 10 |
| 嵌入式 Web | WebView2 | `Microsoft.Web.WebView2 1.0.4078.44` |
| 管理页前端 | React / ReactDOM | **19.2.0** |
| 构建工具 | Vite | **6.4.2**；`@vitejs/plugin-react 5.0.4` |
| 图标 | `@fluentui/react-icons` | `^2.0.337` |
| 本地数据库 | `Microsoft.Data.Sqlite` | `10.0.10` |
| 拼音 | `PinyinM.NET` | `2.0.0` |
| QA | Playwright | `^1.62.1` |
| 安装包 | Inno Setup | **6.7.3**（online/offline） |

---

## 2. 目录结构

```
QuickPhrase/
├─ desktop/                       # 三个冻结的桌面项目 + 测试
│  ├─ QuickPhrase.Core/           # 平台无关领域层（模型/用例/搜索/接口/状态机契约）
│  ├─ QuickPhrase.Platform.Windows/  # Windows 能力（SQLite/Win32/UIA/剪贴板/热键/目标/Adapter/投递）
│  ├─ QuickPhrase.Desktop/        # WPF 生命周期、单实例、托盘、Launcher、WebView2 Host、IPC、组合根
│  └─ (tests 见下)
├─ tests/QuickPhrase.Architecture.Tests/  # 架构约束 + 各 Phase 验证（63/63 通过）
├─ src/                           # React：原型 + 正式管理页
├─ docs/                          # 架构文档、Phase 验证、PRD、codex 执行说明
├─ scripts/                       # 8 个 PowerShell：verify-phase1~6、verify-phase51、build-release
├─ installer/QuickPhrase.iss      # Inno Setup 脚本（未逐字阅读）
├─ worker/index.js + .openai/hosting.json  # Sites SPA fallback 宿主
├─ design-prototype/              # 视觉原型（未深入）
├─ assets/                        # svg / ico
├─ artifacts/release/1.0.0/       # 已构建发布产物（installers / SHA256SUMS / manifest）
├─ dist/                          # 构建输出（client / management）
├─ index.html / management.html   # 原型入口 / 管理入口 HTML
├─ package.json / global.json / Directory.Build.props / QuickPhrase.sln / vite*.config.mjs
```

**模块划分方式**：严格按“平台无关领域层 / Windows 平台层 / 桌面宿主层”三层切分，依赖方向单向固定（见第 3 章）。

---

## 3. 架构设计

### 3.1 分层与依赖方向（FROZEN）
```
Desktop → Core
Desktop → Platform.Windows
Platform.Windows → Core
Core → 无平台项目引用
```
该方向被 **`ArchitectureTests.ProjectReferencesFollowFrozenDirection`** 以单元测试强制：Core 无项目/包引用；Platform.Windows 仅引用 Core 与 `Microsoft.Data.Sqlite`/`PinyinM.NET`；Desktop 仅引用 Core 与 `Microsoft.Web.WebView2`。

### 3.2 架构宪法 10 条（`docs/quickphrase-architecture.md`）
Core 不知道 Windows；React 不拥有业务能力；WebView2 非核心运行时依赖；Launcher 不经过 React；搜索不查询 SQLite；UIA 不运行在 WPF UI Thread；自动发送默认不可信；Target 必须重验证；第三方能力必须版本化验证；降级失败不允许演变成误发送。

其中“Core 不含平台泄漏”由 **`ArchitectureTests.FrozenCoreDoesNotContainPlatformLeakage`** 通过全文扫描强制（禁止 `WebView2`/`Windows.UI`/`SQLite`/`IManagementBridge`/`IUiAutomationWorker` 等关键字出现在 Core 源码中）。

### 3.3 关键边界与数据流
- **IPC 边界**：Desktop 自有 `IManagementBridge` 与 IPC DTO，Core 不含 IPC 概念。协议版本化（v1），`ManagementBridge` 覆盖协议不匹配（`IPC_PROTOCOL_MISMATCH`）、未知命令（`IPC_UNKNOWN_COMMAND`）、重复 requestId 重放、超大数据（`IPC_PAYLOAD_TOO_LARGE`）、取消（`IPC_TIMEOUT`）、`window.sceneChanged` 场景白名单（library/editor/settings）等。
- **数据一致性不变量**：`DB Commit → Publish Domain Change → Search Index Update`；索引更新异常不回滚数据库，置 `IndexDirty` 并后台重建；搜索全程不查 SQLite。
- **投递安全状态机**（`TextDeliveryStateMachine.cs`，9 阶段）：`CaptureTarget → ValidateTarget → ResolveAdapter → DetectCapabilities → Insert → VerifyInsert → RevalidateBeforeSend → OptionalSend → VerifySend → Completed/Fallback`。自动发送条件需同时 `UserEnabled && TargetValid && AdapterMatched && SendText==Verified && InsertSucceeded && InsertVerified && TargetForeground`。V1 不允许后台目标自动发送；`VerifyInsert` 不确定时绝不重复插入/粘贴；`VerifySend` 不确定时不重试。
- **线程模型**：WPF Dispatcher 固定 STA；UIA 调用集中在无窗口 COM MTA 的 `UiAutomationWorker`；剪贴板操作在独立 STA 线程（`ClipboardTransaction`），以序列号判断用户是否产生新复制内容再决定是否恢复。

---

## 4. 关键实现

### 4.1 入口与组合根
- **`App.xaml.cs`**：`Application` 入口，`OnStartup` 处理单实例 / 升级备份 / 数据初始化 / 托盘 / Onboarding / WebView 生命周期 smoke 测试（`--smoke-*`）。
- **`ApplicationController.cs`**：显式组合根，聚合各协调器，处理 IPC attach、投递调度、设置保存、Launcher 门禁（`LauncherEligibilityPolicy`）。

### 4.2 IPC 与前端
- **`ManagementBridge`**（`ManagementIpc.cs`）：版本化 IPC v1 命令路由 `phrase.*` / `category.*` / `settings.*` / `hotkey.status` / `adapter.*` / `launcher.open` / `window.sceneChanged`，256 响应缓存。
- **正式管理页**：`src/management-main.jsx` → `ManagementHostApp.jsx`（话术库/编辑器/设置三表面，未保存改动确认导航）。
- **`src/managementBridge.js`（`ManagementClient`）**：`crypto.randomUUID` 生成 requestId，超时发 `system.cancel`，`window.chrome.webview` 检测 hostMode。
- **原型**：`src/main.jsx` → `App.jsx`（4 场景 + Launcher + 演示器，全部会话内模拟；`src/data.js` 为明确标注“只服务交互原型”的示例数据）。

### 4.3 核心业务实现位置
| 能力 | 文件 |
| --- | --- |
| 领域模型/契约 | `Core/Contracts.cs` |
| 搜索（不可变快照 + 强/模糊匹配） | `Core/SearchService.cs`、`Core/PhraseSearchRuntime.cs` |
| 快捷键规范化 | `Core/ShortcutNormalizer.cs` |
| 数据组合根 / 迁移 | `Platform.Windows/QuickPhraseDataRuntime.cs`、`MigrationRunner.cs` |
| SQLite 单写者队列 | `Platform.Windows/SqliteWriteQueue.cs` |
| 仓库（校验/乐观并发） | `Platform.Windows/Sqlite*Repository.cs` |
| Adapter 解析（仅 WXWork） | `Platform.Windows/WindowsAdapterResolver.cs`、`WeComAdapter` |
| 投递状态机 | `Platform.Windows/TextDeliveryStateMachine.cs` |
| 目标检测（防句柄复用） | `Platform.Windows/WindowsTargetDetector.cs` |
| 剪贴板事务 | `Platform.Windows/ClipboardTransaction.cs` |
| Win32 P/Invoke（SendCtrlV） | `Platform.Windows/WindowsNativeMethods.cs` |
| 热键 / 启动 / 前台监听 | `WindowsHotkeyService.cs`、`WindowsStartupRegistration.cs`、`ForegroundApplicationWatcher.cs` |

### 4.4 配置与路由定义
- `global.json`（SDK 10.0.400，`rollForward latestPatch`）、`Directory.Build.props`（LangVersion 14、`TreatWarningsAsErrors`、`Deterministic`）。
- `vite.config.mjs`：输出 `dist/client`，多入口 index+management，`manualChunks` 拆分 react/fluentIcons，host `0.0.0.0` allowedHosts `terminal.local`。
- `vite.management.config.mjs`：输出 `dist/management`，`publicDir false`（避免把原型带进发布包）。
- `QuickPhrase.sln`：3 桌面项目 + `Architecture.Tests`，Phase 配置全 Any CPU。

---

## 5. 数据层

### 5.1 数据模型（SQLite，6 表）
`schema_migrations`、`categories`、`phrases`、`tags`、`phrase_tags`、`settings`。
- **migrations**：`001_initial`（含 7 默认分类、20 默认标签、18 默认话术、默认设置）、`002_category_hierarchy`（加 `parent_id`）、`003_phrase_color_key`（加 8 色 CHECK 的 `color_key`）；迁移经单事务 + 升级前备份 + 兼容性修复，校验和（SHA256）校验。
- **仓库校验**：标题 ≤80、正文 ≤4000、标签 ≤10；`ValidColorKeys` = default/red/orange/yellow/green/blue/purple/gray；三级分类树校验（深度/循环/非空删除保护）；`SqlitePhraseRepository` 乐观并发（`version+1`）。
- **设置**：JSON 聚合存储，保存时强制 `AutoSend=false`，WXWork 默认开启。
- **拼音**：`IPinyinProvider`（Core 契约）/ `PinyinMProvider`（Platform 薄适配，限制 32 变体组合）；`SearchService` 不直接引用 PinyinM.NET。

### 5.2 外部服务 / 第三方集成
- V1 **唯一目标应用**：企业微信 `5.0.9.6065`，固定走**受保护剪贴板 + Ctrl+V**，`InsertText=Verified`、`VerifyInsert=Unverified`、`SendText=Unsupported`、`VerifySend=Unsupported`，不开放 Unicode 直输或自动发送。
- 通过**版本化 Adapter Profile**（`Verified`/`Unverified`/`Unsupported` 三态）验收；未按客户端版本验收的能力默认不开放自动发送。
- **无云/网络/远程依赖**（纯本地工具）；Sites 相关（`worker/index.js` + `.openai/hosting.json`）仅为 SPA fallback 宿主，`.openai/hosting.json` 内容极小（`{"d1":null,"r2":null}`）。

---

## 6. 工程化配置

### 6.1 构建
- `npm run build` → `dist/client`（多入口）；`npm run build:management` → 独立 `dist/management`（不含原型）。
- `npm run dev`（host `0.0.0.0`、allowedHosts `terminal.local`）、`npm run preview`。
- `.NET` 侧：`dotnet build/test QuickPhrase.sln`（Debug 与 Release 均 0 warning，63/63 通过）。

### 6.2 测试
- `npm run test:sites`（Sites 4/4）。
- `dotnet test`：架构约束测试 + Phase1~6/5.1 验证（共 63/63）。
- Playwright：`qa:management`（QA 自动化）。

### 6.3 部署 / 发布（`scripts/build-release.ps1`）
完整流水线：
1. **发布门禁**：要求 `QUICKPHRASE_WECOM_ACCEPTANCE=passed` 与 Inno Setup 6.7.3；运行 `verify-phase51`。
2. React 双构建 + Sites 测试。
3. Debug/Release `dotnet build` + `dotnet test`（均 0 warning）。
4. 下载 WebView2 Bootstrapper 与 Standalone x64（带完整性校验）。
5. **Self-contained ReadyToRun** 发布 `win-x64`（`--self-contained true`、`PublishTrimmed=false`、`PublishSingleFile=false`）。
6. 产物检查：必须仅含 `Web/management.html`、不含原型 `index.html`/演示壁纸/`WebView2Loader.dll` 存在。
7. Inno Setup 生成 **online / offline** 安装器。
8. 生成 `SHA256SUMS.txt` 与 `release-manifest.json`（标注 `signed=false`、`supportedOs=Windows 11 x64`、`windows10Status=unverified`）。

### 6.4 代码规范与质量门禁
- `TreatWarningsAsErrors`、`Deterministic`、`Nullable`、`LangVersion 14`。
- **架构约束由测试强制**（项目引用方向、Core 无平台泄漏、IPC 协议版本化）。

---

## 7. 问题与建议（按优先级）

### P0 — 发布门禁（已知未通过，阻断 1.0.0 正式发布）
1. **Phase 6 最终门禁未关闭**：状态仍为 `PHASE6_INFRA_PASS`，`PHASE6_VERIFY_PASS_WIN11` 未写入。企业微信 30 次人工插入矩阵 + Windows 11 安装/冷启动矩阵仍需发布负责人以时间/TraceId/安装矩阵证据确认（详见 `docs/phase6-validation.md` 第 31–38 行）。
2. **Windows 10 标记为不支持**（`UNVERIFIED / NOT SUPPORTED IN V1.0.0`）；安装器**未签名**，SmartScreen “未知发布者”为已知限制。

### P1 — 潜在风险 / 技术债
3. **`UiAutomationWorker` 名不副实**：文件命名与架构文档均声称 UIA 用于 Chrome/Edge，但 V1 企业微信仅用 Clipboard+Ctrl+V；源码中该 Worker 实际**未调用任何 UIA API**（仅占位 MTA 线程）。建议：确认其为预留能力还是死代码，若是后者应加注释或移除，避免误导维护者。
4. **前端新旧视图并存**：`ManagementHostApp.jsx` 中 `SettingsView` 与 `SettingsViewV2`、`LibraryView` 与 `LibraryTreeView` 并存，疑似未清理的旧版死代码（旧版非树状 vs 新版树状分类）。建议确认当前激活路径后移除旧实现，降低维护面。
5. **`CoreAssemblyMarker.Phase = "Phase 3 — Search"` 与项目时间线不一致**：项目已到 Phase 6，但 Core 标记仍停留在 Phase 3。若仅表示“Core 自身最后变更阶段”则无害，但建议在文档/注释中说明，避免与整体 Phase 进度混淆。
6. **`App.xaml.cs` 硬编码 `desktopVersion = "0.1.0-phase5"`**：版本标记滞后，与发布版本 `1.0.0` 不一致，建议统一版本来源（如从程序集/清单读取）。

### P2 — 可优化项
7. **无版本控制集成**：`release-manifest.json` 中 `sourceState = "workspace-no-git"`。建议纳入 Git，以追溯每次构建的源码来源。
8. **大数据量管理页**：列表接口每页最多 100 条；话术库上万条时需确认管理页已做虚拟滚动/分页，避免渲染卡顿。
9. **可观测性有限**：仅依赖脱敏 `DeliveryTrace` jsonl（7 天清理），无集中遥测/告警；上线后故障定位成本较高。
10. **功能范围限制（已知 V1 边界）**：仅纯文本话术、仅企业微信、无图片/文件/富文本/跨平台/后台发送/自动更新（均在 V2 Backlog），非缺陷但需在需求沟通中明确。

---

## 附：不确定性标注（未深入阅读部分）
- `tests/` 各 Phase 测试的具体断言未逐一阅读（已知全 63/63 通过）。
- `design-prototype/` 视觉原型未读，管理页视觉基线以 `AGENTS.md` 描述为据。
- `installer/QuickPhrase.iss` 全文未读（已知 Inno Setup 6.7.3 online/offline 可编译）。
- `docs/` 其余 Phase 验证文档未逐字阅读（仅读架构与 phase6）。
- `artifacts/` 为构建产物未读；`worker/index.js` 仅确认 SPA fallback 用途，未逐行分析。
- `.openai/hosting.json` 内容极小且为占位，Sites 宿主具体行为未深入。
