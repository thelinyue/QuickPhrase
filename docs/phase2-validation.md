# QuickPhrase Phase 2 验证记录

状态：`PHASE2_VERIFY_PASS`  
基线：`QuickPhrase Architecture v1.0 — FROZEN`  
范围：本地 SQLite 数据层；不包含搜索、UIA、热键、剪贴板和投递。

## 已实现

- Core 领域模型、快捷键规范化、Repository Contracts、结果码和 Commit 后变更结果。
- Platform.Windows `Microsoft.Data.Sqlite 10.0.10`、事务迁移、checksum、升级备份、WAL 和单写者队列。
- categories、phrases、tags、phrase_tags、settings schema 与 18 条固定标准话术种子数据。
- 乐观版本更新、重复 UUID 幂等、幂等删除、分类非空保护、快捷键唯一性和孤儿标签清理。
- Desktop 启动初始化正式数据目录：`%LOCALAPPDATA%\QuickPhrase`；WebView2 用户目录与 SQLite 数据目录分离。

## 验收命令

由 `scripts/verify-phase2.ps1 -IncludeDesktopSmoke` 串行执行：

| 检查 | 结果 |
| --- | --- |
| Phase 1 React/Sites/浏览器 QA 回归 | PASS |
| Phase 1 Native Launcher smoke | PASS |
| Phase 1 WebView2 lifecycle smoke | PASS |
| Phase 2 Data tests | PASS |
| Debug build/test | PASS |
| Release build/test | PASS |

Phase 2 Data tests 覆盖迁移种子、默认设置、版本冲突、快捷键冲突、标签规范化、孤儿标签、WAL/外键/busy timeout、checksum 失败、升级备份可读性、备份回滚、并发写入和取消。

## 明确未实现

搜索索引、Pinyin Provider、IPC CRUD、全局快捷键、UI Automation、Clipboard Transaction、TargetIdentity、Adapter Profile 和 Delivery State Machine 留到后续阶段。

## 下一执行项

**Phase 3 — 搜索引擎**
