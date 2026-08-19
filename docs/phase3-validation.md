# QuickPhrase Phase 3 验证记录

状态：`PHASE3_VERIFY_PASS`  
基线：`QuickPhrase Architecture v1.0 — FROZEN`  
范围：不可变内存搜索、拼音适配、Commit 后索引一致性；不包含 Native Launcher 和真实投递。

## 已实现

- Core `ISearchService`、查询规范化、固定 MatchKind 排序和有限模糊兜底。
- `PhraseSearchRuntime`：Repository Mutation Gate、不可变快照、增量失败保留旧快照和后台重建。
- Platform.Windows `PinyinMProvider`，固定 `PinyinM.NET 2.0.0`，支持全拼、首字母、多音字组合上限和混合文本。
- `QuickPhraseDataRuntime.Search` 从 SQLite 启动快照构建，但搜索过程不查询 SQLite。
- 拼音构建失败时降级到标题、标签和正文中文搜索，并返回 `SEARCH_INDEX_DIRTY` 状态。

## 验收命令

由 `scripts/verify-phase3.ps1 -IncludeDesktopSmoke` 串行执行：

| 检查 | 结果 |
| --- | --- |
| Phase 1/2 回归 | PASS |
| Phase 3 Debug 搜索测试 | PASS |
| Phase 3 Release 构建 | PASS |
| Phase 3 Release 搜索与性能测试 | PASS |
| React/Sites/浏览器 QA | PASS |
| Native Launcher/WebView2 smoke | PASS |

Phase 3 测试覆盖中文标题、标签、正文、`hfcc`、`hf`、全拼、混合文本、空查询、稳定排序、模糊兜底、Commit 后增量更新、索引 Dirty 恢复、启动降级和零 SQLite 搜索访问。

## 性能

固定 10,000 条话术，预热后在 Release 测量搜索调用；报告格式为：

```text
SEARCH_PERF count=10000 p50=1.547ms p95=8.729ms p99=14.396ms
```

验收门槛：搜索 P95 ≤ 50ms。

## 明确未实现

Native Launcher UI、全局快捷键、React IPC CRUD、UI Automation、剪贴板投递、TargetIdentity 和 Adapter Profile 留到 Phase 4/5。

## 下一执行项

**Phase 4 — Native Launcher 与生命周期**
