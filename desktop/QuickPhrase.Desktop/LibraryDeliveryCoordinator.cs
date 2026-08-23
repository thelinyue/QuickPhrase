using QuickPhrase.Core;

namespace QuickPhrase.Desktop;

/// <summary>话术级投递可用性；禁用原因直接供 WPF ToolTip 使用，不暴露任何窗口或聊天内容。</summary>
public sealed record LibraryDeliveryAvailability(
    bool CanInsert,
    bool CanSend,
    string InsertDisabledReason,
    string SendDisabledReason);

/// <summary>话术库依赖的 Desktop 抽象，View/ViewModel 不直接引用 Windows Adapter、剪贴板或 HWND。</summary>
public interface ILibraryDeliveryCoordinator
{
    event EventHandler? AvailabilityChanged;
    LibraryDeliveryAvailability Evaluate(Phrase phrase);
    Task<DeliveryResult?> DeliverAsync(Phrase phrase, SendMode mode, CancellationToken cancellationToken);
}

/// <summary>
/// 保存最近外部目标与运行时能力，并把一次明确操作转交给应用级安全投递链。
/// 每次调用先冻结目标快照；投递进行中拒绝后续请求，避免双击或连点造成重复投递。
/// </summary>
internal sealed class LibraryDeliveryCoordinator : ILibraryDeliveryCoordinator
{
    private readonly Func<Phrase, DeliveryTarget?, SendMode, CancellationToken, Task<DeliveryResult?>> _deliver;
    private DeliveryTarget? _target;
    private AdapterCapabilities _capabilities = UnknownCapabilities;
    private int _isDelivering;

    private static AdapterCapabilities UnknownCapabilities { get; } = new(
        CapabilityStatus.Unverified, CapabilityStatus.Unverified,
        CapabilityStatus.Unsupported, CapabilityStatus.Unsupported,
        CapabilityStatus.Unsupported, CapabilityStatus.Unsupported);

    public LibraryDeliveryCoordinator(
        Func<Phrase, DeliveryTarget?, SendMode, CancellationToken, Task<DeliveryResult?>> deliver) =>
        _deliver = deliver;

    public event EventHandler? AvailabilityChanged;

    public void UpdateTarget(DeliveryTarget? target, AdapterCapabilities? capabilities = null)
    {
        _target = target;
        _capabilities = target is null ? UnknownCapabilities : capabilities ?? UnknownCapabilities;
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public LibraryDeliveryAvailability Evaluate(Phrase phrase)
    {
        if (Volatile.Read(ref _isDelivering) != 0)
            return new(false, false, "正在投递，请稍候。", "正在投递，请稍候。");
        return EvaluateCore(phrase);
    }

    private LibraryDeliveryAvailability EvaluateCore(Phrase phrase)
    {
        if (_target is null)
        {
            if (phrase.Body.IsSingleText)
                return new(true, false, string.Empty, "当前没有可验证的业务窗口，无法插入并发送。");
            return new(false, false, "当前没有可验证的业务窗口，多段或图片话术不能降级复制。", "当前没有可验证的业务窗口，无法插入并发送。");
        }

        var hasText = phrase.Body.Segments.Any(segment => segment.Kind == PhraseSegmentKind.Text);
        var hasImage = phrase.Body.Segments.Any(segment => segment.Kind == PhraseSegmentKind.Image);
        if (hasText && (_capabilities.InsertText != CapabilityStatus.Verified || _capabilities.VerifyTextInsert != CapabilityStatus.Verified))
            return new(false, false, "当前业务窗口未验证文字插入能力。", "当前业务窗口未验证文字插入能力。");
        if (hasImage && (_capabilities.InsertImage != CapabilityStatus.Verified || _capabilities.VerifyImageInsert != CapabilityStatus.Verified))
            return new(false, false, "当前业务窗口未验证图片插入能力。", "当前业务窗口未验证图片插入能力。");
        if (_capabilities.TriggerSend != CapabilityStatus.Verified)
            return new(true, false, string.Empty, "当前业务窗口未验证发送能力。");
        return new(true, true, string.Empty, string.Empty);
    }

    public async Task<DeliveryResult?> DeliverAsync(Phrase phrase, SendMode mode, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isDelivering, 1, 0) != 0) return null;
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var availability = EvaluateCore(phrase);
            if (mode == SendMode.InsertOnly && !availability.CanInsert ||
                mode == SendMode.InsertAndSend && !availability.CanSend)
                return null;

            // 冻结本次目标；后续目标变化只能影响下一次操作，平台状态机仍会在每段执行前重新验证。
            var frozenTarget = _target;
            return await _deliver(phrase, frozenTarget, mode, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Exchange(ref _isDelivering, 0);
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
