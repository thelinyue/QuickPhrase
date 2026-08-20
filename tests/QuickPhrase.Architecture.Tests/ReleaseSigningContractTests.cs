using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace QuickPhrase.Architecture.Tests;

/// <summary>锁定 SignPath 政策、0.0.1 版本和双阶段签名发布链的源码契约。</summary>
public sealed class ReleaseSigningContractTests
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
        Assert.DoesNotMatch(
            new Regex("(?i)api-token:\\s*['\"]?(?!\\$\\{\\{)", RegexOptions.CultureInvariant),
            workflow);
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
            "IncludeSourceRevisionInInformationalVersion=false",
            "QuickPhrase-Setup-$Version",
            "QuickPhrase-$Version-win-x64",
            "Copy-Item -Path (Join-Path $publishRoot '*')",
        })
            Assert.Contains(expected, build, StringComparison.Ordinal);
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

    [Fact]
    public void FinalizeScriptRequiresValidAuthenticodeAndTimestamp()
    {
        var finalize = File.ReadAllText(Path.Combine(Root, "scripts", "finalize-signed-release.ps1"));
        Assert.Contains("Get-AuthenticodeSignature", finalize, StringComparison.Ordinal);
        Assert.Contains("SignatureStatus]::Valid", finalize, StringComparison.Ordinal);
        Assert.Contains("TimeStamperCertificate", finalize, StringComparison.Ordinal);
        Assert.Contains("signed = $true", finalize, StringComparison.Ordinal);
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
