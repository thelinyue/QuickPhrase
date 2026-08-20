using System.Collections.Immutable;
using System.IO;
using QuickPhrase.Core;
using QuickPhrase.Desktop;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

public sealed class Phase5DeliveryTests
{
    [Fact]
    public async Task WeComFocusWaiterWaitsForCaretAfterLauncherCloses()
    {
        var target = CreateWindowsTarget();
        var invalid = new WeComFocusFingerprint(
            target.Hwnd, target.Hwnd, "WeWorkWindow", 0, 1346, 650, 338, 455, 339, 474);
        var valid = invalid with { Flags = 1 };
        var attempts = 0;

        var result = await WeComFocusWaiter.WaitAsync(
            target,
            _ => ++attempts < 3 ? invalid : valid,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task DeliveryQueueAcceptsOneActiveAndFourWaitingInFifoOrder()
    {
        var machine = new ControlledDeliveryMachine();
        await using var queue = new DeliveryQueueCoordinator(machine, maxPending: 4);
        var target = CreateTarget();
        var tickets = Enumerable.Range(1, 5)
            .Select(index => queue.TryEnqueue(CreateRequest(CreatePhrase($"话术 {index}"), target), Guid.NewGuid()))
            .ToArray();

        Assert.All(tickets, ticket => Assert.True(ticket.Accepted));
        var overflow = queue.TryEnqueue(CreateRequest(CreatePhrase("话术 6"), target), Guid.NewGuid());
        Assert.False(overflow.Accepted);
        Assert.Equal("DELIVERY_QUEUE_FULL", overflow.Code);

        for (var index = 1; index <= 5; index++)
        {
            var invocation = await machine.NextInvocationAsync();
            Assert.Equal($"话术 {index}", invocation.Request.Phrase.Title);
            invocation.Complete(Success(invocation.Request));
        }

        var results = await Task.WhenAll(tickets.Select(ticket => ticket.Completion!));
        Assert.All(results, result => Assert.True(result.Inserted));
        Assert.Equal(new DeliveryQueueStatus(false, 0), queue.Status);
    }


    [Fact]
    public async Task DeliveryQueueDoesNotRunDeliveryInlineOnTheCallerThread()
    {
        var machine = new BlockingStartDeliveryMachine();
        await using var queue = new DeliveryQueueCoordinator(machine, maxPending: 4);
        var target = CreateTarget();
        var enqueueTask = Task.Run(() => queue.TryEnqueue(
            CreateRequest(CreatePhrase("不会阻塞闪念"), target),
            Guid.NewGuid()));

        Assert.True(machine.WaitUntilEntered(TimeSpan.FromSeconds(1)));
        try
        {
            var completed = await Task.WhenAny(enqueueTask, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(enqueueTask, completed);
        }
        finally
        {
            machine.Release();
        }

        var ticket = await enqueueTask;
        Assert.True(ticket.Accepted);
        Assert.True((await ticket.Completion!).Inserted);
    }

    [Fact]
    public async Task AdapterResolutionDoesNotRunInlineOnTheCallerThread()
    {
        var resolver = new BlockingAdapterResolver(new FakeAdapter("WXWork", CapabilityStatus.Unverified));

        var resolution = ApplicationController.ResolveAdapterOffUiThreadAsync(
            resolver,
            CreateTarget(),
            CancellationToken.None);

        Assert.True(resolver.WaitUntilEntered(TimeSpan.FromSeconds(1)));
        try
        {
            Assert.False(resolution.IsCompleted);
        }
        finally
        {
            resolver.Release();
        }

        Assert.Equal("WXWork", (await resolution).AdapterId);
    }

    [Fact]
    public async Task SingleDeliveryDoesNotRunPlatformWorkInlineOnTheCallerThread()
    {
        var machine = new BlockingStartDeliveryMachine();
        var request = CreateRequest(CreatePhrase("运行时能力投递"), CreateTarget());

        var delivery = ApplicationController.RunSingleDeliveryOffUiThreadAsync(
            machine,
            request,
            CancellationToken.None);

        Assert.True(machine.WaitUntilEntered(TimeSpan.FromSeconds(1)));
        try
        {
            Assert.False(delivery.IsCompleted);
        }
        finally
        {
            machine.Release();
        }

        Assert.True((await delivery).Inserted);
    }
    [Fact]
    public async Task TargetChangeCancelsSameTargetWaitingItemsWithoutExecutingThem()
    {
        var machine = new ControlledDeliveryMachine();
        await using var queue = new DeliveryQueueCoordinator(machine, maxPending: 4);
        var target = CreateTarget();
        var first = queue.TryEnqueue(CreateRequest(CreatePhrase("第一条"), target), Guid.NewGuid());
        var second = queue.TryEnqueue(CreateRequest(CreatePhrase("第二条"), target), Guid.NewGuid());
        var third = queue.TryEnqueue(CreateRequest(CreatePhrase("第三条"), target), Guid.NewGuid());

        var invocation = await machine.NextInvocationAsync();
        invocation.Complete(new DeliveryResult(DeliveryStatus.Cancelled, DeliveryEffect.None, DeliveryStage.Insert, DeliveryConfidence.Confirmed, "TARGET_CHANGED", "目标已变化。", false, Guid.NewGuid()));

        Assert.Equal("TARGET_CHANGED", (await first.Completion!).ErrorCode);
        Assert.Equal("DELIVERY_QUEUE_CANCELLED", (await second.Completion!).ErrorCode);
        Assert.Equal("DELIVERY_QUEUE_CANCELLED", (await third.Completion!).ErrorCode);
        Assert.Equal(1, machine.CallCount);
    }

    [Fact]
    public async Task UsageUpdateQueueDoesNotWaitForDatabaseWriteBeforeAcceptingNextItem()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<Guid>();
        await using var queue = new UsageUpdateQueue(async (id, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            lock (written) written.Add(id);
        });
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var firstAdmission = queue.EnqueueAsync(first, CancellationToken.None);
        var secondAdmission = queue.EnqueueAsync(second, CancellationToken.None);

        Assert.True(firstAdmission.IsCompletedSuccessfully);
        Assert.True(secondAdmission.IsCompletedSuccessfully);
        release.TrySetResult();
        await queue.DrainAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { first, second }, written);
    }

    [Fact]
    public void LauncherSubmissionGuardAcceptsOnlyOneEnterPerOpen()
    {
        var guard = new LauncherSubmissionGuard();

        Assert.True(guard.TrySubmit());
        Assert.False(guard.TrySubmit());
        guard.Reset();
        Assert.True(guard.TrySubmit());
    }


    [Fact]
    public void ContinuousQueueAcceptsOnlyVerifiedInsertOnlyRequests()
    {
        var verified = new AdapterProfile("AnyAdapter", "AnyApp", "runtime-capability-test",
            CapabilityStatus.Verified, CapabilityStatus.Verified, CapabilityStatus.Verified, CapabilityStatus.Unsupported, "CopyOnly", null);
        var unverified = verified with { InsertTextStatus = CapabilityStatus.Unverified };

        Assert.True(DeliveryQueuePolicy.CanQueue(verified, SendMode.InsertOnly));
        Assert.False(DeliveryQueuePolicy.CanQueue(verified, SendMode.InsertAndSend));
        Assert.False(DeliveryQueuePolicy.CanQueue(unverified, SendMode.InsertOnly));
    }

    [Fact]
    public void DeliveryTargetRejectsChangedProcessMetadata()
    {
        var detector = new FakeTargetDetector
        {
            Current = CreateTarget()
        };
        var captured = detector.CaptureForeground()!;
        detector.Current = captured with { RuntimeKey = Guid.NewGuid().ToString("N") };

        var result = detector.Validate(captured, requireForeground: true);

        Assert.False(result.IsValid);
        Assert.Equal("TARGET_CHANGED", result.ErrorCode);
    }

    [Fact]
    public async Task UnverifiedTargetUsesCopyOnlyAndNeverCallsAdapter()
    {
        var phrase = CreatePhrase();
        var adapter = new FakeAdapter("Unknown", CapabilityStatus.Unverified);
        var clipboard = new FakeClipboardTransaction();
        var engine = new TextDeliveryStateMachine(
            new FakeTargetDetector { Current = null },
            new FakeAdapterResolver(adapter),
            clipboard,
            static (_, _) => Task.CompletedTask);

        var result = await engine.DeliverAsync(new DeliveryRequest(phrase, null, SendMode.InsertOnly, true));

        Assert.Equal(DeliveryStatus.Unsupported, result.Status);
        Assert.Equal("TARGET_VALIDATION_FAILED", result.ErrorCode);
        Assert.Equal(0, adapter.InsertCalls);
        Assert.Equal(phrase.Content, clipboard.LastCopiedText);
    }

    [Fact]
    public async Task InsertVerificationInconclusiveNeverSendsOrRetries()
    {
        var phrase = CreatePhrase();
        var adapter = new FakeAdapter("WXWork", CapabilityStatus.Verified)
        {
            Capabilities = new AdapterCapabilities(
                CapabilityStatus.Verified,
                CapabilityStatus.Verified,
                CapabilityStatus.Verified,
                CapabilityStatus.Unsupported),
            VerifyInsertResult = VerificationResult.Inconclusive("INSERT_VERIFICATION_INCONCLUSIVE")
        };
        var detector = new FakeTargetDetector
        {
            Current = CreateTarget()
        };
        var clipboard = new FakeClipboardTransaction();
        var engine = new TextDeliveryStateMachine(detector, new FakeAdapterResolver(adapter), clipboard, static (_, _) => Task.CompletedTask);

        var result = await engine.DeliverAsync(new DeliveryRequest(phrase, detector.Current, SendMode.InsertAndSend, true));

        Assert.Equal("INSERT_VERIFICATION_INCONCLUSIVE", result.ErrorCode);
        Assert.Equal(DeliveryStatus.Unknown, result.Status);
        Assert.Equal(1, adapter.InsertCalls);
        Assert.Equal(0, adapter.SendCalls);
    }

    [Fact]
    public async Task InconclusiveInsertDoesNotFallbackToSecondPaste()
    {
        var phrase = CreatePhrase();
        var adapter = new FakeAdapter("WXWork", CapabilityStatus.Verified)
        {
            InsertResponse = new InsertResult(false, true, "INSERT_VERIFICATION_INCONCLUSIVE")
        };
        var detector = new FakeTargetDetector { Current = CreateTarget() };
        var clipboard = new FakeClipboardTransaction();
        var engine = new TextDeliveryStateMachine(detector, new FakeAdapterResolver(adapter), clipboard, static (_, _) => Task.CompletedTask);

        var result = await engine.DeliverAsync(new DeliveryRequest(phrase, detector.Current, SendMode.InsertOnly, true));

        Assert.Equal(DeliveryStatus.Unknown, result.Status);
        Assert.Equal("INSERT_VERIFICATION_INCONCLUSIVE", result.ErrorCode);
        Assert.Equal(0, clipboard.CopyOnlyCalls);
    }

    [Fact]
    public async Task CancelledDeliveryReturnsCancelledWithoutCopying()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var clipboard = new FakeClipboardTransaction();
        var engine = new TextDeliveryStateMachine(new FakeTargetDetector(), new FakeAdapterResolver(new FakeAdapter("Unknown", CapabilityStatus.Unverified)), clipboard, static (_, _) => Task.CompletedTask);

        var result = await engine.DeliverAsync(new DeliveryRequest(CreatePhrase(), null, SendMode.InsertOnly, true), cancellation.Token);

        Assert.Equal(DeliveryStatus.Cancelled, result.Status);
        Assert.Equal(0, clipboard.CopyOnlyCalls);
    }

    [Fact]
    public async Task TargetChangeCancelBehaviorDoesNotTouchClipboard()
    {
        var phrase = CreatePhrase();
        var target = CreateTarget();
        var clipboard = new FakeClipboardTransaction();
        var engine = new TextDeliveryStateMachine(new FakeTargetDetector { Current = target with { RuntimeKey = Guid.NewGuid().ToString("N") } }, new FakeAdapterResolver(new FakeAdapter("WXWork", CapabilityStatus.Verified)), clipboard, static (_, _) => Task.CompletedTask);

        var result = await engine.DeliverAsync(new DeliveryRequest(phrase, target, SendMode.InsertOnly, true, TargetChangeBehavior.Cancel));

        Assert.Equal("TARGET_CHANGED", result.ErrorCode);
        Assert.Equal(DeliveryStatus.Cancelled, result.Status);
        Assert.Equal(0, clipboard.CopyOnlyCalls);
    }

    [Fact]
    public async Task TargetChangeDuringInsertDoesNotFallbackToClipboard()
    {
        var phrase = CreatePhrase();
        var target = CreateTarget();
        var clipboard = new FakeClipboardTransaction();
        var adapter = new FakeAdapter("WXWork", CapabilityStatus.Verified)
        {
            InsertResponse = new InsertResult(false, false, "TARGET_CHANGED"),
        };
        var engine = new TextDeliveryStateMachine(new FakeTargetDetector { Current = target }, new FakeAdapterResolver(adapter), clipboard, static (_, _) => Task.CompletedTask);

        var result = await engine.DeliverAsync(new DeliveryRequest(phrase, target, SendMode.InsertOnly, true, TargetChangeBehavior.Cancel));

        Assert.Equal(DeliveryStatus.Cancelled, result.Status);
        Assert.Equal(0, clipboard.CopyOnlyCalls);
    }

    [Fact]
    public async Task SendSafetyGateRequiresVerifiedInsertAndForegroundTarget()
    {
        var phrase = CreatePhrase();
        var adapter = new FakeAdapter("WXWork", CapabilityStatus.Verified)
        {
            VerifyInsertResult = VerificationResult.Verified,
            Capabilities = new AdapterCapabilities(CapabilityStatus.Verified, CapabilityStatus.Verified, CapabilityStatus.Verified, CapabilityStatus.Verified)
        };
        var detector = new FakeTargetDetector
        {
            Current = CreateTarget()
        };
        var engine = new TextDeliveryStateMachine(detector, new FakeAdapterResolver(adapter), new FakeClipboardTransaction(), static (_, _) => Task.CompletedTask);

        var result = await engine.DeliverAsync(new DeliveryRequest(phrase, detector.Current, SendMode.InsertAndSend, false));

        Assert.Equal(DeliveryStatus.Success, result.Status);
        Assert.Equal(1, adapter.SendCalls);
    }

    [Fact]
    public async Task UiAutomationWorkerRunsOperationsOnOneMtaThread()
    {
        using var worker = new UiAutomationWorker();
        var first = await worker.InvokeAsync(() => (Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.GetApartmentState()), CancellationToken.None, TimeSpan.FromSeconds(1));
        var second = await worker.InvokeAsync(() => (Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.GetApartmentState()), CancellationToken.None, TimeSpan.FromSeconds(1));

        Assert.Equal(first.Item1, second.Item1);
        Assert.Equal(ApartmentState.MTA, first.Item2);
    }


    [Theory]
    [InlineData("5.0.9.6065")]
    [InlineData("5.1.0.100")]
    [InlineData("")]
    [InlineData(null)]
    public void WeComCapabilitiesDoNotDependOnDetectedVersion(string? version)
    {
        using var resolver = new WindowsAdapterResolver(_ => version, _ => true);
        var adapter = resolver.Resolve(CreateTarget());

        Assert.Equal(CapabilityStatus.Verified, adapter.Profile.InsertTextStatus);
        Assert.Equal(CapabilityStatus.Verified, adapter.Profile.VerifyInsertStatus);
        Assert.Equal(CapabilityStatus.Verified, adapter.Profile.SendTextStatus);
        Assert.Equal(CapabilityStatus.Unsupported, adapter.Profile.VerifySendStatus);
        Assert.Equal(version, adapter.DetectedProductVersion);
    }

    [Fact]
    public void WeComCapabilitiesRemainAvailableWhenVersionReaderFails()
    {
        using var resolver = new WindowsAdapterResolver(_ => throw new IOException("无法读取版本"), _ => true);

        var adapter = resolver.Resolve(CreateTarget());

        Assert.Equal(CapabilityStatus.Verified, adapter.DetectCapabilities().InsertText);
        Assert.Equal(CapabilityStatus.Verified, adapter.DetectCapabilities().SendText);
        Assert.Null(adapter.DetectedProductVersion);
    }





    [Fact]
    public void WeComFocusPolicyAcceptsChatComposerAndRejectsTopSearch()
    {
        var target = CreateWindowsTarget();
        var chat = new WeComFocusFingerprint((nint)42, (nint)42, "WeWorkWindow", 1, 1346, 650, 338, 574, 339, 593);
        var search = chat with { CaretLeft = 108, CaretTop = 76, CaretRight = 109, CaretBottom = 93 };

        Assert.True(WeComFocusPolicy.IsChatComposer(target, chat));
        Assert.False(WeComFocusPolicy.IsChatComposer(target, search));
    }

    [Fact]
    public void KeyboardInputUsesNativeWin32InputSize()
    {
        Assert.Equal(40, System.Runtime.InteropServices.Marshal.SizeOf<WindowsNativeMethods.KeyboardInput>());
        Assert.Equal(32, System.Runtime.InteropServices.Marshal.SizeOf<WindowsNativeMethods.KeyboardInputData>());
    }



    [Fact]
    public async Task DeliveryTraceIncludesDetectedProductVersion()
    {
        var traces = new List<DeliveryTrace>();
        var phrase = CreatePhrase();
        var adapter = new FakeAdapter("WXWork", CapabilityStatus.Verified)
        {
            VerifyInsertResult = VerificationResult.Inconclusive("INSERT_VERIFICATION_INCONCLUSIVE")
        };
        var target = CreateTarget();
        var engine = new TextDeliveryStateMachine(new FakeTargetDetector { Current = target }, new FakeAdapterResolver(adapter), new FakeClipboardTransaction(), static (_, _) => Task.CompletedTask, traces.Add);

        await engine.DeliverAsync(new DeliveryRequest(phrase, target, SendMode.InsertOnly, true));

        Assert.Contains(traces, trace => trace.AdapterId == "WXWork" && trace.ProductVersion == "5.0.9.6065");
    }

    [Fact]
    public async Task FallbackTraceIncludesDetectedProductVersion()
    {
        var traces = new List<DeliveryTrace>();
        var phrase = CreatePhrase();
        var adapter = new FakeAdapter("WXWork", CapabilityStatus.Verified)
        {
            InsertResponse = new InsertResult(false, false, "TARGET_CONTROL_PROFILE_MISMATCH")
        };
        var target = CreateTarget();
        var engine = new TextDeliveryStateMachine(new FakeTargetDetector { Current = target }, new FakeAdapterResolver(adapter), new FakeClipboardTransaction(), static (_, _) => Task.CompletedTask, traces.Add);

        await engine.DeliverAsync(new DeliveryRequest(phrase, target, SendMode.InsertOnly, false));

        Assert.Contains(traces, trace => trace.Stage == DeliveryStage.Fallback && trace.ProductVersion == "5.0.9.6065");
    }

    [Fact]
    public void DeliveryTraceWriterOmitsUserContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "QuickPhraseTraceTests", Guid.NewGuid().ToString("N"));
        using (var writer = new DeliveryTraceWriter(directory))
        {
            writer.Write(new DeliveryTrace(Guid.NewGuid(), DeliveryStage.Fallback, "Unknown", "unverified", "notepad", null, "CLIPBOARD_FAILED", 12.5, DateTimeOffset.UtcNow));
        }

        var file = Directory.GetFiles(directory, "delivery-*.jsonl").Single();
        var json = File.ReadAllText(file);
        Assert.Contains("CLIPBOARD_FAILED", json);
        Assert.DoesNotContain("正文", json);
        Directory.Delete(directory, recursive: true);
    }

    private static Phrase CreatePhrase() => new(
        Guid.NewGuid(), "请求设备序列号", "请提供设备序列号（SN），方便我们进一步确认设备信息。", Guid.NewGuid(),
        ShortcutMode.None, null, 0, null, 1,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static Phrase CreatePhrase(string title) => CreatePhrase() with { Id = Guid.NewGuid(), Title = title };

    private static DeliveryTarget CreateTarget() =>
        new("WXWork", "WindowsDesktopWindow", "WXWork", "WXWork", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

    private static WindowsTargetIdentity CreateWindowsTarget() =>
        new((nint)42, 7, 9, DateTimeOffset.UtcNow.AddMinutes(-1), "WXWork", DateTimeOffset.UtcNow);

    private static DeliveryRequest CreateRequest(Phrase phrase, DeliveryTarget target) =>
        new(phrase, target, SendMode.InsertOnly, true, TargetChangeBehavior.Cancel);

    private static DeliveryResult Success(DeliveryRequest request) =>
        new(DeliveryStatus.Success, DeliveryEffect.Inserted, DeliveryStage.Completed, DeliveryConfidence.Confirmed, "INSERTED", "已插入。", false, Guid.NewGuid());

    private sealed class BlockingStartDeliveryMachine : ITextDeliveryStateMachine
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public Task<DeliveryResult> DeliverAsync(DeliveryRequest request, CancellationToken cancellationToken = default)
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(5), cancellationToken))
                throw new TimeoutException("测试未能及时释放同步投递入口。");
            return Task.FromResult(Success(request));
        }

        public bool WaitUntilEntered(TimeSpan timeout) => _entered.Wait(timeout);
        public void Release() => _release.Set();
    }

    private sealed class ControlledDeliveryMachine : ITextDeliveryStateMachine
    {
        private readonly System.Threading.Channels.Channel<Invocation> _invocations =
            System.Threading.Channels.Channel.CreateUnbounded<Invocation>();
        public int CallCount { get; private set; }

        public async Task<DeliveryResult> DeliverAsync(DeliveryRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var invocation = new Invocation(request);
            await _invocations.Writer.WriteAsync(invocation, cancellationToken);
            return await invocation.Task.WaitAsync(cancellationToken);
        }

        public Task<Invocation> NextInvocationAsync() => _invocations.Reader.ReadAsync().AsTask();
    }

    private sealed class Invocation(DeliveryRequest request)
    {
        private readonly TaskCompletionSource<DeliveryResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DeliveryRequest Request { get; } = request;
        public Task<DeliveryResult> Task => _completion.Task;
        public void Complete(DeliveryResult result) => _completion.TrySetResult(result);
    }

    private sealed class FakeTargetDetector : ITargetDetector
    {
        public DeliveryTarget? Current { get; set; }
        public DeliveryTarget? CaptureForeground() => Current;
        public TargetValidationResult Validate(DeliveryTarget identity, bool requireForeground) =>
            Current is null ? TargetValidationResult.Invalid("TARGET_VALIDATION_FAILED", "没有可用目标。") :
            Current == identity ? TargetValidationResult.Valid : TargetValidationResult.Invalid("TARGET_CHANGED", "目标已变化。", identity);
    }

    private sealed class FakeAdapterResolver(IApplicationAdapter adapter) : IAdapterResolver
    {
        public IApplicationAdapter Resolve(DeliveryTarget target, string? productVersion = null) => adapter;
    }


    private sealed class BlockingAdapterResolver(IApplicationAdapter adapter) : IAdapterResolver
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public IApplicationAdapter Resolve(DeliveryTarget target, string? productVersion = null)
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("测试未能及时释放同步适配器解析入口。");
            return adapter;
        }

        public bool WaitUntilEntered(TimeSpan timeout) => _entered.Wait(timeout);
        public void Release() => _release.Set();
    }

    private sealed class FakeAdapter(string id, CapabilityStatus insertStatus) : IApplicationAdapter
    {
        public string AdapterId { get; } = id;
        public string? DetectedProductVersion => "5.0.9.6065";
        public AdapterProfile Profile => new(AdapterId, "WXWork", "phase5-test", Capabilities.InsertText, Capabilities.VerifyInsert, Capabilities.SendText, Capabilities.VerifySend, "CopyOnly", null);
        public AdapterCapabilities Capabilities { get; set; } = new(insertStatus, insertStatus, CapabilityStatus.Unsupported, CapabilityStatus.Unsupported);
        public VerificationResult VerifyInsertResult { get; set; } = VerificationResult.Verified;
        public int InsertCalls { get; private set; }
        public int SendCalls { get; private set; }
        public InsertResult InsertResponse { get; set; } = QuickPhrase.Core.InsertResult.Applied;
        public SendResult SendResponse { get; set; } = QuickPhrase.Core.SendResult.Applied;
        public VerificationResult VerifySendResult { get; set; } = VerificationResult.Verified;
        public AdapterCapabilities DetectCapabilities() => Capabilities;
        public Task<InsertResult> InsertAsync(DeliveryRequest request, CancellationToken cancellationToken) { InsertCalls++; return Task.FromResult(InsertResponse); }
        public Task<VerificationResult> VerifyInsertAsync(DeliveryRequest request, CancellationToken cancellationToken) => Task.FromResult(VerifyInsertResult);
        public Task<SendResult> SendAsync(DeliveryRequest request, CancellationToken cancellationToken) { SendCalls++; return Task.FromResult(SendResponse); }
        public Task<VerificationResult> VerifySendAsync(DeliveryRequest request, CancellationToken cancellationToken) => Task.FromResult(VerifySendResult);
    }

    private sealed class FakeClipboardTransaction : IClipboardTransaction
    {
        public string? LastCopiedText { get; private set; }
        public int CopyOnlyCalls { get; private set; }
        public Task<ClipboardResult> CopyOnlyAsync(string text, CancellationToken cancellationToken) { CopyOnlyCalls++; LastCopiedText = text; return Task.FromResult(ClipboardResult.Copied); }
        public Task<ClipboardResult> PasteAsync(string text, DeliveryTarget target, CancellationToken cancellationToken) { LastCopiedText = text; return Task.FromResult(ClipboardResult.Pasted); }
    }
}
