using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Desktop;

/// <summary>连续队列只接受经过精确版本验收的企业微信插入能力。</summary>
internal static class DeliveryQueuePolicy
{
    public static bool CanQueue(AdapterProfile profile) =>
        profile.AdapterId == "WXWork" &&
        profile.ProductVersionRange == WindowsAdapterResolver.SupportedWeComProductVersion &&
        profile.InsertTextStatus == CapabilityStatus.Verified;
}
