# QuickPhrase WPF Design System：公共组件规范

> 状态：**实施基准，非已实现说明**
> 适用范围：`desktop/QuickPhrase.Desktop` 正式 Pure WPF 产品
> 视觉方向：Windows 11 Fluent Design、Raycast、Linear；亮色、低噪音、效率工具感；延续 QuickPhrase 蓝金品牌
> 最终真源：WPF `ResourceDictionary`、`Style`、`ControlTemplate` 与组件代码。本文件用于设计评审、开发对照和 QA，不代表组件已经完成。

## 1. 设计原则与架构边界

组件必须遵循固定消费链：

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

### 1.1 共通原则

1. 页面不得自行实现 Button、Input、Select、Switch、Card、Dialog、Popup 的视觉体系。
2. 页面不得硬编码 Hex、标准字号、标准圆角、标准间距或标准控件高度。
3. 主题 `Color.*`、`Brush.*`、Background、Foreground、Border、Accent 和 `Effect.Shadow.*` 使用 `DynamicResource`；控件只消费主题提供的同名阴影资源，不直接声明阴影参数。
4. Typography、Thickness、Radius、Size、Motion 与 Style 使用 `StaticResource`。
5. 所有可交互组件必须至少覆盖：Default、Hover、Pressed/Checked、Disabled、Keyboard Focus。
6. 输入类组件额外覆盖 Validation Error；错误状态不能只依赖颜色表达。
7. 禁用状态使用独立背景、文字和边框资源，不通过降低整个控件 `Opacity` 实现。
8. 默认内容区域使用轻边框和低噪音背景；阴影仅用于 Elevated Card、Dialog、Popup 等需要层级分离的表面。
9. 保留 WPF `ListBox` / `ListView` 虚拟化；复合列表项不得嵌套新的 `ItemsControl` 复制集合。
10. 复合组件只承担视觉、绑定、输入捕获和必要的局部交互，不承担保存、搜索、热键注册、SQLite 持久化或业务编排。

### 1.2 资源命名

组件样式统一使用点分语义 Key：

```text
Style.Button.Primary
Style.Button.Secondary
Style.Button.Ghost
Style.Button.Danger
Style.Input.Default
Style.Input.Search
Style.Select.Default
Style.Switch.Default
Style.Card.Default
Style.Card.Elevated
Style.Dialog.Window
Style.Popup.Surface
Style.ListItem.Navigation
Style.ListItem.Phrase
```

页面只能通过公共 Style 或复合组件表达标准视觉，不建立页面专属的同类控件体系。

## 2. 全局尺寸与状态基准

### 2.1 控件尺寸

| 类别 | Compact | Default | 说明 |
|---|---:|---:|---|
| Button 高度 | 32 | 36 | Compact 用于工具栏和密集操作区；Default 用于表单、弹窗和主要动作 |
| Input 高度 | 32 | 36 | SearchInput 固定使用 36 |
| Select 高度 | 32 | 36 | 与相邻 Input/Button 对齐 |
| Icon Button | 32×32 | — | 图标热区不得小于 32×32 |
| Switch | 40×22 | — | Thumb 18 |
| Navigation Item 高度 | — | 40 | 侧栏与设置导航统一 |
| Phrase Row 最小高度 | — | 32 | 内容增高时允许自然扩展 |

统一圆角：Button、Input、Select 使用 `Radius.Control` 6；Card 使用 `Radius.Card` 8；Popup 使用 `Radius.Popup` 8；Dialog 使用 `Radius.Dialog` 12。

### 2.2 状态语义

| 状态 | 视觉要求 | 行为要求 |
|---|---|---|
| Default | 使用语义背景、文字和边框资源 | 正常响应鼠标与键盘 |
| Hover | 轻量背景或边框变化，避免大面积强色块 | 不改变布局尺寸 |
| Pressed / Checked | 比 Hover 更明确，但不造成跳动 | 保持命令与绑定语义 |
| Disabled | 独立 Disabled Brush；可读但不抢占注意力 | 不响应命令，不进入错误的捕获/提交流程 |
| Keyboard Focus | 明确 Focus Ring，使用主题 Focus Brush | `Tab` / `Shift+Tab` 顺序合理，可见焦点不能被 Hover 覆盖 |
| Validation Error | Error Border、错误文本或 Automation 提示 | 不仅用红色；错误与字段建立可访问关联 |

## 3. Button

### 3.1 变体与用途

| Style | 用途 | 示例 | 禁止用途 |
|---|---|---|---|
| `Style.Button.Primary` | 当前流程唯一主要动作 | 保存、确认、提交 | 同一区域同时出现多个 Primary |
| `Style.Button.Secondary` | 中性次要动作 | 修改、取消、浏览 | 代替危险操作 |
| `Style.Button.Ghost` | 工具栏、列表尾部、低强调操作 | 更多、刷新、关闭辅助面板 | 作为主要提交动作 |
| `Style.Button.Danger` | 不可逆或高风险动作 | 删除话术、清空记录 | 普通取消或关闭 |

### 3.2 尺寸与内容

- 高度只使用 Compact 32 或 Default 36。
- 水平 Padding 使用 Button 语义 Thickness，短按钮不得依赖固定宽度制造对齐。
- 文本使用 14px 语义排版 Style；图标与文本间距引用 Inline Gap Token。
- 图标按钮必须提供可读的 `AutomationProperties.Name` 或 Tooltip。
- Primary 默认填充使用 `Brush.Brand.BlueStrong`；Hover/Pressed 使用对应 Brand 状态 Brush。

### 3.3 状态

- **Hover**：背景或边框平滑切换，持续时间使用 `Motion.Duration.Fast`。
- **Pressed**：颜色加深，不改变 BorderThickness、Margin 或控件尺寸。
- **Disabled**：保持文本可辨识，不使用整控件 Opacity。
- **Keyboard Focus**：焦点环独立于 Border，不能被模板裁切。
- **Danger**：Default、Hover、Pressed、Disabled、Focus 都使用 Status.Error 语义资源，不在页面内临时覆盖红色。

### 3.4 职责边界与可访问性

- Button 只触发绑定的 `ICommand` / Click，不承担保存流程事务、确认策略或错误恢复。
- 命令是否可执行由 `CanExecute` 或绑定状态控制。
- 文本按钮使用动作动词；危险动作需由业务层决定是否弹出确认。
- Enter 只触发明确的默认动作；Escape 只由窗口或对话框层处理取消。

## 4. Input 与 SearchInput

### 4.1 Input

公共 Style：`Style.Input.Default`。

- 高度：Compact 32、Default 36。
- Padding：使用 `Thickness.Control.Input`，确保光标从稳定起始位置显示，消除左侧异常空白。
- 文本：Body Medium；Placeholder / 辅助文本使用 Muted Brush。
- 边框：Default、Hover、Focus、Error、Disabled 使用独立语义 Brush。
- Focus 时显示清晰 Border/Focus Ring，不改变布局宽高。
- Validation Error 时显示错误边框，并通过相邻错误文本或 Automation HelpText 说明原因。

职责边界：Input 负责文本输入和绑定，不自行持久化、不调用应用服务、不把输入内容写入日志。

可访问性：

- 每个 Input 必须有可见 Label，或设置 `AutomationProperties.LabeledBy` / `Name`。
- 保留标准文本选择、复制、粘贴和键盘导航。
- Placeholder 不替代 Label。

### 4.2 SearchInput

实现形式：复合 `UserControl`，内部复用 `Style.Input.Search`，不创建独立搜索数据源。

应支持：

- 搜索图标。
- 文本输入区域。
- 可选清除按钮。
- Empty、Typing、Focused、Disabled 状态。
- 输入法组合输入，不在组合阶段误触发确认或清空。

尺寸：固定高度 36；左右内边距与图标间距全部来自 Token。

职责边界：

- 可以暴露 `Text`、`Placeholder`、清除命令或清除事件。
- 不执行搜索、不访问 Core 索引、不记录搜索历史、不处理 SQLite。
- 搜索节流、结果请求、历史记录和 Enter 后业务动作由现有 ViewModel / Application Service 完成。

可访问性：清除按钮必须有名称；搜索框应声明可理解的 Automation Name；Escape 清空或关闭的规则由宿主页面明确决定。

## 5. Select

公共 Style：`Style.Select.Default`，基于 WPF `ComboBox`。

### 5.1 状态与尺寸

- 高度：Compact 32、Default 36。
- 状态：Default、Hover、DropDownOpen、Selected、Disabled、Keyboard Focus、Validation Error。
- 箭头、弹出面板、选中项、滚动条使用统一主题资源。
- Popup 使用 `Style.Popup.Surface`，不得由页面另写 Border、Shadow 和圆角。

### 5.2 职责边界与可访问性

- Select 负责选择交互和绑定，不自行保存选项。
- 保持上下方向键、Home/End、Enter、Escape 和字符导航等标准 ComboBox 行为。
- 可见标签与控件建立关联；错误状态提供文本说明。
- 不用只改变颜色表示当前选项，文本内容必须明确。

## 6. Switch

公共 Style：`Style.Switch.Default`，基于可键盘操作的 `CheckBox` / `ToggleButton` 模板。

### 6.1 尺寸与状态

- 轨道：40×22；Thumb：18。
- 状态：Unchecked、Unchecked Hover、Checked、Checked Hover、Pressed、Disabled、Keyboard Focus。
- Checked 使用 Brand 语义 Brush；Disabled 使用独立 Disabled 资源。
- 动画仅用于 Thumb 的短距离状态切换，使用 Fast/Normal Motion Token；在禁用动画环境下仍能正确显示最终状态。

### 6.2 职责边界与可访问性

- Switch 只表达即时二元状态，不承载配置保存或副作用执行。
- Space 切换状态，Tab 获得焦点；焦点环必须包围整个可点击区域。
- Switch 必须与可见标题绑定，不以“开启/关闭”作为唯一上下文。
- 若状态改变需要失败回滚，由 ViewModel 控制绑定值和错误提示。

## 7. Card

公共 Style：`Style.Card.Default`、`Style.Card.Elevated`。

| 变体 | 背景 | Border | Shadow | 用途 |
|---|---|---|---|---|
| Default | Card/Primary Surface | 1px Subtle/Default | 无 | 页面分组、设置组、普通内容区域 |
| Elevated | Elevated Surface | 轻边框 | 轻量 Shadow | 需要与底层明显分离的浮层内容 |

- 默认 Padding：16，使用 `Thickness.Card`。
- 圆角：8。
- Card 不应包裹每个 Phrase 列表项，避免后台管理系统式“卡片海洋”。
- Card 本身不是交互控件；如果整个表面可点击，应使用合适的 Button/ListBoxItem 语义和焦点状态，而不是给 Border 增加鼠标事件。
- 内容顺序应符合视觉与键盘阅读顺序。

## 8. Dialog

公共 Style：`Style.Dialog.Window`。

### 8.1 结构

```text
Title
Optional description
Content
Error / status region
Actions: Secondary → Primary or Danger
```

- 圆角：12。
- Padding：`Thickness.Dialog`。
- 使用 Elevated Surface、轻边框和受控阴影。
- 动作按钮复用公共 Button Style；不得在 Dialog 内重新定义按钮模板。
- 内容高度应允许自然布局和必要滚动，不使用固定空白填充。

### 8.2 行为与可访问性

- 打开后将焦点移动到标题后的首个合理控件或主要输入。
- Tab 焦点限制在对话框范围；关闭后恢复到触发控件。
- Escape 对应取消；Enter 仅在不会误提交多行输入或冲突状态时触发默认动作。
- 标题应提供 Automation Name；错误信息应可被屏幕阅读器感知。
- Dialog 只负责窗口级交互；保存事务、冲突检测和回滚由宿主 ViewModel / 协调器实现。

## 9. Popup

公共 Style：`Style.Popup.Surface`。

- 圆角：8；使用 Elevated Surface、轻边框和轻量 Shadow。
- 状态：Closed、Opening/Open、Focused、Disabled Item；可选轻量淡入/位移动画，不增加持续动画。
- 弹出层不得抢夺或遗失键盘焦点；Escape 关闭并恢复焦点。
- 菜单项、候选项保持清晰 Hover、Selected、Keyboard Focus 状态。
- Popup 只提供表面与呈现容器；选项加载、命令执行和业务状态由宿主控制。

## 10. SettingItem

实现形式：复合 `UserControl`。

### 10.1 公共属性

```csharp
public string Title { get; set; }
public string? Description { get; set; }
public object? ControlContent { get; set; }
public bool ShowDivider { get; set; }
```

实际实现使用 WPF DependencyProperty，并提供中文设计注释说明内容承载与职责边界。

### 10.2 固定布局

```text
┌──────────────────────────────────────────────────────────────┐
│ Title                                         ControlContent │
│ Description                                                  │
└──────────────────────────────────────────────────────────────┘
```

- 左侧标题使用 Label / Title Small 语义 Style。
- 说明使用 Body Small / Caption 与 Muted Brush。
- 右侧 `ControlContent` 支持 Switch、Select、ButtonAction、ShortcutInput 或简单状态文本。
- 行 Padding 使用 `Thickness.Settings.Row`；分隔线仅由 `ShowDivider` 控制。
- 内容拥挤时优先允许说明换行，不压缩右侧控件的可操作区域。

### 10.3 状态、职责与可访问性

- SettingItem 自身通常不是可点击控件；焦点进入右侧实际控件。
- Disabled 状态由承载控件及必要的标题/说明 Brush 共同表达。
- Validation / Save Error 显示在具体控件或该项的状态区域，不改变整个设置组结构。
- **SettingItem 不保存设置、不执行命令编排、不访问 SQLite。**
- 标题应作为右侧控件的可访问标签；说明可绑定为 HelpText。

## 11. ShortcutInput

实现形式：复合 `UserControl`。只负责捕获和展示结构化快捷键。

### 11.1 公共状态

```csharp
public ShortcutChord? Chord { get; set; }
public bool IsCapturing { get; set; }
public string? ErrorMessage { get; set; }
```

公开 Routed Event：

- CaptureCompleted：用户完成一个可表达的组合键捕获。
- CaptureCanceled：用户通过 Escape 或取消动作退出捕获。

### 11.2 展示状态

| 状态 | 展示示例 | 行为 |
|---|---|---|
| Display | `[Ctrl] + [Shift] + [Space]` | 展示当前 `ShortcutChord`，允许进入捕获 |
| Capturing | `请按下新的快捷键` | 捕获下一组受支持组合；Escape 取消 |
| Conflict / Error | `快捷键冲突，请尝试其他组合` | 保留弹窗和原配置，不自动关闭 |
| Disabled | 当前组合的弱化展示 | 不进入捕获，不吞掉宿主快捷键 |
| Keyboard Focus | 可见焦点环 | Enter/Space 可开始捕获，Escape 可取消 |

键帽使用一致的 Typography.Mono、Control Radius、Border 与间距 Token，不用多个临时 Border 拼出不同样式。

### 11.3 输入规则

- 捕获 Modifier + 支持的主键；不接受 Modifier-only。
- 支持 Core 定义的 Space、A–Z、Digit0–Digit9、F1–F12。
- 组件将 WPF KeyEvent 转换为平台无关 `ShortcutChord`；纯规则合法性由 Core 校验器负责。
- 捕获期间应拦截会触发宿主默认按钮或窗口快捷键的按键，但 Escape 始终可取消。
- 错误状态不得覆盖原 `Chord`；新的候选值通过事件交给宿主处理。

### 11.4 严格职责边界

**ShortcutInput 不得：**

- 调用 `RegisterHotKey` / `UnregisterHotKey` 或任何 Win32 API。
- 创建 HWND、消息窗口或 Windows 消息循环。
- 访问 SQLite、读取或写入 Settings JSON。
- 判断系统热键占用。
- 执行 Stage、Commit、Rollback。
- 打开 Launcher 或改变应用级 Hotkey Scope。

上述职责分别属于 Core 校验、Platform.Windows `IShortcutService` 实现、Desktop 协调器和 Settings ViewModel。

### 11.5 可访问性

- 整体提供清晰 Automation Name，如“打开闪念快捷键”。
- 状态文本通过 Automation HelpText 或 Live Region 传达。
- 键帽文本不能是唯一状态信息；冲突需有明确中文文本。
- 绝不记录实际组合键、实际按键输入或其他敏感数据；错误日志只记录结果码、阶段、TraceId 和耗时。

## 12. PhraseResultItem

实现形式：复合 `UserControl`，作为虚拟化列表的 ItemTemplate 内容，不拥有集合。

### 12.1 内容与状态

可包含：

- 话术标题或首行摘要。
- 匹配高亮或辅助元数据。
- 分类/标签的低强调展示。
- 可选尾部操作入口。

状态：Default、Hover、Selected、Keyboard Focus、Disabled/Unavailable；选中状态使用 `Brush.State.Selected` 与 SelectedBorder，不能只依赖文本颜色。

尺寸：最小高度 32，内容换行时允许自然增高；Padding 和内部 Gap 使用 Token。

### 12.2 职责边界与可访问性

- 不执行搜索、不访问 Core 索引、不保存历史。
- 不负责 Enter 插入、删除、移动、上下文菜单策略或投递安全链；这些行为保持在现有列表、命令和 ViewModel 中。
- 不内嵌新的 ItemsControl，不破坏外层 ListBox/ListView 虚拟化。
- 提供可理解的 Automation Name；Selected 与 Keyboard Focus 必须同时可辨识。
- 截断文本应提供 Tooltip 或可访问完整名称，但不得把话术正文写入日志。

## 13. CategoryTreeItem

实现形式：复合 `UserControl` 或 TreeView ItemTemplate 内容，继续由外层树控件管理层级与虚拟化能力。

### 13.1 内容与状态

可包含：展开/折叠指示、分类图标、名称、计数、尾部操作入口。

状态：Collapsed、Expanded、Hover、Selected、Keyboard Focus、Disabled；拖放或目标高亮仅在现有业务已支持时映射公共状态，不在 Design System 阶段新增行为。

- 高度与 Padding 使用 Navigation/List Token。
- 层级缩进使用语义 Thickness 或现有层级转换逻辑，不在每个页面写 Magic Number。
- 展开箭头使用统一图标资源和 Motion Token；无子项时不显示伪展开能力。

### 13.2 职责边界与可访问性

- 不加载分类、不修改树、不执行新增、重命名、移动或删除。
- 不改变现有最大层级、上下文菜单或级联删除规则。
- 保持左右方向键展开/折叠、上下方向键导航、Enter/Space 选择等标准 TreeView 行为。
- Automation 信息应包含分类名称、展开状态和必要计数。

## 14. 组件组合规则

1. `SettingItem` 可以承载 `Switch`、`Select`、Button 或 `ShortcutInput`，但不承载它们的保存流程。
2. Shortcut 捕获弹窗使用 `Dialog + ShortcutInput + Button`；冲突检测、Stage/Save/Commit/Rollback 在宿主层完成。
3. `SearchInput` 与 `PhraseResultItem` 可以在同一页面出现，但搜索请求和结果集合由 ViewModel 连接。
4. `PhraseResultItem`、`CategoryTreeItem` 必须作为现有列表/树模板使用，不创建第二套集合控件。
5. Page 只组合组件、绑定状态与命令；不得复制组件内部模板或覆盖核心视觉状态。

## 15. 组件实施完成定义

每个组件只有同时满足以下条件才可标记为已实现：

- 使用批准的 Token 和 Style Key，无页面局部视觉字面量。
- Default、Hover、Pressed/Checked、Disabled、Keyboard Focus 状态完整；输入类含 Validation Error。
- 键盘操作和 Automation Name / Label 经过检查。
- 职责边界测试或代码审查确认未包含保存、搜索、SQLite、Win32 注册等越层逻辑。
- ResourceDictionary 可解析，项目可编译，相关自动测试通过。
- 已在实际 WPF 窗口检查视觉与交互；仅有构建或静态测试不能替代 GUI 验收。

本文件当前只定义实施基准；未完成代码、自动测试、实际启动、截图审查和 Windows 热键人工验证前，不得声称组件已交付。
