# QuickPhrase Phase 5.1 验证记录

状态：`PHASE5_1_INFRA_PASS`。连续投递和启动性能自动化门禁通过；当前主流版本企业微信的运行时能力人工矩阵仍按 Phase 5 门禁管理，未在本轮重复宣称通过。

## 已实施边界

- 企业微信不使用客户端版本门禁；版本号仅作为脱敏诊断信息，能力由目标、前台窗口和焦点/Caret 运行时检查决定。
- 已验证插入路径先做 Win32 caret 指纹，再进入受序列号保护的 Clipboard + Ctrl+V；不回退到 UIA 扫描。
- 新增 1 条执行、4 条等待的有界 FIFO，且只接受 `InsertOnly`。`InsertAndSend` 不进入队列；目标变化取消同目标剩余队列，不 Copy Only、不重定向。
- 未识别应用不进入连续队列，仍为单次 Copy Only；全局投递闸门仍保证系统输入不并行。
- 使用次数写入移出投递关键路径，容量 128 的后台单写队列在退出时排空。
- 管理窗口先显示原生骨架；正式 WebView 通过独立 `management.html` bundle 加载，React 首次数据读取完成后发送 `system.ready` 才切换。

## 自动化证据

| 项目 | 结果 |
| --- | --- |
| Release build | 通过，0 warning / 0 error |
| Release tests | 历史记录 61/61；本次功能改造后的完整结果见 Phase 5/6 文档 |
| 队列 FIFO、1+4 容量、满载、目标变化取消 | 通过 |
| Launcher 单次 Enter 保护 | 通过 |
| 使用次数入队不阻塞下一条 | 通过 |
| TargetChangeBehavior=Cancel 不触碰剪贴板 | 通过 |
| `system.ready` IPC | 通过 |
| management bundle 不含演示壁纸引用 | 通过 |
| React build / Sites | 通过，Sites 4/4 |
| 浏览器 QA | `QA_PASS`，consoleErrors=[] |
| Native Launcher smoke | 通过，退出码 0 |
| WebView2 lifecycle smoke | 通过，退出码 0；收到 `system.ping` 后关闭并通过 BrowserProcessExited |
| StartupTrace smoke | 原生首帧约 490ms、Environment 约 499ms、Controller 约 732ms、Ping 约 2765ms（首次冷启动，仍需后续多轮 P50/P95/P99 采样） |
| Launcher 热呼出 200 次 | 最终 Release smoke：P50 5.639ms，P95 7.011ms，P99 11.513ms |

## 性能口径

本轮没有伪造当前主流版本企业微信的真实人工矩阵。单条真实执行 P95≤300ms、管理窗口冷/热 ready P50/P95/P99 和 10,000 条数据初始化门禁需在后续 Windows 验收环境补采；当前 Release smoke 已确认 Launcher P95 远低于 120ms，WebView2 生命周期可重复通过。

## 复跑

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-phase51.ps1 -IncludeDesktopSmoke
```

通过后下一项仍为 Phase 5 的剩余人工安全矩阵；企业微信矩阵全部确认后才进入 `Phase 6 — Release`。
