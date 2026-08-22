using System.Windows;
using System.Windows.Input;

namespace QuickPhrase.Desktop;

/// <summary>
/// 话术列表的宿主注入点：共享资源只负责显示结构，列表宿主决定是否显示显式发送动作及其命令。
/// 话术库和 Launcher 复用紧凑行模板；Launcher 仅在当前目标允许显式发送时注入按钮命令。
/// </summary>
public static class PhraseListActions
{
    public static readonly DependencyProperty ShowSendButtonProperty =
        DependencyProperty.RegisterAttached(
            "ShowSendButton",
            typeof(bool),
            typeof(PhraseListActions),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty SendCommandProperty =
        DependencyProperty.RegisterAttached(
            "SendCommand",
            typeof(ICommand),
            typeof(PhraseListActions),
            new FrameworkPropertyMetadata(null));

    public static void SetShowSendButton(DependencyObject element, bool value) => element.SetValue(ShowSendButtonProperty, value);
    public static bool GetShowSendButton(DependencyObject element) => (bool)element.GetValue(ShowSendButtonProperty);

    public static void SetSendCommand(DependencyObject element, ICommand? value) => element.SetValue(SendCommandProperty, value);
    public static ICommand? GetSendCommand(DependencyObject element) => (ICommand?)element.GetValue(SendCommandProperty);
}
