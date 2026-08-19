using System.Windows;
using System.Windows.Controls;

namespace QuickPhrase.Desktop;

/// <summary>自定义标题栏：系统按钮直接操作所属 Window；拖动/双击/Snap 由 WindowChrome 的 caption 区域处理。</summary>
public partial class TitleBar : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty PageTitleProperty =
        DependencyProperty.Register(
            nameof(PageTitle), typeof(string), typeof(TitleBar),
            new PropertyMetadata(string.Empty));

    public string PageTitle
    {
        get => (string)GetValue(PageTitleProperty);
        set => SetValue(PageTitleProperty, value);
    }

    public TitleBar() => InitializeComponent();

    private void MinButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) window.WindowState = WindowState.Minimized;
    }

    private void MaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) window.Close();
    }
}
