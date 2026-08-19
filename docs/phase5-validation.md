# QuickPhrase Phase 5 验证记录

状态：`PHASE5_INFRA_PASS`；完整企业微信人工矩阵尚未全部确认，因此暂不写入 `PHASE5_VERIFY_PASS`。

## 已冻结的实现边界

- 唯一正式 Adapter：企业微信 `WXWork 5.0.9.6065`。
- 正常模式能力：`InsertText=Verified`、`VerifyInsert=Unverified`、`SendText=Unsupported`、`VerifySend=Unsupported`。
- 企业微信始终使用受保护 Clipboard Transaction + `Ctrl+V`，忽略全局兼容模式关闭状态；不开放 Unicode 直输、后台发送或自动发送。
- 非企业微信、未知版本、目标变化、非聊天控件和无法确认的插入统一 Copy Only，并提示 `Ctrl + V`。
- `VerifyInsert` 保持 `Unverified` 是有意的安全结论：插入后不自动发送、不自动重试。

## 自动化验证

| 项目 | 结果 |
| --- | --- |
| Debug/Release build | 通过，零警告 |
| Debug/Release tests | 通过，61/61（含 Phase 5.1 队列、启动握手与企业微信焦点恢复回归测试） |
| 精确版本 Profile 与 ProductVersion Trace | 通过 |
| 控件指纹正负样本（聊天区/顶部搜索区） | 通过 |
| x64 `INPUT` 结构 | 通过，`KeyboardInput=40`、`KeyboardInputData=32` |
| UIA MTA、Clipboard STA、目标重校验、并发闸门 | 通过 |
| Clipboard 操作超时与取消传播 | 通过；单次操作最多等待 3 秒，避免 `DELIVERY_BUSY` 长时间占用 |
| Insert 验证不确定时禁止发送/重试 | 通过 |
| Launcher 隐藏后企业微信焦点/Caret 延迟恢复 | 通过；条件轮询最多 500ms，不重复粘贴 |
| DeliveryTrace 脱敏与实际版本记录 | 通过 |
| React/Sites/Phase 4 回归 | 待完整门禁复跑 |

## 人工矩阵

### 已准备的验收话术

已在当前用户数据目录创建 4 条固定测试话术，均未绑定快捷键，分类为“信息收集”，可在 Launcher 搜索“验收”：

- `【验收】草稿中间插入`
- `【验收】多行标点 Emoji`
- `【验收】剪贴板保护`
- `【验收】短句`

日志时间字段 `timestampUtc` 按架构固定使用 UTC，并带 `+00:00` 偏移；东八区显示需加 8 小时。日志只用于按 `traceId` 对照结果，不记录话术正文、标题、联系人、窗口标题或输入框文本。

| 场景 | TraceId/证据 | 当前结论 |
| --- | --- | --- |
| 空输入框 | `40953301-aacd-4c93-9f28-890dd62d7da8`；用户确认输入框出现话术 | 通过；未发送 |
| 焦点恢复后的真实插入 | `7480cd68-b460-47d7-94e4-7c48427eb849`；`FINGERPRINT_READY`、`CLIPBOARD_PASTED`；输入框确认出现“请提供设备序列号（SN），方便我们进一步确认设备信息。” | 通过；`VerifyInsert=Unverified`，未发送 |
| 顶部搜索框负样本 | `c6d620a2-82c2-40e3-af35-46abe6761528`、`8a017398-5e39-4e1e-9a14-86796309834a`、`dba5eb35-d514-4d70-ad85-f3214043048e`；`TARGET_CONTROL_PROFILE_MISMATCH` | 通过；用户确认搜索框未出现话术 |
| 草稿中间插入 | `bfabb262-001d-473e-9a0c-477abe500b46`；输入框确认“前缀 + 话术正文 + 后缀” | 通过；只插入一次，未发送 |
| 多行、中文标点和 emoji | `07cd7dc0-12c2-4116-a9c2-060a4b2a6760`；输入框确认三行中文、标点和 Emoji | 通过；只插入一次，未发送 |
| Launcher 捕获后切换窗口 | 用户确认切换到其他窗口后真实目标取消，未向新窗口投递 | 通过；返回目标变化取消 |
| 企业微信退出/重启后的旧 Target | 用户确认企业微信退出或重启后旧目标失效，未重定向到新窗口 | 通过；安全拒绝 |
| 权限级别不一致（UIPI） | 用户确认普通权限 QuickPhrase 对管理员权限企业微信未绕过权限边界；未自动发送、未重试 | 通过；安全降级 |
| Clipboard 期间用户产生新复制内容 | 用户确认并发复制后保留用户新剪贴板内容，未被旧话术恢复覆盖 | 通过；序列号保护生效 |
| 连续快速触发/取消 | Phase 5.1 自动化覆盖 1+4 FIFO、满载和目标变化取消；用户曾观察“上一次话术还在准备中” | 基础设施通过；现改为队列状态“处理中 1 · 等待 N”，真实企业微信突发矩阵待补采 |
| `Ctrl+Enter` | 用户确认未发送消息 | 通过；只执行插入流程 |
| 关闭全局兼容模式 | — | 代码保证仍走 Clipboard；待用户确认 |

人工测试必须使用不会发送消息的测试会话。每项都要由用户确认输入框实际内容或确认负样本没有收到话术；出现不确定结果时停止，不重复动作。

## 复跑命令

普通程序启动即可执行正式路径，不再使用临时验收旁路：

```powershell
dotnet run -c Release --project desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj
```

完整人工矩阵全部确认后才运行：

```powershell
$env:QUICKPHRASE_WECOM_ACCEPTANCE = "passed"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-phase5.ps1 -IncludeDesktopSmoke -IncludeWeComAcceptance
```

脚本中的 `-IncludeWeComAcceptance` 只作为人工确认门禁，不接管真实窗口。未设置确认变量时，脚本不得打印 `PHASE5_VERIFY_PASS`。

## 阶段结论

代码和自动化安全基础已完成；人工矩阵尚有未确认项目，当前不能声称企业微信验收全部通过，也不应进入 Release 阶段。完整矩阵通过后，下一执行项为 `Phase 6 — Release`。
