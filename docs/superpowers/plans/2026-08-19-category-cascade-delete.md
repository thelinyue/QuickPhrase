# 分类级联删除 Implementation Plan

**Goal:** 允许分类在二次确认后级联删除其子分类、话术和话术标签，并保持数据库事务与搜索索引一致。

**Architecture:** 分类仓储在单个 SQLite 写事务中读取目标子树，先删除 `phrase_tags`、话术，再删除分类，提交成功后返回实际删除的话术 ID。Core 的 `PhraseSearchRuntime` 根据删除结果精确移除索引；WPF 在调用命令前连续显示两次确认，只有两次确认通过才执行删除。

**Tech Stack:** .NET 10、Pure WPF、Microsoft.Data.Sqlite、xUnit。

---

### Task 1: 删除结果契约与失败测试

**Files:**
- Modify: `desktop/QuickPhrase.Core/Contracts.cs`
- Modify: `desktop/QuickPhrase.Core/PhraseSearchRuntime.cs`
- Modify: `tests/QuickPhrase.Architecture.Tests/Phase2DataTests.cs`
- Modify: `tests/QuickPhrase.Architecture.Tests/Phase3SearchTests.cs`
- Modify: `tests/QuickPhrase.Desktop.Tests/CategoryDeletionTests.cs`
- Modify: `tests/QuickPhrase.Desktop.Tests/Fakes/FakeCommandService.cs`

- [ ] 扩展 `DeleteResult`，携带级联删除实际涉及的话术 ID。
- [ ] 先增加非空一级分类、含二级分类、标签清理、索引移除和事务回滚测试，并确认新增测试在旧实现上失败。
- [ ] 更新内存 Fake，使其模拟级联删除，供删除编排测试使用。

### Task 2: SQLite 单事务级联删除

**Files:**
- Modify: `desktop/QuickPhrase.Platform.Windows/SqliteCategoryRepository.cs`

- [ ] 在事务中读取目标分类及所有后代分类 ID。
- [ ] 查询这些分类下的全部话术 ID。
- [ ] 按 `phrase_tags` → `phrases` → `categories` 顺序删除。
- [ ] 保留版本冲突和不存在分类语义；任意 SQL 异常回滚并返回错误。
- [ ] 返回被删除话术 ID 集合，供 Core 精确更新索引。

### Task 3: 搜索索引与 WPF 二次确认

**Files:**
- Modify: `desktop/QuickPhrase.Core/PhraseSearchRuntime.cs`
- Modify: `desktop/QuickPhrase.Desktop/MainWindow.xaml.cs`

- [ ] 话术删除继续移除单个 ID，分类级联删除遍历结果中的全部话术 ID。
- [ ] 更新中文注释和删除确认文案，移除“非空分类不能删除”的旧提示。
- [ ] 保证第一次或第二次取消均不调用删除命令。

### Task 4: 验证

- [ ] 运行 `dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore`。
- [ ] 运行分类/搜索/数据相关架构测试。
- [ ] 运行 `dotnet build QuickPhrase.sln --no-restore`，如遇已有无关问题如实报告。
- [ ] 检查改动文件和测试结果，确认未触碰原型链路。
