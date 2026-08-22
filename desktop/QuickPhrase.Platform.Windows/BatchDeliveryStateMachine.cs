using System.Collections.Immutable;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 多段图文批次状态机。文字段复用既有 TextDeliveryStateMachine；图片段只有在运行时六字段能力
/// 和图片 Adapter 契约都明确 Verified 时才执行。任一失败或不确定结果立即停止，且整批成功只记录一次使用次数。
/// </summary>
public sealed class BatchDeliveryStateMachine : IBatchDeliveryStateMachine
{
    private readonly ITextDeliveryStateMachine _single;
    private readonly ITargetDetector _targets;
    private readonly IAdapterBatchStabilityWaiter _stabilityWaiter;
    private readonly IAdapterResolver _adapters;
    private readonly Func<IMediaAssetStore?> _mediaAssets;
    private readonly Func<Phrase, CancellationToken, Task> _recordUsage;

    public BatchDeliveryStateMachine(
        ITextDeliveryStateMachine single,
        ITargetDetector targets,
        IAdapterBatchStabilityWaiter stabilityWaiter,
        IAdapterResolver adapters,
        Func<IMediaAssetStore?> mediaAssets,
        Func<Phrase, CancellationToken, Task> recordUsage)
    {
        _single = single;
        _targets = targets;
        _stabilityWaiter = stabilityWaiter;
        _adapters = adapters;
        _mediaAssets = mediaAssets;
        _recordUsage = recordUsage;
    }

    public async Task<BatchDeliveryResult> DeliverAsync(DeliveryRequest request, CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid();
        var results = ImmutableArray.CreateBuilder<DeliveryResult>();
        var segments = request.Phrase.Body.Segments;
        for (var index = 0; index < segments.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = segments[index];
            if (request.Target is null || !_targets.Validate(request.Target, requireForeground: true).IsValid)
                return Stop(DeliveryStatus.Failed, DeliveryEffect.None, index, "TARGET_CHANGED", "目标窗口已变化，整批发送已停止。", traceId, results, segments.Length);

            var result = segment.Kind == PhraseSegmentKind.Image
                ? await DeliverImageSegmentAsync(request, segment, cancellationToken).ConfigureAwait(false)
                : await DeliverTextSegmentAsync(request, segment, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (!result.IsSuccess || result.Effect is not (DeliveryEffect.SendTriggered or DeliveryEffect.Sent))
                return new BatchDeliveryResult(result.Status, result.Effect, segments.Length, index, index + 1, results.ToImmutable(), traceId);

            if (index + 1 < segments.Length)
            {
                // 段间等待由 Adapter 根据目标、前台窗口和脱敏焦点指纹决定，不使用用户配置或固定秒数。
                var stability = await _stabilityWaiter.WaitForStabilityAsync(request.Target, cancellationToken).ConfigureAwait(false);
                var afterWait = _targets.Validate(request.Target, requireForeground: true);
                if (!afterWait.IsValid)
                    return Stop(DeliveryStatus.Failed, DeliveryEffect.SendTriggered, index + 1,
                        afterWait.ErrorCode ?? "TARGET_CHANGED", "段间等待后目标窗口或输入焦点已变化，整批发送已停止。",
                        traceId, results, segments.Length, DeliveryStage.AdapterStabilityWait);

                if (!stability.IsVerified)
                {
                    var status = stability.IsInconclusive ? DeliveryStatus.Unknown : DeliveryStatus.Failed;
                    var confidence = stability.IsInconclusive ? DeliveryConfidence.Unknown : DeliveryConfidence.Confirmed;
                    var message = stability.IsInconclusive
                        ? "无法确认目标在段间等待后保持稳定，整批发送已停止，未自动重试。"
                        : "目标在段间等待后未达到稳定条件，整批发送已停止。";
                    return Stop(status, DeliveryEffect.SendTriggered, index + 1, stability.Code, message,
                        traceId, results, segments.Length, DeliveryStage.AdapterStabilityWait, confidence);
                }
            }
        }

        await _recordUsage(request.Phrase, cancellationToken).ConfigureAwait(false);
        // 即使某个测试 Adapter 能验证单段发送，整批也只声明已触发发送，不宣称目标应用最终全部发送成功。
        return new BatchDeliveryResult(DeliveryStatus.Success, DeliveryEffect.SendTriggered, segments.Length, segments.Length, null, results.ToImmutable(), traceId);
    }

    private Task<DeliveryResult> DeliverTextSegmentAsync(DeliveryRequest request, PhraseSegment segment, CancellationToken cancellationToken)
    {
        var segmentPhrase = request.Phrase with { Body = PhraseBody.FromText(segment.Text!, request.Phrase.Body.BatchSeparator) };
        var segmentRequest = request with
        {
            Phrase = segmentPhrase,
            Mode = SendMode.InsertAndSend,
            TargetChangeBehavior = TargetChangeBehavior.Cancel,
            RecordUsageOnSuccess = false,
        };
        return _single.DeliverAsync(segmentRequest, cancellationToken);
    }

    private async Task<DeliveryResult> DeliverImageSegmentAsync(DeliveryRequest request, PhraseSegment segment, CancellationToken cancellationToken)
    {
        var traceId = Guid.NewGuid();
        if (request.Target is null || segment.Image is null)
            return ImageResult(DeliveryStatus.Failed, DeliveryEffect.None, DeliveryStage.NotStarted, DeliveryConfidence.Confirmed,
                "IMAGE_REFERENCE_INVALID", "图片段引用无效，整批发送已停止。", traceId);

        var adapter = _adapters.Resolve(request.Target);
        var capabilities = adapter.DetectCapabilities();
        if (capabilities.InsertImage != CapabilityStatus.Verified
            || capabilities.VerifyImageInsert != CapabilityStatus.Verified
            || capabilities.TriggerSend != CapabilityStatus.Verified
            || adapter is not IImageApplicationAdapter imageAdapter)
            return ImageResult(DeliveryStatus.Unsupported, DeliveryEffect.None, DeliveryStage.DetectCapabilities, DeliveryConfidence.Confirmed,
                "IMAGE_INSERT_UNSUPPORTED", "当前目标尚未通过图片投递人工矩阵，整批发送已停止。", traceId);

        var mediaStore = _mediaAssets();
        if (mediaStore is null)
            return ImageResult(DeliveryStatus.Failed, DeliveryEffect.None, DeliveryStage.Insert, DeliveryConfidence.Confirmed,
                "MEDIA_STORE_UNAVAILABLE", "图片媒体库尚未就绪，整批发送已停止。", traceId);

        MediaAssetContent? content;
        try { content = await mediaStore.ReadAsync(segment.Image.AssetId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return ImageResult(DeliveryStatus.Failed, DeliveryEffect.None, DeliveryStage.Insert, DeliveryConfidence.Confirmed,
                "MEDIA_READ_FAILED", "读取图片媒体失败，整批发送已停止。", traceId);
        }

        if (content is null
            || content.Image.AssetId != segment.Image.AssetId
            || !string.Equals(content.Image.MimeType, segment.Image.MimeType, StringComparison.OrdinalIgnoreCase)
            || content.Image.ByteLength != segment.Image.ByteLength
            || content.Image.PixelWidth != segment.Image.PixelWidth
            || content.Image.PixelHeight != segment.Image.PixelHeight
            || content.Bytes.Length == 0
            || content.Bytes.LongLength != content.Image.ByteLength)
            return ImageResult(DeliveryStatus.Failed, DeliveryEffect.None, DeliveryStage.Insert, DeliveryConfidence.Confirmed,
                "MEDIA_ASSET_INVALID", "图片媒体缺失或元数据不一致，整批发送已停止。", traceId);

        var segmentPhrase = request.Phrase with
        {
            Body = new PhraseBody([segment], request.Phrase.Body.BatchSeparator),
        };
        var segmentRequest = request with
        {
            Phrase = segmentPhrase,
            Mode = SendMode.InsertAndSend,
            TargetChangeBehavior = TargetChangeBehavior.Cancel,
            RecordUsageOnSuccess = false,
        };

        try
        {
            var insert = await imageAdapter.InsertImageAsync(segmentRequest, content, cancellationToken).ConfigureAwait(false);
            if (!insert.WasApplied)
                return insert.Inconclusive
                    ? ImageResult(DeliveryStatus.Unknown, DeliveryEffect.Unknown, DeliveryStage.Insert, DeliveryConfidence.Unknown,
                        insert.Code, "图片插入动作结果无法确认，整批发送已停止，未自动重试。", traceId)
                    : ImageResult(DeliveryStatus.Failed, DeliveryEffect.None, DeliveryStage.Insert, DeliveryConfidence.Confirmed,
                        insert.Code, "图片插入失败，整批发送已停止。", traceId);

            var verification = await imageAdapter.VerifyImageInsertAsync(segmentRequest, cancellationToken).ConfigureAwait(false);
            if (!verification.IsVerified)
                return verification.IsInconclusive
                    ? ImageResult(DeliveryStatus.Unknown, DeliveryEffect.Unknown, DeliveryStage.VerifyInsert, DeliveryConfidence.Unknown,
                        verification.Code, "图片插入结果无法确认，整批发送已停止，未自动重试。", traceId)
                    : ImageResult(DeliveryStatus.Failed, DeliveryEffect.Inserted, DeliveryStage.VerifyInsert, DeliveryConfidence.Confirmed,
                        verification.Code, "图片已执行插入，但目标或焦点验证失败，未触发发送。", traceId);

            var beforeSend = _targets.Validate(request.Target, requireForeground: true);
            if (!beforeSend.IsValid)
                return ImageResult(DeliveryStatus.Failed, DeliveryEffect.Inserted, DeliveryStage.RevalidateBeforeSend, DeliveryConfidence.Confirmed,
                    beforeSend.ErrorCode ?? "TARGET_CHANGED", "图片已插入，但发送前目标窗口或焦点已变化，未触发发送。", traceId);

            var send = await adapter.SendAsync(segmentRequest, cancellationToken).ConfigureAwait(false);
            if (!send.WasApplied)
                return send.Inconclusive
                    ? ImageResult(DeliveryStatus.Unknown, DeliveryEffect.Unknown, DeliveryStage.OptionalSend, DeliveryConfidence.Unknown,
                        send.Code, "图片发送快捷操作结果无法确认，整批发送已停止，未自动重试。", traceId)
                    : ImageResult(DeliveryStatus.Failed, DeliveryEffect.Inserted, DeliveryStage.OptionalSend, DeliveryConfidence.Confirmed,
                        send.Code, "图片已插入，但发送快捷操作执行失败。", traceId);

            if (capabilities.VerifySend != CapabilityStatus.Verified)
                return ImageResult(DeliveryStatus.Success, DeliveryEffect.SendTriggered, DeliveryStage.Completed, DeliveryConfidence.Confirmed,
                    "SEND_TRIGGERED", "已触发图片发送快捷操作，但无法确认目标应用最终发送结果。", traceId);

            var sendVerification = await adapter.VerifySendAsync(segmentRequest, cancellationToken).ConfigureAwait(false);
            if (sendVerification.IsVerified)
                return ImageResult(DeliveryStatus.Success, DeliveryEffect.Sent, DeliveryStage.Completed, DeliveryConfidence.Confirmed,
                    "SENT", "图片段发送已由 Adapter 验证。", traceId);
            return sendVerification.IsInconclusive
                ? ImageResult(DeliveryStatus.Success, DeliveryEffect.SendTriggered, DeliveryStage.Completed, DeliveryConfidence.Confirmed,
                    sendVerification.Code, "已触发图片发送快捷操作，但无法确认目标应用最终发送结果。", traceId)
                : ImageResult(DeliveryStatus.Failed, DeliveryEffect.SendTriggered, DeliveryStage.VerifySend, DeliveryConfidence.Confirmed,
                    sendVerification.Code, "已触发图片发送快捷操作，但目标应用未确认发送成功。", traceId);
        }
        catch (OperationCanceledException)
        {
            return ImageResult(DeliveryStatus.Unknown, DeliveryEffect.Unknown, DeliveryStage.Insert, DeliveryConfidence.Unknown,
                "DELIVERY_CANCELLED", "图片投递已取消，但动作结果无法确认，整批发送已停止，未自动重试。", traceId);
        }
        catch (Exception)
        {
            // 不传播或记录第三方异常消息，避免其中意外包含图片路径、文件名或内容。
            return ImageResult(DeliveryStatus.Unknown, DeliveryEffect.Unknown, DeliveryStage.Insert, DeliveryConfidence.Unknown,
                "IMAGE_DELIVERY_EXCEPTION", "图片投递发生异常且结果无法确认，整批发送已停止，未自动重试。", traceId);
        }
    }

    private static DeliveryResult ImageResult(
        DeliveryStatus status,
        DeliveryEffect effect,
        DeliveryStage stage,
        DeliveryConfidence confidence,
        string code,
        string message,
        Guid traceId) =>
        new(status, effect, stage, confidence, code, message, false, traceId);

    private static BatchDeliveryResult Stop(DeliveryStatus status, DeliveryEffect effect, int zeroBasedIndex, string code, string message,
        Guid traceId, ImmutableArray<DeliveryResult>.Builder results, int total,
        DeliveryStage stage = DeliveryStage.ValidateTarget,
        DeliveryConfidence confidence = DeliveryConfidence.Confirmed)
    {
        results.Add(new DeliveryResult(status, effect, stage, confidence, code, message, false, traceId));
        return new BatchDeliveryResult(status, effect, total, zeroBasedIndex, zeroBasedIndex + 1, results.ToImmutable(), traceId);
    }
}
