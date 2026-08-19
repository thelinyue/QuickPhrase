using System.Windows;

namespace QuickPhrase.Desktop;

/// <summary>导航/关闭前的未保存改动确认结果。</summary>
public enum NavigationDecision
{
    SaveAndLeave,
    DiscardAndLeave,
    ContinueEditing,
}

public partial class NavigationConfirmDialog : Window
{
    public NavigationDecision Decision { get; private set; } = NavigationDecision.ContinueEditing;

    public NavigationConfirmDialog() => InitializeComponent();

    private void SaveLeave_Click(object sender, RoutedEventArgs e)
    {
        Decision = NavigationDecision.SaveAndLeave;
        DialogResult = true;
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        Decision = NavigationDecision.DiscardAndLeave;
        DialogResult = true;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        Decision = NavigationDecision.ContinueEditing;
        DialogResult = false;
    }
}
