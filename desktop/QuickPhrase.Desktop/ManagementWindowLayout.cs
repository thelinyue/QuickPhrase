namespace QuickPhrase.Desktop;

/// <summary>
/// 管理窗口的场景白名单与统一尺寸映射。Desktop 负责把它
/// 转换为可验证的 WPF 尺寸；话术库编辑器属于话术库表面的内部场景，设置保持独立。
/// </summary>
public sealed record ManagementWindowLayout(double Width, double Height, double MinWidth, double MinHeight)
{
    public static bool TryGet(string? scene, out ManagementWindowLayout layout)
    {
        layout = scene switch
        {
            "library" or "editor" or "settings" => new(1200, 760, 900, 560),
            _ => null!,
        };
        return layout is not null;
    }
}
