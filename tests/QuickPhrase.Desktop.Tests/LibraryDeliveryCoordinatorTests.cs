using QuickPhrase.Core;
using QuickPhrase.Desktop;

namespace QuickPhrase.Desktop.Tests;

public sealed class LibraryDeliveryCoordinatorTests
{
    [Fact]
    public void Evaluate_SendRequiresVerifiedInsertVerifyAndTriggerCapabilities()
    {
        var coordinator = new LibraryDeliveryCoordinator((_, _, _, _) => Task.FromResult<DeliveryResult?>(null));
        coordinator.UpdateTarget(CreateTarget(), new AdapterCapabilities(
            CapabilityStatus.Verified, CapabilityStatus.Verified,
            CapabilityStatus.Unsupported, CapabilityStatus.Unsupported,
            CapabilityStatus.Verified, CapabilityStatus.Unsupported));

        var availability = coordinator.Evaluate(CreatePhrase(PhraseBody.FromText("您好")));

        Assert.True(availability.CanInsert);
        Assert.True(availability.CanSend);
    }

    [Fact]
    public void Evaluate_ImagePhraseIsDisabledWhenImageVerificationIsNotVerified()
    {
        var coordinator = new LibraryDeliveryCoordinator((_, _, _, _) => Task.FromResult<DeliveryResult?>(null));
        coordinator.UpdateTarget(CreateTarget(), new AdapterCapabilities(
            CapabilityStatus.Verified, CapabilityStatus.Verified,
            CapabilityStatus.Verified, CapabilityStatus.Unverified,
            CapabilityStatus.Verified, CapabilityStatus.Unsupported));
        var image = new PhraseImageReference(Guid.NewGuid(), "image/png", 10, 1, 1);

        var availability = coordinator.Evaluate(CreatePhrase(new PhraseBody([PhraseSegment.CreateImage(image)])));

        Assert.False(availability.CanInsert);
        Assert.False(availability.CanSend);
        Assert.Contains("图片", availability.SendDisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deliver_FreezesTargetAndRejectsConcurrentRequest()
    {
        var firstTarget = CreateTarget("first");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DeliveryTarget? deliveredTarget = null;
        var coordinator = new LibraryDeliveryCoordinator(async (_, target, _, _) =>
        {
            deliveredTarget = target;
            await gate.Task;
            return null;
        });
        var capabilities = new AdapterCapabilities(
            CapabilityStatus.Verified, CapabilityStatus.Verified,
            CapabilityStatus.Unsupported, CapabilityStatus.Unsupported,
            CapabilityStatus.Verified, CapabilityStatus.Unsupported);
        coordinator.UpdateTarget(firstTarget, capabilities);
        var phrase = CreatePhrase(PhraseBody.FromText("您好"));

        var first = coordinator.DeliverAsync(phrase, SendMode.InsertOnly, CancellationToken.None);
        coordinator.UpdateTarget(CreateTarget("second"), capabilities);
        var second = await coordinator.DeliverAsync(phrase, SendMode.InsertOnly, CancellationToken.None);
        gate.SetResult();
        await first;

        Assert.Null(second);
        Assert.Equal(firstTarget, deliveredTarget);
    }

    private static DeliveryTarget CreateTarget(string key = "runtime") =>
        new("WXWork", "WindowsDesktopWindow", "wecom", "企业微信", key, DateTimeOffset.UtcNow);

    private static Phrase CreatePhrase(PhraseBody body) =>
        new(Guid.NewGuid(), "标题", body, Guid.NewGuid(), ShortcutMode.None, null, 0, null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
