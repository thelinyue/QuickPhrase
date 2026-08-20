# QuickPhrase WPF Design System

状态：设计基准文档与视觉板已建立；运行时迁移和验收状态以当前 XAML、自动测试与人工记录为准。

QuickPhrase Design System 用于统一正式 WPF 客户端的颜色、排版、间距、圆角、尺寸、动效、阴影和组件状态。目标是在保留现有“天空蓝、冰川蓝、云雾白、少量金色”品牌方向的基础上，形成轻量、低噪音、Windows 原生的效率工具视觉语言，避免页面继续以局部样式拼接。

> 本目录中的文档和视觉板用于设计审查、开发对照与 QA。生产运行时的唯一真源是 `desktop/QuickPhrase.Desktop/DesignSystem` 下的 XAML `ResourceDictionary`；文档不得覆盖或替代运行时资源定义。

## 1. 适用范围

本系统只服务 QuickPhrase 正式产品：

- `.NET 10 LTS + Pure WPF`；
- 正式 UI 位于 `desktop/QuickPhrase.Desktop`；
- 不引入 React、Vue、WebView2、CSS Token 或网页桥接层；
- 不以 `src/`、`prototype/`、Sites 页面或历史 Web 原型作为正式界面依据；
- 不改变既有绑定、命令、导航、窗口生命周期和投递安全链。

## 2. 分层架构

固定资源消费链如下：

```text
Design Token
    ↓
Theme Resource
    ↓
WPF Styles / ControlTemplates
    ↓
Reusable UserControls
    ↓
Page
```

各层职责：

| 层级 | 职责 | 禁止事项 |
| --- | --- | --- |
| Design Token | 定义排版、Thickness、Radius、Size、Motion 等与主题无关的基础值 | 页面自行复制视觉常量 |
| Theme Resource | 定义 Light/Dark 的 `Color.*`、`Brush.*` 和 `Effect.Shadow.*` | 在主题字典外写 Hex |
| Styles / ControlTemplates | 统一原生控件结构及 Default、Hover、Pressed、Disabled、Focus、Validation 状态 | 页面重新实现按钮、输入框和开关模板 |
| Reusable UserControls | 封装重复的组合布局和必要输入行为 | 在组件中承担业务保存、搜索、热键注册或 SQLite 写入 |
| Page | 组合组件并绑定 ViewModel | 建立页面专属 Token 或第二套主题系统 |

## 3. 冻结目录

```text
desktop/QuickPhrase.Desktop/
├── DesignSystem/
│   ├── Tokens/
│   │   ├── Typography.xaml
│   │   ├── Thickness.xaml
│   │   ├── Radius.xaml
│   │   ├── Sizes.xaml
│   │   ├── Motion.xaml
│   │   ├── Colors.xaml
│   │   └── Brushes.xaml
│   ├── Themes/
│   │   ├── Theme.Light.xaml
│   │   └── Theme.Dark.xaml
│   ├── Styles/
│   │   ├── Text.xaml
│   │   ├── Buttons.xaml
│   │   ├── Inputs.xaml
│   │   ├── SelectionControls.xaml
│   │   ├── Lists.xaml
│   │   ├── Surfaces.xaml
│   │   └── Dialogs.xaml
│   └── Components/
│       ├── Components.xaml
│       ├── SearchInput.xaml
│       ├── PhraseResultItem.xaml
│       ├── CategoryTreeItem.xaml
│       ├── SettingItem.xaml
│       └── ShortcutInput.xaml
├── Themes/
│   ├── QuickPhraseTheme.xaml
│   ├── Controls.xaml
│   └── Converters.xaml
└── App.xaml
```

目录层级冻结。后续应优先向现有 Token、Style 或 Component 字典增加资源，不允许页面随意新增资源目录或页面专属主题体系。

## 4. 固定加载顺序

`App.xaml` 最终只合并三个生产入口，顺序固定为：

1. `Themes/Converters.xaml`
2. `Themes/QuickPhraseTheme.xaml`
3. `Themes/Controls.xaml`

聚合入口内部顺序固定：

```text
QuickPhraseTheme.xaml
  Typography → Thickness → Radius → Sizes → Motion → Colors → Light Theme → Brushes

Controls.xaml
  Text → Buttons → Inputs → SelectionControls → Lists
  → Surfaces → Dialogs → Components
```

V1 默认只加载 Light Theme。Dark Theme 必须与 Light Theme 暴露完全相同的主题资源键，但本阶段不增加主题切换 UI。未来主题服务只替换 Light/Dark 主题字典，不重建页面、Style 或 ControlTemplate。

## 5. 资源命名与引用规则

资源键采用点分语义命名：

```text
Color.Brand.SkyBlue
Brush.Background.Default
Brush.Text.Primary
Typography.Body.Medium
Thickness.Control.Input
Radius.Control
Size.Control.Default
Motion.Duration.Fast
Style.Button.Primary
Style.Input.Default
Style.Card.Default
```

引用规则：

- 品牌原色和固定话术色板定义在 `Colors.xaml`；语义 `Color.*` 定义在 Light/Dark Theme 字典中。
- 页面和模板优先消费 `Brush.*`，不得直接写颜色值。
- 主题相关的 `Color.*`、`Brush.*`、背景、前景、边框、Accent 和 `Effect.Shadow.*` 使用 `{DynamicResource ...}`；三个 Shadow Effect 由 Light/Dark Theme 以同名键提供，控件不直接写阴影参数。
- Typography、FontSize、FontWeight、Thickness、Radius、Size 和 Motion 使用 `{StaticResource ...}`。
- Style 使用 `{StaticResource ...}`。
- 生产页面禁止直接写 Hex、FontSize、标准 CornerRadius 和标准视觉尺寸。
- `Auto`、`*`、`0`、Grid 比例以及确有必要的一次性结构尺寸可以保留字面量。
- 新旧 Resource Key 一次性迁移；旧 Key 删除后不建立兼容 Alias，所有消费者必须同步更新。

## 6. 主题同构

Light 与 Dark 主题遵守以下约束：

1. 资源键集合完全一致，只允许值不同。
2. 固定话术色板使用 `Color.Phrase.*` / `Brush.Phrase.*`，保持业务色值不变。
3. 组件模板只依赖语义 Brush，不判断当前主题。
4. 主题切换时 `Color.*`、`Brush.*` 与 `Effect.Shadow.*` 通过 `DynamicResource` 重新解析；Light/Dark Theme 暴露完全相同的阴影资源键。
5. Token、Style、Component 不复制 Light/Dark 两份实现。

## 7. 公共组件边界

原生 WPF Style / ControlTemplate 负责：

- Button：Primary、Secondary、Ghost、Danger；Compact 32px、Default 36px；
- TextBox / Search 输入、ComboBox、Switch；
- Navigation/Phrase ListItem；
- Card、Dialog、Popup。

Reusable UserControl 负责：

- `SettingItem`：`Title + Description + ControlContent`；
- `SearchInput`；
- `PhraseResultItem`；
- `CategoryTreeItem`；
- `ShortcutInput`：只捕获、展示、报错和取消，不注册 Win32 热键、不写 SQLite。

所有可交互控件必须统一覆盖 Default、Hover、Pressed/Checked、Disabled、Keyboard Focus；输入类额外覆盖 Validation Error。禁用状态不得仅通过降低整个控件 Opacity 表达，焦点环必须使用主题 Brush。

## 8. 页面使用原则

- 页面先复用公共 Style 和 Component，再组合业务内容。
- 设置项统一使用 `SettingItem`，不重复编写 `Grid + StackPanel + Control`。
- 列表继续使用 ListBox/ListView 虚拟化，不在列表项 UserControl 内再嵌套 ItemsControl。
- 默认 Card 使用轻边框，不为每个列表项套 Card；DropShadow 只用于 Elevated、Dialog 和 Popup。
- 页面迁移只处理视觉资源与组件复用，不顺带重构业务逻辑或清理无关代码。

## 9. 文档治理

- `tokens.md` 记录 Token 名称、设计值和使用规则。
- `components.md` 记录组件结构、状态、依赖属性和交互边界。
- `migration.md` 记录分阶段迁移顺序、检查项和遗留问题。
- `quickphrase-design-system-board.svg/.png` 仅用于视觉对照。
- 如果文档与运行 XAML 不一致，以 XAML `ResourceDictionary` 为准，并在同一变更中修正文档。

## 10. 当前实施状态

截至 2026-08-20，本次文档任务对生产资源进行了只读核验：

- `App.xaml` 仍按 `Converters → QuickPhraseTheme → Controls` 的固定顺序加载三个聚合入口；
- `DesignSystem/Tokens`、`Themes`、`Styles`、`Components` 的计划文件均已存在；
- Light/Dark Theme 当前各暴露 87 个同名资源键，键集合一致；
- `SettingItem`、`SearchInput`、`PhraseResultItem`、`CategoryTreeItem`、`ShortcutInput` 组件文件已存在。

以上只说明文档与当前资源目录、入口和主题键集合一致，不等同于 Phase 0–7 已全部验收。本次任务没有启动 WPF 应用，也没有执行 DPI、全键盘、截图对比或 Windows 系统热键人工验证；这些项目必须继续按 `migration.md` 分项记录，不能由文档、构建或静态检查替代。
