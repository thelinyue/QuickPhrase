namespace QuickPhrase.Desktop.ViewModels;

/// <summary>设置页「应用适配」列表项：开发者登记的 Adapter 开关（WXWork 等）。</summary>
public sealed class AdapterToggleItem(string id, bool enabled)
{
    public string Id { get; } = id;

    public string DisplayName => Id switch
    {
        "WXWork" => "企业微信",
        _ => Id,
    };

    public bool Enabled { get; set; } = enabled;
}
