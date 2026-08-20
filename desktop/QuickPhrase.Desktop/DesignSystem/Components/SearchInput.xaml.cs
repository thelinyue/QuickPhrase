using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace QuickPhrase.Desktop.DesignSystem.Components;

/// <summary>
/// 统一搜索输入的高度、内边距和占位文本起点。
/// 组件只承载文本输入，不执行搜索、历史记录或持久化操作。
/// </summary>
public partial class SearchInput : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(SearchInput),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            null,
            null,
            false,
            UpdateSourceTrigger.PropertyChanged));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder),
        typeof(string),
        typeof(SearchInput),
        new PropertyMetadata("搜索"));

    public SearchInput()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>将键盘焦点交给内部原生 TextBox，保留标准选择、复制、粘贴与输入法行为。</summary>
    public bool FocusInput() => InputBox.Focus();
}
