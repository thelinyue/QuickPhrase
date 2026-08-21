# Code signing policy

QuickPhrase 当前不使用第三方代码签名服务，也不为 0.0.1 发布 Authenticode 签名资产。

## 发布资产与风险提示

- GitHub Release 是正式发布资产的唯一公开来源。
- 每次稳定版发布都必须提供 ZIP、Inno Setup 安装器、`SHA256SUMS.txt` 和 `release-manifest.json`。
- `release-manifest.json` 必须声明 `signed: false` 与 `releaseChannel: stable`；不得将未签名资产描述为已签名或已时间戳。
- Windows SmartScreen 可能显示未知发布者警告。使用者应在安装前核对下载资产与 `SHA256SUMS.txt` 的 SHA-256 值。

## 发布负责人职责

发布负责人必须在创建正式 Release 前核对：

1. 源标签 `v<version>` 指向预期且不可变的提交。
2. CI、测试、Launcher smoke、企业微信人工矩阵与 Windows 11 安装矩阵均已通过。
3. 资产不包含 WebView2、React、原型网页资源或非预期二进制文件。
4. ProductVersion、FileVersion、文件名、发布通道和 manifest 内容正确。
5. SHA-256 清单与公开资产一致，GitHub Actions 运行记录可追溯。
6. Release Notes 明确说明未签名状态与 SHA-256 校验方式。

## 事件响应

出现以下任一情况时立即停止发布并撤回受影响 Release：

- GitHub 凭据可能泄露，或工作流、发布脚本、安装器配置发生非预期变更。
- 发布来源、资产哈希或构建产物无法核验。
- 怀疑存在恶意软件、未授权二进制文件或安全回归。

恢复发布需要完成凭据轮换、策略复核、创建新版本并重新执行完整验证。既有标签不得移动或强制推送。

## 隐私

项目隐私政策见 [PRIVACY.md](PRIVACY.md)，安全报告流程见 [SECURITY.md](SECURITY.md)。
