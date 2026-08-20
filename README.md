# 闪语（QuickPhrase）

闪语（QuickPhrase）是面向 Windows 11 的本地快捷话术工具。按 `Alt + Space` 呼出纯 WPF Launcher，搜索固定标准回复，再安全插入当前聊天输入框。

## V1.0.0 支持范围

- Windows 11 x64。
- 支持当前主流版本企业微信：不依赖客户端版本号，通过运行时目标、前台窗口和焦点/Caret 能力检测保证输入稳定性。
- 当前实际 WPF 界面包含话术库、编辑器、设置、分类管理和 Native Launcher。
- `Enter` 仅插入；`Ctrl+Enter` 是 Launcher 的通用显式发送操作。具体发送按键由 Adapter 决定，企业微信当前已验收配置使用 `Enter` 作为发送协议。后台发送、无用户授权自动发送、图片/文件话术和云同步不属于 V1。
- Windows 10 22H2 尚未作为 V1 支持平台验证。

## 正式架构

正式产品为单进程纯 WPF：

```text
QuickPhrase.Desktop
├── QuickPhrase.Core
└── QuickPhrase.Platform.Windows
        └── QuickPhrase.Core
```

正式产品不使用 WebView2、React 管理页、ManagementIpc、ManagementBridge 或网页桥接协议。仓库中的 `src/` 和 Sites 文件属于独立原型/展示链路，不是生产代码，也不作为当前 UI 参考；正式 UI 以 `desktop/QuickPhrase.Desktop` 的实际 WPF XAML、ViewModel 和交互为准。

## 安装

安装器是当前用户、纯 WPF、自包含安装包，安装到 `%LOCALAPPDATA%\Programs\QuickPhrase`，不需要管理员权限，也不需要额外安装 WebView2 Runtime。

管理数据位于 `%LOCALAPPDATA%\QuickPhrase`。卸载会移除程序，但保留 `Data`、`Backups` 与 `Logs`，重新安装后会继续使用原话术库。

本 V1 安装包未进行代码签名，Windows SmartScreen 可能显示未知发布者提示；请从可信来源核对 `SHA256SUMS.txt`。

## 安全边界

QuickPhrase 宁可不能发送，也不能发错窗口、发错内容或重复发送。目标变化、权限不一致、控件无法确认和 Clipboard 失败都会停止动作；仅插入模式可按安全策略降级为复制提示。插入或发送结果不确定时不自动重试。

## 许可证

MIT License，见 [LICENSE](LICENSE)。
