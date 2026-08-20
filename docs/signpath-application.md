# SignPath Foundation 申请与配置清单

本文档用于将 QuickPhrase 接入 SignPath Foundation。它只记录公开证据、GitHub 配置名称和人工操作步骤，不保存 API Token、密码、恢复码或真实秘密值。

## 项目资料

| 项目 | 内容 |
| --- | --- |
| 项目名称 | QuickPhrase（闪语） |
| 公开仓库 | https://github.com/thelinyue/QuickPhrase |
| 许可证 | MIT License；https://github.com/thelinyue/QuickPhrase/blob/master/LICENSE |
| 隐私政策 | https://github.com/thelinyue/QuickPhrase/blob/master/PRIVACY.md |
| 安全政策 | https://github.com/thelinyue/QuickPhrase/blob/master/SECURITY.md |
| Code signing policy | https://github.com/thelinyue/QuickPhrase/blob/master/CODE_SIGNING.md |
| 目标平台 | Windows 11 x64，.NET 10 LTS，Pure WPF |
| 当前正式版本目标 | v0.0.1 |
| 未签名候选 | v0.0.1-rc.1；公开 Pre-release URL 在候选发布后补充 |
| CI 证据 | Draft PR 和 Actions run URL 在分支推送后补充 |
| Candidate workflow 证据 | workflow run URL 在公开 RC 构建后补充 |

## 单维护者角色

当前采用 SignPath 单维护者模式：

```text
Author / Committer: thelinyue
Reviewer: thelinyue
Approver: thelinyue
```

`thelinyue` 负责源代码、审查和每次签名请求的人工批准。GitHub 与 SignPath 账户必须启用 MFA。签名请求禁止 automatic approval。

## 申请前公开证据

提交 Open Source Project 申请前确认：

1. 仓库公开，MIT `LICENSE`、`PRIVACY.md`、`SECURITY.md` 和 `CODE_SIGNING.md` 可直接访问。
2. GitHub Private Vulnerability Reporting 已启用。
3. `v0.0.1-rc.1` 作为未签名 Pre-release 发布，Release notes 明确 SmartScreen 风险及 SHA-256 核验方法。
4. CI 和候选 workflow 都来自 GitHub-hosted Windows runner，并保留对应 run URL。
5. 仓库中包含 SignPath Foundation 要求的免费签名鸣谢。

## GitHub 配置名称

只在 GitHub 仓库设置中配置以下名称，本文档不得填写对应值。

### Secret

```text
SIGNPATH_API_TOKEN
```

API Token 只写入 GitHub Actions Secret。不得在 Issue、Pull Request、聊天、日志、源码或文档中粘贴 Token。

### Variables

```text
SIGNPATH_ORGANIZATION_ID
SIGNPATH_PROJECT_SLUG
SIGNPATH_SIGNING_POLICY_SLUG
SIGNPATH_APP_ARTIFACT_CONFIGURATION_SLUG
SIGNPATH_INSTALLER_ARTIFACT_CONFIGURATION_SLUG
```

这些标识必须以 SignPath 审批通过后显示的真实值为准，不预填占位值冒充有效配置。

## SignPath 人工配置步骤

1. 为 SignPath 和 GitHub 账户启用 MFA，并安全保存恢复方式。
2. 在 SignPath 安装并授权 GitHub App，仅授予 `thelinyue/QuickPhrase` 所需范围。
3. 提交 SignPath Foundation 的 Open Source Project 申请，附上本页列出的公开证据。
4. 审批通过后创建 QuickPhrase Project 和人工审批 Signing Policy。
5. 创建应用 Artifact Configuration，只允许签名：
   - `QuickPhrase.exe`
   - `QuickPhrase.dll`
   - `QuickPhrase.Core.dll`
   - `QuickPhrase.Platform.Windows.dll`
6. 创建安装器 Artifact Configuration，只允许签名 `QuickPhrase-Setup-0.0.1.exe`。
7. 将批准后的标识写入 GitHub Variables，将 API Token 写入 `SIGNPATH_API_TOKEN` Secret。
8. 为 GitHub Environment `production-signing` 配置人工保护规则；单维护者仍必须逐次核对 tag、commit、workflow run 和 unsigned hash。

## 双阶段签名链

`.github/workflows/release-signed.yml` 只允许手动运行，并要求当前 ref 为不可移动的 `refs/tags/v0.0.1`。流程为：

1. 构建 unsigned publish，并作为 GitHub Actions artifact 提交第一次 SignPath 请求。
2. 验证四个 QuickPhrase 自有 PE 的 Authenticode 与可信时间戳。
3. 从已签名 publish 构建 unsigned Inno installer。
4. 提交第二次 SignPath 请求并验证安装器签名与可信时间戳。
5. `scripts/finalize-signed-release.ps1` 生成最终 ZIP、安装器、`SHA256SUMS.txt` 和 `release-manifest.json`。
6. `scripts/verify-phase6.ps1` 要求企业微信与 Windows 11 人工矩阵均已明确通过。

workflow 只上传 Actions artifact，不创建 tag 或 GitHub Release。正式 Release 仍需单独展示最终资产和哈希并获得用户确认。

## 审批与发布阻塞项

在以下条件全部完成前，不运行正式签名 workflow，也不发布 `v0.0.1`：

- SignPath Foundation 已批准项目。
- GitHub App、Secret、Variables、Signing Policy 和两个 Artifact Configuration 已按实际值配置。
- 企业微信人工验收矩阵仍为通过状态。
- Windows 11 安装、升级、启动、卸载保留数据矩阵已明确通过。
- 正式 tag、commit、CI、unsigned input hash 和最终 signed asset hash 已由 Approver 核对。

SignPath 项目审批只代表签名服务可用，不等于 Windows 11 Phase 6 人工验收通过。

## 发布后回填

公开 `v0.0.1-rc.1` 后回填：

```text
Candidate Release URL:
Candidate workflow run URL:
CI workflow run URL:
Tag commit:
Unsigned ZIP SHA-256:
Unsigned installer SHA-256:
```

不要修改或移动已经公开的候选 tag；发现问题时使用新的 `v0.0.1-rc.2` 计划。