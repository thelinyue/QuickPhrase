# SignPath Foundation Readiness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 QuickPhrase 建立 SignPath Foundation 申请政策、参数化 `0.0.1` 发布链、可验证 GitHub Actions，并发布未签名 `v0.0.1-rc.1` Pre-release；正式 `v0.0.1` 在 SignPath 和 Windows 11 门禁完成前保持阻塞。

**Architecture:** 发布链拆成 Publish、Installer、Finalize 三阶段。候选 workflow 使用固定内存/人工确认门禁生成 unsigned ZIP 和 Inno installer；正式 workflow 通过两个 SignPath Artifact Configuration 先签应用 PE，再用已签名 publish 构建并签安装器。所有版本、文件名、目录、manifest 和 GitHub Release 由同一 SemVer 输入派生。

**Tech Stack:** .NET 10、WPF、PowerShell 5/7、Inno Setup 6.7.3、GitHub Actions、SignPath GitHub Action v2、xUnit、Authenticode/SignTool、GitHub CLI。

---

## 实施约束

- 只在 `codex/signpath-readiness` 分支实施。
- 严格 TDD：先观察契约测试失败，再最小修改。
- 不创建或移动正式 tag `v0.0.1`。
- 未签名候选只能标记为 Pre-release。
- 不把任何 Token、密码、真实 SignPath ID 写入仓库或日志。
- 不修改 Core 搜索、投递状态机、企业微信 Adapter、企业同步业务协议或数据库 schema。
- 不使用 `git add -A`。
- 候选 Release 是公开不可逆操作，创建前必须再次展示资产列表和 tag。

### Task 1: 锁定政策、版本和 workflow 契约

**Files:**
- Create: `tests/QuickPhrase.Architecture.Tests/ReleaseSigningContractTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
using System.Xml.Linq;

namespace QuickPhrase.Architecture.Tests;

public sealed class ReleaseSigningContractTests
{
    [Fact]
    public void RepositoryPublishesRequiredSignPathPolicies()
    {
        foreach (var file in new[] { "PRIVACY.md", "SECURITY.md", "CODE_SIGNING.md" })
            Assert.True(File.Exists(Path.Combine(Root, file)), $"缺少 {file}。");

        var policy = File.ReadAllText(Path.Combine(Root, "CODE_SIGNING.md"));
        Assert.Contains("Code signing policy", policy, StringComparison.Ordinal);
        Assert.Contains("thelinyue", policy, StringComparison.Ordinal);
        Assert.Contains("Author / Committer", policy, StringComparison.Ordinal);
        Assert.Contains("Reviewer", policy, StringComparison.Ordinal);
        Assert.Contains("Approver", policy, StringComparison.Ordinal);
        Assert.Contains("Free code signing provided by SignPath.io", policy, StringComparison.Ordinal);
        Assert.Contains("certificate by SignPath Foundation", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopDefaultsToVersionZeroZeroOne()
    {
        var project = XDocument.Load(Path.Combine(
            Root, "desktop", "QuickPhrase.Desktop", "QuickPhrase.Desktop.csproj"));
        Assert.Equal("0.0.1", project.Descendants("Version").Single().Value);
        Assert.Equal("0.0.1.0", project.Descendants("FileVersion").Single().Value);
        Assert.Equal("0.0.1.0", project.Descendants("AssemblyVersion").Single().Value);
    }

    [Fact]
    public void ReleaseScriptsContainNoLegacyVersionOrOnlineOfflineArtifacts()
    {
        var governed = new[]
        {
            Path.Combine(Root, "scripts", "build-release.ps1"),
            Path.Combine(Root, "scripts", "verify-phase6.ps1"),
            Path.Combine(Root, "installer", "QuickPhrase.iss"),
        };
        foreach (var file in governed)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("1.0.0", source, StringComparison.Ordinal);
            Assert.DoesNotContain("-online.exe", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-offline.exe", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SigningWorkflowUsesTwoSignPathRequestsAndNoLiteralCredentials()
    {
        var workflow = File.ReadAllText(Path.Combine(
            Root, ".github", "workflows", "release-signed.yml"));
        Assert.Equal(2, workflow.Split(
            "signpath/github-action-submit-signing-request@v2",
            StringSplitOptions.None).Length - 1);
        Assert.Contains("secrets.SIGNPATH_API_TOKEN", workflow, StringComparison.Ordinal);
        Assert.Contains("vars.SIGNPATH_APP_ARTIFACT_CONFIGURATION_SLUG", workflow, StringComparison.Ordinal);
        Assert.Contains("vars.SIGNPATH_INSTALLER_ARTIFACT_CONFIGURATION_SLUG", workflow, StringComparison.Ordinal);
        Assert.DoesNotMatch("(?i)api-token:\\s*['\"]?(?!\\$\\{\\{)", workflow);
    }

    [Fact]
    public void CandidateWorkflowCannotPublishAStableRelease()
    {
        var workflow = File.ReadAllText(Path.Combine(
            Root, ".github", "workflows", "release-candidate.yml"));
        Assert.Contains("workflow_dispatch", workflow, StringComparison.Ordinal);
        Assert.Contains("0.0.1-rc.1", workflow, StringComparison.Ordinal);
        Assert.Contains("unsigned", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gh release create", workflow, StringComparison.OrdinalIgnoreCase);
    }

    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
        }
    }
}
```

- [ ] **Step 2: 运行确认失败**

```powershell
dotnet test tests/QuickPhrase.Architecture.Tests/QuickPhrase.Architecture.Tests.csproj --no-restore --filter FullyQualifiedName~ReleaseSigningContractTests
```

Expected: FAIL，政策、workflow 和 0.0.1 参数化尚不存在。

- [ ] **Step 3: 提交失败测试**

```powershell
git add -- tests/QuickPhrase.Architecture.Tests/ReleaseSigningContractTests.cs
git commit -m "test: 锁定 SignPath 发布契约"
```

### Task 2: 添加隐私、安全和代码签名政策

**Files:**
- Create: `PRIVACY.md`
- Create: `SECURITY.md`
- Create: `CODE_SIGNING.md`
- Modify: `README.md`
- Modify: `tests/QuickPhrase.Architecture.Tests/ReleaseSigningContractTests.cs`

- [ ] **Step 1: 添加隐私内容测试**

测试必须断言 `PRIVACY.md` 包含：

```text
%LOCALAPPDATA%\QuickPhrase
默认本地模式
企业同步
密码不持久化
Windows DPAPI
企业话术标题及正文
不包含广告或第三方分析
```

并拒绝“从不联网”这种与企业同步冲突的绝对表述。

- [ ] **Step 2: 添加安全政策测试**

断言 `SECURITY.md` 包含：

```text
0.0.1
Private Vulnerability Reporting
3 个工作日
7 个工作日
不得提交公开 Issue
```

- [ ] **Step 3: 编写 `CODE_SIGNING.md`**

使用正式标题：

```markdown
# Code signing policy
```

包含单维护者角色、MFA、人工审批、外部贡献审核、签名范围、停止/撤销条件，以及：

```text
Free code signing provided by SignPath.io,
certificate by SignPath Foundation
```

- [ ] **Step 4: 更新 README**

- 将“V1.0.0 支持范围”改为不硬编码版本的“当前支持范围”。
- 安装章节区分 unsigned RC 与未来 SignPath signed release。
- 增加 Privacy、Security、Code signing 链接。
- 不宣称 SignPath 已批准。

- [ ] **Step 5: 运行政策测试**

```powershell
dotnet test tests/QuickPhrase.Architecture.Tests/QuickPhrase.Architecture.Tests.csproj --no-restore --filter FullyQualifiedName~ReleaseSigningContractTests
```

Expected: 政策相关测试通过，版本/workflow 测试仍失败。

- [ ] **Step 6: 尝试启用 GitHub Private Vulnerability Reporting**

先读取当前状态，再使用 GitHub API 的 private vulnerability reporting endpoint。若 API 返回不支持或权限不足，把具体错误写入 `docs/signpath-application.md` 的人工步骤，不修改其他安全设置。

- [ ] **Step 7: 提交政策**

```powershell
git add -- PRIVACY.md SECURITY.md CODE_SIGNING.md README.md tests/QuickPhrase.Architecture.Tests/ReleaseSigningContractTests.cs
git commit -m "docs: 添加 SignPath 隐私安全与签名政策"
```

### Task 3: 参数化 0.0.1 发布和 Inno 安装器

**Files:**
- Modify: `desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj`
- Modify: `scripts/build-release.ps1`
- Modify: `scripts/verify-phase6.ps1`
- Modify: `installer/QuickPhrase.iss`
- Create: `scripts/finalize-signed-release.ps1`
- Modify: `tests/QuickPhrase.Architecture.Tests/ArchitectureTests.cs`
- Modify: `tests/QuickPhrase.Architecture.Tests/ReleaseSigningContractTests.cs`

- [ ] **Step 1: 添加版本派生失败测试**

测试读取脚本并断言存在：

```text
-Version
-UnsignedCandidate
-Stage
Publish
Installer
All
IncludeSourceRevisionInInformationalVersion=false
QuickPhrase-Setup-$Version
QuickPhrase-$Version-win-x64
```

测试 Inno 文件存在 `#ifndef AppVersion`、`#ifndef ReleaseRoot`、`#ifndef OutputBase`，并且 Source/OutputDir 使用宏。

- [ ] **Step 2: 修改 csproj 默认版本**

```xml
<Version>0.0.1</Version>
<FileVersion>0.0.1.0</FileVersion>
<AssemblyVersion>0.0.1.0</AssemblyVersion>
```

- [ ] **Step 3: 参数化 Inno**

```pascal
#ifndef AppVersion
  #define AppVersion "0.0.1"
#endif
#ifndef ReleaseRoot
  #define ReleaseRoot "..\artifacts\release\0.0.1"
#endif
#ifndef OutputBase
  #define OutputBase "QuickPhrase-Setup-0.0.1"
#endif
```

`AppVerName` 使用 `{#AppVersion}`；OutputDir 和 Source 使用 `{#ReleaseRoot}`；OutputBaseFilename 使用 `{#OutputBase}`。

- [ ] **Step 4: 重构 build-release 参数**

```powershell
param(
  [ValidatePattern('^\d+\.\d+\.\d+(?:-rc\.\d+)?$')]
  [string]$Version = '0.0.1',
  [ValidateSet('Publish', 'Installer', 'All')]
  [string]$Stage = 'All',
  [switch]$UnsignedCandidate,
  [string]$PublishRootOverride
)
```

派生：

```powershell
$numericVersion = ($Version -split '-')[0]
$fileVersion = "$numericVersion.0"
$releaseRoot = Join-Path $workspace "artifacts\release\$Version"
$publishRoot = if ($PublishRootOverride) { [IO.Path]::GetFullPath($PublishRootOverride) } else { Join-Path $releaseRoot 'publish' }
$suffix = if ($UnsignedCandidate) { '-unsigned' } else { '' }
$installerBase = "QuickPhrase-Setup-$Version$suffix"
$archiveName = "QuickPhrase-$Version-win-x64$suffix.zip"
```

Publish 阶段运行完整 build/test/smoke 后 `dotnet publish`，显式设置：

```text
Version=$Version
FileVersion=$fileVersion
AssemblyVersion=$fileVersion
InformationalVersion=$Version
IncludeSourceRevisionInInformationalVersion=false
```

Installer 阶段要求 PublishRoot 已存在，再调用 ISCC：

```powershell
& $iscc "/DAppVersion=$Version" "/DReleaseRoot=$releaseRoot" "/DOutputBase=$installerBase" installer\QuickPhrase.iss
```

All 顺序执行 Publish 和 Installer。RC manifest 固定 `signed=false`、`releaseChannel=prerelease`。

- [ ] **Step 5: 修复 verify-phase6**

参数化 `-Version`，要求：

```text
QUICKPHRASE_WECOM_ACCEPTANCE=passed
QUICKPHRASE_WIN11_ACCEPTANCE=passed
```

只检查：

```text
QuickPhrase-Setup-$Version.exe
QuickPhrase-$Version-win-x64.zip
SHA256SUMS.txt
release-manifest.json
```

移除 online/offline 和固定 `1.0.0`。

- [ ] **Step 6: 添加 finalize 脚本**

`finalize-signed-release.ps1` 接收 signed publish 和 signed installer，使用 `Get-AuthenticodeSignature` 验证：

```text
Status == Valid
SignerCertificate 不为空
TimeStamperCertificate 不为空
```

然后生成正式 ZIP、uppercase SHA-256 和 `signed=true` manifest。任意文件无签名或无时间戳时失败，不生成正式资产。

- [ ] **Step 7: 更新 ArchitectureTests**

正式 artifact 测试不再固定 `artifacts/release/1.0.0`，改为从 manifest/version 或 `0.0.1` 目录读取，并继续拒绝 HTML/JS/CSS/WebView2/React。

- [ ] **Step 8: 运行定向测试和构建**

```powershell
dotnet test tests/QuickPhrase.Architecture.Tests/QuickPhrase.Architecture.Tests.csproj --no-restore --filter "FullyQualifiedName~ReleaseSigningContractTests|FullyQualifiedName~ArchitectureTests"
dotnet build QuickPhrase.sln --no-restore
```

- [ ] **Step 9: 提交版本链**

```powershell
git add -- desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj scripts/build-release.ps1 scripts/verify-phase6.ps1 scripts/finalize-signed-release.ps1 installer/QuickPhrase.iss tests/QuickPhrase.Architecture.Tests/ArchitectureTests.cs tests/QuickPhrase.Architecture.Tests/ReleaseSigningContractTests.cs
git commit -m "feat: 参数化 0.0.1 发布链"
```

### Task 4: 建立持续集成 workflow

**Files:**
- Create: `.github/workflows/ci.yml`
- Modify: `tests/QuickPhrase.Architecture.Tests/ReleaseSigningContractTests.cs`

- [ ] **Step 1: 添加 CI workflow 失败测试**

断言：

```text
runs-on: windows-latest
actions/checkout@v4
fetch-depth: 0
actions/setup-dotnet@v4
dotnet restore
dotnet build QuickPhrase.sln -c Release
dotnet test QuickPhrase.sln -c Release
invoke-launcher-smoke.ps1 -Mode Native
invoke-launcher-smoke.ps1 -Mode Performance
```

- [ ] **Step 2: 创建 ci.yml**

触发 push、pull_request 和 workflow_dispatch。设置最小权限：

```yaml
permissions:
  contents: read
```

使用 PowerShell 和 Windows runner，安装/选择 `10.0.400`，运行 Debug/Release、完整测试和两种 smoke。上传测试结果不是发布门禁，不上传用户数据或本地日志。

- [ ] **Step 3: 运行 workflow 契约测试**

```powershell
dotnet test tests/QuickPhrase.Architecture.Tests/QuickPhrase.Architecture.Tests.csproj --no-restore --filter FullyQualifiedName~ReleaseSigningContractTests
```

- [ ] **Step 4: 提交 CI**

```powershell
git add -- .github/workflows/ci.yml tests/QuickPhrase.Architecture.Tests/ReleaseSigningContractTests.cs
git commit -m "ci: 添加 Windows 发布质量门禁"
```

### Task 5: 建立未签名候选构建 workflow

**Files:**
- Create: `.github/workflows/release-candidate.yml`
- Modify: `tests/QuickPhrase.Architecture.Tests/ReleaseSigningContractTests.cs`

- [ ] **Step 1: 添加候选 workflow 失败测试**

断言 workflow：

- 只由 `workflow_dispatch` 触发，不因普通 push 自动发布。
- 固定默认版本 `0.0.1-rc.1`。
- 要求布尔输入 `confirmWeComAcceptance`。
- `permissions.contents` 仅为 read。
- 调用 `build-release.ps1 -Version 0.0.1-rc.1 -UnsignedCandidate -Stage All`。
- 使用 `actions/upload-artifact@v4`。
- 上传四项：unsigned ZIP、unsigned installer、hash、manifest。
- 不包含 `gh release create`、`git tag` 或 SignPath Action。

- [ ] **Step 2: 创建 workflow**

```yaml
name: release-candidate-build
on:
  workflow_dispatch:
    inputs:
      version:
        description: Candidate SemVer
        required: true
        default: 0.0.1-rc.1
      confirmWeComAcceptance:
        description: Confirm current WeCom manual matrix
        required: true
        type: boolean
        default: false
permissions:
  contents: read
```

job 首先拒绝非 `0.0.1-rc.N` 和未确认企业微信门禁，然后在当前进程设置：

```powershell
$env:QUICKPHRASE_WECOM_ACCEPTANCE = 'passed'
```

安装/验证 Inno Setup 6.7.3，执行候选构建，上传 Actions artifact。workflow 不创建 tag 或 GitHub Release。

- [ ] **Step 3: 运行契约测试**

```powershell
dotnet test tests/QuickPhrase.Architecture.Tests/QuickPhrase.Architecture.Tests.csproj --no-restore --filter FullyQualifiedName~ReleaseSigningContractTests
```

- [ ] **Step 4: 提交 workflow**

```powershell
git add -- .github/workflows/release-candidate.yml tests/QuickPhrase.Architecture.Tests/ReleaseSigningContractTests.cs
git commit -m "ci: 添加未签名发布候选构建"
```

### Task 6: 准备双阶段 SignPath workflow 和申请材料

**Files:**
- Create: `.github/workflows/release-signed.yml`
- Create: `docs/signpath-application.md`
- Modify: `tests/QuickPhrase.Architecture.Tests/ReleaseSigningContractTests.cs`

- [ ] **Step 1: 添加 signed workflow 失败测试**

除两个 SignPath Action 外，断言：

```text
workflow_dispatch
confirmWeComAcceptance
confirmWin11Acceptance
environment: production-signing
actions/upload-artifact@v4
output-artifact-directory
wait-for-completion: true
Get-AuthenticodeSignature
finalize-signed-release.ps1
```

并拒绝：

```text
pull_request
push:
automatic approval
literal API token
```

- [ ] **Step 2: 创建 signed workflow**

workflow_dispatch 输入：

```text
version = 0.0.1
confirmWeComAcceptance = false
confirmWin11Acceptance = false
```

设置：

```yaml
permissions:
  contents: read
jobs:
  sign-release:
    environment: production-signing
    runs-on: windows-latest
```

步骤：

1. 校验当前 ref 为 `refs/tags/v0.0.1` 且两个确认均为 true。
2. 设置两个 process-scoped acceptance 变量。
3. Publish 阶段生成 unsigned publish ZIP。
4. `actions/upload-artifact@v4` 上传 application signing input，并使用其 `artifact-id`。
5. 第一次 `signpath/github-action-submit-signing-request@v2`：

```yaml
with:
  api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
  organization-id: ${{ vars.SIGNPATH_ORGANIZATION_ID }}
  project-slug: ${{ vars.SIGNPATH_PROJECT_SLUG }}
  signing-policy-slug: ${{ vars.SIGNPATH_SIGNING_POLICY_SLUG }}
  artifact-configuration-slug: ${{ vars.SIGNPATH_APP_ARTIFACT_CONFIGURATION_SLUG }}
  github-artifact-id: ${{ steps.upload-app.outputs.artifact-id }}
  wait-for-completion: true
  output-artifact-directory: artifacts/signed-app
```

6. 验证四个 QuickPhrase 自有 PE Authenticode 和 timestamp。
7. Installer 阶段以 signed publish 为输入构建 unsigned installer。
8. 上传 installer signing input。
9. 第二次 SignPath Action 使用 installer artifact configuration。
10. 验证 installer Authenticode 和 timestamp。
11. 运行 finalize 脚本。
12. 上传 formal release bundle 为 Actions artifact；workflow 不直接创建 GitHub Release。

- [ ] **Step 3: 编写申请材料**

`docs/signpath-application.md` 必须包含真实仓库和角色信息，候选 Release URL 在发布后补充。Secrets 章节只列 GitHub key 名，不包含值。明确用户人工步骤：MFA、GitHub App、Open Source Project 申请、Policy、两个 Artifact Configuration 和人工审批。

- [ ] **Step 4: 运行测试并提交**

```powershell
dotnet test tests/QuickPhrase.Architecture.Tests/QuickPhrase.Architecture.Tests.csproj --no-restore --filter FullyQualifiedName~ReleaseSigningContractTests
git add -- .github/workflows/release-signed.yml docs/signpath-application.md tests/QuickPhrase.Architecture.Tests/ReleaseSigningContractTests.cs
git commit -m "ci: 准备 SignPath 双阶段签名链"
```

### Task 7: 本地构建和审计 v0.0.1-rc.1

**Files:**
- Generated only: `artifacts/release/0.0.1-rc.1/**`
- Modify Task 3-6 files only if diagnostics prove a defect.

- [ ] **Step 1: 完整测试**

```powershell
dotnet build QuickPhrase.sln -c Release --no-restore
dotnet test QuickPhrase.sln -c Release --no-build
```

Expected: 0 build errors，全部测试通过。

- [ ] **Step 2: 运行两种 Launcher smoke**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-launcher-smoke.ps1 -Mode Native -Configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-launcher-smoke.ps1 -Mode Performance -Configuration Release
```

Expected: 两者退出 0；Performance P95 `<=120ms`；无 smoke 残留 PID。

- [ ] **Step 3: 构建候选资产**

只在当前命令进程设置已确认的企业微信门禁：

```powershell
$env:QUICKPHRASE_WECOM_ACCEPTANCE = 'passed'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-release.ps1 -Version 0.0.1-rc.1 -UnsignedCandidate -Stage All
```

Expected directory：

```text
artifacts/release/0.0.1-rc.1
```

- [ ] **Step 4: 审计候选资产**

验证：

```text
QuickPhrase.exe ProductVersion = 0.0.1-rc.1
QuickPhrase.exe FileVersion = 0.0.1.0
Installer filename = QuickPhrase-Setup-0.0.1-rc.1-unsigned.exe
ZIP filename = QuickPhrase-0.0.1-rc.1-win-x64-unsigned.zip
manifest version = 0.0.1-rc.1
manifest signed = false
manifest releaseChannel = prerelease
SHA256SUMS 与文件一致
```

扫描 publish/ZIP，拒绝 `.html`、`.js`、`.css`、`.map`、WebView2、React、原型壁纸。验证 installer 和 app 当前确实为 unsigned，避免把意外证书混入候选声明。

- [ ] **Step 5: 候选安装边界**

自动化只能证明构建和静态 artifact。未实际执行 Windows 11 安装时，报告必须明确“候选安装 GUI/升级/卸载矩阵未在本步执行”。

- [ ] **Step 6: 修复时使用单一根因**

只修改 Task 3-6 明确文件；禁止降低测试、签名或纯 WPF门禁。修复后从 Step 1 重跑。

### Task 8: 推送 readiness 分支并创建 PR

**Files:**
- No new source files expected.

- [ ] **Step 1: 最终本地验证**

```powershell
dotnet test QuickPhrase.sln -c Release --no-restore
git diff --check
git status --short --branch
```

- [ ] **Step 2: 推送分支**

```powershell
git push -u origin codex/signpath-readiness
```

- [ ] **Step 3: 创建 Draft PR**

PR title：

```text
[codex] prepare SignPath signing and v0.0.1 release chain
```

正文包含政策、版本参数化、CI、候选资产、双阶段签名、测试结果和仍需人工完成的 SignPath/MFA/Windows 11 项目。

- [ ] **Step 4: 等待 PR CI**

使用 `gh pr checks --watch`。失败时读取 Actions log，按根因修复并推送。不得在 CI 未通过时合并。

- [ ] **Step 5: 用户审核 checkpoint**

展示 PR、文件列表、候选资产本地 hash 和 CI 结果。只有用户批准后才合并 PR 或创建公开 RC tag。

### Task 9: 合并后创建公开 v0.0.1-rc.1 Pre-release

**Files:**
- Remote tag and GitHub Release only.
- Generated downloaded verification directory under `artifacts/verification/v0.0.1-rc.1`.

- [ ] **Step 1: 合并并同步 master**

按用户选择 merge PR；同步本地 master，重新验证 merge result。不得 squash 掉用户要求保留的审计历史，除非用户另行指定。

- [ ] **Step 2: 运行候选 workflow**

在 GitHub Actions 手动运行 `release-candidate.yml`：

```text
version = 0.0.1-rc.1
confirmWeComAcceptance = true
```

下载 Actions artifact 并在本地复核。

- [ ] **Step 3: 展示公开资产清单**

在创建 tag 前向用户展示：

```text
v0.0.1-rc.1
QuickPhrase-0.0.1-rc.1-win-x64-unsigned.zip
QuickPhrase-Setup-0.0.1-rc.1-unsigned.exe
SHA256SUMS.txt
release-manifest.json
```

再次确认这是公开未签名 Pre-release。

- [ ] **Step 4: 创建不可移动候选 tag 和 Pre-release**

```powershell
git tag -a v0.0.1-rc.1 -m "QuickPhrase 0.0.1 release candidate 1"
git push origin v0.0.1-rc.1
gh release create v0.0.1-rc.1 `
  artifacts/release/0.0.1-rc.1/QuickPhrase-0.0.1-rc.1-win-x64-unsigned.zip `
  artifacts/release/0.0.1-rc.1/installers/QuickPhrase-Setup-0.0.1-rc.1-unsigned.exe `
  artifacts/release/0.0.1-rc.1/SHA256SUMS.txt `
  artifacts/release/0.0.1-rc.1/release-manifest.json `
  --prerelease --verify-tag `
  --title "QuickPhrase 0.0.1 RC1 (unsigned)" `
  --notes-file artifacts/release/0.0.1-rc.1/release-notes.md
```

Release notes 明确未签名、非正式版、SmartScreen 和 SHA-256 核验方法。

- [ ] **Step 5: 远端重新下载验证**

```powershell
gh release download v0.0.1-rc.1 --dir artifacts/verification/v0.0.1-rc.1
```

对每个资产重新计算 hash，与公开 `SHA256SUMS.txt` 一致。解压 ZIP 检查版本、文件数和禁止资源。确认 tag commit 与 workflow commit 一致。

- [ ] **Step 6: 不修改候选 tag**

发现问题时停止并创建 `v0.0.1-rc.2` 计划，不覆盖、不强推、不替换 RC1 asset 后声称其未变化。

### Task 10: 完成 SignPath 申请交接

**Files:**
- Modify: `docs/signpath-application.md` with public RC URL and workflow run URL.
- No secrets.

- [ ] **Step 1: 填入公开证据**

记录：

```text
Repository URL
v0.0.1-rc.1 Release URL
CI workflow run URL
Candidate workflow run URL
License URL
Policy URLs
Maintainer/Reviewer/Approver
Unsigned app and installer artifact names
```

- [ ] **Step 2: 提交证据更新**

通过新的 PR 或用户批准的窄提交提交 URL 更新，不在 Release 后直接修改历史 tag。

- [ ] **Step 3: 用户人工申请**

用户完成 SignPath MFA、GitHub App、Open Source Project 申请。不得把 API Token 提供给 Codex 聊天；只写入 GitHub Secret `SIGNPATH_API_TOKEN`。

- [ ] **Step 4: 等待审批**

在批准前停止正式 `v0.0.1`。不得运行 signed workflow、创建正式 tag 或公开伪签名资产。

- [ ] **Step 5: 获批后的下一计划**

审批后读取实际 organization/project/policy/artifact configuration 标识，验证 GitHub Variables/Secret 存在，再单独制定并审核正式签名发布执行清单。Windows 11 安装矩阵仍需明确确认。

## 计划自审

### Spec coverage

- 单维护者政策：Task 2。
- 隐私和企业同步真实边界：Task 2。
- 版本 0.0.1/RC1 参数化：Task 3。
- CI：Task 4。
- 未签名候选 workflow：Task 5。
- 双阶段 SignPath workflow：Task 6。
- 本地产物和 smoke：Task 7。
- PR 审核：Task 8。
- 公开 RC 和远端 hash：Task 9。
- SignPath 人工申请交接：Task 10。
- 正式 v0.0.1 阻塞：Task 6、10。

### Placeholder scan

workflow 只引用约定的 GitHub Secrets/Variables 名称；文档和代码不写入未知的真实值。运行时生成的 Release URL 和 workflow URL 只在实际创建后写入申请材料，不伪造地址。

### Type and filename consistency

统一使用：

```text
v0.0.1-rc.1
v0.0.1
QuickPhrase-0.0.1-rc.1-win-x64-unsigned.zip
QuickPhrase-Setup-0.0.1-rc.1-unsigned.exe
QuickPhrase-0.0.1-win-x64.zip
QuickPhrase-Setup-0.0.1.exe
SIGNPATH_API_TOKEN
SIGNPATH_ORGANIZATION_ID
SIGNPATH_PROJECT_SLUG
SIGNPATH_SIGNING_POLICY_SLUG
SIGNPATH_APP_ARTIFACT_CONFIGURATION_SLUG
SIGNPATH_INSTALLER_ARTIFACT_CONFIGURATION_SLUG
```
