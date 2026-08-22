using System.Collections.Immutable;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

/// <summary>
/// 首发 Adapter 契约与批次段间稳定性测试。测试只检查脱敏能力和调用顺序，不接触剪贴板正文或 Windows 句柄。
/// </summary>
public sealed class AdapterBatchContractTests
{
    [Fact]
    public void AdapterCapabilitiesExposeOnlySixFirstReleaseFields()
    {
        var propertyNames = typeof(AdapterCapabilities)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "InsertImage",
                "InsertText",
                "TriggerSend",
                "VerifyImageInsert",
                "VerifySend",
                "VerifyTextInsert",
            },
            propertyNames);
    }

    [Fact]
    public void GenericKeepsImageCapabilitiesUnsupportedWhileWeComExposesVerifiedImageCapabilities()
    {
        using var resolver = new WindowsAdapterResolver(
            productVersionReader: _ => "diagnostic-only",
            targetValidator: _ => true);
        var genericTarget = CreateTarget("Notepad", "GenericTextInput");
        var weComTarget = CreateTarget("WXWork", "WXWork");

        var generic = resolver.Resolve(genericTarget, "1.0.0").DetectCapabilities();
        var oldWeCom = resolver.Resolve(weComTarget, "3.1.0").DetectCapabilities();
        var newWeCom = resolver.Resolve(weComTarget, "99.0.0").DetectCapabilities();

        Assert.Equal(CapabilityStatus.Unsupported, generic.InsertImage);
        Assert.Equal(CapabilityStatus.Unsupported, generic.VerifyImageInsert);
        Assert.Equal(CapabilityStatus.Verified, oldWeCom.InsertImage);
        Assert.Equal(CapabilityStatus.Verified, oldWeCom.VerifyImageInsert);
        Assert.Equal(CapabilityStatus.Verified, newWeCom.InsertImage);
        Assert.Equal(CapabilityStatus.Verified, newWeCom.VerifyImageInsert);
        Assert.False(resolver.Resolve(genericTarget) is IImageApplicationAdapter);
        Assert.IsAssignableFrom<IImageApplicationAdapter>(resolver.Resolve(weComTarget));
    }

    [Fact]
    public async Task BatchUsesAdapterStabilityWaiterBeforeNextSegmentAndRevalidatesAfterWait()
    {
        var events = new List<string>();
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new RecordingTargetDetector(target, events);
        var single = new RecordingSingleDelivery(events);
        var waiter = new RecordingStabilityWaiter(events, VerificationResult.Verified);
        var usageCalls = 0;
        var machine = new BatchDeliveryStateMachine(
            single,
            detector,
            waiter,
            new FixedAdapterResolver(new ConfigurableAdapter()),
            () => null,
            (_, _) =>
            {
                usageCalls++;
                return Task.CompletedTask;
            });

        var result = await machine.DeliverAsync(CreateBatchRequest(target));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.CompletedSegments);
        Assert.Equal(1, waiter.Calls);
        Assert.Equal(1, usageCalls);
        Assert.Equal(
            new[] { "Validate:1", "Deliver:1", "Wait", "Validate:2", "Validate:3", "Deliver:2" },
            events);
        Assert.Equal(2, single.Requests.Count);
        Assert.All(single.Requests, request =>
        {
            Assert.Equal(SendMode.InsertAndSend, request.Mode);
            Assert.Equal(1, request.Phrase.Body.SegmentCount);
        });
        Assert.Equal(new[] { "第一段", "第二段" }, single.Requests.Select(request => request.Phrase.Body.TextProjection));
    }

    [Fact]
    public async Task BatchInsertOnlyInsertsEachTextSegmentWithoutSending()
    {
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new ConfigurableTargetDetector(target);
        var adapter = new ConfigurableAdapter();
        var usageCalls = 0;
        using var single = new TextDeliveryStateMachine(
            detector,
            new FixedAdapterResolver(adapter),
            new RecordingClipboardTransaction(),
            (_, _) => { usageCalls++; return Task.CompletedTask; });
        var machine = new BatchDeliveryStateMachine(
            single,
            detector,
            new RecordingStabilityWaiter([], VerificationResult.Verified),
            new FixedAdapterResolver(adapter),
            () => null,
            (_, _) => { usageCalls++; return Task.CompletedTask; });

        var result = await machine.DeliverAsync(CreateBatchRequest(target) with { Mode = SendMode.InsertOnly });

        Assert.True(result.IsSuccess);
        Assert.Equal(DeliveryEffect.Inserted, result.Effect);
        Assert.Equal(2, result.CompletedSegments);
        Assert.Equal(0, adapter.SendCalls);
        Assert.Equal(1, usageCalls);
    }

    [Fact]
    public async Task VerifiedImageCapabilityInsertOnlyInsertsWithoutSending()
    {
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new ConfigurableTargetDetector(target);
        var adapter = new ConfigurableAdapter(imageCapabilities: CapabilityStatus.Verified);
        var image = CreateImageReference();
        var media = new RecordingMediaStore(image, [1, 2, 3]);
        var usageCalls = 0;
        using var single = new TextDeliveryStateMachine(
            detector,
            new FixedAdapterResolver(adapter),
            new RecordingClipboardTransaction(),
            static (_, _) => Task.CompletedTask);
        var machine = new BatchDeliveryStateMachine(
            single,
            detector,
            new RecordingStabilityWaiter([], VerificationResult.Verified),
            new FixedAdapterResolver(adapter),
            () => media,
            (_, _) => { usageCalls++; return Task.CompletedTask; });

        var request = CreateBatchRequest(target, [PhraseSegment.CreateImage(image)]) with { Mode = SendMode.InsertOnly };
        var result = await machine.DeliverAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(DeliveryEffect.Inserted, result.Effect);
        Assert.Equal(1, result.CompletedSegments);
        Assert.Equal(1, adapter.ImageInsertCalls);
        Assert.Equal(1, adapter.VerifyImageCalls);
        Assert.Equal(0, adapter.SendCalls);
        Assert.Equal(1, usageCalls);
    }

    [Fact]
    public async Task BatchStopsBeforeNextSegmentWhenTargetChangesDuringAdapterWait()
    {
        var events = new List<string>();
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new RecordingTargetDetector(target, events);
        var single = new RecordingSingleDelivery(events);
        var waiter = new RecordingStabilityWaiter(
            events,
            VerificationResult.Verified,
            () => detector.IsValid = false);
        var machine = new BatchDeliveryStateMachine(
            single,
            detector,
            waiter,
            new FixedAdapterResolver(new ConfigurableAdapter()),
            () => null,
            static (_, _) => Task.CompletedTask);

        var result = await machine.DeliverAsync(CreateBatchRequest(target));

        Assert.Equal(DeliveryStatus.Failed, result.Status);
        Assert.Equal(DeliveryEffect.SendTriggered, result.Effect);
        Assert.Equal(1, result.CompletedSegments);
        Assert.Equal(2, result.FailedSegmentIndex);
        Assert.Equal("TARGET_CHANGED", result.SegmentResults[^1].ErrorCode);
        Assert.Equal(1, single.Calls);
        Assert.Equal(1, waiter.Calls);
        Assert.Equal(new[] { "Validate:1", "Deliver:1", "Wait", "Validate:2" }, events);
    }


    [Fact]
    public async Task TextDeliveryHonorsRecordUsageOnSuccessFalse()
    {
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new ConfigurableTargetDetector(target);
        var adapter = new ConfigurableAdapter();
        var usageCalls = 0;
        using var single = new TextDeliveryStateMachine(
            detector,
            new FixedAdapterResolver(adapter),
            new RecordingClipboardTransaction(),
            (_, _) => { usageCalls++; return Task.CompletedTask; });

        var request = CreateBatchRequest(target) with
        {
            Phrase = CreatePhrase([PhraseSegment.CreateText("单段")]),
            RecordUsageOnSuccess = false,
        };
        var result = await single.DeliverAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(DeliveryEffect.SendTriggered, result.Effect);
        Assert.Equal(0, usageCalls);
    }

    [Fact]
    public async Task RealTextStateMachineBatchRecordsUsageExactlyOnceAfterFullSuccess()
    {
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new ConfigurableTargetDetector(target);
        var adapter = new ConfigurableAdapter();
        var usageCalls = 0;
        Task RecordUsage(Phrase _, CancellationToken __) { usageCalls++; return Task.CompletedTask; }
        using var single = new TextDeliveryStateMachine(detector, new FixedAdapterResolver(adapter), new RecordingClipboardTransaction(), RecordUsage);
        var machine = new BatchDeliveryStateMachine(single, detector, new RecordingStabilityWaiter([], VerificationResult.Verified), new FixedAdapterResolver(adapter), () => null, RecordUsage);

        var result = await machine.DeliverAsync(CreateBatchRequest(target));

        Assert.True(result.IsSuccess);
        Assert.Equal(DeliveryEffect.SendTriggered, result.Effect);
        Assert.Equal(1, usageCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealTextStateMachineBatchDoesNotRecordUsageAfterPartialFailureOrUnknown(bool unknown)
    {
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new ConfigurableTargetDetector(target);
        var adapter = new ConfigurableAdapter();
        adapter.VerifyTextResults.Enqueue(VerificationResult.Verified);
        adapter.VerifyTextResults.Enqueue(unknown
            ? VerificationResult.Inconclusive("INSERT_UNKNOWN")
            : VerificationResult.Failed("FOCUS_CHANGED"));
        var usageCalls = 0;
        Task RecordUsage(Phrase _, CancellationToken __) { usageCalls++; return Task.CompletedTask; }
        using var single = new TextDeliveryStateMachine(detector, new FixedAdapterResolver(adapter), new RecordingClipboardTransaction(), RecordUsage);
        var machine = new BatchDeliveryStateMachine(single, detector, new RecordingStabilityWaiter([], VerificationResult.Verified), new FixedAdapterResolver(adapter), () => null, RecordUsage);

        var result = await machine.DeliverAsync(CreateBatchRequest(target));

        Assert.Equal(unknown ? DeliveryStatus.Unknown : DeliveryStatus.Failed, result.Status);
        Assert.Equal(1, result.CompletedSegments);
        Assert.Equal(2, result.FailedSegmentIndex);
        Assert.Equal(0, usageCalls);
    }

    [Fact]
    public async Task VerifiedImageCapabilityExecutesMediaClipboardVerificationSendAndReturnsBatchSendTriggered()
    {
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new ConfigurableTargetDetector(target);
        var adapter = new ConfigurableAdapter(imageCapabilities: CapabilityStatus.Verified);
        var image = CreateImageReference();
        var media = new RecordingMediaStore(image, [1, 2, 3]);
        var usageCalls = 0;
        using var single = new TextDeliveryStateMachine(detector, new FixedAdapterResolver(adapter), new RecordingClipboardTransaction(), static (_, _) => Task.CompletedTask);
        var machine = new BatchDeliveryStateMachine(
            single, detector, new RecordingStabilityWaiter([], VerificationResult.Verified), new FixedAdapterResolver(adapter), () => media,
            (_, _) => { usageCalls++; return Task.CompletedTask; });

        var result = await machine.DeliverAsync(CreateBatchRequest(target, [PhraseSegment.CreateImage(image)]));

        Assert.True(result.IsSuccess);
        Assert.Equal(DeliveryEffect.SendTriggered, result.Effect);
        Assert.Equal(1, result.CompletedSegments);
        Assert.Equal(1, media.ReadCalls);
        Assert.Equal(1, adapter.ImageInsertCalls);
        Assert.Equal(1, adapter.VerifyImageCalls);
        Assert.Equal(1, adapter.SendCalls);
        Assert.Equal(1, usageCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedImageCapabilityStopsOnInsertFailureOrUnknownWithoutUsage(bool unknown)
    {
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new ConfigurableTargetDetector(target);
        var adapter = new ConfigurableAdapter(imageCapabilities: CapabilityStatus.Verified)
        {
            ImageInsertResult = unknown
                ? new InsertResult(false, true, "IMAGE_INSERT_UNKNOWN")
                : new InsertResult(false, false, "IMAGE_INSERT_FAILED"),
        };
        var image = CreateImageReference();
        var media = new RecordingMediaStore(image, [1, 2, 3]);
        var usageCalls = 0;
        using var single = new TextDeliveryStateMachine(detector, new FixedAdapterResolver(adapter), new RecordingClipboardTransaction(), static (_, _) => Task.CompletedTask);
        var machine = new BatchDeliveryStateMachine(
            single, detector, new RecordingStabilityWaiter([], VerificationResult.Verified), new FixedAdapterResolver(adapter), () => media,
            (_, _) => { usageCalls++; return Task.CompletedTask; });

        var result = await machine.DeliverAsync(CreateBatchRequest(target, [PhraseSegment.CreateImage(image), PhraseSegment.CreateText("后续段") ]));

        Assert.Equal(unknown ? DeliveryStatus.Unknown : DeliveryStatus.Failed, result.Status);
        Assert.Equal(0, result.CompletedSegments);
        Assert.Equal(1, result.FailedSegmentIndex);
        Assert.Equal(0, adapter.SendCalls);
        Assert.Equal(0, usageCalls);
    }

    [Fact]
    public async Task VerifiedImageCapabilityStopsWhenTargetChangesBeforeSend()
    {
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new ConfigurableTargetDetector(target) { FailOnValidationCall = 2 };
        var adapter = new ConfigurableAdapter(imageCapabilities: CapabilityStatus.Verified);
        var image = CreateImageReference();
        var media = new RecordingMediaStore(image, [1, 2, 3]);
        using var single = new TextDeliveryStateMachine(detector, new FixedAdapterResolver(adapter), new RecordingClipboardTransaction(), static (_, _) => Task.CompletedTask);
        var machine = new BatchDeliveryStateMachine(
            single, detector, new RecordingStabilityWaiter([], VerificationResult.Verified), new FixedAdapterResolver(adapter), () => media,
            static (_, _) => Task.CompletedTask);

        var result = await machine.DeliverAsync(CreateBatchRequest(target, [PhraseSegment.CreateImage(image)]));

        Assert.Equal(DeliveryStatus.Failed, result.Status);
        Assert.Equal(DeliveryEffect.Inserted, result.Effect);
        Assert.Equal("TARGET_CHANGED", result.SegmentResults[0].ErrorCode);
        Assert.Equal(0, adapter.SendCalls);
    }

    [Fact]
    public async Task ImageMediaMetadataMismatchStopsBeforeClipboardInsertion()
    {
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new ConfigurableTargetDetector(target);
        var adapter = new ConfigurableAdapter(imageCapabilities: CapabilityStatus.Verified);
        var image = CreateImageReference();
        var mismatched = image with { PixelWidth = image.PixelWidth + 1 };
        var media = new RecordingMediaStore(image, [1, 2, 3], mismatched);
        using var single = new TextDeliveryStateMachine(detector, new FixedAdapterResolver(adapter), new RecordingClipboardTransaction(), static (_, _) => Task.CompletedTask);
        var machine = new BatchDeliveryStateMachine(
            single, detector, new RecordingStabilityWaiter([], VerificationResult.Verified), new FixedAdapterResolver(adapter), () => media,
            static (_, _) => Task.CompletedTask);

        var result = await machine.DeliverAsync(CreateBatchRequest(target, [PhraseSegment.CreateImage(image)]));

        Assert.Equal(DeliveryStatus.Failed, result.Status);
        Assert.Equal("MEDIA_ASSET_INVALID", result.SegmentResults[0].ErrorCode);
        Assert.Equal(0, adapter.ImageInsertCalls);
    }

    [Fact]
    public async Task VerifiedImageAdapterExceptionReturnsUnknownAndStopsWithoutUsageOrRetry()
    {
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new ConfigurableTargetDetector(target);
        var adapter = new ConfigurableAdapter(imageCapabilities: CapabilityStatus.Verified) { ThrowOnImageVerify = true };
        var image = CreateImageReference();
        var media = new RecordingMediaStore(image, [1, 2, 3]);
        var usageCalls = 0;
        using var single = new TextDeliveryStateMachine(detector, new FixedAdapterResolver(adapter), new RecordingClipboardTransaction(), static (_, _) => Task.CompletedTask);
        var machine = new BatchDeliveryStateMachine(
            single, detector, new RecordingStabilityWaiter([], VerificationResult.Verified), new FixedAdapterResolver(adapter), () => media,
            (_, _) => { usageCalls++; return Task.CompletedTask; });

        var result = await machine.DeliverAsync(CreateBatchRequest(target, [PhraseSegment.CreateImage(image), PhraseSegment.CreateText("不得执行") ]));

        Assert.Equal(DeliveryStatus.Unknown, result.Status);
        Assert.Equal(DeliveryEffect.Unknown, result.Effect);
        Assert.Equal("IMAGE_DELIVERY_EXCEPTION", result.SegmentResults[0].ErrorCode);
        Assert.Equal(0, result.CompletedSegments);
        Assert.Equal(1, adapter.ImageInsertCalls);
        Assert.Equal(1, adapter.VerifyImageCalls);
        Assert.Equal(0, adapter.SendCalls);
        Assert.Equal(0, usageCalls);
    }

    [Fact]
    public async Task UnsupportedProductionImageCapabilityStopsBeforeReadingMediaOrInserting()
    {
        var target = CreateTarget("WXWork", "WXWork");
        var detector = new ConfigurableTargetDetector(target);
        var adapter = new ConfigurableAdapter(imageCapabilities: CapabilityStatus.Unsupported);
        var image = CreateImageReference();
        var media = new RecordingMediaStore(image, [1, 2, 3]);
        using var single = new TextDeliveryStateMachine(detector, new FixedAdapterResolver(adapter), new RecordingClipboardTransaction(), static (_, _) => Task.CompletedTask);
        var machine = new BatchDeliveryStateMachine(
            single, detector, new RecordingStabilityWaiter([], VerificationResult.Verified), new FixedAdapterResolver(adapter), () => media,
            static (_, _) => Task.CompletedTask);

        var result = await machine.DeliverAsync(CreateBatchRequest(target, [PhraseSegment.CreateImage(image)]));

        Assert.Equal(DeliveryStatus.Unsupported, result.Status);
        Assert.Equal(0, media.ReadCalls);
        Assert.Equal(0, adapter.ImageInsertCalls);
        Assert.Equal(0, adapter.SendCalls);
    }

    private static DeliveryRequest CreateBatchRequest(
        DeliveryTarget target,
        PhraseSegment[]? segments = null,
        SendMode mode = SendMode.InsertAndSend)
    {
        var phrase = CreatePhrase(segments ?? [PhraseSegment.CreateText("第一段"), PhraseSegment.CreateText("第二段")]);
        return new DeliveryRequest(phrase, target, mode, ClipboardCompatibilityMode: true);
    }

    private static Phrase CreatePhrase(PhraseSegment[] segments)
    {
        var body = new PhraseBody(segments.ToImmutableArray(), PhraseBody.DefaultBatchSeparator);
        return new Phrase(
            Guid.NewGuid(),
            "批次话术",
            body,
            Guid.NewGuid(),
            ShortcutMode.None,
            null,
            0,
            null,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private static DeliveryTarget CreateTarget(string applicationId, string adapterId) =>
        new(applicationId, "WindowsDesktopWindow", adapterId, applicationId, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

    private sealed class RecordingSingleDelivery(List<string> events) : ITextDeliveryStateMachine
    {
        public int Calls { get; private set; }
        public List<DeliveryRequest> Requests { get; } = [];

        public Task<DeliveryResult> DeliverAsync(DeliveryRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Requests.Add(request);
            events.Add($"Deliver:{Calls}");
            return Task.FromResult(new DeliveryResult(
                DeliveryStatus.Success,
                DeliveryEffect.SendTriggered,
                DeliveryStage.Completed,
                DeliveryConfidence.Confirmed,
                "SEND_TRIGGERED",
                "已触发发送。",
                false,
                Guid.NewGuid()));
        }
    }

    private sealed class RecordingTargetDetector(DeliveryTarget target, List<string> events) : ITargetDetector
    {
        private int _validationCalls;
        public bool IsValid { get; set; } = true;
        public DeliveryTarget? CaptureForeground() => target;

        public TargetValidationResult Validate(DeliveryTarget identity, bool requireForeground)
        {
            _validationCalls++;
            events.Add($"Validate:{_validationCalls}");
            return IsValid && identity == target
                ? TargetValidationResult.Valid
                : TargetValidationResult.Invalid("TARGET_CHANGED", "目标窗口或输入焦点已变化。");
        }
    }

    private sealed class RecordingStabilityWaiter(
        List<string> events,
        VerificationResult result,
        Action? onWait = null) : IAdapterBatchStabilityWaiter
    {
        public int Calls { get; private set; }

        public Task<VerificationResult> WaitForStabilityAsync(DeliveryTarget target, CancellationToken cancellationToken)
        {
            Calls++;
            events.Add("Wait");
            onWait?.Invoke();
            return Task.FromResult(result);
        }
    }

    private static PhraseImageReference CreateImageReference() =>
        new(Guid.NewGuid(), "image/png", 3, 1, 1);

    private sealed class ConfigurableTargetDetector(DeliveryTarget target) : ITargetDetector
    {
        private int _validationCalls;
        public int? FailOnValidationCall { get; set; }
        public DeliveryTarget? CaptureForeground() => target;
        public TargetValidationResult Validate(DeliveryTarget identity, bool requireForeground)
        {
            _validationCalls++;
            return identity == target && FailOnValidationCall != _validationCalls
                ? TargetValidationResult.Valid
                : TargetValidationResult.Invalid("TARGET_CHANGED", "目标窗口或输入焦点已变化。");
        }
    }

    private sealed class FixedAdapterResolver(IApplicationAdapter adapter) : IAdapterResolver
    {
        public IApplicationAdapter Resolve(DeliveryTarget target, string? productVersion = null) => adapter;
    }

    private sealed class ConfigurableAdapter(CapabilityStatus imageCapabilities = CapabilityStatus.Unsupported) : IApplicationAdapter, IImageApplicationAdapter
    {
        public string AdapterId => "TestAdapter";
        public string? DetectedProductVersion => "test";
        public AdapterCapabilities Capabilities { get; } = new(
            CapabilityStatus.Verified,
            CapabilityStatus.Verified,
            imageCapabilities,
            imageCapabilities,
            CapabilityStatus.Verified,
            CapabilityStatus.Unsupported);
        public AdapterProfile Profile => new(
            AdapterId, "WXWork", "test", Capabilities.InsertText, Capabilities.VerifyTextInsert,
            Capabilities.InsertImage, Capabilities.VerifyImageInsert, Capabilities.TriggerSend, Capabilities.VerifySend,
            "Cancel", null);
        public Queue<VerificationResult> VerifyTextResults { get; } = new();
        public InsertResult ImageInsertResult { get; set; } = InsertResult.Applied;
        public VerificationResult ImageVerificationResult { get; set; } = VerificationResult.Verified;
        public bool ThrowOnImageVerify { get; set; }
        public int ImageInsertCalls { get; private set; }
        public int VerifyImageCalls { get; private set; }
        public int SendCalls { get; private set; }
        public AdapterCapabilities DetectCapabilities() => Capabilities;
        public Task<InsertResult> InsertAsync(DeliveryRequest request, CancellationToken cancellationToken) => Task.FromResult(InsertResult.Applied);
        public Task<VerificationResult> VerifyInsertAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(VerifyTextResults.Count == 0 ? VerificationResult.Verified : VerifyTextResults.Dequeue());
        public Task<InsertResult> InsertImageAsync(DeliveryRequest request, MediaAssetContent image, CancellationToken cancellationToken)
        {
            ImageInsertCalls++;
            return Task.FromResult(ImageInsertResult);
        }
        public Task<VerificationResult> VerifyImageInsertAsync(DeliveryRequest request, CancellationToken cancellationToken)
        {
            VerifyImageCalls++;
            if (ThrowOnImageVerify) throw new InvalidOperationException("敏感图片异常内容不得进入结果或日志");
            return Task.FromResult(ImageVerificationResult);
        }
        public Task<SendResult> SendAsync(DeliveryRequest request, CancellationToken cancellationToken)
        {
            SendCalls++;
            return Task.FromResult(SendResult.Applied);
        }
        public Task<VerificationResult> VerifySendAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(VerificationResult.Inconclusive("SEND_RESULT_UNVERIFIED"));
    }

    private sealed class RecordingClipboardTransaction : IClipboardTransaction
    {
        public Task<ClipboardResult> CopyOnlyAsync(string text, CancellationToken cancellationToken) => Task.FromResult(ClipboardResult.Copied);
        public Task<ClipboardResult> PasteAsync(string text, DeliveryTarget target, CancellationToken cancellationToken) => Task.FromResult(ClipboardResult.Pasted);
        public Task<ClipboardResult> PasteImageAsync(byte[] normalizedImage, DeliveryTarget target, CancellationToken cancellationToken) => Task.FromResult(ClipboardResult.Pasted);
    }

    private sealed class RecordingMediaStore(PhraseImageReference image, byte[] bytes, PhraseImageReference? returnedImage = null) : IMediaAssetStore
    {
        public int ReadCalls { get; private set; }
        public Task<MediaImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<MediaAssetContent?> ReadAsync(Guid assetId, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return Task.FromResult<MediaAssetContent?>(assetId == image.AssetId ? new MediaAssetContent(returnedImage ?? image, bytes) : null);
        }
    }

}
