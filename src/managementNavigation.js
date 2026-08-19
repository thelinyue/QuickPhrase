function stableValue(value) {
  if (Array.isArray(value)) return value.map(stableValue);
  if (value && typeof value === "object") {
    return Object.keys(value).sort().reduce((result, key) => {
      result[key] = stableValue(value[key]);
      return result;
    }, {});
  }
  return value;
}

/** 比较管理页草稿与已保存基线，避免导航时静默丢失用户输入。 */
export function hasUnsavedChanges(baseline, draft) {
  return JSON.stringify(stableValue(baseline)) !== JSON.stringify(stableValue(draft));
}

/** 保存后保留原列表上下文；新建或脱离筛选时切回全部话术以确保结果可见。 */
export function restoreLibraryContextAfterSave(context, saved, options = {}) {
  if (options.isNew || options.isVisible === false) {
    return { activeFilter: "all", query: "", selectedId: saved.id, scrollTop: 0 };
  }
  return { ...context, selectedId: saved.id };
}

/** 删除当前话术后优先选中下一项，删除末项时回退到上一项。 */
export function nextSelectionAfterDelete(ids, deletedId) {
  const index = ids.indexOf(deletedId);
  if (index < 0) return ids[0] || null;
  return ids[index + 1] || ids[index - 1] || null;
}
