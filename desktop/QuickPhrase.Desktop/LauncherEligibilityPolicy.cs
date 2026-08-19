using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Desktop;

/// <summary>全局 Launcher 的最小准入判断；不负责投递能力，投递仍由 Adapter/Profile 再次验证。</summary>
public static class LauncherEligibilityPolicy
{
    public static bool CanOpen(string? adapterId, IReadOnlyDictionary<string, bool>? enabledAdapters) =>
        WindowsAdapterResolver.IsKnownAdapterId(adapterId)
        && enabledAdapters is not null
        && enabledAdapters.TryGetValue(adapterId!, out var enabled)
        && enabled;
}
