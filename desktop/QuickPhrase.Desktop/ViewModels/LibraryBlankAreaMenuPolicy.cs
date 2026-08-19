namespace QuickPhrase.Desktop.ViewModels;

/// <summary>
/// 话术库空白区域右键菜单的分类上下文。
/// ActiveCategory 表示“新增话术”的目标分类；TopCategory 表示当前操作所属的一级分类。
/// 当 ActiveCategory 为二级分类时，TopCategory 用于创建同级二级分类，避免产生三级分类。
/// </summary>
public sealed record LibraryBlankAreaMenuContext(
    CategoryItem? ActiveCategory,
    CategoryItem? TopCategory)
{
    public bool HasTopCategory => TopCategory is not null;

    public bool IsSubCategory => ActiveCategory?.ParentId is not null;
}

/// <summary>
/// 话术库空白区域菜单的纯规则策略。
/// 该类不访问 WPF 控件、数据库或平台服务，只统一表达空白命中和两级分类下的操作目标，
/// 让视图事件代码与可测试的业务规则保持分离。
/// </summary>
public static class LibraryBlankAreaMenuPolicy
{
    public static LibraryBlankAreaMenuContext CreateContext(CategoryItem? activeCategory, CategoryItem? topCategory) =>
        new(activeCategory, topCategory);

    /// <summary>只有未命中话术/分类节点时，空白菜单才允许打开。</summary>
    public static bool ShouldOpenMenu(bool nodeHit) => !nodeHit;

    public static string? GetNewPhraseUnavailableMessage(LibraryBlankAreaMenuContext context) =>
        context.HasTopCategory ? null : "先新增一级分类，再新增话术";

    public static string? GetNewSubCategoryUnavailableMessage(LibraryBlankAreaMenuContext context) =>
        context.HasTopCategory ? null : "先新增一级分类，再新建二级分类";

    /// <summary>新增话术始终落在实际当前分类：一级或二级。</summary>
    public static CategoryItem? ResolveNewPhraseTarget(LibraryBlankAreaMenuContext context) =>
        context.HasTopCategory ? context.ActiveCategory : null;

    /// <summary>
    /// 新增二级分类始终以一级分类为父级。
    /// 在二级分类上下文中，这会创建同级节点，而不是继续嵌套。
    /// </summary>
    public static CategoryItem? ResolveNewSubCategoryParent(LibraryBlankAreaMenuContext context) =>
        context.HasTopCategory ? context.TopCategory : null;
}
