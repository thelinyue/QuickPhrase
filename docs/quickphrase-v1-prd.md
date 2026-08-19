# QuickPhrase V1.0 产品需求文档

状态：与 Architecture v1.1 对齐  
产品名称：闪语（QuickPhrase）  
正式技术：`.NET 10 + Pure WPF + Win32/UIA + SQLite + Core 内存搜索`

> 正式产品以当前实际 WPF 界面和交互为准。仓库中的 Web/React 原型仅是独立展示资产，不作为本 PRD 的视觉或架构依据。

## 1. 产品定义

闪语是 Windows 本地快捷话术工具。用户通过 `Alt + Space` 呼出 WPF Launcher，搜索固定标准回复，并将内容安全插入当前目标应用的输入区。

核心安全目标：宁可不能发送，也不能发错窗口、发错内容或重复发送。

## 2. V1 范围

### P0

- WPF MainWindow：当前实际话术库、编辑器、设置和导航确认流程。
- WPF Launcher：`Alt + Space` 呼出、搜索、方向键、Enter、Esc、单击选中、双击安全插入。
- 话术：纯文本正文、标题、分类、二级分类、标签、收藏、排序、稳定 ColorKey、快捷键。
- SQLite 本地持久化、事务 migration、备份、单写者和内存搜索索引。
- 企业微信版本适配、目标窗口重校验、Clipboard Transaction、投递 Trace 和安全降级。
- 单实例、托盘、开机启动、升级备份和当前用户安装。

### 明确不做

插件、AI、团队共享、云同步、图片/文件话术、复杂富文本、浏览器扩展、跨平台、后台目标发送和自动更新进入 V2 Backlog。

## 3. 正式 UI 基线

当前实际界面是唯一参考：

```text
desktop/QuickPhrase.Desktop/MainWindow.xaml
├── TitleBar
└── ContentRegion
    ├── Views/LibraryView.xaml
    ├── Views/EditorView.xaml
    └── Views/SettingsView.xaml

desktop/QuickPhrase.Desktop/LauncherWindow.xaml
```

MainWindow 为 `1200×760`，最小 `900×560`。话术库、编辑器、设置、分类对话框、移动对话框、未保存导航确认和 Launcher 的现状以代码为准，不引入旧原型中的假桌面、演示壁纸、任务栏或调试控件。

## 4. 数据模型

### Phrase

```text
id                  UUID
标题                 1–80 字，必填
正文                 1–4000 字，必填
categoryId          必填
tags                0–10 个，去重
favorite            bool
shortcutMode       none | quick | custom
shortcutDisplay     可空
shortcutNormalized  可空，规范化冲突键
colorKey            稳定颜色键，旧数据迁移为 default
usageCount          int
lastUsedAt          UTC，可空
createdAt           UTC
updatedAt           UTC
```

标签和分类名称去除首尾空格；标签大小写和全角差异不产生重复。删除话术需要确认；删除非空分类前必须先移动其中话术。

### Category / Settings

分类支持创建、重命名、移动和非空删除保护，最多二级树。设置保存在本地 SQLite，不上传网络。

## 5. 搜索规则

查询进入 Core 后执行 Unicode 规范化、大小写归一、空白清理和全角半角归一。排名固定为：

1. 标题精确、前缀、包含。
2. 标签精确、前缀、包含。
3. 拼音首字母前缀和包含。
4. 拼音全拼前缀和包含。
5. 正文包含。
6. 有限模糊匹配。

同分时按 `usageCount`、`lastUsedAt`、`updatedAt` 和标题稳定排序。搜索只访问 Core 内存快照，不查询 SQLite；DB Commit 后才更新索引。

## 6. 投递规则

### Target

Core 使用平台无关的 `DeliveryTarget`。HWND、PID、WindowThreadId、ProcessStartTimeUtc、ProcessName 和 UIA 上下文只存在于 Platform.Windows。

目标由 Launcher 呼出时捕获，但动作执行前必须重新验证：

```text
CaptureTarget
→ ValidateTarget
→ ResolveAdapter
→ DetectCapabilities
→ Insert
→ VerifyInsert
→ RevalidateBeforeSend
→ OptionalSend
→ VerifySend
```

### Insert

`Enter` 执行安全插入。目标变化、能力未验证、UIA 失败或 Clipboard 失败时停止动作或降级为复制提示。

### Send

V1 不开放后台目标自动发送。只有未来具体 Adapter/Profile 同时满足以下条件时，才允许在用户明确开启后发送：

```text
目标身份有效
Adapter Profile 匹配
SendText = Verified
Insert 成功且已可靠验证
发送前 Target 仍在前台
```

### 不确定结果

`DeliveryResult` 正交表达 `Status`、`Effect`、`Stage`、`Confidence`、`ErrorCode`、`Message`、`Retryable` 和 `TraceId`。

插入或发送动作开始后结果不确定时返回 `Unknown + Unknown`，不自动重试，不重复粘贴，不重复发送。

## 7. 企业微信 Adapter 能力矩阵

当前精确 Profile：`WXWork 5.0.9.6065`。

| 能力 | 状态 |
|---|---|
| 文本插入 | Verified |
| 插入验证 | Unverified |
| 自动发送 | Unsupported |
| 发送验证 | Unsupported |

企业微信固定使用受保护 Clipboard + `Ctrl+V`，不开放 Unicode 直输、后台投递和自动发送。

## 8. 首次使用与错误状态

首次启动展示核心价值、`Alt + Space` 和“试一下”；试用使用当前 WPF Launcher 和本地示例数据。

正式错误码至少包括：

```text
TARGET_CHANGED
TARGET_VALIDATION_FAILED
CAPABILITY_UNVERIFIED
INSERT_FAILED
INSERT_VERIFICATION_FAILED
INSERT_VERIFICATION_INCONCLUSIVE
SEND_FAILED
SEND_VERIFICATION_FAILED
SEND_VERIFICATION_INCONCLUSIVE
DELIVERY_CANCELLED
DELIVERY_TIMEOUT
CLIPBOARD_FAILED
DATABASE_BUSY
MIGRATION_FAILED
SEARCH_INDEX_DIRTY
HOTKEY_CONFLICT
```

用户可见错误使用中文；日志包含 TraceId、阶段、结果码和耗时，但不得记录话术正文、剪贴板、输入框、聊天、联系人或客户资料。

## 9. 性能与平台验收

- 主要平台：Windows 11 x64。
- Windows 10 22H2：`UNVERIFIED / NOT SUPPORTED IN V1.0.0`。
- Launcher 热呼出 P95 ≤ 120ms。
- 一万条话术搜索 P95 ≤ 50ms。
- 稳定空闲五分钟平均 CPU ≤ 0.1%。
- 稳定空闲五分钟不产生周期性持久化写入。
- 发布目录为纯 WPF 自包含产物，不包含网页资源或 WebView2 Runtime 安装器。

## 10. 安装与数据

按当前用户安装到 `%LOCALAPPDATA%\Programs\QuickPhrase`，不需要管理员权限。数据位于 `%LOCALAPPDATA%\QuickPhrase`；卸载保留 `Data`、`Backups` 和 `Logs`，重装后继续使用原话术库。

## 11. V1 冻结后的 Backlog

插件、AI、团队共享、云同步、文件/图片话术、浏览器扩展、跨平台、后台发送和自动更新不修改 V1 项目边界和安全原则。
