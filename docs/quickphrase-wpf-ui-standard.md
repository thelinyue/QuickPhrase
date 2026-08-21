# QuickPhrase 正式 WPF 界面统一标准

> 适用范围：`desktop/QuickPhrase.Desktop` 的正式 WPF Window、View、共享控件和对话框。`src/`、`prototype/` 与 Sites 链路不属于本标准，也不得作为正式产品 UI 的实现依据。

## 1. 目标与边界

QuickPhrase 使用 `.NET 10 + Pure WPF`。界面保持当前蓝色轻量品牌、浅/深色主题和既有页面结构；本标准只收敛视觉资源与可访问性，不改变话术管理、目标捕获、插入或发送等业务流程。

优先级固定为：**宁可界面状态不执行，也不能因视觉改造改变投递安全行为。**

## 2. 资源分层与引用规则

- `DesignSystem/Tokens`：只定义字体、尺寸、间距、圆角、动效和品牌/业务色板原语。
- `DesignSystem/Themes`：只定义 Light/Dark 语义颜色；两个主题必须暴露相同的语义 Key。
- `DesignSystem/Styles`：组合 Token 与主题 Brush，提供文字、按钮、输入、选择、列表、表面和弹窗的公共 Style。
- `DesignSystem/Components`：仅封装有稳定交互语义的复合控件；View/ViewModel 不依赖 Platform.Windows 具体实现。

页面、窗口和对话框必须引用 `StaticResource` 的 Token/Style，以及 `DynamicResource` 的主题 Brush。禁止在正式页面直接写 Hex 颜色、数值字号、数值圆角，或复制公共控件模板。颜色、边框、字体、阴影和焦点态的通用模式必须先进入 DesignSystem。

允许页面局部 Style 的唯一情形是它表达不可复用的**业务状态**（例如分类展开、引导步骤可见性或数据反馈折叠）。局部 Style 必须优先 `BasedOn` 公共 Style，且不能复制按钮、输入框、列表项或窗口的通用 Template。

## 3. 组件选型

| 场景 | 必须使用 |
| --- | --- |
| 应用主窗口、独立新建窗口、独立设置窗口 | `Style.Window.Shell` + `Style.Surface.ContentRegion` |
| 对话框与确认窗口 | `Style.Dialog.Window` |
| 标题栏、页面根背景 | `Style.Surface.TitleBar`、`Style.Surface.Page` 或 `Style.View.Root` |
| 标题、正文、标签、提示、错误 | `Style.Text.*` 的语义变体；错误使用 `Style.Text.Status.Error` |
| 主操作、次操作、幽灵操作、危险操作、图标操作 | `Style.Button.Primary`、`Secondary`、`Ghost`、`Danger`、`Icon` |
| 文本输入、密码、搜索、下拉 | `Style.Input.*`、`Style.Select.Default` |
| 导航、话术行、共享搜索历史 | `Style.ListItem.*`，共享历史固定使用 `Style.ListItem.SearchHistory` |
| 设置分组、卡片、Popup、空/加载/错误状态 | `Style.Setting.*`、`Style.Card.*`、`Style.Popup.Surface`、`StatePresenter` |

默认 Card 不加阴影；只有 Elevated、Popup 和 Dialog 可使用受管阴影。危险操作必须使用危险语义 Style，并沿用现有确认流程，不得因为界面统一而绕过确认。

## 4. 主题、状态与无障碍

- Light/Dark 主题只能通过语义 Brush 切换；组件不得引用固定主题颜色。
- 可交互控件必须保留鼠标悬停、按下、选中、禁用、校验失败和键盘焦点状态；焦点环不能被隐藏或遮挡。
- 所有仅由图标表达的按钮必须包含 `AutomationProperties.Name`，并在非显而易见的情况下提供 `ToolTip`。
- 输入、下拉、密码和开关必须有可读标签；无法通过可见文字推断时，必须设置 `AutomationProperties.Name`，必要时补充 `AutomationProperties.HelpText`。
- Tab 顺序遵循视觉顺序；弹窗和菜单维持现有的焦点循环，不允许焦点落入不可见控件。
- 用户可见错误信息采用清晰中文；日志和错误提示不得包含话术正文、剪贴板、联系人或聊天内容。

## 5. 变更与验收

修改正式 WPF UI 时：

1. 先选用已有 Style/Component；确认确实不存在可复用模式后，才在 DesignSystem 新增语义 Key，并补充中文设计注释。
2. 新增页面局部 Style 时，说明其业务状态边界，并使用 `BasedOn` 复用公共视觉定义。
3. 为新增图标操作和无标签输入补齐 UIA 名称；验证键盘与深浅主题状态。
4. 运行 `QuickPhrase.Desktop.Tests` 的设计系统、XAML 解析和统一标准测试，再运行 `dotnet build QuickPhrase.sln`。

自动化守卫覆盖窗口/页面表面、对话框 Style、共享搜索历史模板、核心输入 UIA 名称和正式 XAML 的 Hex 边界。现有业务行为测试失败不得被 UI 改动掩盖，应单独定位并报告。
