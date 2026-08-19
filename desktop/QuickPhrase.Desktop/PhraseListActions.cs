using System.Windows;
using System.Windows.Input;

namespace QuickPhrase.Desktop;

/// <summary>
/// 话术列表的宿主注入点：共享资源只负责显示结构，列表宿主决定是否显示发送动作、命令和标题列宽。
/// Launcher 将发送动作关闭，话术库则注入现有的直接发送命令，避免两页复制两套行布局。
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

    public static readonly DependencyProperty TitleColumnWidthProperty =
        DependencyProperty.RegisterAttached(
            "TitleColumnWidth",
            typeof(GridLength),
            typeof(PhraseListActions),
            new FrameworkPropertyMetadata(new GridLength(160)));

    public static void SetShowSendButton(DependencyObject element, bool value) => element.SetValue(ShowSendButtonProperty, value);
    public static bool GetShowSendButton(DependencyObject element) => (bool)element.GetValue(ShowSendButtonProperty);

    public static void SetSendCommand(DependencyObject element, ICommand? value) => element.SetValue(SendCommandProperty, value);
    public static ICommand? GetSendCommand(DependencyObject element) => (ICommand?)element.GetValue(SendCommandProperty);

    public static void SetTitleColumnWidth(DependencyObject element, GridLength value) => element.SetValue(TitleColumnWidthProperty, value);
    public static GridLength GetTitleColumnWidth(DependencyObject element) => (GridLength)element.GetValue(TitleColumnWidthProperty);
}
