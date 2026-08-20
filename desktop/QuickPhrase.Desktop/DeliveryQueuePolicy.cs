using QuickPhrase.Core;

namespace QuickPhrase.Desktop;

/// <summary>
/// 连续队列只承载可安全重复排队的纯插入请求。显式发送属于不可逆即时操作，永不延迟入队。
/// </summary>
internal static class DeliveryQueuePolicy
{
    public static bool CanQueue(AdapterProfile profile, SendMode mode) =>
        mode == SendMode.InsertOnly && profile.InsertTextStatus == CapabilityStatus.Verified;
}
