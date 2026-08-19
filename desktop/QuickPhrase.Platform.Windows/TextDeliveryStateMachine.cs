using System.Collections.Concurrent;
using System.Diagnostics;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// Windows 投递安全闸门。它只允许已验证的能力继续向后执行；任何目标、能力或验证异常都降级为复制，
/// 并且不会自动重试可能已经发生过的输入动作。
/// </summary>
internal sealed class TextDeliveryStateMachine : ITextDeliveryStateMachine, IDisposable
{
    private readonly ITargetDetector _targetDetector;
    private readonly IAdapterResolver _adapterResolver;
    private readonly IClipboardTransaction _clipboard;
    private readonly Func<Guid, CancellationToken, Task> _usageRecorder;
    private readonly Action<DeliveryTrace>? _traceWriter;
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, long> _traceStarts = new();

    internal TextDeliveryStateMachine(
        ITargetDetector targetDetector,
        IAdapterResolver adapterResolver,
        IClipboardTransaction clipboard,
        Func<Guid, CancellationToken, Task> usageRecorder,
        Action<DeliveryTrace>? traceWriter = null)
    {
        _targetDetector = targetDetector;
        _adapterResolver = adapterResolver;
        _clipboard = clipboard;
        _usageRecorder = usageRecorder;
        _traceWriter = traceWriter;
    }

    public async Task<DeliveryResult> DeliverAsync(DeliveryRequest request, CancellationToken cancellationToken = default)
    {
        bool acquired;
        try { acquired = await _deliveryGate.WaitAsync(0, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return NewResult(DeliveryStatus.Cancelled, DeliveryEffect.None, DeliveryStage.NotStarted, DeliveryConfidence.Confirmed, "DELIVERY_CANCELLED", "投递已取消。", false, Guid.NewGuid()); }
        if (!acquired)
            return NewResult(DeliveryStatus.Failed, DeliveryEffect.None, DeliveryStage.NotStarted, DeliveryConfidence.Confirmed, "DELIVERY_BUSY", "上一条话术仍在处理中。", false, Guid.NewGuid());

        var traceId = Guid.NewGuid();
        _traceStarts[traceId] = Stopwatch.GetTimestamp();
        var currentStage = DeliveryStage.NotStarted;
        var actionStarted = false;
        IApplicationAdapter? activeAdapter = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Target is null)
            {
                if (request.TargetChangeBehavior == TargetChangeBehavior.Cancel)
                    return Result(traceId, DeliveryStatus.Cancelled, DeliveryEffect.None, DeliveryStage.ValidateTarget, DeliveryConfidence.Confirmed,
                        "TARGET_VALIDATION_FAILED", "没有可用目标，已取消投递。", false, "Unknown", "unknown", null);
                return await CopyOnlyAsync(request, traceId, "TARGET_VALIDATION_FAILED", cancellationToken).ConfigureAwait(false);
            }

            currentStage = DeliveryStage.ValidateTarget;
            var initialValidation = _targetDetector.Validate(request.Target, requireForeground: false);
            if (!initialValidation.IsValid)
            {
                var code = initialValidation.ErrorCode ?? "TARGET_VALIDATION_FAILED";
                if (request.TargetChangeBehavior == TargetChangeBehavior.Cancel)
                    return Result(traceId, DeliveryStatus.Cancelled, DeliveryEffect.None, currentStage, DeliveryConfidence.Confirmed,
                        code, "目标窗口已变化，已取消投递。", false, "Unknown", "unknown", request.Target);
                return await CopyOnlyAsync(request, traceId, code, cancellationToken).ConfigureAwait(false);
            }
            TraceStage(traceId, currentStage, "Unknown", "unknown", request.Target, "VALIDATED");

            currentStage = DeliveryStage.ResolveAdapter;
            var adapter = _adapterResolver.Resolve(request.Target);
            activeAdapter = adapter;
            TraceStage(traceId, currentStage, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, "RESOLVED", adapter.DetectedProductVersion);

            currentStage = DeliveryStage.DetectCapabilities;
            var capabilities = adapter.DetectCapabilities();
            TraceStage(traceId, currentStage, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, capabilities.InsertText.ToString(), adapter.DetectedProductVersion);
            if (capabilities.InsertText != CapabilityStatus.Verified)
                return await CopyOnlyAsync(request, traceId, "CAPABILITY_UNVERIFIED", cancellationToken, adapter).ConfigureAwait(false);

            currentStage = DeliveryStage.Insert;
            actionStarted = true;
            TraceStage(traceId, currentStage, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, "STARTED", adapter.DetectedProductVersion);
            var insert = await adapter.InsertAsync(request, cancellationToken).ConfigureAwait(false);
            foreach (var substage in insert.Substages.IsDefaultOrEmpty ? [] : insert.Substages)
            {
                if (Enum.TryParse<DeliveryStage>(substage.Name switch
                {
                    "target-activation" => nameof(DeliveryStage.TargetActivation),
                    "control-fingerprint" => nameof(DeliveryStage.ControlFingerprint),
                    "clipboard-paste" => nameof(DeliveryStage.ClipboardPaste),
                    "clipboard-restore" => nameof(DeliveryStage.ClipboardRestore),
                    "usage-enqueue" => nameof(DeliveryStage.UsageEnqueue),
                    _ => string.Empty,
                }, out var stage))
                    TraceStage(traceId, stage, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, substage.Code, adapter.DetectedProductVersion);
            }

            if (!insert.WasApplied)
            {
                if (insert.Inconclusive)
                {
                    await RecordUsageAsync(request, cancellationToken).ConfigureAwait(false);
                    return Result(traceId, DeliveryStatus.Unknown, DeliveryEffect.Unknown, DeliveryStage.VerifyInsert, DeliveryConfidence.Unknown,
                        insert.Code, "插入动作结果无法确认，未复制或重试。", false, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, adapter.DetectedProductVersion);
                }
                if (request.TargetChangeBehavior == TargetChangeBehavior.Cancel && insert.Code == "TARGET_CHANGED")
                    return Result(traceId, DeliveryStatus.Cancelled, DeliveryEffect.None, DeliveryStage.Insert, DeliveryConfidence.Confirmed,
                        "TARGET_CHANGED", "目标窗口已变化，已取消投递。", false, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, adapter.DetectedProductVersion);
                return await CopyOnlyAsync(request, traceId, insert.Code, cancellationToken, adapter).ConfigureAwait(false);
            }

            currentStage = DeliveryStage.VerifyInsert;
            TraceStage(traceId, currentStage, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, "STARTED", adapter.DetectedProductVersion);
            var verification = await adapter.VerifyInsertAsync(request, cancellationToken).ConfigureAwait(false);
            if (!verification.IsVerified)
            {
                await RecordUsageAsync(request, cancellationToken).ConfigureAwait(false);
                var code = verification.IsInconclusive ? "INSERT_VERIFICATION_INCONCLUSIVE" : verification.Code;
                return Result(traceId, DeliveryStatus.Unknown, DeliveryEffect.Unknown, currentStage, DeliveryConfidence.Unknown,
                    code, "已执行插入，但无法确认结果；未发送。", false, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, adapter.DetectedProductVersion);
            }

            if (!request.SendRequested || !request.UserAutoSendEnabled)
            {
                await RecordUsageAsync(request, cancellationToken).ConfigureAwait(false);
                return Result(traceId, DeliveryStatus.Success, DeliveryEffect.Inserted, DeliveryStage.Completed, DeliveryConfidence.Confirmed,
                    "INSERTED", "已插入话术。", false, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, adapter.DetectedProductVersion);
            }

            if (capabilities.SendText != CapabilityStatus.Verified || capabilities.VerifySend != CapabilityStatus.Verified)
            {
                await RecordUsageAsync(request, cancellationToken).ConfigureAwait(false);
                return Result(traceId, DeliveryStatus.Success, DeliveryEffect.Inserted, DeliveryStage.Completed, DeliveryConfidence.Confirmed,
                    "CAPABILITY_UNVERIFIED", "已插入话术，自动发送能力尚未验证。", false, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, adapter.DetectedProductVersion);
            }

            currentStage = DeliveryStage.RevalidateBeforeSend;
            TraceStage(traceId, currentStage, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, "STARTED", adapter.DetectedProductVersion);
            var beforeSend = _targetDetector.Validate(request.Target, requireForeground: true);
            if (!beforeSend.IsValid)
            {
                await RecordUsageAsync(request, cancellationToken).ConfigureAwait(false);
                return Result(traceId, DeliveryStatus.Success, DeliveryEffect.Inserted, currentStage, DeliveryConfidence.Confirmed,
                    beforeSend.ErrorCode ?? "TARGET_CHANGED", "目标窗口已变化，未发送。", false, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, adapter.DetectedProductVersion);
            }

            currentStage = DeliveryStage.OptionalSend;
            TraceStage(traceId, currentStage, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, "STARTED", adapter.DetectedProductVersion);
            var send = await adapter.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!send.WasApplied)
            {
                await RecordUsageAsync(request, cancellationToken).ConfigureAwait(false);
                return Result(traceId, DeliveryStatus.Failed, DeliveryEffect.Inserted, currentStage, DeliveryConfidence.Confirmed,
                    send.Code, "已插入话术，但发送失败。", false, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, adapter.DetectedProductVersion);
            }

            currentStage = DeliveryStage.VerifySend;
            TraceStage(traceId, currentStage, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, "STARTED", adapter.DetectedProductVersion);
            var sendVerification = await adapter.VerifySendAsync(request, cancellationToken).ConfigureAwait(false);
            await RecordUsageAsync(request, cancellationToken).ConfigureAwait(false);
            if (!sendVerification.IsVerified)
                return Result(traceId, DeliveryStatus.Unknown, DeliveryEffect.Unknown, currentStage, DeliveryConfidence.Unknown,
                    sendVerification.Code, "发送结果无法确认，未自动重试。", false, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, adapter.DetectedProductVersion);

            return Result(traceId, DeliveryStatus.Success, DeliveryEffect.Sent, DeliveryStage.Completed, DeliveryConfidence.Confirmed,
                "SENT", "已插入并发送话术。", false, adapter.AdapterId, adapter.Profile.ProfileVersion, request.Target, adapter.DetectedProductVersion);
        }
        catch (OperationCanceledException)
        {
            var status = actionStarted ? DeliveryStatus.Unknown : DeliveryStatus.Cancelled;
            var effect = actionStarted ? DeliveryEffect.Unknown : DeliveryEffect.None;
            var confidence = actionStarted ? DeliveryConfidence.Unknown : DeliveryConfidence.Confirmed;
            var message = actionStarted ? "投递已取消，但动作结果无法确认，未自动重试。" : "投递已取消。";
            return Result(traceId, status, effect, currentStage, confidence, "DELIVERY_CANCELLED", message, false,
                activeAdapter?.AdapterId ?? "Unknown", activeAdapter?.Profile.ProfileVersion ?? "unknown", request.Target, activeAdapter?.DetectedProductVersion);
        }
        catch (Exception exception)
        {
            // 不把异常消息写入 DeliveryTrace，避免第三方组件消息意外携带用户内容；只保留稳定错误码和异常类型。
            var code = exception switch
            {
                TimeoutException => "DELIVERY_TIMEOUT",
                UnauthorizedAccessException => "INSERT_ACCESS_DENIED",
                System.Runtime.InteropServices.ExternalException => "CLIPBOARD_FAILED",
                _ => "INSERT_FAILED",
            };
            Console.Error.WriteLine($"投递失败（{traceId}）：{code}，异常类型 {exception.GetType().Name}");
            return Result(traceId, actionStarted ? DeliveryStatus.Unknown : DeliveryStatus.Failed,
                actionStarted ? DeliveryEffect.Unknown : DeliveryEffect.None,
                currentStage, actionStarted ? DeliveryConfidence.Unknown : DeliveryConfidence.Confirmed,
                code, actionStarted ? "投递动作结果无法确认，未自动重试。" : "投递失败，未自动重试。", false,
                activeAdapter?.AdapterId ?? "Unknown", activeAdapter?.Profile.ProfileVersion ?? "unknown", request.Target, activeAdapter?.DetectedProductVersion);
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    public void Dispose()
    {
        _traceStarts.Clear();
        _deliveryGate.Dispose();
    }

    private async Task<DeliveryResult> CopyOnlyAsync(DeliveryRequest request, Guid traceId, string code, CancellationToken cancellationToken, IApplicationAdapter? adapter = null)
    {
        var copied = await _clipboard.CopyOnlyAsync(request.Phrase.Content, cancellationToken).ConfigureAwait(false);
        if (!copied.Succeeded)
            return Result(traceId, DeliveryStatus.Failed, DeliveryEffect.None, DeliveryStage.Fallback, DeliveryConfidence.Confirmed,
                copied.Code, "无法复制话术到剪贴板。", false, adapter?.AdapterId ?? "Unknown", adapter?.Profile.ProfileVersion ?? "unknown", request.Target, adapter?.DetectedProductVersion);

        await RecordUsageAsync(request, cancellationToken).ConfigureAwait(false);
        return Result(traceId, DeliveryStatus.Unsupported, DeliveryEffect.None, DeliveryStage.Fallback, DeliveryConfidence.Confirmed,
            code, "已复制话术，请按 Ctrl + V 粘贴。", false, adapter?.AdapterId ?? "Unknown", adapter?.Profile.ProfileVersion ?? "unknown", request.Target, adapter?.DetectedProductVersion);
    }

    private async Task RecordUsageAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        try { await _usageRecorder(request.Phrase.Id, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) { Console.Error.WriteLine($"使用次数保存失败：{exception.Message}"); }
    }

    private void TraceStage(Guid traceId, DeliveryStage stage, string adapterId, string profileVersion, DeliveryTarget? target, string code, string? productVersion = null)
    {
        if (_traceWriter is null) return;
        try
        {
            var duration = _traceStarts.TryGetValue(traceId, out var started) ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0;
            _traceWriter(new DeliveryTrace(traceId, stage, adapterId, profileVersion, target?.ApplicationId ?? "", productVersion, code, duration, DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"投递诊断写入失败：{exception.Message}");
        }
    }

    private DeliveryResult Result(Guid traceId, DeliveryStatus status, DeliveryEffect effect, DeliveryStage stage,
        DeliveryConfidence confidence, string errorCode, string message, bool retryable, string adapterId,
        string profileVersion, DeliveryTarget? target, string? productVersion = null)
    {
        try
        {
            var duration = _traceStarts.TryRemove(traceId, out var started)
                ? Stopwatch.GetElapsedTime(started).TotalMilliseconds
                : 0;
            _traceWriter?.Invoke(new DeliveryTrace(traceId, stage, adapterId, profileVersion,
                target?.ApplicationId ?? "", productVersion, errorCode, duration, DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"投递诊断写入失败：{exception.Message}");
        }
        return new DeliveryResult(status, effect, stage, confidence, errorCode, message, retryable, traceId);
    }

    private DeliveryResult NewResult(DeliveryStatus status, DeliveryEffect effect, DeliveryStage stage,
        DeliveryConfidence confidence, string errorCode, string message, bool retryable, Guid traceId) =>
        new(status, effect, stage, confidence, errorCode, message, retryable, traceId);
}

/// <summary>Desktop composition root 使用的公开工厂；剪贴板实现仍隐藏在 Platform.Windows 内部。</summary>
public static class TextDeliveryFactory
{
    public static ITextDeliveryStateMachine Create(
        ITargetDetector targetDetector,
        IAdapterResolver adapterResolver,
        Func<Guid, CancellationToken, Task> usageRecorder,
        Action<DeliveryTrace>? traceWriter = null) =>
        new TextDeliveryStateMachine(targetDetector, adapterResolver, new ClipboardTransaction(), usageRecorder, traceWriter);
}
