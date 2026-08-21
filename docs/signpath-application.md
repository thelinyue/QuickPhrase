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
| 未签名候选 | v0.0.1-rc.1；https://github.com/thelinyue/QuickPhrase/releases/tag/v0.0.1-rc.1 |
| Candidate workflow 证据 | https://github.com/thelinyue/QuickPhrase/actions/runs/32404984862 |
| CI 证据 | https://github.com/thelinyue/QuickPhrase/actions/runs/32404969586 |
| Readiness PR | https://github.com/thelinyue/QuickPhrase/pull/2 |

## 申请证据快照

以下证据于 2026-08-21 核对，供 SignPath Foundation Open Source Project 申请使用：

| 证据 | 已核对内容 |
| --- | --- |
| 源代码修订 | `b8c2e66bfc6b54f2dd13124cf7ec4eaae3d13e78` |
| 候选发布 | `v0.0.1-rc.1`，公开、非 Draft、Pre-release |
| 候选构建 | `release-candidate-build` run `32404984862`，结论 `success`，head SHA 与源代码修订一致 |
| 主分支 CI | `windows-ci` run `32404969586`，结论 `success`，head SHA 与源代码修订一致 |
| Actions artifact | ID `9420086047`；名称 `QuickPhrase-0.0.1-rc.1-unsigned` |
| 未签名应用包 | `QuickPhrase-0.0.1-rc.1-win-x64-unsigned.zip` |
| 未签名安装器 | `QuickPhrase-Setup-0.0.1-rc.1-unsigned.exe` |

候选资产哈希：

```text
QuickPhrase-0.0.1-rc.1-win-x64-unsigned.zip
SHA-256: B62633C96B6FE9CFFC6DAD6D1869DEB27777BBA0B47EC73E49985FEA8EE4AD31

QuickPhrase-Setup-0.0.1-rc.1-unsigned.exe
SHA-256: FC3E33A01EC43A72C0576CD9B648D2B2E68F6AE661C56AD9A5ECA4BB32E15154

SHA256SUMS.txt
SHA-256: AA25160ABCC88B81B6221392E590BF359A7D9EEA23B385A16323D12AED88DADD

release-manifest.json
SHA-256: 42A5D1E21A413F5574A54CB6FAF89CAA317701AF12667193345230B04BC814A2
```

`v0.0.1-rc.1` 是申请证据和公开未签名候选，不得移动 tag 或替换已有资产；候选有缺陷时创建新的 `v0.0.1-rc.2`，不覆盖现有证据。

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
3. `v0.0.1-rc.1` 已作为未签名 Pre-release 发布，Release notes 明确 SmartScreen 风险及 SHA-256 核验方法。
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

当前门禁状态：

- [x] 当前主流版本企业微信人工验收矩阵已由发布负责人明确确认通过。
- [x] Windows 11 x64 安装、升级、启动、卸载保留数据矩阵已于 2026-08-21 由发布负责人明确确认通过。
- [ ] SignPath Foundation 批准项目。
- [ ] GitHub App、Secret、Variables、Signing Policy 和两个 Artifact Configuration 按实际值配置。
- [ ] 应用与安装器取得有效 Authenticode 签名和可信时间戳。
- [ ] 正式 tag、commit、CI、unsigned input hash 和最终 signed asset hash 由 Approver 核对并获得新的发布批准。

在所有未完成项关闭前，不运行正式签名 workflow，也不创建或发布 `v0.0.1`。人工矩阵通过不等于签名资产已经验证，SignPath 项目审批也不单独等于 `PHASE6_VERIFY_PASS_WIN11`。

## 后续操作

1. 维护者手工完成 GitHub 与 SignPath MFA。
2. 维护者授权 SignPath GitHub App 并提交 Open Source Project 申请。
3. SignPath 批准后，配置本页列出的 Variables、Secret、Signing Policy 和两个 Artifact Configuration。
4. 配置完成后另行制定签名正式版 `v0.0.1` 执行清单，并在展示 tag、commit、workflow 与最终资产哈希后取得新的发布批准。

本页记录的人工验收来自发布负责人的明确确认，不是自动化 CI 推断。只有 signed stable 资产通过 `scripts/verify-phase6.ps1` 的签名、时间戳、manifest 与哈希检查后，才允许记录 `PHASE6_VERIFY_PASS_WIN11`。
