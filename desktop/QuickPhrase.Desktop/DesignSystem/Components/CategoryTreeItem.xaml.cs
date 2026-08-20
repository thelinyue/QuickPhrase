using System.Windows;
using System.Windows.Controls;

namespace QuickPhrase.Desktop.DesignSystem.Components;

/// <summary>
/// 分类树节点的纯内容视图。树层级、展开选择、虚拟化和分类命令继续由外层 TreeView 管理，
/// 本组件不加载或修改分类数据，也不改变既有层级规则。
/// </summary>
public partial class CategoryTreeItem : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(CategoryTreeItem), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty CountTextProperty = DependencyProperty.Register(
        nameof(CountText), typeof(string), typeof(CategoryTreeItem), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty LeadingContentProperty = DependencyProperty.Register(
        nameof(LeadingContent), typeof(object), typeof(CategoryTreeItem), new PropertyMetadata(null));
    public static readonly DependencyProperty TrailingContentProperty = DependencyProperty.Register(
        nameof(TrailingContent), typeof(object), typeof(CategoryTreeItem), new PropertyMetadata(null));

    public CategoryTreeItem()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string CountText
    {
        get => (string)GetValue(CountTextProperty);
        set => SetValue(CountTextProperty, value);
    }

    public object? LeadingContent
    {
        get => GetValue(LeadingContentProperty);
        set => SetValue(LeadingContentProperty, value);
    }

    public object? TrailingContent
    {
        get => GetValue(TrailingContentProperty);
        set => SetValue(TrailingContentProperty, value);
    }
}
