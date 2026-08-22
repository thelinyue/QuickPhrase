using System.Xml.Linq;

namespace QuickPhrase.Architecture.Tests;

/// <summary>
/// 锁定 0.0.1 未签名正式发布的公开政策、GitHub Actions 门禁和发布资产契约。
/// 这些断言只验证源码声明，实际资产仍必须由 Phase 6 脚本在发布工作流中重新核验。
/// </summary>
public sealed class ReleasePublicationContractTests
{
    [Fact]
    public void PrivacyPolicyMatchesLocalAndEnterpriseSyncBehavior()
    {
        var privacy = File.ReadAllText(Path.Combine(Root, "PRIVACY.md"));
        Assert.Contains("默认本地模式", privacy, StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA%\\QuickPhrase", privacy, StringComparison.Ordinal);
        Assert.Contains("企业同步", privacy, StringComparison.Ordinal);
        Assert.Contains("密码不持久化", privacy, StringComparison.Ordinal);
        Assert.Contains("Windows DPAPI", privacy, StringComparison.Ordinal);
        Assert.Contains("企业话术标题及正文", privacy, StringComparison.Ordinal);
        Assert.Contains("不包含广告或第三方分析", privacy, StringComparison.Ordinal);
        Assert.DoesNotContain("从不联网", privacy, StringComparison.Ordinal);
    }

    [Fact]
    public void SecurityPolicyDefinesPrivateReportingAndResponseTargets()
    {
        var security = File.ReadAllText(Path.Combine(Root, "SECURITY.md"));
        Assert.Contains("0.0.1", security, StringComparison.Ordinal);
        Assert.Contains("Private Vulnerability Reporting", security, StringComparison.Ordinal);
        Assert.Contains("3 个工作日", security, StringComparison.Ordinal);
        Assert.Contains("7 个工作日", security, StringComparison.Ordinal);
        Assert.Contains("不得提交公开 Issue", security, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicationPolicyClearlyDeclaresUnsignedStableAssets()
    {
        foreach (var file in new[] { "PRIVACY.md", "SECURITY.md", "CODE_SIGNING.md" })
            Assert.True(File.Exists(Path.Combine(Root, file)), $"缺少 {file}。");

        var policy = File.ReadAllText(Path.Combine(Root, "CODE_SIGNING.md"));
        Assert.Contains("Code signing policy", policy, StringComparison.Ordinal);
        Assert.Contains("当前不使用第三方代码签名服务", policy, StringComparison.Ordinal);
        Assert.Contains("signed: false", policy, StringComparison.Ordinal);
        Assert.Contains("SHA-256", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("SignPath", policy, StringComparison.OrdinalIgnoreCase);
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
    public void StableReleaseWorkflowBuildsAndPublishesUnsignedAssetsFromImmutableTag()
    {
        var workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"));
        foreach (var expected in new[]
        {
            "workflow_dispatch",
            "confirmWeComAcceptance",
            "confirmWin11Acceptance",
            "refs/tags/v$version",
            "contents: write",
            "shell: pwsh",
            "scripts/build-release.ps1",
            "-Stage All",
            "scripts/verify-phase6.ps1",
            "actions/upload-artifact@v4",
            "gh release create",
            "GH_TOKEN: ${{ github.token }}",
            "--generate-notes",
            "--notes",
            "SHA256SUMS.txt",
            "release-manifest.json",
        })
            Assert.Contains(expected, workflow, StringComparison.Ordinal);

        Assert.DoesNotContain("SignPath", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-AuthenticodeSignature", workflow, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Root, ".github", "workflows", "release-signed.yml")));
    }

    [Fact]
    public void Phase6VerifierRequiresUnsignedStableAssetsHashesAndPureWpfArchive()
    {
        var verifier = File.ReadAllText(Path.Combine(Root, "scripts", "verify-phase6.ps1"));
        foreach (var expected in new[]
        {
            "QUICKPHRASE_WECOM_ACCEPTANCE",
            "QUICKPHRASE_WIN11_ACCEPTANCE",
            "signed -ne $false",
            "releaseChannel -ne 'stable'",
            "SHA256SUMS.txt",
            "QuickPhrase.exe",
            "QuickPhrase.dll",
            "QuickPhrase.Core.dll",
            "QuickPhrase.Platform.Windows.dll",
            "wwwroot",
            "node_modules",
            "webview2",
            "PHASE6_VERIFY_PASS_WIN11",
        })
            Assert.Contains(expected, verifier, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("Get-AuthenticodeSignature", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeStamperCertificate", verifier, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Root, "scripts", "finalize-signed-release.ps1")));
    }

    [Fact]
    public void ReleaseDocumentationContainsNoRemovedSigningProvider()
    {
        foreach (var relativePath in new[]
        {
            "README.md",
            "SECURITY.md",
            "CODE_SIGNING.md",
            Path.Combine("docs", "phase6-validation.md"),
            Path.Combine("docs", "quickphrase-codex-execution.md"),
            Path.Combine("docs", "codebase-analysis-report.md"),
        })
        {
            var source = File.ReadAllText(Path.Combine(Root, relativePath));
            Assert.DoesNotContain("SignPath", source, StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(File.Exists(Path.Combine(Root, "docs", "signpath-application.md")));
    }

    [Fact]
    public void ManualReleaseWorkflowsUsePowerShellSevenForUtf8Diagnostics()
    {
        foreach (var fileName in new[] { "release-candidate.yml", "release.yml" })
        {
            var workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", fileName));
            Assert.Contains("shell: pwsh", workflow, StringComparison.Ordinal);
            Assert.DoesNotContain("shell: powershell", workflow, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CandidateWorkflowCannotPublishAStableRelease()
    {
        var workflow = File.ReadAllText(Path.Combine(
            Root, ".github", "workflows", "release-candidate.yml"));
        foreach (var expected in new[]
        {
            "workflow_dispatch",
            "default: 0.0.1-rc.1",
            "confirmWeComAcceptance",
            "contents: read",
            "build-release.ps1",
            "-UnsignedCandidate",
            "-Stage All",
            "actions/upload-artifact@v4",
            "QuickPhrase-${{ inputs.version }}-win-x64-unsigned.zip",
            "QuickPhrase-Setup-${{ inputs.version }}-unsigned.exe",
            "SHA256SUMS.txt",
            "release-manifest.json",
        })
            Assert.Contains(expected, workflow, StringComparison.Ordinal);

        Assert.DoesNotContain("gh release create", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git tag", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signpath/github-action", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("push:", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseBuildSupportsCandidateStagesAndDerivedNames()
    {
        var build = File.ReadAllText(Path.Combine(Root, "scripts", "build-release.ps1"));
        foreach (var expected in new[]
        {
            "[string]$Version = '0.0.1'",
            "[switch]$UnsignedCandidate",
            "[string]$Stage = 'All'",
            "'Publish'",
            "'Installer'",
            "signed = $false",
            "IncludeSourceRevisionInInformationalVersion=false",
            "dotnet restore desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -r win-x64 -p:PublishReadyToRun=true",
            "QuickPhrase-Setup-$Version",
            "QuickPhrase-$Version-win-x64",
            "Copy-Item -Path (Join-Path $publishRoot '*')",
        })
            Assert.Contains(expected, build, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryBuildKeepsOnlySimplifiedChineseSatelliteResources()
    {
        var buildProps = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));

        Assert.Contains("<SatelliteResourceLanguages>zh-Hans</SatelliteResourceLanguages>", buildProps, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerUsesCommandLineVersionAndReleaseMacros()
    {
        var installer = File.ReadAllText(Path.Combine(Root, "installer", "QuickPhrase.iss"));
        Assert.Contains("#ifndef AppVersion", installer, StringComparison.Ordinal);
        Assert.Contains("#ifndef ReleaseRoot", installer, StringComparison.Ordinal);
        Assert.Contains("#ifndef OutputBase", installer, StringComparison.Ordinal);
        Assert.Contains("{#ReleaseRoot}\\publish\\*", installer, StringComparison.Ordinal);
        Assert.Contains("OutputDir={#ReleaseRoot}\\installers", installer, StringComparison.Ordinal);
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
