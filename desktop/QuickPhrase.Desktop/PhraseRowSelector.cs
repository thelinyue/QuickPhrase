using System.Windows;
using System.Windows.Controls;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop;

/// <summary>
/// 为话术库扁平化列表（VisibleItems）按运行时类型选择模板：
/// SubHeaderItem → 二级分类标题条模板；PhraseItemViewModel → 话术行模板。
/// </summary>
public sealed class PhraseRowSelector : DataTemplateSelector
{
    public DataTemplate? PhraseRowTemplate { get; set; }
    public DataTemplate? SubHeaderTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item switch
        {
            SubHeaderItem => SubHeaderTemplate,
            PhraseItemViewModel => PhraseRowTemplate,
            _ => PhraseRowTemplate,
        };
    }
}