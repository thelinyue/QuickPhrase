# QuickPhrase Design Token 规范

状态：Phase 0 设计基准，尚不代表下列资源键已全部写入生产 XAML。

本文定义 QuickPhrase 正式 Pure WPF 客户端的 Token 命名、值域和消费规则。后续实现必须以 `desktop/QuickPhrase.Desktop/DesignSystem` 中的 XAML `ResourceDictionary` 为运行真源；本文用于设计审查、迁移和 QA 对照。

## 1. 总体规则

```text
Token → Theme Resource → Style / ControlTemplate → Component → Page
```

- 资源键统一使用点分语义命名。
- `Color.*` 只存在于 Light/Dark Theme 字典。
- `Brush.*` 由对应 `Color.*` 创建，页面和模板优先消费 Brush。
- `Color.*`、`Brush.*` 和 `Effect.Shadow.*` 使用 `{DynamicResource ...}`；Light/Dark Theme 暴露完全相同的阴影资源键。
- Typography、Thickness、Radius、Size 和 Motion 使用 `{StaticResource ...}`。
- Light/Dark 主题键集合必须完全同构。
- 不保留旧 Resource Key，不建立兼容 Alias。
- 生产 XAML 不直接写 Hex、FontSize、标准 CornerRadius 或标准视觉尺寸。

## 2. Color 与 Brush

### 2.1 Light Theme

| Color Token | Light 值 | 语义 |
| --- | --- | --- |
| `Color.Brand.Primary` | `#3478F6` | 焦点、选择指示和非文字品牌强调 |
| `Color.Brand.Primary.Hover` | `#2869E8` | 品牌交互 Hover |
| `Color.Brand.Primary.Pressed` | `#2059C9` | 品牌交互 Pressed |
| `Color.Brand.BlueStrong` | `#2563D9` | Primary Button 默认填充 |
| `Color.Brand.Gold` | `#F2B735` | 收藏、少量品牌高光 |
| `Color.Background.Window` | `#EDF3FA` | 窗口背景 |
| `Color.Background.Navigation` | `#E8F0F9` | 导航背景 |
| `Color.Surface.Primary` | `#FFFFFF` | 主内容表面、默认 Card |
| `Color.Surface.Secondary` | `#F5F8FC` | 次级表面、弱分组 |
| `Color.Surface.Elevated` | `#FFFFFF` | 浮层、弹窗、高层级 Card |
| `Color.Text.Primary` | `#172033` | 标题和主要正文 |
| `Color.Text.Secondary` | `#44516A` | 次级正文 |
| `Color.Text.Muted` | `#6F7D94` | 辅助说明 |
| `Color.Text.Disabled` | `#A5AFBD` | 禁用文字 |
| `Color.Border.Default` | `#CBD6E4` | 控件和 Card 默认边框 |
| `Color.Border.Subtle` | `#D9E2EC` | 分隔线、弱边框 |
| `Color.Border.Focus` | `#3478F6` | 键盘焦点和输入焦点 |
| `Color.State.Hover` | `#EAF3FF` | 低噪音 Hover 背景 |
| `Color.State.Selected` | `#DCEAFF` | 选中背景 |
| `Color.State.SelectedBorder` | `#76AAFF` | 选中边框 |
| `Color.Status.Success` | `#2E9B63` | 成功状态 |
| `Color.Status.Warning` | `#E5A12D` | 警告状态 |
| `Color.Status.Error` | `#D64545` | 错误、Danger 操作 |
| `Color.Text.OnBrand` | `#FFFFFF` | 品牌色填充上的文字/图标 |
| `Color.Overlay` | `#000000` | 遮罩基础色，透明度由使用方语义资源控制 |
| `Color.Shadow.Default` | `#B8C7D9` | Light 主题阴影色 |

Primary Button 使用 `Color.Brand.BlueStrong` 作为默认填充，Hover/Pressed 使用 Brand Hover/Pressed Token。按钮前景统一引用主题语义 Token `Color.Text.OnBrand`：Light Theme 为 `#FFFFFF`，Dark Theme 为 `#172033`，使不同主题可以针对各自的品牌填充提供满足可读性的前景色。`Color.Brand.Primary` 不作为小号文字按钮的默认底色。

### 2.2 Dark Theme

Dark Theme 与 Light Theme 使用完全相同的键。已冻结的对照值如下：

| Color Token | Dark 值 |
| --- | --- |
| `Color.Brand.Primary` | `#75AEFF` |
| `Color.Brand.Primary.Hover` | `#8EC5FF` |
| `Color.Brand.Primary.Pressed` | `#5D97E8` |
| `Color.Brand.BlueStrong` | `#9BCBFF` |
| `Color.Brand.Gold` | `#F5B940` |
| `Color.Background.Window` | `#141B26` |
| `Color.Background.Navigation` | `#182331` |
| `Color.Surface.Primary` | `#1D2A39` |
| `Color.Surface.Secondary` | `#223142` |
| `Color.Surface.Elevated` | `#26384B` |
| `Color.Text.Primary` | `#F3F7FC` |
| `Color.Text.Secondary` | `#C4D0DE` |
| `Color.Text.Muted` | `#93A4B8` |
| `Color.Text.Disabled` | `#6F8094` |
| `Color.Border.Default` | `#3B4B60` |
| `Color.Border.Subtle` | `#2B3A4B` |
| `Color.Border.Focus` | `#8EC5FF` |
| `Color.State.Hover` | `#1F344E` |
| `Color.State.Selected` | `#203F67` |
| `Color.State.SelectedBorder` | `#75AEFF` |
| `Color.Status.Success` | `#58C78F` |
| `Color.Status.Warning` | `#F3B85B` |
| `Color.Status.Error` | `#F07878` |
| `Color.Text.OnBrand` | `#172033` |
| `Color.Overlay` | `#000000` |
| `Color.Shadow.Default` | `#0B111A` |

### 2.3 固定话术色板

固定话术色板与应用主题分离，Light/Dark 使用相同值，迁移时只重命名资源键，不改变既有业务色值：

| Color Token | 值 |
| --- | --- |
| `Color.Phrase.Default` | `#FFFFFF` |
| `Color.Phrase.Orange` | `#FF8839` |
| `Color.Phrase.Blue` | `#178BFF` |
| `Color.Phrase.Magenta` | `#FF73FF` |
| `Color.Phrase.Purple` | `#AF60FF` |
| `Color.Phrase.Green` | `#41C028` |
| `Color.Phrase.Pink` | `#F67E91` |
| `Color.Phrase.Teal` | `#00A8A8` |
| `Color.Phrase.Tan` | `#CB9563` |
| `Color.Phrase.Gray` | `#5C6772` |

每个 Color Token 均应有同名语义 Brush，例如：

```xaml
<SolidColorBrush x:Key="Brush.Text.Primary"
                 Color="{DynamicResource Color.Text.Primary}" />
```

组件模板通过 `{DynamicResource Brush.Text.Primary}` 等 Brush 键消费主题，不直接引用 Hex。

## 3. Typography

### 3.1 字体族

UI 字体回退顺序：

```text
Segoe UI Variable
Segoe UI
Microsoft YaHei UI
Microsoft YaHei
```

`Typography.Mono` 用于快捷键键帽或需要等宽对齐的短文本；实现时可沿用当前 WPF 的等宽字体回退资源，但不得在页面内单独指定字体族。

### 3.2 排版层级

| Token | FontSize | FontWeight | LineHeight | LetterSpacing | 用途 |
| --- | ---: | --- | ---: | ---: | --- |
| `Typography.Title.Large` | 18 | SemiBold | 24 | 0 | 页面主标题 |
| `Typography.Title.Medium` | 16 | SemiBold | 22 | 0 | 区域标题、弹窗标题 |
| `Typography.Title.Small` | 14 | SemiBold | 20 | 0 | 卡片或设置组标题 |
| `Typography.Body.Large` | 14 | Normal | 22 | 0 | 强调正文、主要列表正文 |
| `Typography.Body.Medium` | 13 | Normal | 20 | 0 | 默认正文和控件文字 |
| `Typography.Body.Small` | 12 | Normal | 18 | 0 | 次级正文 |
| `Typography.Caption` | 12 | Normal | 16 | 0 | 辅助说明、状态提示 |
| `Typography.Label` | 13 | Medium | 18 | 0 | 表单标签和控件标签 |
| `Typography.Mono` | 13 | Normal | 18 | 0 | 快捷键键帽、短等宽内容 |

WPF 原生 `TextBlock` 没有 CSS 式 `letter-spacing` 属性。V1 的 LetterSpacing 统一为 `0`，不引入自定义文本渲染器。页面必须使用 `Style.Text.Title.Large`、`Style.Text.Body.Medium` 等文字 Style，不自行组合字号、字重和行高。

## 4. Thickness

### 4.1 4px 基础网格

| Token | 值 |
| --- | ---: |
| `Thickness.None` | 0 |
| `Thickness.XS` | 4 |
| `Thickness.SM` | 8 |
| `Thickness.MD` | 12 |
| `Thickness.LG` | 16 |
| `Thickness.XL` | 20 |
| `Thickness.XXL` | 24 |
| `Thickness.XXXL` | 32 |
| `Thickness.4XL` | 40 |
| `Thickness.5XL` | 48 |

在 WPF 中，标准视觉间距统一定义为 `Thickness` 资源，而不是以裸 `Double` 分散在页面中。单值表示四边相同；方向性间距使用语义化复合 `Thickness`。

### 4.2 语义 Thickness

| Token | 设计用途 / 已冻结要求 |
| --- | --- |
| `Thickness.Border.Default` | 1px 边框；用于 Card、Input、Button 等标准边框 |
| `Thickness.Page` | 正式页面内容边距；具体复合值由页面类型的共享资源统一定义 |
| `Thickness.Section` | 页面区域之间的垂直节奏 |
| `Thickness.Card` | Card 内容 Padding，基准为 16px |
| `Thickness.Dialog` | Dialog 内容区域 Padding |
| `Thickness.Popup` | Popup 浮层内容 Padding |
| `Thickness.Control.Button.Compact` | 32px 高按钮的水平/垂直 Padding |
| `Thickness.Control.Button.Default` | 36px 高按钮的水平/垂直 Padding；水平基准 12–16px |
| `Thickness.Control.Input` | 32/36px 输入控件 Padding，避免左侧空白和光标偏移 |
| `Thickness.Gap.Inline.*` | 同一行控件、图标与文字的水平间隔 |
| `Thickness.Gap.Stack.*` | 垂直堆叠内容的间隔 |
| `Thickness.Settings.Page` | SettingsWindow 页面内容边距的唯一来源 |
| `Thickness.Settings.Row` | SettingItem 行内 Padding 的唯一来源 |

批准计划未冻结所有方向性复合 Thickness 的四边数值。Phase 1 写入 XAML 时应基于 4px 网格集中确定，并以 ResourceDictionary 为真源；页面不得自行补充同义 Token。

## 5. Radius

| Token | 值 | 用途 |
| --- | ---: | --- |
| `Radius.None` | 0 | 无圆角 |
| `Radius.XS` | 4 | 小型标签或紧凑元素 |
| `Radius.Small` | 6 | 小型表面 |
| `Radius.Medium` | 8 | 中型表面 |
| `Radius.Large` | 12 | 大型浮层 |
| `Radius.XL` | 16 | 保留的大圆角层级，谨慎使用 |
| `Radius.Control` | 6 | Button、Input、Select |
| `Radius.Card` | 8 | 默认 Card |
| `Radius.Popup` | 8 | Popup |
| `Radius.Dialog` | 12 | Dialog |
| `Radius.Launcher` | 12 | Launcher 主表面 |

圆角用于建立层级，不应让所有元素都成为胶囊形。页面禁止直接写标准 `CornerRadius`。

## 6. Sizes

### 6.1 控件尺寸

| Token | 值 | 用途 |
| --- | ---: | --- |
| `Size.Control.Compact` | 32 | 紧凑 Button/Input 高度 |
| `Size.Control.Default` | 36 | 默认 Button/Input 高度 |
| `Size.Button.Icon.Width` | 32 | 图标按钮宽度 |
| `Size.Button.Icon.Height` | 32 | 图标按钮高度 |
| `Size.Input.Search` | 36 | SearchInput 高度 |
| `Size.Switch.Width` | 40 | Switch 轨道宽度 |
| `Size.Switch.Height` | 22 | Switch 轨道高度 |
| `Size.Switch.Thumb` | 18 | Switch Thumb 直径 |
| `Size.TitleBar.Height` | 32 | 自定义窗口标题栏 |
| `Size.Navigation.Item` | 40 | 导航项高度 |
| `Size.Phrase.Row.Minimum` | 32 | 话术列表行最小高度 |
| `Size.Phrase.IndexColumn.GridLength` | 32 | 话术行序号/发送槽列宽（GridLength） |

### 6.2 窗口与布局尺寸

| Token | 值 | 用途 |
| --- | ---: | --- |
| `Size.Settings.Sidebar.Width` | 176 | 设置页侧栏宽度 |
| `Size.Settings.Content.Maximum` | 640 | 设置页内容最大宽度 |
| `Size.MainWindow.Width` | 1200 | 主窗口默认宽度 |
| `Size.MainWindow.Height` | 760 | 主窗口默认高度 |
| `Size.MainWindow.MinimumWidth` | 900 | 主窗口最小宽度 |
| `Size.MainWindow.MinimumHeight` | 560 | 主窗口最小高度 |
| `Size.SettingsWindow.Width` | 860 | 设置窗口默认宽度 |
| `Size.SettingsWindow.Height` | 680 | 设置窗口默认高度 |
| `Size.SettingsWindow.MinimumWidth` | 560 | 设置窗口最小宽度 |
| `Size.SettingsWindow.MinimumHeight` | 480 | 设置窗口最小高度 |
| `Size.Launcher.MinimumWidth` | 680 | Launcher 最小宽度 |
| `Size.Launcher.MaximumHeight` | 520 | Launcher 最大高度 |

窗口尺寸属于统一布局约束，仍允许 `GridLength` 的 `Auto`、`*`、比例和 `0` 直接存在于结构 XAML。

## 7. Motion

| Token | 值 | 用途 |
| --- | --- | --- |
| `Motion.Duration.Fast` | 80ms | Hover、Pressed 等即时反馈 |
| `Motion.Duration.Normal` | 140ms | Focus、Checked、轻量状态切换 |
| `Motion.Duration.Slow` | 200ms | Popup 或层级切换 |
| `Motion.Easing.Standard` | Cubic EaseOut | 常规进入/反馈 |
| `Motion.Easing.Emphasized` | Cubic EaseInOut | 需要强调的短状态切换 |

动效只用于 Hover、Pressed、Focus、Popup 和轻量状态切换，不增加持续装饰动画，不以动画延迟效率工具的主要操作反馈。

## 8. Shadow

阴影遵循低噪音原则：默认 Card 只使用 `Brush.Border.Default` 的 1px Border，不使用 DropShadow；DropShadow 仅用于 Elevated Card、Dialog 和 Popup。

| Token | 所属字典 | 说明 |
| --- | --- | --- |
| `Color.Shadow.Default` | Light/Dark Theme | 唯一的主题阴影资源；Light `#B8C7D9`，Dark `#0B111A` |
| `Effect.Shadow.Elevated` | Light/Dark Theme | Elevated Card 的轻量 `DropShadowEffect` |
| `Effect.Shadow.Dialog` | Light/Dark Theme | Dialog 层级 `DropShadowEffect` |
| `Effect.Shadow.Popup` | Light/Dark Theme | Popup 层级 `DropShadowEffect` |

Theme 定义 `Color.Shadow.*` 以及三个同名 `Effect.Shadow.*`，Effect 的 `Color` 属性使用 `DynamicResource` 引用主题阴影颜色。页面和控件不得声明额外 Shadow Effect。

批准计划只冻结了阴影的使用层级和“轻量”原则。运行真源由 Light/Dark Theme 中的三个 `Effect.Shadow.*` 确定；页面和视觉板不得复制具体阴影参数。

## 9. Token 使用示例

```xaml
<Border Background="{DynamicResource Brush.Surface.Primary}"
        BorderBrush="{DynamicResource Brush.Border.Default}"
        BorderThickness="{StaticResource Thickness.Border.Default}"
        CornerRadius="{StaticResource Radius.Card}"
        Padding="{StaticResource Thickness.Card}">
    <TextBlock Style="{StaticResource Style.Text.Body.Medium}"
               Foreground="{DynamicResource Brush.Text.Primary}" />
</Border>
```

示例表达引用规则，不代表 `Thickness.Border.Default` 等资源已在当前生产字典中落地。实际资源键和实现状态必须由 Phase 1/2 的 XAML 与测试确认。

## 10. 验证要求

后续实现至少验证：

- Light/Dark 主题资源键集合完全一致；
- 主题字典外不存在 Hex；
- 生产 XAML 不再引用旧 Token Key；
- `Color.*`、`Brush.*`、`Effect.Shadow.*` 的消费点使用 `DynamicResource`；三个 Shadow Effect 只在 Light/Dark Theme 中定义；
- Typography、Thickness、Radius、Size、Motion 使用 `StaticResource`；
- 页面不存在直接 FontSize、标准 CornerRadius 和可被 Token 覆盖的标准视觉尺寸；
- 文档与 XAML 不一致时，以 XAML ResourceDictionary 为准并同步修正文档。
