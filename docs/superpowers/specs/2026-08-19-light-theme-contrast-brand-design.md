# QuickPhrase 应用图标品牌主题与能力收口设计

日期：2026-08-19
状态：已确认，实施中的正式设计依据
范围：`QuickPhrase.Core`、`QuickPhrase.Platform.Windows`、`QuickPhrase.Desktop`、对应测试和当前正式文档

## 1. 设计目标

QuickPhrase 是 Windows 桌面快捷话术工具。视觉语言直接来源于应用图标：

- **天空蓝**：智能、连接、速度，用于品牌识别、Focus 和选择指示。
- **云白**：轻量、空间感，用于页面背景与 Surface。
- **闪电金**：快捷、能量，用于快捷键、Badge 和核心能力提示。

整体遵循 Windows 11 Fluent Design，保持高级、轻量、低视觉噪音，不采用传统客服后台的大面积蓝色铺底。

## 2. 视觉原则

1. 品牌蓝不是页面背景色。
2. 主操作按钮使用 `#2563EB`，保证白字可读性。
3. 天空蓝 `#4A90FF` 用于 Focus、品牌元素和左侧选择指示线。
4. 浅蓝 `#EFF6FF` 用于 Selected；浅灰白 `#F8FAFC` 用于背景和中性 Hover。
5. 金色 `#FBBF24` 只强调快捷键、Badge 或核心能力，不用于普通按钮。
6. 默认边框使用 `#E2E8F0`，Focus 使用天空蓝。
7. Disabled 由统一组件模板降低视觉权重，不在页面局部硬编码透明度。

## 3. Design Token 架构

正式运行时使用四文件分层：

```text
DesignSystem/Tokens/Colors.xaml
DesignSystem/Themes/Theme.Light.xaml
DesignSystem/Themes/Theme.Dark.xaml
DesignSystem/Tokens/Brushes.xaml
```

职责：

- `Colors.xaml`：品牌原色和固定话术数据色板。
- `Theme.Light.xaml`：Light 主题语义颜色。
- `Theme.Dark.xaml`：与 Light 同 Key 的可读暗色语义颜色。
- `Brushes.xaml`：把语义颜色映射为页面和组件消费的 Brush，并集中定义阴影。

核心 Brush：

```text
Brush.Background.Default
Brush.Background.Secondary
Brush.Surface.Default
Brush.Surface.Hover
Brush.Surface.Selected
Brush.Accent.Primary
Brush.Accent.Primary.Hover
Brush.Accent.Primary.Pressed
Brush.Accent.Soft
Brush.Accent.Gold
Brush.Text.Primary
Brush.Text.Secondary
Brush.Text.Disabled
Brush.Text.OnAccent
Brush.Border.Default
Brush.Border.Strong
Brush.Border.Focus
Brush.Selection.Indicator
```

不保留旧主题文件和旧颜色 Alias。页面、Style 和 ControlTemplate 禁止直接写 Hex。

## 4. 话术库颜色映射

保持当前 `1200×760` WPF 布局、尺寸、绑定、命令和交互不变，只调整颜色与选择指示：

- 页面背景：`Brush.Background.Default`。
- 次级导航区域：`Brush.Background.Secondary`。
- 分类默认：透明或白色背景，`Brush.Text.Secondary` 文字。
- 分类选中：`Brush.Surface.Selected` 背景，`Brush.Accent.Primary` 文字。
- 话术行默认：`Brush.Surface.Default`。
- 话术行 Hover：`Brush.Surface.Hover`。
- 话术行 Selected：`Brush.Surface.Selected`，蓝色文字，左侧 `3px` `Brush.Selection.Indicator`。
- 默认边框：`Brush.Border.Default`；Focus：`Brush.Border.Focus`。

禁止用整块深蓝表示分类或列表选中状态。

## 5. 组件状态

| 状态 | 统一表现 |
| --- | --- |
| Default | 白色或默认 Surface |
| Hover | 浅灰或浅蓝 |
| Pressed | 在 Hover 基础上轻微加深 |
| Selected | 浅蓝背景、蓝色文字、必要时增加选择指示线 |
| Focus | 天空蓝描边，与 Selected 状态正交 |
| Disabled | 禁用文字和模板状态，保持可辨识 |

## 6. 数据与迁移边界

正式产品不再提供星标能力。Core 公开契约、Desktop 透传、SQLite 仓储读写和导入链路均不保留对应业务字段。

SQLite schema 从 v1 升级到 v2：

1. 在写连接事务中删除旧索引。
2. 物理删除 `phrases` 中的旧字段。
3. 验证目标结构、外键和数据库完整性。
4. 在同一事务中写入 `PRAGMA user_version = 2`。
5. 任一步失败则完整回滚，不重建或删除数据库。

迁移保留分类层级、话术其他字段、ID、排序、快捷键、颜色、使用计数、时间、设置和搜索历史。

## 7. 验证标准

自动验证：

- Light/Dark 拥有一致的语义 Key。
- 新四文件可被 WPF 正确加载，旧主题文件不再引用。
- 正式 XAML 不包含旧 Token 引用或直接颜色值。
- 话术选中指示线宽度为 `3px`。
- 新数据库直接创建 v2；v1 数据库迁移后保留非移除数据。
- Core、Repository、Importer、ViewModel 和 Fake 构造契约通过编译和测试。

人工验证：

- Light/Dark 的 Default、Hover、Pressed、Selected、Focus、Disabled。
- 主窗口、Launcher、编辑器、设置、对话框在 Windows 11 下的实际显示。
- 100%、125%、150%、200% DPI 下的边框、文字和选择指示线。

构建和自动测试通过不等同于 GUI、DPI 或 Windows 11 人工验收完成。

## 8. 范围外

- 不修改 `src/`、`prototype/`、`design-prototype/` 和 Sites 构建链路。
- 不参考 Web 原型重构正式 WPF 布局。
- 不新增主题切换 UI、插件、AI、云同步或其他 V2 能力。
- 不重置、清理或覆盖混合工作区中的既有改动。
