# Phase 6：Windows 11 发布验证

## 当前发布策略

QuickPhrase 0.0.1 使用纯 WPF、self-contained `win-x64` 发布包和当前用户 Inno Setup 安装器。正式版当前**未附带 Authenticode 签名**；Windows SmartScreen 可能显示未知发布者警告，发布资产必须同时提供 `SHA256SUMS.txt` 供使用者校验。

## 已确认的人工门禁

- 企业微信当前主流版本的人工投递矩阵已由发布负责人确认通过。
- Windows 11 x64 的安装、升级、启动、卸载保留数据矩阵已由发布负责人确认通过。
- 当前候选构建 `v0.0.1-rc.1` 仅作为历史候选验证记录，不代表正式版签名状态。

## 正式发布门禁

正式版 `v0.0.1` 必须从不可变 `v0.0.1` 标签手动触发 GitHub Actions 发布工作流。工作流必须：

1. 确认两项人工门禁。
2. 构建自包含 ZIP 与安装器，并生成 `SHA256SUMS.txt` 和 `release-manifest.json`。
3. 验证 manifest 声明 `signed=false` 与 `releaseChannel=stable`，并验证公开资产与 SHA-256 清单一致。
4. 验证 ZIP 包含四个 QuickPhrase 自有程序集，且不包含网页、React、WebView2 或原型资源。
5. 创建非预发布 GitHub Release，并在自动生成的 Release Notes 前说明未签名状态和 SHA-256 校验方法。

完整门禁命令：

```powershell
$env:QUICKPHRASE_WECOM_ACCEPTANCE = 'passed'
$env:QUICKPHRASE_WIN11_ACCEPTANCE = 'passed'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-phase6.ps1 -Version 0.0.1
```

命令成功后输出 `PHASE6_VERIFY_PASS_WIN11`。该标识只表示人工矩阵、未签名发布资产、哈希和纯 WPF 发布边界已验证通过，不表示任何 Authenticode 签名已存在。
