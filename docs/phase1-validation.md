# QuickPhrase Phase 1 验证记录

状态：**PHASE1_VERIFY_PASS**  
冻结基线：`QuickPhrase Architecture v1.0 — FROZEN`  
验证日期：2026-08-16（Asia/Shanghai）

## 环境

- .NET SDK：`10.0.400`（由 `global.json` 锁定）
- Visual Studio Build Tools：`17.14.37`
- WebView2 Runtime：本机 Evergreen Runtime 已安装；Desktop 使用 `Microsoft.Web.WebView2 1.0.4078.44`
- Node.js：用于现有 React/Vite 视觉原型链
- 主要验证目标：Windows 11 x64

## 命令结果

以下命令由 `scripts/verify-phase1.ps1 -IncludeDesktopSmoke` 串行执行并通过：

| 命令 | 结果 |
| --- | --- |
| `dotnet --version` = `10.0.400` | PASS |
| `npm run build` | PASS |
| `npm run test:sites`（4 tests） | PASS |
| `node scripts/qa.mjs`（consoleErrors = 0） | PASS |
| `dotnet build QuickPhrase.sln`（0 warning / 0 error） | PASS |
| `dotnet test QuickPhrase.sln`（5 tests） | PASS |
| `--smoke-native-launcher` | PASS / exit 0 |
| `--smoke-webview-lifecycle` | PASS / exit 0 |

## 已验证边界

- 生产项目只有 `QuickPhrase.Core`、`QuickPhrase.Platform.Windows`、`QuickPhrase.Desktop`；依赖方向由架构测试锁定。
- Core 未引用 WPF、WebView2、SQLite、Win32/UIA、IPC DTO 或 React。
- WebView2 只在管理窗口打开后延迟初始化；关闭窗口释放控件和 Controller，Environment 级 `BrowserProcessExited` 作为退出信号。
- Native Launcher 是纯 WPF 窗口，不加载 React/WebView2，不注册真实 `Alt + Space`，只验证焦点、方向键、Enter、Esc 与生命周期。
- IPC 仅开放 `system.ping` / `system.cancel`，覆盖协议不匹配、未知命令、重复 requestId、消息上限和取消超时。
- WebView2/React 缺失时使用原生中文故障面板，托盘与 Launcher 继续可用。
- React bridge 在浏览器/Sites 环境安全降级为 mock，并通过 `document.documentElement.dataset.hostMode/hostStatus` 暴露 smoke 状态，不改变现有视觉。

## Phase 1 明确未实现

SQLite、Phrase Repository、搜索索引、全局快捷键、UI Automation、剪贴板、TargetIdentity、应用 Adapter、文本投递状态机和真实第三方客户端适配均留在后续阶段。

## 下一执行项

**Phase 2 — 数据与 SQLite**
