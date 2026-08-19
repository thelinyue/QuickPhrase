using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>
/// 设置页「应用适配」列表项：开发者登记的 Adapter 开关（WXWork 等）。
/// 通过可通知属性让单个适配器开关能够触发设置即时保存。
/// </summary>
public sealed partial class AdapterToggleItem : ObservableObject
{
    public AdapterToggleItem(string id, bool enabled)
    {
        Id = id;
        Enabled = enabled;
    }

    public string Id { get; }

    public string DisplayName => Id switch
    {
        "WXWork" => "企业微信",
        _ => Id,
    };

    [ObservableProperty]
    private bool _enabled;
}
