using System.Windows;

namespace QuickPhrase.Desktop;

/// <summary>
/// Ctrl+Enter 快捷发送的风险授权窗口。
/// 对话框只收集本次用户的明确选择，不自行写入设置或触发投递；ApplicationController 必须在
/// “开启快捷发送并继续”时先完成设置持久化，成功后才允许执行本次发送，避免保存失败演变成误发送。
/// </summary>
public enum QuickSendGuideDecision
{
    Cancel,
    ContinueOnce,
    EnableAndContinue,
}

public partial class QuickSendGuideDialog : Window
{
    /// <summary>默认取消；关闭窗口、按 Esc 或点击取消均不会产生投递副作用。</summary>
    public QuickSendGuideDecision Decision { get; private set; } = QuickSendGuideDecision.Cancel;

    public QuickSendGuideDialog() => InitializeComponent();

    private void EnableAndContinue_Click(object sender, RoutedEventArgs e)
    {
        Decision = QuickSendGuideDecision.EnableAndContinue;
        DialogResult = true;
    }

    private void ContinueOnce_Click(object sender, RoutedEventArgs e)
    {
        Decision = QuickSendGuideDecision.ContinueOnce;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Decision = QuickSendGuideDecision.Cancel;
        DialogResult = false;
    }
}
