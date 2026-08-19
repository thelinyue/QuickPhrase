namespace QuickPhrase.Desktop;

/// <summary>
/// 内容表面（编辑器 / 设置）实现此接口，供 MainWindow 在导航切换或关闭前
/// 拦截未保存改动，统一走「保存并离开 / 放弃改动 / 继续编辑」确认流程。
/// </summary>
public interface INavigationGuard
{
    bool HasUnsavedChanges { get; }
    Task SaveAsync();
    void DiscardChanges();
}
