# Phase 6：Windows 11 发布验证

## 当前发布策略

QuickPhrase 0.0.2 使用纯 WPF、self-contained `win-x64` 发布包和当前用户 Inno Setup 安装器。正式版当前**未附带 Authenticode 签名**；Windows SmartScreen 可能显示未知发布者警告，发布资产必须同时提供 `SHA256SUMS.txt` 供使用者校验。

## 人工门禁状态

- `v0.0.1` 的企业微信与 Windows 11 人工矩阵只作为历史证据。
- `v0.0.2` 新增话术库投递入口及贴边窗口行为，正式发布前必须由发布负责人重新确认当前企业微信投递矩阵，以及 Windows 11 x64 安装、升级、启动、卸载保留数据、100%/150% DPI 和双显示器矩阵。
- 自动化测试、Launcher smoke 和构建成功不能替代上述真实 GUI 验收。

## 正式发布门禁

正式版 `v0.0.2` 必须从不可变 `v0.0.2` 标签手动触发 GitHub Actions 发布工作流。工作流必须：

1. 确认两项人工门禁。
2. 构建自包含 ZIP 与安装器，并生成 `SHA256SUMS.txt` 和 `release-manifest.json`。
3. 验证 manifest 声明 `signed=false` 与 `releaseChannel=stable`，并验证公开资产与 SHA-256 清单一致。
4. 验证 ZIP 包含四个 QuickPhrase 自有程序集，且不包含网页、React、WebView2 或原型资源。
5. 创建非预发布 GitHub Release，并在自动生成的 Release Notes 前说明未签名状态和 SHA-256 校验方法。

完整门禁命令：

```powershell
$env:QUICKPHRASE_WECOM_ACCEPTANCE = 'passed'
$env:QUICKPHRASE_WIN11_ACCEPTANCE = 'passed'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-phase6.ps1 -Version 0.0.2
```

命令成功后输出 `PHASE6_VERIFY_PASS_WIN11`。该标识只表示人工矩阵、未签名发布资产、哈希和纯 WPF 发布边界已验证通过，不表示任何 Authenticode 签名已存在。
