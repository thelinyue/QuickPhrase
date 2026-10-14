# 签名和发布政策

闪语当前不使用第三方代码签名服务，Rust/Tauri 开发构建及发布资产均未附带 Authenticode 签名。WebView2 官方引导程序的微软签名独立校验，不能称为闪语程序已签名。

## 资产与来源

- GitHub Release 是正式发布资产的公开来源；候选构建只上传 Actions artifact。
- 沿用已有发布要求：提供 ZIP、Inno Setup 安装器、`SHA256SUMS.txt`、`release-manifest.json` 和 `SHA256SUMS.txt.minisig`。
- manifest 如实声明 `signed: false`、版本、来源提交和发布通道；正式版通道为 `stable`，候选为 `prerelease`。
- 更新清单同时覆盖manifest，并使用Minisign签名。公钥嵌入客户端，私钥及密码只在发布Secrets和受限备份中保管；缺少或无效签名时不得通过应用内安装。此签名不等于Windows代码签名，历史无签名资产不补写。具体流程见[在线更新](docs/online-update.md)。
- Windows SmartScreen 可能显示未知发布者提示。发布说明必须明确未签名状态和 SHA256 校验方式。
- 程序嵌入实际 Tauri 前端资源；安装器可嵌入签名有效的微软 WebView2 引导程序。不能携带无关原型资源、用户数据或非预期二进制。

## 发布负责人职责

1. 正式源标签 `v<version>` 指向预期提交，版本与源码一致；既有标签和资产不可移动或覆盖。
2. 自动 CI 通过；真实企业微信、原生发送及 Windows 安装验收结果和环境缺口如实记录。发布不设置人工确认门禁，模拟测试和构建不能写作人工验收通过。
3. 核对程序与安装器版本、ZIP 内容、发布通道、manifest、SHA256 清单及 Actions 记录。
4. Release Notes 明确支持范围、未验证环境、未签名状态和安装依赖。

## 事件响应

凭据可能泄露、发布链路被篡改、来源或校验值无法核验、存在恶意软件或安全回归时立即停止发布，撤回受影响资产并按 [SECURITY.md](SECURITY.md) 处理。

恢复发布需完成凭据和策略复核，创建新版本并重新验证；不得移动既有标签或强制推送。隐私行为见 [PRIVACY.md](PRIVACY.md)。
