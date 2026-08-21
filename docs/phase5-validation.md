# QuickPhrase Phase 5 验证记录

状态：`PHASE5_VERIFY_PASS`。当前主流版本企业微信完整人工矩阵已由发布负责人明确确认通过；自动化测试不替代真实 GUI 验收。

## 已冻结的实现边界

- 唯一正式 Adapter：企业微信；兼容目标为当前主流版本，不设置客户端版本门禁。
- 运行时能力：`InsertText=Verified`、`VerifyInsert=Verified`、`SendText=Verified`、`VerifySend=Unsupported`。
- 企业微信始终使用受保护 Clipboard Transaction + `Ctrl+V` 插入；`VerifyInsert` 只确认动作完整且目标、前台窗口、输入焦点/Caret 指纹稳定，不读取正文。
- Launcher 的 `Enter` 为通用 `InsertOnly`，`Ctrl+Enter` 为通用 `InsertAndSend`；Launcher 手势不等同于目标按键协议，企业微信 Adapter 在发送前重校验后按当前已验收配置注入一次 `Enter`。
- `InsertAndSend` 遇到不支持发送的目标时不插入、不发送、不降级；`InsertOnly` 才允许安全 Copy Only。连续投递队列只接受 `InsertOnly`。
- `VerifySend=Unsupported` 不阻止显式发送动作；完整注入返回 `SendTriggered`，不声明目标应用最终 `Sent`。部分注入或状态不确定返回 `Unknown + Unknown`，不重试。
- 版本号仅写入不包含用户内容的诊断 Trace；空版本号或读取失败产生相同能力判断。

## 自动化验证

| 项目 | 结果 |
| --- | --- |
| Debug/Release build | 通过，零警告 |
| Debug/Release tests | 本次通过：Architecture 213/213，Desktop 199/199 |
| 多版本、空版本、读取失败与 ProductVersion 诊断 Trace | 通过；能力判断不受版本号影响 |
| 控件指纹正负样本（聊天区/顶部搜索区） | 通过 |
| x64 `INPUT` 结构 | 通过，`KeyboardInput=40`、`KeyboardInputData=32` |
| UIA MTA、Clipboard STA、目标重校验、并发闸门 | 通过 |
| Clipboard 操作超时与取消传播 | 通过；单次操作最多等待 3 秒，避免 `DELIVERY_BUSY` 长时间占用 |
| 插入动作/焦点稳定性验证；目标或焦点变化时保留 `Inserted` 且禁止发送 | 通过 |
| Launcher 隐藏后企业微信焦点/Caret 延迟恢复 | 通过；条件轮询最多 500ms，不重复粘贴 |
| 通用 `InsertAndSend`、一次发送调用、`SendTriggered`/Unknown 结果与 DeliveryTrace 脱敏 | 通过 |
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
| 焦点恢复后的真实插入（历史证据） | `7480cd68-b460-47d7-94e4-7c48427eb849`；`FINGERPRINT_READY`、`CLIPBOARD_PASTED`；输入框确认出现“请提供设备序列号（SN），方便我们进一步确认设备信息。” | 旧实现通过且未发送；仅保留为插入历史证据，不替代新运行时能力矩阵 |
| 顶部搜索框负样本 | `c6d620a2-82c2-40e3-af35-46abe6761528`、`8a017398-5e39-4e1e-9a14-86796309834a`、`dba5eb35-d514-4d70-ad85-f3214043048e`；`TARGET_CONTROL_PROFILE_MISMATCH` | 通过；用户确认搜索框未出现话术 |
| 草稿中间插入 | `bfabb262-001d-473e-9a0c-477abe500b46`；输入框确认“前缀 + 话术正文 + 后缀” | 通过；只插入一次，未发送 |
| 多行、中文标点和 emoji | `07cd7dc0-12c2-4116-a9c2-060a4b2a6760`；输入框确认三行中文、标点和 Emoji | 通过；只插入一次，未发送 |
| Launcher 捕获后切换窗口 | 用户确认切换到其他窗口后真实目标取消，未向新窗口投递 | 通过；返回目标变化取消 |
| 企业微信退出/重启后的旧 Target | 用户确认企业微信退出或重启后旧目标失效，未重定向到新窗口 | 通过；安全拒绝 |
| 权限级别不一致（UIPI） | 用户确认普通权限 QuickPhrase 对管理员权限企业微信未绕过权限边界；未自动发送、未重试 | 通过；安全降级 |
| Clipboard 期间用户产生新复制内容 | 用户确认并发复制后保留用户新剪贴板内容，未被旧话术恢复覆盖 | 通过；序列号保护生效 |
| 连续快速触发/取消 | Phase 5.1 自动化覆盖 1+4 FIFO、满载和目标变化取消；发布负责人确认真实企业微信突发场景通过 | 通过；队列状态为“处理中 1 · 等待 N”，未重复投递或误发送 |
| `Ctrl+Enter` 显式发送 | 发布负责人确认当前通用 `InsertAndSend` 人工矩阵通过；旧实现“未发送”只保留为历史证据 | 通过；确认/取消、快捷发送模式、已有草稿、焦点变化和异常中断均按安全边界验收 |
| 关闭全局兼容模式 | 发布负责人确认关闭后仍按受保护 Clipboard 路径执行 | 通过；未开放 Unicode 直输或后台发送 |

人工测试必须使用可控且允许发送测试消息的专用会话。执行 `Ctrl+Enter` 前必须确认目标与已有草稿，记录确认/取消、是否触发快捷操作及最终人工观察；QuickPhrase 不读取正文，也不能以自动化结果声明目标应用最终发送成功。出现不确定结果时停止，不重复动作。

2026-08-21 状态同步：发布负责人已明确确认上述当前实现的完整企业微信人工矩阵通过。该结论只代表人工观察符合安全预期，不改变 `VerifySend=Unsupported`，也不允许把 `SendTriggered` 宣称为目标应用最终 `Sent`。

## 复跑命令

普通程序启动即可执行正式路径，不再使用临时验收旁路：

```powershell
dotnet run -c Release --project desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj
```

完整人工矩阵已确认；需要复核门禁时运行：

```powershell
$env:QUICKPHRASE_WECOM_ACCEPTANCE = "passed"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-phase5.ps1 -IncludeDesktopSmoke -IncludeWeComAcceptance
```

脚本中的 `-IncludeWeComAcceptance` 只作为人工确认门禁，不接管真实窗口。未设置确认变量时，脚本不得打印 `PHASE5_VERIFY_PASS`。

## 阶段结论

代码路径、自动化安全基础和当前主流版本企业微信人工矩阵均已通过，阶段状态为 `PHASE5_VERIFY_PASS`。Windows 11 人工矩阵也已由发布负责人另行确认通过；最终 Phase 6 标记仍必须等待正式签名资产验证和新的发布批准。
