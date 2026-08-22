# 纯 WPF 阶段 0 审计记录

审计日期：2026-08-19  
审计范围：正式桌面 Project、架构文档、安装清单和 `artifacts/release/1.0.0` 目录  
结论：`PURE_WPF_BOUNDARY_PASS_WITH_LEGACY_ARTIFACTS_RETAINED`

## 1. 正式代码边界

正式桌面 Project 只有：

```text
QuickPhrase.Desktop
QuickPhrase.Platform.Windows
QuickPhrase.Core
```

架构回归测试确认：

- Core 无项目引用和平台类型泄漏。
- Platform.Windows 只引用 Core，并承载 SQLite/PinyinM.NET 等平台能力。
- Desktop 只引用 Core、Platform.Windows 和 WPF 所需的 CommunityToolkit.Mvvm。
- Desktop csproj 无 WebView2、React 或 JavaScript 包。
- `desktop/` 生产源码无 ManagementBridge、ManagementRequest、ManagementResponse、protocolVersion、requestId 等旧桥接符号。
- 当前 MainWindow、LibraryView、EditorView、SettingsView、LauncherWindow 均为 WPF XAML/代码实现。

## 2. 原型隔离

仓库根目录的 `src/`、`package.json`、Vite 配置、`worker/`、Sites 脚本和 Sites 测试属于独立 Prototype/Sites 链路。它们保留用于展示和构建验证，但不被正式桌面 Project 引用，也不作为产品 UI 参考。

本阶段未删除或移动原型文件，避免破坏既有 Sites 构建链。

## 3. 发布目录审计

`artifacts/release/1.0.0/publish` 当前未发现 HTML、JavaScript、CSS、React bundle、wwwroot 或 WebView2 Runtime 安装器。目录中存在的空 `Web` 目录属于历史发布结构残留，不包含文件。

`installers`、`prerequisites` 以及以下目录仍保留历史中间产物；其中旧安装器/前置文件仍带有旧 Web 技术链，不能作为本次纯 WPF 发布物使用。它们未在本阶段删除：

```text
artifacts/release/1.0.0/previous-publish-20260817-114116
artifacts/release/1.0.0/publish-generated
artifacts/release/1.0.0/previous-installers-20260817-114116
```

删除这些目录会改变发布留档边界，需单独确认后再处理。

## 4. 发布配置修正

`release-manifest.json` 已改为只描述当前 `publish/QuickPhrase.exe`，并标记 `REBUILD_REQUIRED_AFTER_PURE_WPF_MIGRATION`；`SHA256SUMS.txt` 只保留当前纯 WPF EXE 的哈希。旧安装器不能继续分发，必须重新执行 Inno Setup 生成纯 WPF 安装包。安装器脚本本身已经声明无 WebView2 Runtime 依赖。README、Architecture、PRD 和 Codex 执行文档已同步为“当前实际 WPF 界面是唯一 UI 参考”。

## 5. 未覆盖事项

本审计不等价于以下真实验收：

- WPF 窗口的人工视觉/可访问性验收。
- Windows 11 安装、升级、卸载矩阵。
- 当前主流版本企业微信的运行时能力人工矩阵，包括 Enter 插入、Ctrl+Enter 显式发送、草稿、焦点切换和异常中断。
- 代码签名、安装包分发和远程下载验证。

这些仍按 Phase 5/Phase 6 的人工门禁执行。

