# 2026-11-12 闪云分栏与首次读取反馈

本次为客户端源码改动，版本仍为 0.2.15，未正式发布。使用现有 React 依赖，Rust IPC、Hub 协议和持久化配置不变；隔离验收未替换用户安装版或连接生产闪云。

## 交互

- 云命令双栏默认列表占 34%，列表与正文最小宽度分别为 240／320 DIP。12 DIP 拖动热区叠放在交界处，保留两列布局和独立滚动；弹性仅作用于把手。
- 左右键每次调整 16 DIP，Shift＋左右键调整 48 DIP，Home／End 到达边界；双击或非拖动时 Esc 恢复默认。拖动时 Esc／指针取消恢复拖动前比例，松手、失焦、丢失捕获、进入窄窗结束拖动。
- 当前窗口切换模块和缩放时保留比例，重新打开恢复默认。900 DIP 及以上启用拖宽，低于 900 DIP 沿用列表／详情切换。聚焦以主题浅底、主色和显现把手反馈，不增加外框。
- 云文件、云命令各使用一处 120×120 DIP、20×20 点阵，只在当前账号会话首次成功读取目录前的真实请求期间显示。空目录也算成功；失败停止，刷新重试可继续。后续筛选／刷新使用普通文案，上传下载保留真实进度。
- 加载文案使用 `role="status"`，不放入会延后播报的忙碌父区域。隐藏模块或不可见页面停止动画帧；减少动态效果时保留静态点阵和中文说明。卸载清理动画帧、观察者及监听。

## 来源

通过 LeiBox 技能下载两项完整 React 源包，再裁剪其拖拽、弹簧和点阵计算以适配已有 React 窗口及主题。完整下载副本位于 `.build/leibox-cloud-source`；没有引入演示场景、Shadow DOM 运行层或额外 npm 依赖。

- [Elastic Panel Resize](https://leibox.pantoria.cn/docs/animations/elastic-panel-resize)：LeiBox 独立 DOM／SVG 重建，设计参考 [Alvish](https://x.com/alvish/status/2111084319716647114/video/1)，原作者源码未提供。
- [Dot Grid Loader](https://leibox.pantoria.cn/docs/animations/ai-generating)：LeiBox 组件源码。

适配代码保留来源声明与设计参考署名，不从预览或参考链接推断额外许可。

## 本轮验证

- 前端完整复验 `npm test -- --maxWorkers=4`：37 个文件、426 项通过；[日志](../../.build/leibox-frontend-tests.log)。TypeScript、[前端构建](../../.build/leibox-frontend-build.log)通过。
- Rust 格式检查、[231 项测试](../../.build/leibox-rust-tests.log)、[Clippy](../../.build/leibox-clippy.log)和[含 custom-protocol 的 Windows Debug 构建](../../.build/leibox-client-build.log)通过。隔离闪云由当前本地源码构建，未修改其工作区。
- 最终构建的[云命令 WebView2 专项](../../.build/cloud-commands-1794449580586/report.json) 10 项通过，包含新增点阵检查以及分栏拖动、键盘、七主题焦点、900／899 DIP、最小窗口、长中文与长正文回归。
- 最终构建的[完整云文件回归](../../.build/company-1794449595195/report.json) 31 项通过，覆盖上传下载、取消、权限、回收、个人附件副本、同步缓存和切号后的迟到响应。

默认并发完整运行曾有一次未改动的 `TeamManagement.test.tsx`“删除恢复后清除旧草稿，旧保存失败不能覆盖新生命周期”用例在等待保存回调时超时：[失败日志](../../.build/leibox-frontend-default-concurrency-failure.log)。该文件[单独复验 25 项通过](../../.build/leibox-team-management-recheck.log)，限制 4 个 worker 的完整复验 426 项通过。没有修改共享管理实现、测试或全局测试配置；时序不稳定的根因未完全确定，复跑通过不代表该问题已经修复。

本轮截图：[双栏与把手](../../.build/cloud-commands-1794449580586/commands-default.png)、[760×480 详情](../../.build/cloud-commands-1794449580586/commands-minimum.png)、[文件目录首次读取](../../.build/cloud-commands-1794449580586/files-initial-loading.png)、[命令目录减少动态效果](../../.build/cloud-commands-1794449580586/commands-initial-loading-reduced.png)。

## 验收边界

新增前端用例覆盖键盘、拖动边界、取消和失焦、窄窗与模块切换、无障碍数值边界、首次空目录、失败重试、切号与旧响应，以及动画暂停和卸载清理。实际 WebView2 专项通过延迟真实 Rust IPC 回执核对首次加载，在七主题、900／899 DIP 和 760×480 窗口中检查布局、焦点与滚动。

真实企业微信接收、实体键鼠和资源管理器拖拽、系统文件对话框、Windows 10、多屏系统 DPI、安装升级及真实局域网／HTTPS 代理不属于本轮隔离环境验证结果。云命令专项不改写系统剪贴板，复制成功仍需单独实机确认。
