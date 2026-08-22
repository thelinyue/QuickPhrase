using System.IO;
using QuickPhrase.Core;

namespace QuickPhrase.Desktop.Tests;

/// <summary>整批预览必须展示当前目标的六字段能力，避免用静态文案掩盖图片或发送能力缺失。</summary>
public sealed class BatchPreviewCapabilityTests
{
    [Fact]
    public void PreviewDisplaysAllSixCurrentTargetCapabilitiesAndKeepsUnsupportedImageDisabled()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var phrase = new Phrase(
                Guid.NewGuid(),
                "图文批次",
                new PhraseBody(
                    [PhraseSegment.CreateText("第一段"), PhraseSegment.CreateImage(new PhraseImageReference(Guid.NewGuid(), "image/png", 64, 10, 20))],
                    PhraseBody.DefaultBatchSeparator),
                Guid.NewGuid(),
                ShortcutMode.None,
                null,
                0,
                null,
                1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            var capabilities = new AdapterCapabilities(
                CapabilityStatus.Verified,
                CapabilityStatus.Verified,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Verified,
                CapabilityStatus.Unsupported);
            var window = new BatchPreviewWindow(phrase, null, confirmation: true, capabilities);
            try
            {
                var text = window.CapabilityText.Text;
                Assert.Contains("InsertText：已验证", text, StringComparison.Ordinal);
                Assert.Contains("VerifyTextInsert：已验证", text, StringComparison.Ordinal);
                Assert.Contains("InsertImage：不支持", text, StringComparison.Ordinal);
                Assert.Contains("VerifyImageInsert：不支持", text, StringComparison.Ordinal);
                Assert.Contains("TriggerSend：已验证", text, StringComparison.Ordinal);
                Assert.Contains("VerifySend：不支持", text, StringComparison.Ordinal);
                Assert.False(window.ConfirmButton.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ApplicationControllerAndLauncherPassCurrentTargetCapabilitiesIntoPreview()
    {
        var root = FindRepositoryRoot();
        var controller = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "ApplicationController.cs"));
        var launcher = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml.cs"));

        Assert.Contains("var capabilities = GetTargetCapabilities(resolvedTarget);", controller, StringComparison.Ordinal);
        Assert.Contains("_launcher.Open(initialQuery, resolvedTarget, phraseId, canExplicitSend, invocationContext, capabilities);", controller, StringComparison.Ordinal);
        Assert.Contains("_targetCapabilities = targetCapabilities ??", launcher, StringComparison.Ordinal);
        Assert.Contains("new BatchPreviewWindow(phrase, _mediaAssets, confirmation, _targetCapabilities)", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewAllowsConfirmedImageBatchOnlyForFakeVerifiedImageCapabilities()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var image = new PhraseImageReference(Guid.NewGuid(), "image/png", 64, 10, 20);
            var phrase = new Phrase(
                Guid.NewGuid(), "图片批次", new PhraseBody([PhraseSegment.CreateImage(image)], PhraseBody.DefaultBatchSeparator),
                Guid.NewGuid(), ShortcutMode.None, null, 0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var capabilities = new AdapterCapabilities(
                CapabilityStatus.Verified,
                CapabilityStatus.Verified,
                CapabilityStatus.Verified,
                CapabilityStatus.Verified,
                CapabilityStatus.Verified,
                CapabilityStatus.Unsupported);
            var window = new BatchPreviewWindow(phrase, null, confirmation: true, capabilities);
            try
            {
                Assert.True(window.ConfirmButton.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln。");
    }

}

