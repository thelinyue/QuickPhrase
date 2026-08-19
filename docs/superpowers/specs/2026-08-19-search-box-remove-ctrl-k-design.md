# 话术库搜索框移除 Ctrl+K 设计说明

**日期：** 2026-08-19

## 目标

移除正式 WPF 话术库底部搜索框的 `Ctrl+K` 快捷键功能及其视觉提示，同时保留搜索框本身、搜索命令和其他话术库快捷键。

## 范围

- 修改 `desktop/QuickPhrase.Desktop/Views/LibraryView.xaml`：删除搜索框上的 `Ctrl+K` `KeyBinding`。
- 修改 `desktop/QuickPhrase.Desktop/Themes/Controls.xaml`：删除搜索框模板中的 `Ctrl K` 徽标，并恢复不需要为徽标预留的右侧内边距。
- 更新相关中文注释，避免继续描述已移除的快捷键。
- 增加架构回归测试，确保这两个正式 WPF 文件不再声明 `Ctrl+K` 搜索入口或徽标。

## 不在范围内

- 不修改搜索查询、搜索命令或 Core 内存搜索实现。
- 不修改 `Enter`、`Ctrl+Enter`、`Delete` 等话术库操作快捷键。
- 不修改全局 `Alt+Space` Launcher 快捷键。
- 不修改 `src/` 等原型链路。

## 验收标准

1. 搜索框仍可通过鼠标和 Tab 聚焦并正常搜索。
2. 搜索框不再显示 `Ctrl K` 徽标。
3. 按 `Ctrl+K` 不再由搜索框触发搜索框聚焦逻辑。
4. 相关测试与正式桌面构建通过。
