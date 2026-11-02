# 2026-11-02 样式统一实施与验收

后续已实施用户采纳的统一关闭方案，并移除设置侧栏“本地话术工具”文字。最新关闭行为、原生权限补齐与独立重跑结果见 [关闭方式统一验收](dialog-close-2026-11-02.md)；本文的数量和截图仅对应先完成的样式阶段。

## 范围与交付

基于 `master` 的 `4b30c70e6d3deff909b833534129638ed3a52d7f` 实施已确认的统一样式方案，客户端版本保持 `0.2.13`。本轮不提交、不推送、不打标签、不发布或部署；用户原有未跟踪 `.vitest/` 保留。

- [统一样式规范](../theme.md) 作为唯一规范入口，旧验收迁入 [主题历史记录](theme-history.md)，不把历史截图和旧发布状态当成本轮证明。
- [设计变量](../../src/styles/tokens.css) 统一语义颜色、字体层级、32／28px 控件、间距、圆角和浮层阴影；[共享材料](../../src/styles/controls.css) 统一按钮、原生表单和交互状态。
- [Button／IconButton](../../src/ui.tsx) 统一普通操作、图标操作及危险语义，保留原生事件、ref、无障碍和表单属性；默认不是提交按钮，提交点显式使用 `type="submit"`。
- [样式入口](../../src/styles.css) 只按顺序导入变量、基础、共享控件和七个业务布局文件；收敛重复按钮和表单材料，不增加 UI 框架、设置项或样式门禁。
- 迁移设置、编辑器、账号／共享管理、闪云中心、主窗口、搜索、底栏、候选、独立菜单和最小化浮条。命令式编辑媒体节点复用共享按钮类和原 SVG，不重建 React 根或改变编辑模型。
- 保留七种主题、用户自定义色、紧凑工具布局及专用窗口几何；不修改 Rust 业务、数据库、IPC、权限、发送、剪贴板保护或 Hub 服务端源码。Hub 仅用本机 Debug 产物做隔离联调。

## 自动检查

| 检查 | 本轮结果 | 证据 |
|---|---|---|
| `npm test` | 34 个文件、385 项通过，包含新增的 7 项共享控件测试 | [前端日志](../../.build/style-unification-frontend-tests.log) |
| `npm run check` | 通过 | [类型检查日志](../../.build/style-unification-typecheck.log) |
| `npm run build` | TypeScript 与生产前端构建通过 | [构建日志](../../.build/style-unification-build.log) |
| `npm run test:release` | 12 项通过；只验证既有脚本，不执行发布 | [脚本日志](../../.build/style-unification-release-tests.log) |
| `cargo fmt --manifest-path src-tauri/Cargo.toml -- --check` | 通过 | [格式日志](../../.build/style-unification-rust-fmt.log) |
| `cargo test --manifest-path src-tauri/Cargo.toml` | 227 项通过 | [Rust 测试日志](../../.build/style-unification-rust-tests.log) |
| `cargo clippy --manifest-path src-tauri/Cargo.toml --all-targets -- -D warnings` | 通过 | [Clippy 日志](../../.build/style-unification-rust-clippy.log) |
| `cargo build --manifest-path src-tauri/Cargo.toml --features custom-protocol` | Debug 客户端构建通过，使用本轮生产前端资产 | [Rust 构建日志](../../.build/style-unification-rust-build.log) |

共享控件测试覆盖原生属性与 ref、默认不提交、显式提交和无效输入、键盘激活、禁用／处理中、表单防重复、失败保留草稿与关闭后焦点恢复。文件关联测试保留真实共享控件，登录测试实际点击提交按钮，不以直接触发表单事件替代表单迁移验收。

## 实际 WebView2 回归

所有脚本串行启动隔离客户端，必要时连接本机隔离 Hub；不连接生产数据，不执行真实聊天发送或成功复制。

| 专项 | 本轮结果 | 覆盖 |
|---|---|---|
| `verify-illustrations.mjs` | [28 组通过](../../.build/illustrations-1793600115646/report.json) | 七主题主要状态、尺寸稳定、无额外外框、选中叠加焦点、对比度、独立窗口和最小视口、长中文、10／14／18px、空态／失败态、候选与预览显示快照 |
| `verify-teams.mjs` | [20 组通过](../../.build/teams-1793600160828/report.json) | 真实 HTTP／SSE 与账号窗口、表单校验及错误草稿、最小客户区、长字段、多库成员、分页、权限及关闭保护 |
| `verify-company.mjs` | [31 组通过](../../.build/company-1793599976357/report.json) | 云文件十条记录与分页、最小客户区、关联编辑器、权限、回收与安全边界 |
| `verify-cloud-commands.mjs` | [8 组通过](../../.build/cloud-commands-1793600149727/report.json) | 七主题真实控件焦点与表单、模块切换、最小窗口、长正文、权限、离线／冲突与切号 |

四套专项共 87 组通过。最终报告均使用完成修正后的客户端产物；此前中断或失败的部分结果不计入通过数。

代表截图：[默认设置页](../../.build/illustrations-1793600115646/all-windows-default-settings.png)、[粉色编辑器](../../.build/illustrations-1793600115646/all-windows-pink-editor.png)、[300px 窄窗与 18px 话术](../../.build/illustrations-1793600115646/full-list-300-18.png)、[最小账号窗长字段与多库成员](../../.build/teams-1793600160828/accounts-minimum-memberships.png)、[云文件完整十条与分页](../../.build/company-1793599976357/company-manager.png)、[云命令](../../.build/cloud-commands-1793600149727/commands-default.png)、[直角独立菜单](../../.build/illustrations-1793600115646/compact-default-common-menu.png)。

## 回归中定位并处理的问题

1. **窗口尺寸覆盖**：拆分后，基础层的客户区尺寸被旧主窗／编辑器／设置窗固定宽高重新覆盖。移除这些旧规则，让窗口继续跟随实际客户区，并加入独立窗口尺寸断言，没有更改原生默认／最小尺寸。
2. **长字段溢出**：管理专项在 480×440 客户区复现长联系方式溢出。行内值和共享库名不应继承动作按钮的不可收缩规则，改为可收缩、省略；文字仍用正文色，操作图标热区不缩小。修复后管理专项 20 组通过。
3. **云文件末行裁切**：模块切换迁成普通 32px 操作后，在默认 1000×650 客户区挤占列表，十条记录的末行被裁切。模块切换改用共用的 28px 紧凑规格，不缩小文字、不放松十条记录与分页的有效断言；修复后云文件专项 31 组通过。
4. **选中行焦点不可辨识**：新增样板先复现选中话术行聚焦前后都为 `filter: none`；让 `role="option"` 共用明暗反馈，同时保留选中底色和左侧标记，不追加外框或改变尺寸。七种主题逐一确认聚焦前后底色、标记和尺寸不变，且明暗反馈可辨识。

另有旧验收脚本漂移，均对照当前源码修正，而非改产品以迎合断言：关于页原图标一直是 72px；清空搜索后先退出历史遮罩；快捷键徽标先悬停再等待可见；当前搜索不展示来源标签；候选显示快照补齐现有会话／布局及测量上限字段，底栏沿用实际 29px 材料；四开关延迟保存检查拦截现有 `patch_settings`，不再等待旧的全量保存命令。候选测量保持显示样本与原生真实会话的边界。

## 复验方式与边界

构建后，逐个运行以上专项，不与共用 Debug 单实例标识的其他验收脚本并行。需要 Hub 的脚本显式提供当前本机产物，例如：

```powershell
$env:SHANYU_TEAM_SERVER_EXE = 'D:\code\QuickPhrase-hub\target\debug\quickphrase-hub.exe'
node scripts/verify-teams.mjs
node scripts/verify-company.mjs
node scripts/verify-cloud-commands.mjs
node scripts/verify-illustrations.mjs
```

- 本轮完成真实 WebView2、Rust 与隔离数据回归，CDP 伪类检查属于样式验证；候选／预览种入显示快照，不代表实体快捷键、无激活行为及实际发送已验收。
- 未完成 Windows 100%／150%／200% 系统 DPI 及多显示器验收矩阵；WebView 渲染倍率与 CDP 视口不能替代系统 DPI、跨屏和原生窗口验收。
- 真实企业微信、输入法发送、系统剪贴板成功复制、资源管理器拖拽／多选对话框、新安装和升级未在本轮实机验收。
- 未修改独立 Hub 源码，未做生产部署，也没有创建安装包或正式发布资产。
- 本轮不是正式发布，不执行发布后构建清理；保留必要的本轮报告、截图和排障材料。`.build/` 为本地可重建证据目录，不纳入提交，文档链接在目录清理后可能失效。
