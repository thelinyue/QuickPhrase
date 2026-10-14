# 全界面焦点与选中外框验收

2026-10-14，源码调整，未提交或发布。

## 调整范围

共用 `src/styles.css` 移除 React／WebView 控件的额外焦点外框。按钮、复选框、单选框和账号开关使用明暗反馈；输入框、编辑区和可滚动容器使用浅色底；链接使用更明显的下划线。菜单保持方向键焦点底色。回收站库入口和云文件侧栏删除选中时的蓝色边框，保留本体边框、选中背景与品牌色。

`src/ui.tsx` 的分类色板使用现有勾号图标，按色块明度决定勾号颜色；选择、恢复默认或 Esc 关闭后焦点回到入口。黑色色块使用透明度提示键盘焦点，避免降低亮度无法辨认纯黑色。未改 API、数据结构、发送或保存逻辑。

## 自动检查

- `npm test`：199 项通过。
- `npm run check`、`npm run build`：通过。
- `npm run test:release`：12 项通过。
- `cargo test --manifest-path src-tauri/Cargo.toml`：141 项通过。
- Rust 格式检查、Clippy（所有目标、警告视为错误）、含 `custom-protocol` 的 Debug 构建：通过。
- `git diff --check -- src/styles.css src/ui.tsx`：通过。

## 隔离 WebView2 检查

焦点专项通过 48 组、714 次控件检查，报告为 `.build/focus-1791982221395/report.json`。使用实际 Tab／Shift+Tab 建立键盘焦点，逐项检查实际计算样式、控件尺寸、勾选值与选中状态；检查菜单方向键、Home／End、Esc 及焦点恢复。包含 WebView 自动进入 Tab 顺序的滚动区域。

覆盖主窗口、底栏、话术／文件编辑器、常用短语与各排序窗口、设置全部页签、登录／改密、账号／共享管理和成员页、回收站／恢复／删除确认、使用指引全部主题、云文件列表／权限／上传／下载／回收站。预览和快捷键候选通过已有事件种入显示快照，验证实际渲染，未触发真实预览唤起或实体快捷键。

标准与最小视口已复核；代表截图位于同一报告目录：`category-palette-selected.png`、`editor-minimum.png`、`settings-minimum.png`、`cloud-minimum.png`、`cloud-context-menu.png`、`password-dialog.png`、`phrase-preview-display.png`。截图已目视复核；编辑区域等原有静态蓝色本体边框按约定保留。

`node scripts/verify-company.mjs`：28 组通过，报告为 `.build/company-1791981998282/report.json`，包括个人／共享话术右键新增云文件、实际附件保存、多选、权限和原生云文件窗口最小尺寸避让。

`node scripts/verify-categories.mjs`：前 34 组通过，最后备份设置恢复断言仍失败于第 669 行，差异仍为 `dockSearch`、`teamsFirst`。报告为 `.build/category-menus-1791981913683/failure.json`。与本次之前的已知失败一致，本次未修改备份或设备设置逻辑。

0.2.2发布前复验已按个人／设备分别确认的当前导入设计修正脚本，35组全部通过，报告为 `.build/category-menus-1791983371468/report.json`。个人导入保留设备设置，设备部分独立确认后恢复，未修改产品备份逻辑或减少断言；上述失败记录仅保留历史诊断。

## 边界

专项使用隔离 WebView2／Rust／Hub 和独立数据目录，不操作正式数据，不执行聊天发送或系统剪贴板操作。各页面的最小尺寸检查使用 CDP 视口，不能替代真实 DPI 或多显示器检查。Windows 原生菜单、文件选择器和系统消息框沿用系统绘制，本次未覆盖。真实企业微信、新安装和升级也未由本次验收。

保留全部无关工作区改动，未暂存、提交、推送或发布。
