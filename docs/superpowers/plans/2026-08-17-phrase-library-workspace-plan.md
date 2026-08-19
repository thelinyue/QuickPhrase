# 闪语话术库独立工作区改造计划

## Global Constraints

- 话术库负责话术完整编辑流程；管理后台只保留设置、快捷键、适配器和投递安全配置。
- 贴边话术库每次启动默认收起，展开后进入完整话术工作区，窗口基线 `1200×760`、最小 `900×560`。
- 浮动 Native Launcher 无可见标题，呼出后第一控件是自动聚焦的加宽搜索框；单击选中、双击和 `Enter` 安全插入。
- 直接发送只有在 Adapter/Profile 验证通过时才显示；当前企业微信发送能力保持禁用。
- V1 只实现纯文本话术与 `ColorKey`；媒体、图片、文件和复杂富文本不实现。
- Core 不引用 Windows；Native Host 独占目标捕获、Adapter 和投递安全校验；React 只负责话术库/设置显示和交互。

## Tasks

1. 以测试先行扩展 `Phrase` 的 `ColorKey`、数据库 migration、仓储和 IPC DTO，旧数据默认为 `default`。
2. 将 React 话术库和编辑器从管理后台表面拆出为独立 Phrase Library 工作区；设置表面只呈现设置。
3. 添加客服宝式话术库操作层级：分类树、彩色列表、底部搜索、右键菜单、排序/移动弹窗、完整编辑和未保存保护。
4. 调整托盘与宿主入口，使新建话术进入 Phrase Library，设置进入 Settings。
5. 调整 Native Launcher：无标题即搜索、双击结果安全插入、无匹配时进入 Phrase Library 编辑流程。
6. 执行构建、管理界面 QA、Core/Desktop 测试和 Native Launcher 验收。

## Verification

- `npm run build:management`
- `npm run qa:management`
- `dotnet test QuickPhrase.sln --no-restore`
- 视觉 QA 覆盖 `1200×760` 和窄屏；人工检查 Launcher 无标题、自动聚焦、双击只执行一次安全插入。
