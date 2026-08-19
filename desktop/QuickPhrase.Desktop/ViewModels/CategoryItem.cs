using QuickPhrase.Core;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>
/// 分类展示模型：与 Core.Category 同构，但补充 UI 需要的排序、计数、展开折叠与选中态。
/// 用于话术库顶部一级 chips 横向条 + 二级内联嵌套树。
/// </summary>
public sealed record CategoryItem(
    Guid Id,
    string Name,
    Guid? ParentId,
    int SortOrder = 0,
    int Count = 0,
    bool IsExpanded = false,
    bool IsSelected = false,
    long Version = 0);
