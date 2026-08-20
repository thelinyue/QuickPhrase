# QuickPhrase 品牌主题重构与正式能力收口实施计划

日期：2026-08-19
状态：已确认并执行
正式产品边界：`.NET 10 LTS + Pure WPF + Win32/UIA + SQLite + Core 内存搜索`

## 成功标准

1. 新四文件 Token 架构成为正式 WPF 运行真源。
2. 话术库保持布局、绑定和交互不变，改为浅灰白背景、浅蓝选择和 3px 天空蓝指示线。
3. Core、Desktop、Repository 和导入链路不再暴露星标业务能力。
4. SQLite v1 数据库可事务升级到 v2，且所有其他有效数据保持不变。
5. 定向测试、Desktop 测试、Architecture 测试和解决方案构建通过。
6. 自动验证与 Windows 11 / DPI 人工验收边界分别报告。

## Task 1：视觉预览确认

- 依据当前真实 `1200×760` WPF 话术库布局生成预览。
- 删除星形入口和相关说明。
- 使用天空蓝、云白、闪电金品牌语言。
- 用户确认后才修改正式代码。

验证：预览图经用户确认。

## Task 2：建立四文件 Design Token

新增：

```text
desktop/QuickPhrase.Desktop/DesignSystem/Tokens/Colors.xaml
desktop/QuickPhrase.Desktop/DesignSystem/Tokens/Brushes.xaml
desktop/QuickPhrase.Desktop/DesignSystem/Themes/Theme.Light.xaml
desktop/QuickPhrase.Desktop/DesignSystem/Themes/Theme.Dark.xaml
```

删除旧 Light/Dark 主题文件，不建立兼容 Alias。更新：

```text
desktop/QuickPhrase.Desktop/Themes/QuickPhraseTheme.xaml
desktop/QuickPhrase.Desktop/Services/ThemeService.cs
```

聚合顺序固定为：

```text
Typography → Thickness → Radius → Sizes → Motion
→ Colors → Theme.Light → Brushes
```

验证：Light/Dark Key 集合同构；资源可由 WPF 测试加载；旧文件不再引用。

## Task 3：迁移组件与页面颜色

- 更新共享 Button、Input、List、Navigation、Dialog、Launcher 和页面资源引用。
- 页面背景改为 `Brush.Background.Default`。
- 分类和话术 Selected 改为 `Brush.Surface.Selected` + `Brush.Accent.Primary`。
- 话术行保留左侧指示线并将宽度设为 `3px`。
- 主按钮使用 `Brush.Accent.Primary`，Focus 使用 `Brush.Border.Focus`。
- 金色只用于快捷键、Badge 或核心能力提示。
- 保留当前布局、绑定、命令和交互。

验证：正式 Desktop XAML 不再出现旧 Brush Key 或直接颜色声明；Desktop 主题契约测试通过。

## Task 4：移除正式产品业务契约

从以下公开契约删除对应参数：

```text
Phrase
CreatePhraseCommand
UpdatePhraseCommand
```

同步更新：

- Desktop 编辑、移动、排序、首次引导和 Fake。
- SQLite INSERT、UPDATE、SELECT、Reader 映射与相等比较。
- 话术包导入固定写入。
- 过期中文注释和用户文案。

验证：正式业务代码除 v1→v2 历史迁移所需字符串外无相关字段引用；解决方案编译通过。

## Task 5：SQLite v1 → v2 事务迁移

- `CurrentSchemaVersion` 升到 `2`。
- 全新 `InitialSchema.sql` 直接创建 v2。
- v1 在单一事务中删除旧索引和旧字段，验证结构、外键和完整性后更新 `user_version`。
- 失败时回滚并返回稳定中文错误：
  - `DATABASE_MIGRATION_FAILED`
  - `DATABASE_SCHEMA_INVALID`
  - `DATABASE_UNSUPPORTED_VERSION`
- 不恢复通用迁移框架、checksum、自动备份或重建数据库逻辑。

验证：

- v1 数据中不同历史值均能迁移。
- 分类、设置、搜索历史和话术其他字段保持不变。
- 失败事务不产生半迁移。
- v2 重复打开不重复迁移。
- 新数据库直接为 v2。

## Task 6：更新当前正式文档

更新：

```text
docs/quickphrase-v1-prd.md
docs/design-system/README.md
docs/design-system/tokens.md
docs/design-system/components.md
docs/design-system/migration.md
docs/superpowers/specs/2026-08-19-light-theme-contrast-brand-design.md
docs/superpowers/plans/2026-08-19-light-theme-contrast-brand-plan.md
```

不修改 Web 原型和其他历史归档方案。

验证：当前文档使用新文件名、新 Token 和新品牌使用规则。

## Task 7：最终验证

```powershell
dotnet test tests/QuickPhrase.Architecture.Tests/QuickPhrase.Architecture.Tests.csproj --no-restore --filter FullyQualifiedName~FavoriteRemovalMigrationTests
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~BrandThemeContractTests
dotnet build QuickPhrase.sln --no-restore
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-build
dotnet test tests/QuickPhrase.Architecture.Tests/QuickPhrase.Architecture.Tests.csproj --no-build
```

执行源码扫描：

- 正式项目无残留业务字段、旧主题文件引用和旧 Brush Key。
- Light/Dark 语义 Key 完全一致。
- 新 v2 schema 不创建被移除字段和索引。

最后检查 `git status` 和本次相关差异，不 reset、clean、批量格式化或暂存无关文件。

## 人工验收边界

自动测试通过后，仍需在真实 Windows 11 WPF 会话检查：

- Light/Dark 的 Default、Hover、Pressed、Selected、Focus、Disabled。
- MainWindow、Launcher、Editor、Settings 和 Dialog。
- 100%、125%、150%、200% DPI。

没有实际执行上述矩阵时，最终报告必须标注为“未完成人工 GUI/DPI 验收”。
