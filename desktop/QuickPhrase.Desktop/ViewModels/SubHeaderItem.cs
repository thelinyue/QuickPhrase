namespace QuickPhrase.Desktop.ViewModels;

/// <summary>
/// 二级分类标题条条目：作为扁平化列表（VisibleItems）中的分隔元素，
/// 渲染为 28px 的 SubHeader（左 3px 主色竖条 + 分类名 + 数量 + 折叠箭头）。
/// IsSubHeader 恒为 true，供列表容器样式识别并关闭选中/悬停视觉效果。
/// </summary>
public sealed record SubHeaderItem(CategoryItem Category)
{
    public Guid Id => Category.Id;
    public string Name => Category.Name;
    public int Count => Category.Count;
    public bool IsExpanded => Category.IsExpanded;
    public bool IsSubHeader => true;

    /// <summary>对应一级分类名。用于派生二级分类的层级色（基于一级颜色调淡）。</summary>
    public string? ParentName { get; init; }
}