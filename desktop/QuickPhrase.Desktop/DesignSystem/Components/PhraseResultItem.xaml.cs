using System.Windows;
using System.Windows.Controls;

namespace QuickPhrase.Desktop.DesignSystem.Components;

/// <summary>
/// 话术结果项的纯内容视图。选择、焦点、命令和虚拟化仍由外层 ListBox/ListView 管理，
/// 本组件不拥有集合，也不参与搜索、插入、删除或投递安全链。
/// </summary>
public partial class PhraseResultItem : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty TitleProperty = RegisterTextProperty(nameof(Title));
    public static readonly DependencyProperty DescriptionProperty = RegisterTextProperty(nameof(Description));
    public static readonly DependencyProperty MetadataProperty = RegisterTextProperty(nameof(Metadata));
    public static readonly DependencyProperty TrailingContentProperty = DependencyProperty.Register(
        nameof(TrailingContent), typeof(object), typeof(PhraseResultItem), new PropertyMetadata(null));

    public PhraseResultItem()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? Metadata
    {
        get => (string?)GetValue(MetadataProperty);
        set => SetValue(MetadataProperty, value);
    }

    public object? TrailingContent
    {
        get => GetValue(TrailingContentProperty);
        set => SetValue(TrailingContentProperty, value);
    }

    private static DependencyProperty RegisterTextProperty(string name) => DependencyProperty.Register(
        name, typeof(string), typeof(PhraseResultItem), new PropertyMetadata(string.Empty));
}
