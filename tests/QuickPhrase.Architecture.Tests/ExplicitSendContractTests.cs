using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

public sealed class ExplicitSendContractTests
{
    [Fact]
    public void SendModeSeparatesInsertOnlyFromExplicitSend()
    {
        Assert.NotEqual(SendMode.InsertOnly, SendMode.InsertAndSend);
    }

    [Fact]
    public async Task InsertOnlyNeverEntersSendStage()
    {
        var fixture = CreateFixture(sendStatus: CapabilityStatus.Verified);

        var result = await fixture.Machine.DeliverAsync(CreateRequest(fixture.Target, SendMode.InsertOnly));

        Assert.Equal(DeliveryStatus.Success, result.Status);
        Assert.Equal(DeliveryEffect.Inserted, result.Effect);
        Assert.Equal(0, fixture.Adapter.SendCalls);
    }

    [Fact]
    public async Task InsertAndSendWithoutTargetDoesNotFallBackToClipboard()
    {
        var target = CreateTarget();
        var clipboard = new RecordingClipboard();
        using var machine = new TextDeliveryStateMachine(
            new RecordingTargetDetector(target, new List<string>()),
            new StaticAdapterResolver(new RecordingAdapter(new List<string>())),
            clipboard,
            static (_, _) => Task.CompletedTask);

        var result = await machine.DeliverAsync(CreateRequest(null, SendMode.InsertAndSend));

        Assert.Equal(DeliveryStatus.Failed, result.Status);
        Assert.Equal(DeliveryEffect.None, result.Effect);
        Assert.Equal(0, clipboard.CopyCalls);
    }

    [Fact]
    public async Task InsertAndSendWithChangedTargetDoesNotFallBackToClipboard()
    {
        var capturedTarget = CreateTarget();
        var clipboard = new RecordingClipboard();
        using var machine = new TextDeliveryStateMachine(
            new RecordingTargetDetector(CreateTarget(), new List<string>()),
            new StaticAdapterResolver(new RecordingAdapter(new List<string>())),
            clipboard,
            static (_, _) => Task.CompletedTask);

        var result = await machine.DeliverAsync(CreateRequest(capturedTarget, SendMode.InsertAndSend));

        Assert.Equal(DeliveryStatus.Failed, result.Status);
        Assert.Equal(DeliveryEffect.None, result.Effect);
        Assert.Equal(0, clipboard.CopyCalls);
    }

    [Fact]
    public async Task InsertAndSendWithUnsupportedInsertDoesNotFallBackToClipboard()
    {
        var target = CreateTarget();
        var adapter = new RecordingAdapter(new List<string>())
        {
            Capabilities = new AdapterCapabilities(
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Verified,
                CapabilityStatus.Unsupported),
        };
        var clipboard = new RecordingClipboard();
        using var machine = new TextDeliveryStateMachine(
            new RecordingTargetDetector(target, new List<string>()),
            new StaticAdapterResolver(adapter),
            clipboard,
            static (_, _) => Task.CompletedTask);

        var result = await machine.DeliverAsync(CreateRequest(target, SendMode.InsertAndSend));

        Assert.Equal(DeliveryStatus.Unsupported, result.Status);
        Assert.Equal(DeliveryEffect.None, result.Effect);
        Assert.Equal(0, adapter.InsertCalls);
        Assert.Equal(0, clipboard.CopyCalls);
    }

    [Fact]
    public async Task InsertAndSendRejectsUnsupportedSendBeforeInsert()
    {
        var fixture = CreateFixture(sendStatus: CapabilityStatus.Unsupported);

        var result = await fixture.Machine.DeliverAsync(CreateRequest(fixture.Target, SendMode.InsertAndSend));

        Assert.Equal(DeliveryStatus.Unsupported, result.Status);
        Assert.Equal("UNSUPPORTED_SEND", result.ErrorCode);
        Assert.Equal(0, fixture.Adapter.InsertCalls);
        Assert.Equal(0, fixture.Adapter.SendCalls);
    }

    [Fact]
    public async Task InsertAndSendRunsInsertVerifyRevalidateAndSendInOrder()
    {
        var operations = new List<string>();
        var target = CreateTarget();
        var detector = new RecordingTargetDetector(target, operations);
        var adapter = new RecordingAdapter(operations)
        {
            Capabilities = new AdapterCapabilities(
                CapabilityStatus.Verified,
                CapabilityStatus.Verified,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Verified,
                CapabilityStatus.Unsupported),
        };
        using var machine = new TextDeliveryStateMachine(
            detector,
            new StaticAdapterResolver(adapter),
            new RecordingClipboard(),
            static (_, _) => Task.CompletedTask);

        var result = await machine.DeliverAsync(CreateRequest(target, SendMode.InsertAndSend));

        Assert.Equal(
            new[] { "Validate:False", "Insert", "VerifyInsert", "Validate:True", "Send" },
            operations);
        Assert.Equal(DeliveryEffect.SendTriggered, result.Effect);
    }

    [Fact]
    public async Task InsertVerificationFailurePreservesInsertedEffectAndStopsSend()
    {
        var fixture = CreateFixture(sendStatus: CapabilityStatus.Verified);
        fixture.Adapter.VerifyInsertResult = VerificationResult.Failed("TARGET_CONTROL_PROFILE_MISMATCH");

        var result = await fixture.Machine.DeliverAsync(CreateRequest(fixture.Target, SendMode.InsertAndSend));

        Assert.Equal(DeliveryStatus.Failed, result.Status);
        Assert.Equal(DeliveryEffect.Inserted, result.Effect);
        Assert.Equal("TARGET_CONTROL_PROFILE_MISMATCH", result.ErrorCode);
        Assert.Equal(0, fixture.Adapter.SendCalls);
    }

    [Fact]
    public async Task UnsupportedSendVerificationReportsTriggeredInsteadOfSent()
    {
        var fixture = CreateFixture(sendStatus: CapabilityStatus.Verified, verifySendStatus: CapabilityStatus.Unsupported);

        var result = await fixture.Machine.DeliverAsync(CreateRequest(fixture.Target, SendMode.InsertAndSend));

        Assert.Equal(DeliveryStatus.Success, result.Status);
        Assert.Equal(DeliveryEffect.SendTriggered, result.Effect);
        Assert.True(result.SendTriggered);
        Assert.False(result.Sent);
    }

    [Fact]
    public async Task InconclusiveSendReturnsUnknownAndDoesNotRetry()
    {
        var fixture = CreateFixture(sendStatus: CapabilityStatus.Verified);
        fixture.Adapter.SendResult = SendResult.Unknown("SEND_INPUT_INCONCLUSIVE");

        var result = await fixture.Machine.DeliverAsync(CreateRequest(fixture.Target, SendMode.InsertAndSend));

        Assert.Equal(DeliveryStatus.Unknown, result.Status);
        Assert.Equal(DeliveryEffect.Unknown, result.Effect);
        Assert.Equal(1, fixture.Adapter.SendCalls);
    }

    [Fact]
    public void WeComSendInjectionUsesOneCompleteEnterKeySequence()
    {
        var calls = new List<WindowsNativeMethods.KeyboardInput[]>();

        var result = WindowsNativeMethods.SendEnter((count, inputs, _) =>
        {
            calls.Add(inputs.ToArray());
            return count;
        });

        Assert.Equal(KeyboardInjectionResult.Applied, result);
        var sequence = Assert.Single(calls);
        Assert.Equal(
            new[]
            {
                (WindowsNativeMethods.VirtualKeyReturn, 0u),
                (WindowsNativeMethods.VirtualKeyReturn, WindowsNativeMethods.KeyEventKeyUp),
            },
            sequence.Select(input => (input.Data.Key.VirtualKey, input.Data.Key.Flags)).ToArray());

        var adapterSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop",
            "QuickPhrase.Platform.Windows",
            "WindowsAdapterResolver.cs"));
        Assert.Contains("return WindowsNativeMethods.SendEnter() switch", adapterSource);
        Assert.DoesNotContain("return WindowsNativeMethods.SendCtrlEnter() switch", adapterSource);
    }

    [Fact]
    public void PartialEnterInjectionOnlyAttemptsKeyReleaseCleanup()
    {
        var calls = new List<WindowsNativeMethods.KeyboardInput[]>();

        var result = WindowsNativeMethods.SendEnter((_, inputs, _) =>
        {
            calls.Add(inputs.ToArray());
            return calls.Count == 1 ? 1u : (uint)inputs.Length;
        });

        Assert.Equal(KeyboardInjectionResult.Inconclusive, result);
        Assert.Equal(2, calls.Count);
        var cleanup = Assert.Single(calls[1]);
        Assert.Equal(WindowsNativeMethods.VirtualKeyReturn, cleanup.Data.Key.VirtualKey);
        Assert.Equal(WindowsNativeMethods.KeyEventKeyUp, cleanup.Data.Key.Flags);
    }
    [Fact]
    public void FocusStabilityRequiresSameComposerIdentityButAllowsCaretMovement()
    {
        var target = new WindowsTargetIdentity((nint)42, 7, 9, DateTimeOffset.UtcNow.AddMinutes(-1), "WXWork", DateTimeOffset.UtcNow);
        var before = new WeComFocusFingerprint((nint)42, (nint)42, "WeWorkWindow", 1, 1346, 650, 338, 574, 339, 593);
        var movedCaret = before with { CaretLeft = 500, CaretRight = 501 };
        var changedFocus = movedCaret with { FocusHwnd = (nint)43 };

        Assert.True(WeComFocusPolicy.IsStableChatComposer(target, before, movedCaret));
        Assert.False(WeComFocusPolicy.IsStableChatComposer(target, before, changedFocus));
    }

    [Fact]
    public void WeComExplicitSendWaitsForPostPasteStabilityBeforeRecapturingFocus()
    {
        Assert.True(WeComFocusPolicy.PostPasteStabilizationDelay >= TimeSpan.FromMilliseconds(100));

        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop",
            "QuickPhrase.Platform.Windows",
            "WindowsAdapterResolver.cs"));
        var waitIndex = source.IndexOf(
            "await WeComFocusPolicy.WaitForPostPasteStabilityAsync",
            StringComparison.Ordinal);
        var recaptureIndex = source.IndexOf(
            "var after = await WaitForComposerFingerprintAsync",
            waitIndex >= 0 ? waitIndex : 0,
            StringComparison.Ordinal);

        Assert.True(waitIndex >= 0, "企业微信粘贴后必须先等待输入区处理剪贴板消息。");
        Assert.True(recaptureIndex > waitIndex, "稳定等待结束后必须重新采集焦点/Caret 指纹，不能盲目发送。");
    }
    private static Fixture CreateFixture(
        CapabilityStatus sendStatus,
        CapabilityStatus verifySendStatus = CapabilityStatus.Unsupported)
    {
        var target = CreateTarget();
        var adapter = new RecordingAdapter(new List<string>())
        {
            Capabilities = new AdapterCapabilities(
                CapabilityStatus.Verified,
                CapabilityStatus.Verified,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unsupported,
                sendStatus,
                verifySendStatus),
        };
        var machine = new TextDeliveryStateMachine(
            new RecordingTargetDetector(target, new List<string>()),
            new StaticAdapterResolver(adapter),
            new RecordingClipboard(),
            static (_, _) => Task.CompletedTask);
        return new Fixture(target, adapter, machine);
    }

    private static DeliveryRequest CreateRequest(DeliveryTarget? target, SendMode mode) =>
        new(CreatePhrase(), target, mode, ClipboardCompatibilityMode: true);

    private static Phrase CreatePhrase() => new(
        Guid.NewGuid(), "测试话术", PhraseBody.FromText("测试正文"), Guid.NewGuid(), ShortcutMode.None, null, 0, null, 1,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static DeliveryTarget CreateTarget() =>
        new("WXWork", "WindowsDesktopWindow", "WXWork", "WXWork", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
    private sealed record Fixture(DeliveryTarget Target, RecordingAdapter Adapter, TextDeliveryStateMachine Machine);

    private sealed class RecordingTargetDetector(DeliveryTarget target, List<string> operations) : ITargetDetector
    {
        public DeliveryTarget? CaptureForeground() => target;

        public TargetValidationResult Validate(DeliveryTarget identity, bool requireForeground)
        {
            operations.Add($"Validate:{requireForeground}");
            return identity == target ? TargetValidationResult.Valid : TargetValidationResult.Invalid("TARGET_CHANGED", "目标已变化。");
        }
    }

    private sealed class StaticAdapterResolver(IApplicationAdapter adapter) : IAdapterResolver
    {
        public IApplicationAdapter Resolve(DeliveryTarget target, string? productVersion = null) => adapter;
    }

    private sealed class RecordingAdapter(List<string> operations) : IApplicationAdapter
    {
        public string AdapterId => "Test";
        public string? DetectedProductVersion => "diagnostic-version";
        public AdapterProfile Profile => new(
            AdapterId,
            "TestApp",
            "explicit-send-tests",
            Capabilities.InsertText,
            Capabilities.VerifyTextInsert,
            Capabilities.InsertImage,
            Capabilities.VerifyImageInsert,
            Capabilities.TriggerSend,
            Capabilities.VerifySend,
            "CopyOnly",
            null);
        public AdapterCapabilities Capabilities { get; set; } = new(
            CapabilityStatus.Verified,
            CapabilityStatus.Verified,
            CapabilityStatus.Unsupported,
            CapabilityStatus.Unsupported,
            CapabilityStatus.Verified,
            CapabilityStatus.Unsupported);
        public VerificationResult VerifyInsertResult { get; set; } = VerificationResult.Verified;
        public SendResult SendResult { get; set; } = QuickPhrase.Core.SendResult.Applied;
        public int InsertCalls { get; private set; }
        public int SendCalls { get; private set; }

        public AdapterCapabilities DetectCapabilities() => Capabilities;

        public Task<InsertResult> InsertAsync(DeliveryRequest request, CancellationToken cancellationToken)
        {
            InsertCalls++;
            operations.Add("Insert");
            return Task.FromResult(InsertResult.Applied);
        }

        public Task<VerificationResult> VerifyInsertAsync(DeliveryRequest request, CancellationToken cancellationToken)
        {
            operations.Add("VerifyInsert");
            return Task.FromResult(VerifyInsertResult);
        }

        public Task<SendResult> SendAsync(DeliveryRequest request, CancellationToken cancellationToken)
        {
            SendCalls++;
            operations.Add("Send");
            return Task.FromResult(SendResult);
        }

        public Task<VerificationResult> VerifySendAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(VerificationResult.Verified);
    }

    private sealed class RecordingClipboard : IClipboardTransaction
    {
        public int CopyCalls { get; private set; }

        public Task<ClipboardResult> CopyOnlyAsync(string text, CancellationToken cancellationToken)
        {
            CopyCalls++;
            return Task.FromResult(ClipboardResult.Copied);
        }
        public Task<ClipboardResult> PasteAsync(string text, DeliveryTarget target, CancellationToken cancellationToken) => Task.FromResult(ClipboardResult.Pasted);
    }
}
