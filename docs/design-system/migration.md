# QuickPhrase WPF Design System：迁移与 QA 基准

> 状态：**实施计划与验收基准，非已完成报告**
> 适用范围：`desktop/QuickPhrase.Desktop` 正式 Pure WPF 产品
> 本文定义 Phase 0–7 的固定目录、加载顺序、迁移边界、自动测试和手工验收。任何“通过”结论都必须来自实施后的实际证据。

## 1. 目标、非目标与成功标准

### 1.1 目标

将当前 WPF 资源升级为唯一且一致的消费链：

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

成功后应满足：

- 正式页面不再硬编码主题颜色、标准字号、标准圆角、标准间距和标准控件尺寸。
- Button、Input、Select、Switch、Card、Dialog、Popup 与设置项具有一致状态和视觉语言。
- Light/Dark 主题键完全同构，当前默认 Light，未来可替换主题字典而无需重建页面。
- 设置页复用 `SettingItem`，快捷键捕获复用 `ShortcutInput`。
- 快捷键使用 Core 结构化模型，通过 Platform.Windows 服务完成 Stage/Commit/Rollback，并保持 SQLite 与系统注册一致。
- 不改变现有业务功能、投递安全链、导航、窗口生命周期和 Pure WPF 架构边界。

### 1.2 非目标

本轮不做：

- React、Vue、WebView2、HTML/CSS 或 Web 原型迁移。
- WPF Gallery、主题切换 UI、新正式 Project。
- 业务流程重写、搜索算法重写、SQLite 表结构重建。
- 插件、AI、团队、文件/图片话术、浏览器扩展、跨平台、后台发送、自动更新。
- 为 Design System 新增 Hash、Baseline、冻结 Contract 或发布 Gate。

## 2. 冻结目录

生产目录固定为：

```text
D:\code\QuickPhrase\desktop\QuickPhrase.Desktop\
├── DesignSystem\
│   ├── Tokens\
│   │   ├── Typography.xaml
│   │   ├── Thickness.xaml
│   │   ├── Radius.xaml
│   │   ├── Sizes.xaml
│   │   ├── Motion.xaml
│   │   ├── Colors.xaml
│   │   └── Brushes.xaml
│   ├── Themes\
│   │   ├── Theme.Light.xaml
│   │   └── Theme.Dark.xaml
│   ├── Styles\
│   │   ├── Text.xaml
│   │   ├── Buttons.xaml
│   │   ├── Inputs.xaml
│   │   ├── SelectionControls.xaml
│   │   ├── Lists.xaml
│   │   ├── Surfaces.xaml
│   │   └── Dialogs.xaml
│   └── Components\
│       ├── Components.xaml
│       ├── SearchInput.xaml
│       ├── PhraseResultItem.xaml
│       ├── CategoryTreeItem.xaml
│       ├── SettingItem.xaml
│       └── ShortcutInput.xaml
├── Themes\
│   ├── QuickPhraseTheme.xaml
│   ├── Controls.xaml
│   └── Converters.xaml
└── App.xaml
```

文档目录固定为：

```text
D:\code\QuickPhrase\docs\design-system\
├── README.md
├── tokens.md
├── components.md
├── migration.md
├── quickphrase-design-system-board.svg
└── quickphrase-design-system-board.png
```

约束：

- 后续只在上述既有字典中增加 Token 或 Style。
- 页面开发不得创建新的资源目录、页面专属主题体系或重复公共组件。
- `Themes/QuickPhraseTheme.xaml` 和 `Themes/Controls.xaml` 保留为兼容生产入口，但职责收敛为字典聚合。
- `src/`、`prototype/`、Sites Worker、Hosting 与发布脚本属于独立原型/展示或发布链路，不得删除、破坏或作为正式 WPF 视觉参考。

## 3. 固定加载顺序

`App.xaml` 只保留三个生产入口，顺序不可随意调整：

1. `Themes/Converters.xaml`
2. `Themes/QuickPhraseTheme.xaml`
3. `Themes/Controls.xaml`

`QuickPhraseTheme.xaml` 内部固定合并：

1. `DesignSystem/Tokens/Typography.xaml`
2. `DesignSystem/Tokens/Thickness.xaml`
3. `DesignSystem/Tokens/Radius.xaml`
4. `DesignSystem/Tokens/Sizes.xaml`
5. `DesignSystem/Tokens/Motion.xaml`
6. `DesignSystem/Tokens/Colors.xaml`
7. `DesignSystem/Themes/Theme.Light.xaml`
8. `DesignSystem/Tokens/Brushes.xaml`

`Controls.xaml` 内部固定合并：

1. `DesignSystem/Styles/Text.xaml`
2. `DesignSystem/Styles/Buttons.xaml`
3. `DesignSystem/Styles/Inputs.xaml`
4. `DesignSystem/Styles/SelectionControls.xaml`
5. `DesignSystem/Styles/Lists.xaml`
6. `DesignSystem/Styles/Surfaces.xaml`
7. `DesignSystem/Styles/Dialogs.xaml`
8. `DesignSystem/Components/Components.xaml`

规则：

- V1 默认只加载 Light Theme，不增加主题切换 UI。
- Light/Dark Theme 必须暴露完全相同的 `Color.*`、`Brush.*`、`Effect.Shadow.*` 键；阴影参数与颜色可按主题映射，但资源键保持同构。
- 未来主题服务仅替换 Light/Dark 主题字典，不修改 Token、Style、Component 或 Page 字典。
- 旧 Resource Key 一次性删除，所有生产引用同步迁移，不保留兼容 Alias。

## 4. 资源引用与代码边界

### 4.1 引用规则

| 资源 | 引用方式 | 说明 |
|---|---|---|
| Color、Brush、Background、Foreground、Border、Accent、`Color.Shadow.*` | `DynamicResource` | 支持未来运行时替换主题 |
| `Effect.Shadow.Elevated`、`Effect.Shadow.Dialog`、`Effect.Shadow.Popup` | `DynamicResource` | 统一定义在 Light/Dark Theme；只允许 Elevated、Dialog、Popup 使用 |
| Typography、FontSize、FontWeight、Thickness、Radius、Size、Motion | `StaticResource` | 固定设计规格，减少运行时查找 |
| Style | `StaticResource` | 公共 Style/Template 是页面唯一标准入口 |

允许直接保留：`Auto`、`*`、`0`、Grid 比例，以及有明确结构原因的一次性尺寸。任何例外都必须能说明为何现有 Token 不适用，不能以“迁移方便”为理由扩散 Magic Number。

### 4.2 架构边界

- Core 不引用 WPF、Win32、Platform.Windows、SQLite 或 UI Automation。
- Desktop View、ViewModel、Command 不依赖 `WindowsShortcutService` 具体类型，只依赖 Core 接口或 Desktop 自身抽象。
- Platform.Windows 实现 Win32 映射、消息窗口、`RegisterHotKey` / `UnregisterHotKey`。
- `ShortcutInput` 只捕获和展示，不调用 Win32、不访问 SQLite、不注册热键。
- `SettingItem` 不保存配置；`SearchInput` 不执行搜索；`PhraseResultItem` 和 `CategoryTreeItem` 不拥有集合或业务命令。
- 关键类与复杂 ControlTemplate 补充中文设计注释；用户可见错误和日志使用清晰中文。
- 日志不得记录话术正文、剪贴板、输入框文字、聊天内容、联系人、客户资料、实际组合键或实际按键输入。

### 4.3 结构化快捷键冻结契约

`ShortcutModifiers`、`ShortcutKey` 的数值会持久化到 Settings JSON，是跨版本稳定契约。所有成员必须显式赋值，禁止依赖 C# 隐式枚举顺序；已有数值不得重排、复用或改义。未来新增主键只能分配新的显式值。

```csharp
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

public enum ShortcutKey
{
    Space = 1,

    A = 2,
    B = 3,
    C = 4,
    D = 5,
    E = 6,
    F = 7,
    G = 8,
    H = 9,
    I = 10,
    J = 11,
    K = 12,
    L = 13,
    M = 14,
    N = 15,
    O = 16,
    P = 17,
    Q = 18,
    R = 19,
    S = 20,
    T = 21,
    U = 22,
    V = 23,
    W = 24,
    X = 25,
    Y = 26,
    Z = 27,

    Digit0 = 28,
    Digit1 = 29,
    Digit2 = 30,
    Digit3 = 31,
    Digit4 = 32,
    Digit5 = 33,
    Digit6 = 34,
    Digit7 = 35,
    Digit8 = 36,
    Digit9 = 37,

    F1 = 38,
    F2 = 39,
    F3 = 40,
    F4 = 41,
    F5 = 42,
    F6 = 43,
    F7 = 44,
    F8 = 45,
    F9 = 46,
    F10 = 47,
    F11 = 48,
    F12 = 49,
}

public readonly record struct ShortcutChord(
    ShortcutModifiers Modifiers,
    ShortcutKey Key);

public interface IShortcutService : IAsyncDisposable
{
    event EventHandler? Activated;

    ShortcutChord ActiveChord { get; }

    Task<ShortcutStageResult> StageAsync(
        ShortcutChord chord,
        CancellationToken cancellationToken = default);

    Task<ShortcutApplyResult> CommitAsync(
        ShortcutStageToken token,
        CancellationToken cancellationToken = default);

    Task RollbackAsync(
        ShortcutStageToken token,
        CancellationToken cancellationToken = default);

    void SetEnabled(bool enabled);
}
```

Stage Token 关系固定为：

1. `StageAsync(chord)` 成功时，其 `ShortcutStageResult` 携带一个有效且不透明的 `ShortcutStageToken`；Stage 失败时不得产生可提交 Token。
2. Token 唯一关联该次暂存注册及创建它的 `IShortcutService` 实例，Desktop 不解析、不构造、不持久化 Token。
3. `CommitAsync(token)` 与 `RollbackAsync(token)` 只能接收对应成功 Stage 返回且尚未结算的 Token，禁止根据 `ShortcutChord` 重建 Token。
4. Commit 或 Rollback 完成后，该 Token 失效，不得再次用于提交或回滚。
5. JSON 中 `keyCode = 1` 固定表示 `ShortcutKey.Space`；`modifiers = 2` 固定表示 `ShortcutModifiers.Alt`。
## 5. Phase 0–7 实施与验收

## Phase 0：视觉基准与文档

### 输出

在 `docs/design-system` 创建：

- `README.md`
- `tokens.md`
- `components.md`
- `migration.md`
- `quickphrase-design-system-board.svg`
- `quickphrase-design-system-board.png`

视觉板必须包含：

- Light 主色板与 Dark 对照色条。
- Typography 全层级。
- Spacing、Radius、Shadow。
- Button 全状态。
- Input / SearchInput 全状态。
- SettingItem 的 Switch、Select、ButtonAction、Shortcut 示例。
- ShortcutInput 的 Display、Capturing、Conflict、Disabled。
- Card Default / Elevated。
- MainWindow、SettingsWindow、Launcher 布局标注。

### 验收

- 文档与已批准 Token、尺寸、职责边界一致。
- SVG 与 PNG 内容一致、文字清晰、无 Web 管理后台视觉。
- 明确视觉板仅用于审查和 QA，XAML ResourceDictionary 才是运行真源。
- **本阶段不等于代码已实现，也不应把视觉稿状态写成产品现状。**

## Phase 1：Token 与主题资源

### 实施

1. 先扩展 `DesignTokenTests`，覆盖新 Token、Light/Dark 键集合和 Hex 边界。
2. 创建 Typography、Thickness、Radius、Sizes、Motion 字典。
3. 创建同键的 Light/Dark Theme 字典。
4. 将 `Themes/QuickPhraseTheme.xaml` 收敛为聚合入口。
5. 一次性删除旧 Resource Key，并同步更新全部生产消费者。
6. 将主题 Brush 和 `Effect.Shadow.*` 消费点改为 `DynamicResource`；Light/Dark Theme 暴露同名阴影资源。
7. 固定话术业务色板迁移为 `Color.Phrase.*` / `Brush.Phrase.*`，保持业务颜色值不变。

### 验收

- 新 Token 缺失时测试失败。
- Light/Dark 主题键不一致时测试失败。
- Theme 字典外出现 Hex 时测试失败；原型链路不纳入正式 WPF 扫描。
- 生产 XAML 不存在旧 Resource Key。
- ResourceDictionary 可独立解析并按固定顺序合并。

### 阶段命令

```powershell
dotnet test D:\code\QuickPhrase\tests\QuickPhrase.Desktop.Tests\QuickPhrase.Desktop.Tests.csproj --no-restore --filter 'FullyQualifiedName~DesignTokenTests'
```

## Phase 2：Styles 与基础控件

### 实施

1. 将 `Themes/Controls.xaml` 收敛为 Style/Component 聚合入口。
2. 按 Text、Buttons、Inputs、SelectionControls、Lists、Surfaces、Dialogs 拆分。
3. 建立 Primary、Secondary、Ghost、Danger Button 与 Compact/Default 尺寸。
4. 统一 Input、Search Input 基础模板、Select、Switch、Card、Dialog、Popup。
5. 补齐 Default、Hover、Pressed/Checked、Disabled、Keyboard Focus；输入类补齐 Validation Error。
6. Focus Ring 使用主题 Brush；禁用状态不降低整控件 Opacity。
7. `Surfaces.xaml` / `Dialogs.xaml` 只消费 Theme 中的 `Effect.Shadow.Elevated`、`Effect.Shadow.Dialog`、`Effect.Shadow.Popup`；默认 Card 使用 Border，普通面板和 Launcher 不使用 Shadow。

### 验收

- 所有字典可解析。
- Button/Input/Switch 状态触发器完整。
- Focus 与 Hover/Pressed 不互相覆盖。
- Validation.HasError 可稳定触发错误边框和可读信息。
- 页面不再出现同类控件的局部模板复制。

## Phase 3：复合组件

### 固定顺序

1. `SettingItem`
2. `SearchInput`
3. `PhraseResultItem`
4. `CategoryTreeItem`
5. `ShortcutInput`

### 验收

- `SettingItem` 提供 Title、Description、ControlContent、ShowDivider DependencyProperty。
- `SearchInput` 只暴露输入与清除交互，不执行搜索。
- `PhraseResultItem`、`CategoryTreeItem` 作为现有外层列表/树的模板内容，不嵌套新 ItemsControl。
- `ShortcutInput` 提供 Chord、IsCapturing、ErrorMessage 与完成/取消 Routed Event。
- 所有复合组件只负责视觉、绑定和必要输入行为：
  - 不保存设置。
  - 不访问 SQLite。
  - 不执行搜索。
  - 不注册或注销系统热键。
  - 不改变投递、分类或窗口业务规则。

## Phase 4：快捷键 Core 与 Windows 实现

### 实施

1. 先为 `ShortcutChord` 校验、Windows 映射、Stage/Commit/Rollback 写失败测试。
2. 在 Core 建立 `ShortcutModifiers`、`ShortcutKey`、`ShortcutChord`、结果类型与 `IShortcutService`。
3. Core 校验至少一个 Modifier、拒绝 Modifier-only、限制主键枚举范围。
4. Platform.Windows 建立 `WindowsShortcutService`：
   - 独立 Native message-only window 与消息循环。
   - Core Key 到 Win32 Virtual Key 的单向映射。
   - `RegisterHotKey` / `UnregisterHotKey`。
   - 使用备用 Hotkey ID 暂存新 Chord。
5. Desktop 协调器改为依赖 `IShortcutService`，保留 Launcher Scope、Launcher Visible、Practice、Pause 和激活后的 UI 编排。
6. 日志和用户可见错误使用清晰中文。日志绝不记录实际组合键、实际按键输入或其他敏感数据，只记录结果码、阶段、TraceId 和耗时；用户可见冲突统一为可理解中文。

### 固定事务顺序

```text
Stage：暂存注册新热键
    ↓
Save：保存 SQLite settings.value_json
    ↓
Commit：提交新注册并注销旧热键
```

失败规则：

- Stage 冲突：返回 `HOTKEY_CONFLICT`，保留旧热键，不写配置。
- SQLite 保存失败：Rollback 暂存注册，保留旧配置和旧热键。
- Commit 成功：旧热键失效，新热键成为 `ActiveChord`。
- 不允许先注销旧热键再尝试注册新热键。

### 验收

- 全部支持键具有确定的 Win32 映射。
- Stage 冲突不影响旧注册。
- Rollback 释放暂存 ID。
- Commit 后旧注册释放且新注册唯一生效。
- Core 中不存在 HWND、Virtual Key、WPF 或 Win32 类型。
- Desktop View/ViewModel 不引用 `WindowsShortcutService` 具体类型。

## Phase 5：设置迁移与 ShortcutInput 接入

### 实施

1. `settings` 表结构保持不变，继续使用 `value_json`。
2. 读取 schemaVersion 1 的 `launcherShortcutDisplay` / `launcherShortcutNormalized` 作为一次性迁移输入。
3. 成功迁移后写入 schemaVersion 2：

```json
{
  "schemaVersion": 2,
  "shortcuts": {
    "flashLauncher": {
      "modifiers": 2,
      "keyCode": 1
    }
  }
}
```

4. `keyCode` 是稳定的 Core `ShortcutKey` 数值，不是 Win32 Virtual Key。
5. SettingsViewModel 改为绑定 `ShortcutChord`，停止写入 Display/Normalized 字符串。
6. 设置页提供 Alt+Space、Ctrl+Space、自定义三个选择；预设由 Chord 推导而非单独持久化：
   - Alt+Space：默认、推荐。
   - Ctrl+Space：备用。
   - 其他合法 Chord：自定义。
7. 自定义捕获弹窗内部使用 `ShortcutInput`。
8. 只有 Stage、SQLite Save、Commit 全部成功后，才更新设置页展示。

### 验收

- schemaVersion 1 可迁移至 2，成功后不再写旧字段。
- 迁移失败时回退 Alt+Space，并输出可读中文错误。
- 冲突时弹窗保持打开，显示错误，原快捷键继续有效。
- 取消捕获不更改配置和系统注册。
- 保存失败释放暂存热键并保留旧展示。
- 重启后配置展示与实际注册一致。

## Phase 6：页面迁移

### 固定顺序

1. `SettingsView` / `SettingsWindow`
2. `MainWindow` / `LibraryView` / `EditorView`
3. `LauncherWindow`
4. `OnboardingWindow`
5. Dialog、SearchHistory、StatePresenter
6. TitleBar 与剩余共享资源

### 迁移规则

- 先替换公共 Style/组件，再清理页面视觉字面量。
- 不改变 Binding、Command、导航、投递安全链、搜索历史规则或窗口生命周期。
- 不把每个列表项改为独立 Card。
- 不在视觉迁移时顺带重构业务代码、修改文案逻辑或清理无关死代码。
- `PhraseListResources.xaml` 内容迁移到 Lists/Components 后再删除，确保不存在第二套样式源。
- SettingsWindow 保持 860×680、最小 560×480；Settings Sidebar 176、Content Maximum 640。
- MainWindow 保持 1200×760、最小 900×560。
- Launcher Minimum Width 680、Maximum Height 520。
- 页面直接尺寸只保留 Auto、`*`、0、Grid 比例和有明确结构原因的一次性值。

### 每页验收

- 页面无旧 Token Key、主题外 Hex、直接 FontSize 和标准 CornerRadius。
- 标准 Margin/Padding、控件高度和窗口布局引用 Token。
- Tab 顺序、焦点恢复、Enter、Space、Escape 行为与迁移前一致。
- 列表虚拟化、选择、上下文菜单和命令绑定未被复合组件破坏。
- 实际窗口视觉与设计板一致，但不以截图替代交互验证。

## Phase 7：全局审计与文档收口

### 实施

1. 扫描生产 XAML：旧 Key、主题外 Hex、直接字号、标准圆角、错误的资源引用方式。
2. 审查所有交互控件五种基础状态；输入类审查 Validation Error。
3. 更新 Token、组件、迁移和 QA 文档，记录真实实现与尚未验证项。
4. 历史 Superpowers 规格不重写；在新文档中声明本方案取代旧 Alias 策略。
5. 修复 `XamlParseValidationTests` 的共享 WPF `Application` 生命周期/测试隔离，使 Desktop 全套可在一次运行中执行。

### 验收

- `dotnet build` 和 `dotnet test` 使用同一工作树执行并记录结果。
- 已知单独通过、全套失败的 WPF Application 问题被真正修复，而不是通过跳过或改变测试顺序掩盖。
- 自动测试、实际启动、截图审查、Windows 热键人工验证分别报告。
- 任何未完成的 DPI、Windows 11、企业微信或系统热键检查明确标为“未验证”，不得由编译成功推断。

## 6. 自动测试矩阵

| 类别 | 必须覆盖 | 失败时阻止的具体问题 |
|---|---|---|
| Theme 同构 | Light/Dark Key 集合完全一致 | 切换主题后资源缺失或回退错误 |
| Hex 边界 | Theme 字典外无 Hex | 页面和模板绕过语义颜色 |
| 旧 Key | 生产 XAML 无旧 Token Key | 形成两套资源语言 |
| 引用方式 | Brush/`Effect.Shadow.*` 为 Dynamic；Typography/Thickness/Radius/Size/Motion 为 Static | 未来换肤失效或资源使用漂移 |
| Typography | 页面无直接 FontSize/Weight 组合 | 页面自行定义文字层级 |
| Spacing/Radius/Size | 标准值使用 Token | 组件高度、圆角和间距再次分叉 |
| Style 状态 | Button/Input/Switch 触发器完整 | Hover、Pressed、Disabled、Focus、Error 缺失 |
| SettingItem | DependencyProperty 与内容承载 | 设置页重新复制 Grid 布局 |
| ShortcutInput | 捕获、取消、错误、禁用 | 捕获状态吞键、错误不显示或取消仍改值 |
| Core Shortcut | 合法/非法组合、值比较 | Modifier-only 或不支持键进入系统层 |
| Windows Mapping | 所有支持键映射 | 部分键保存成功但无法注册 |
| Stage | 冲突保留旧热键 | 修改失败导致应用失去全局入口 |
| Rollback | Save 失败释放暂存热键 | 配置与系统注册不一致 |
| Commit | 新热键生效、旧热键失效 | 重复激活或注册泄漏 |
| JSON Migration | v1→v2、错误回退 | 升级丢失快捷键或继续写旧字段 |
| Preset | Alt+Space、Ctrl+Space、自定义推导 | UI 预设与实际 Chord 不一致 |
| Architecture | Core 引用边界、Desktop 依赖接口 | WPF/Win32 类型泄漏进 Core 或 ViewModel |
| XAML Isolation | 全套单进程运行 | 多个 `Application` 导致顺序相关失败 |

### 最终自动验证命令

```powershell
dotnet build D:\code\QuickPhrase\QuickPhrase.sln --no-restore
dotnet test D:\code\QuickPhrase\QuickPhrase.sln --no-restore
```

报告必须包含：命令、退出码、通过/失败数量、失败测试名和环境限制。不得把 focused tests 通过描述为全套通过。

## 7. 手工 WPF 与 DPI 验收矩阵

### 7.1 DPI 与窗口

| 对象 | 100% | 125% | 150% | 检查点 |
|---|---|---|---|---|
| MainWindow 默认 1200×760 | 待验 | 待验 | 待验 | 字体清晰、布局无裁切、焦点环完整 |
| MainWindow 最小 900×560 | 待验 | 待验 | 待验 | 内容可滚动/收缩，无重叠和不可达控件 |
| SettingsWindow 默认 860×680 | 待验 | 待验 | 待验 | 176 侧栏、640 内容宽、自然高度分组 |
| SettingsWindow 最小 560×480 | 待验 | 待验 | 待验 | 说明换行、右侧控件可操作、按钮不被裁切 |
| Launcher MinWidth 680 / MaxHeight 520 | 待验 | 待验 | 待验 | 列表、空状态、选中和错误状态不跳动 |
| Dialog / Popup | 待验 | 待验 | 待验 | 阴影、边框、圆角、焦点和屏幕边缘定位 |
| Onboarding | 待验 | 待验 | 待验 | Back 保留输入状态，操作与文案未回归 |

“待验”必须在实际运行后替换为日期、环境和结果；不得预填“通过”。

### 7.2 控件与键盘

| 场景 | 鼠标 | Tab/Shift+Tab | Enter | Space | Escape | Automation/说明 |
|---|---|---|---|---|---|---|
| Primary/Secondary/Ghost/Danger Button | Hover/Pressed | 焦点可见 | 执行动作 | 执行动作 | 由宿主决定 | 名称与动作明确 |
| Input | 光标/选择 | 顺序合理 | 宿主定义 | 输入空格 | 宿主定义 | Label、Error 可读 |
| SearchInput | 清除按钮可用 | 输入与清除顺序 | 宿主搜索动作 | 输入空格 | 清空/关闭规则明确 | 搜索与清除名称明确 |
| Select | 打开/选择 | 焦点可见 | 选择/关闭 | 打开或选择 | 关闭 Popup | 当前值可读 |
| Switch | 点击切换 | 焦点可见 | 不产生意外提交 | 切换 | 不改变值 | 标题关联、状态可读 |
| SettingItem | 不伪装整行点击 | 进入右侧控件 | 由控件决定 | 由控件决定 | 由宿主决定 | Title 作为 Label |
| ShortcutInput | 进入捕获 | 焦点可见 | 开始/确认规则明确 | 可作为组合主键 | 取消捕获 | 状态和冲突文本可读 |
| Dialog | 按钮与关闭 | 焦点限制在弹窗 | 仅明确默认动作 | 控件标准行为 | 取消并恢复焦点 | 标题、错误可感知 |
| Phrase/Category 列表 | Hover/Selected | 项间导航 | 保持原命令 | 保持原选择规则 | 保持原关闭规则 | Selected/Expanded 可读 |

## 8. Windows 快捷键人工矩阵

测试环境必须记录 Windows 11 版本、QuickPhrase 构建、应用是否管理员运行、键盘布局和测试日期。

| 场景 | 操作 | 预期 | 状态 |
|---|---|---|---|
| 默认预设 | 选择 Alt+Space 并保存 | 新注册生效，旧注册释放，页面显示默认/推荐 | 待验 |
| 备用预设 | 选择 Ctrl+Space 并保存 | 新注册生效，页面显示备用 | 待验 |
| 自定义成功 | 捕获受支持组合并保存 | Stage→Save→Commit 完成，重启后仍一致 | 待验 |
| Modifier-only | 只按 Ctrl/Alt/Shift/Win | 不完成捕获，显示或保持等待状态 | 待验 |
| 系统冲突 | 捕获已占用组合 | 弹窗保持打开，显示中文冲突，旧热键继续有效 | 待验 |
| 取消捕获 | 捕获中按 Escape/取消 | 弹窗退出或恢复 Display，配置和注册不变 | 待验 |
| SQLite 保存失败 | 模拟/注入保存失败 | Rollback 新注册，旧配置、旧热键和页面展示保持 | 待验 |
| Commit | 成功保存后触发旧/新组合 | 旧组合无效，新组合只激活一次 | 待验 |
| Pause | 暂停热键后触发 | 不打开 Launcher；恢复后正常 | 待验 |
| Launcher Visible Scope | Launcher 已显示时触发 | 保持既有 Scope 行为，不重复创建窗口 | 待验 |
| Practice 模式 | 练习模式下触发 | 保持既有练习模式行为 | 待验 |
| 应用重启 | 保存后完全退出并启动 | JSON、页面展示、ActiveChord、系统注册一致 | 待验 |
| 应用退出 | 退出后触发组合 | 注册已释放，不残留幽灵热键 | 待验 |

人工测试不得使用构建通过替代；系统冲突、全局消息循环和应用退出释放必须在真实 Windows 会话验证。

## 9. 页面视觉一致性检查

逐页检查：

- SettingsView / SettingsWindow。
- MainWindow / LibraryView / EditorView。
- LauncherWindow。
- OnboardingWindow。
- 分类、移动、导航确认和快捷键捕获 Dialog。
- SearchHistory、StatePresenter、TitleBar。

每页记录：

1. 页面级间距与窗口尺寸是否引用 Token。
2. 标题、正文、说明是否使用统一 Typography Style。
3. Button/Input/Select/Switch 是否只使用公共 Style。
4. Default Card 是否保持无阴影；是否仅 Elevated Card、Dialog、Popup 使用统一且受控的 Shadow。
5. Default、Hover、Pressed/Checked、Disabled、Focus、Error 是否完整。
6. 键盘、绑定、命令、虚拟化和窗口生命周期是否保持。
7. 与视觉板的差异是缺陷、合理结构例外还是视觉板需修订。

## 10. 混合工作树与分阶段暂存边界

当前仓库是混合且存在大量未提交修改的工作树。实施期间必须遵守：

1. 禁止 `git reset`、`git checkout`、`git restore`、`git clean` 或任何覆盖现有修改的命令。
2. 禁止 `git add -A`、`git add .` 或整仓暂存。
3. 不修改、不删除 Web 原型、Sites Worker、Hosting 配置和发布脚本。
4. 每个 Phase 开始前先执行只读 `git status --short`，记录目标文件已有状态。
5. 编辑前读取目标文件当前内容；不得用计划版本覆盖他人未提交改动。
6. 每个 Phase 完成后仅检查该阶段文件：

```powershell
git diff -- <phase-specific-paths>
git diff --check -- <phase-specific-paths>
```

7. 需要暂存时优先使用 `git add -p -- <明确路径>`，或逐个明确文件暂存；暂存前后检查：

```powershell
git diff --cached --name-only
git diff --cached --stat
git diff --cached --check
```

8. Design System 与快捷键系统使用独立阶段提交；不得把工作树中的无关改动带入。
9. 如果同一目标文件已有未提交改动，且本阶段修改无法安全分块或确认归属：
   - 停止暂存与提交。
   - 保留工作树现状。
   - 报告冲突文件和原因。
   - 不用大范围覆盖、回滚或格式化绕过边界。
10. 未经明确要求不提交；即使测试通过，也不能自动提交或推送。

### 建议阶段边界

| 阶段提交 | 允许内容 | 不应混入 |
|---|---|---|
| Phase 0 Docs | `docs/design-system` 文档与视觉板 | XAML、C#、测试、原型 |
| Phase 1 Tokens | Token/Theme 字典与对应 Token 测试、必要消费者重命名 | 组件业务、快捷键实现 |
| Phase 2 Styles | Style/Template 字典与状态测试 | 页面业务迁移、Shortcut Service |
| Phase 3 Components | UserControl 与组件测试 | SQLite、Win32 注册 |
| Phase 4 Shortcut Core/Windows | Core 模型、接口、Platform.Windows 服务、协调器测试 | 设置页视觉迁移 |
| Phase 5 Settings Shortcut | JSON 迁移、SettingsViewModel、捕获弹窗接入 | 主页面无关重构 |
| Phase 6 Pages | 按固定顺序迁移的页面与对应测试 | 原型、发布脚本、业务重写 |
| Phase 7 Audit | 扫描测试、Application 隔离修复、文档收口 | 新功能或无关清理 |

## 11. 报告格式

每个阶段完成后分别报告：

### 11.1 修改范围

- 实际修改文件清单。
- 未修改但发现问题的文件清单。
- 是否存在与原工作树改动重叠。

### 11.2 自动验证

- 执行命令。
- 退出码。
- 通过/失败数量。
- 失败测试与原因。
- 是否仅 focused test；不得模糊成全套结果。

### 11.3 WPF 实际启动

- 是否实际启动 EXE。
- 测试窗口、尺寸、DPI。
- 发现的布局、焦点、交互问题。
- 未启动时明确写“未验证”。

### 11.4 截图审查

- 截图环境与分辨率。
- 与视觉板差异。
- 未截图时明确写“未验证”。

### 11.5 Windows 热键人工验证

- Windows 版本与键盘布局。
- Alt+Space、Ctrl+Space、自定义、冲突、取消、保存失败、重启、退出释放结果。
- 未进行真实系统验证时明确写“未验证”。

## 12. 最终完成定义

只有以下条件全部满足，才能声明本计划完成：

- Phase 0–7 的产物和代码均已实际存在，而非仅有文档。
- Token、Theme、Style、Component、Page 消费链符合固定目录和加载顺序。
- 所有要求的自动测试在一次完整运行中通过，或剩余失败被明确报告且未伪称完成。
- Desktop 全套测试不再因多个 WPF `Application` 实例产生顺序相关失败。
- WPF 实际启动、DPI/窗口矩阵、截图审查分别完成并有记录。
- Windows 热键 Stage/Save/Commit/Rollback、冲突、取消、重启与退出释放在真实 Windows 11 会话验证。
- 页面业务、搜索、分类、导航、投递安全链、窗口生命周期和 Pure WPF 边界未回归。
- 工作树与阶段提交没有带入无关修改。

在满足上述条件前，本文件仅作为迁移和 QA 实施基准，任何未执行项都必须标注为“待实施”或“未验证”。
