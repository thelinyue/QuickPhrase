using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace QuickPhrase.Architecture.Tests;

/// <summary>锁定 SignPath 政策、0.0.1 版本和双阶段签名发布链的源码契约。</summary>
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
