# QuickPhrase SignPath Foundation 签名准备与 v0.0.1 发布设计

日期：2026-08-20
状态：已确认，待实施计划审核
分支：`codex/signpath-readiness`

## 1. 目标

为公开 MIT 项目 QuickPhrase 建立可审核、可重复、双阶段 Authenticode 签名链，并在 SignPath Foundation 审核前发布明确标记为未签名的 `v0.0.1-rc.1` Pre-release。SignPath 获批且 Windows 11 人工安装矩阵通过后，再发布签名正式版 `v0.0.1`。

本阶段不得创建正式 tag `v0.0.1`，不得把未签名资产标记为正式版，也不得把 API Token、密码或 SignPath 审批凭据写入仓库、日志或聊天。

## 2. 当前基线

已满足：

- 公共仓库 `thelinyue/QuickPhrase`。
- MIT License。
- `.NET 10 + Pure WPF + Win32/UIA + SQLite + Core 内存搜索`。
- Debug/Release 构建和完整测试。
- 独立 Native/Performance Launcher smoke。
- Inno Setup 当前用户安装器。
- SHA-256 和 release manifest 基础设施。
- 企业微信人工矩阵已由发布负责人确认。

缺口：

- 无 `PRIVACY.md`、`SECURITY.md`、`CODE_SIGNING.md`。
- 无 GitHub Actions workflow。
- 发布脚本、安装器、测试和文档硬编码 `1.0.0`。
- `verify-phase6.ps1` 仍检查旧 online/offline 安装器。
- 没有 SignPath GitHub App、组织、项目、Policy、Artifact Configuration 或 API Token。
- 没有公开 GitHub Release。
- Windows 11 安装/升级/卸载矩阵尚未获得本次正式版明确确认。

## 3. 单维护者角色

代码签名政策声明：

```text
Author / Committer
- thelinyue

Reviewer
- thelinyue

Approver
- thelinyue
```

规则：

1. 外部贡献必须通过 Pull Request，由 `thelinyue` 审核。
2. 签名 workflow、发布脚本、Inno 脚本、Artifact Configuration 和依赖更新属于高风险变更。
3. SignPath 正式签名请求必须由 `thelinyue` 在 SignPath 控制台人工批准。
4. 禁止自动批准签名请求。
5. GitHub 和 SignPath 必须启用 MFA。
6. 只签署可追溯到 GitHub-hosted Actions workflow 的 artifact。
7. 发现发布密钥、workflow 或资产来源异常时停止发布并撤销受影响 Release。

## 4. 隐私数据地图

### 4.1 默认本地模式

- 不连接 QuickPhrase 官方云服务。
- 数据保存在 `%LOCALAPPDATA%\QuickPhrase`。
- 不上传用户话术、联系人、聊天内容、窗口标题或剪贴板。
- 不集成广告、遥测或第三方分析 SDK。
- 日志不得记录话术正文、剪贴板、输入框内容、联系人或客户资料。

### 4.2 用户主动启用企业同步

应用只连接用户填写的闪语中心地址，传输：

```text
Hub 地址
企业账号
连接时密码
设备名称
客户端版本
设备 Token
同步游标和发布号
企业分类
企业话术标题及正文
```

边界：

- 密码只进入当前登录请求，不持久化。
- 设备 Token 使用 Windows DPAPI CurrentUser 保护。
- Hub 地址、账号、设备 ID、同步状态和发布号保存在本地。
- 企业分类和话术作为只读缓存写入本地 SQLite。
- 当前 Hub 只接受用户配置的 `http://` 内网地址；企业部署者负责网络隔离和传输安全。
- 日志和错误结果不得包含密码、Token 或企业话术正文。

## 5. 安全报告政策

`SECURITY.md` 声明：

- 当前受支持版本为 `0.0.1` 系列。
- 安全问题不得提交公开 Issue。
- 首选 GitHub Private Vulnerability Reporting。
- 3 个工作日内确认，7 个工作日内给出初步评估。
- 高风险范围包括错窗口投递、无授权发送、重复发送、剪贴板恢复、令牌泄露、企业认证绕过、SQLite 损坏、签名 workflow 篡改和 Release 资产替换。
- 需要撤销时删除受影响 Release 资产、发布安全公告并生成新版本，不移动或覆盖原 tag。

实现阶段尝试通过 GitHub API 启用 Private Vulnerability Reporting；若权限或 API 不支持，记录为发布负责人手工步骤。

## 6. 版本模型

### 6.1 候选版

```text
Tag                  v0.0.1-rc.1
ProductVersion       0.0.1-rc.1
FileVersion          0.0.1.0
AssemblyVersion      0.0.1.0
Release directory    artifacts/release/0.0.1-rc.1
Installer            QuickPhrase-Setup-0.0.1-rc.1-unsigned.exe
Publish archive      QuickPhrase-0.0.1-rc.1-win-x64-unsigned.zip
Release type         GitHub Pre-release
Signed               false
```

### 6.2 正式版

```text
Tag                  v0.0.1
ProductVersion       0.0.1
FileVersion          0.0.1.0
AssemblyVersion      0.0.1.0
Release directory    artifacts/release/0.0.1
Installer            QuickPhrase-Setup-0.0.1.exe
Publish archive      QuickPhrase-0.0.1-win-x64.zip
Release type         GitHub Release
Signed               true
```

候选版和正式版使用相同 AppId、安装目录、数据目录和数据库升级路径。候选版 tag 发布后不覆盖；后续修复使用 `rc.2`。

## 7. 发布脚本参数化

`QuickPhrase.Desktop.csproj` 默认版本改为：

```xml
<Version>0.0.1</Version>
<FileVersion>0.0.1.0</FileVersion>
<AssemblyVersion>0.0.1.0</AssemblyVersion>
```

`build-release.ps1` 接收：

```powershell
-Version 0.0.1-rc.1
-UnsignedCandidate
```

行为：

1. 从 SemVer 去掉 `-rc.*` 得到数值基版 `0.0.1`。
2. `FileVersion` 和 `AssemblyVersion` 使用 `0.0.1.0`。
3. 禁止 .NET 自动把 Git SHA 追加到 ProductVersion。
4. Release root 由参数决定。
5. candidate 文件名追加 `-unsigned`。
6. Inno 通过 `/DAppVersion`、`/DReleaseRoot`、`/DOutputBase` 接收参数。
7. 生成 publish ZIP、installer、SHA256SUMS 和 manifest。
8. manifest 记录 commit SHA、workflow run、signed 状态、artifact 名称和哈希。

正式签名 workflow 不直接发布 `build-release.ps1` 生成的未签名 formal intermediate；只有二次签名和验证完成后才运行 finalize 步骤生成正式 manifest。

## 8. GitHub Actions

### 8.1 `ci.yml`

触发：

```text
push
pull_request
```

Windows GitHub-hosted runner 执行：

- checkout with full history。
- setup .NET from `global.json`。
- restore。
- Debug build/test。
- Release build/test。
- Native Launcher smoke。
- Performance smoke，P95 `<=120ms`。
- 发布、版本、政策和 workflow 契约测试。

### 8.2 `release-candidate.yml`

触发：

```text
workflow_dispatch
```

职责：

1. 校验输入版本严格匹配 `0.0.1-rc.N`，并要求发布负责人确认当前企业微信人工矩阵。
2. 运行完整 CI 和 smoke。
3. 构建纯 WPF self-contained publish。
4. 构建未签名 Inno 安装器。
5. 校验 ProductVersion、FileVersion、禁止 Web/WebView2 资源。
6. 生成 ZIP、hash、manifest。
7. 使用 `actions/upload-artifact@v4` 上传候选资产。
8. 不自动创建公开 Release；公开动作由发布负责人在验证 artifact 后明确执行。

### 8.3 `release-signed.yml`

在 SignPath 未批准前仅准备，不触发正式 tag。

两阶段链：

```text
build unsigned publish
→ upload app signing input
→ signpath/github-action-submit-signing-request@v2
→ download signed app
→ verify Authenticode/timestamp
→ build installer from signed app
→ upload installer signing input
→ SignPath installer signing request
→ verify Authenticode/timestamp
→ finalize manifest/hash
→ upload formal Release assets
```

SignPath 配置从 GitHub 读取：

```text
secrets.SIGNPATH_API_TOKEN
vars.SIGNPATH_ORGANIZATION_ID
vars.SIGNPATH_PROJECT_SLUG
vars.SIGNPATH_SIGNING_POLICY_SLUG
vars.SIGNPATH_APP_ARTIFACT_CONFIGURATION_SLUG
vars.SIGNPATH_INSTALLER_ARTIFACT_CONFIGURATION_SLUG
```

workflow 不包含真实值，不打印 Token。签名 request 默认等待人工审批完成，超时不得降级为未签名发布。

## 9. 双阶段签名覆盖

### Application Artifact Configuration

签署 QuickPhrase 自有 PE：

```text
QuickPhrase.exe
QuickPhrase.dll
QuickPhrase.Core.dll
QuickPhrase.Platform.Windows.dll
```

不重新签署 Microsoft/.NET/第三方上游二进制。

### Installer Artifact Configuration

签署：

```text
QuickPhrase-Setup-0.0.1.exe
```

安装器必须从已经签名并验证的 publish 目录构建。只签安装器而不签应用文件不视为完成。

## 10. 候选 Release

准备 PR 合并且 GitHub Actions artifact 验证通过后，创建：

```text
v0.0.1-rc.1
```

公开资产：

```text
QuickPhrase-0.0.1-rc.1-win-x64-unsigned.zip
QuickPhrase-Setup-0.0.1-rc.1-unsigned.exe
SHA256SUMS.txt
release-manifest.json
```

Release 正文必须强调：

- 未签名。
- 仅用于 SignPath 审核和候选测试。
- 不是正式版。
- SmartScreen 可能警告。
- 下载者应核对 SHA-256。

发布后从 GitHub 重新下载所有资产，检查版本、文件数、禁止资源和 hash。一致后才把 Release URL 写入 SignPath 申请材料。

## 11. SignPath 申请材料

新增 `docs/signpath-application.md`，记录：

```text
Project name
Repository URL
MIT license URL
Candidate Release URL
Code signing policy URL
Privacy policy URL
Security policy URL
CI workflow URL
Maintainer/Reviewer/Approver
Application Artifact Configuration 目标
Installer Artifact Configuration 目标
```

用户必须人工完成：

1. GitHub 和 SignPath MFA。
2. 安装/授权 SignPath GitHub App。
3. 创建或加入 SignPath organization。
4. 提交 Open Source Project 申请。
5. 获批后创建 project、policy 和两个 artifact configurations。
6. 把非秘密标识写入 GitHub Variables。
7. 把 API Token 写入 GitHub Secret。
8. 人工批准每个正式签名请求。

Token 不进入聊天、commit、Actions log 或 Release asset。

## 12. 门禁

### RC 门禁

- 企业微信人工矩阵已确认。
- Debug/Release 构建和完整测试通过。
- Launcher Native/Performance smoke 通过。
- P95 `<=120ms`。
- 纯 WPF artifact 审计通过。
- 版本、文件名、manifest 和 hash 一致。
- 明确 `signed=false` 和 Pre-release。

### 正式版门禁

- SignPath Foundation 已批准项目。
- 两阶段签名通过。
- Authenticode 链和 timestamp 验证通过。
- Windows 11 安装/升级/卸载矩阵明确确认。
- 企业微信矩阵仍适用于当前 commit。
- GitHub 下载后复核通过。

SignPath 获批不等于 `PHASE6_VERIFY_PASS_WIN11`。

## 13. 非目标

本阶段不修改：

- Core 搜索算法。
- 投递状态机或企业微信 Adapter。
- 企业同步业务协议和数据库 schema。
- Launcher 正式交互。
- Web 原型/Sites 链。
- 自动更新。
- 正式 tag `v0.0.1`。

## 14. 成功标准

1. 政策和隐私描述与实际代码一致。
2. 发布链无 `1.0.0` 和 online/offline 旧约定。
3. CI 可从 clean checkout 构建和验证。
4. RC workflow 生成完整未签名资产。
5. `v0.0.1-rc.1` 为公开 Pre-release 且远端资产复核一致。
6. SignPath 申请材料完整，不包含 secret。
7. 正式签名 workflow 已准备但在批准前不能误发布。
8. 普通产品架构和安全投递行为不变。
