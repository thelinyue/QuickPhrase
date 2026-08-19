# 独立设置窗口设计

## 背景

当前 QuickPhrase 的 `SettingsView` 作为 `MainWindow.Content` 的一部分打开。用户点击话术库底部的“设置”按钮后，主窗口会切换到设置表面，无法同时查看话术库，也不符合独立窗口的交互要求。

## 目标

1. 点击主界面或托盘菜单的“设置”后，打开一个同进程、非模态的 `SettingsWindow`。
2. 主窗口保持可见且可以继续操作；重复点击设置时激活已有设置窗口，不重复创建。
3. 设置窗口拥有独立标题栏、最小/最大化/关闭按钮、设置内容和保存/取消操作。
4. 复用现有 `SettingsViewModel` 和 `ICommandService` 设置保存链路，继续使用本地 SQLite 持久化。
5. 关闭前检测未保存修改；取消或关闭不写入未保存值，保存后关闭才使配置生效。
6. 设置内容在窗口缩小时通过滚动区域保持可访问，不发生横向溢出。

## 非目标

- 不引入 WebView2、React、IPC 或新进程。
- 不把 `src/` 或 `design-prototype/` 的 Web 原型带入正式 WPF 项目。
- 本轮不扩展 `AppSettings` 数据契约到账号、通知、隐私或语言字段；先完成现有设置页的独立窗口化。
- 不改变现有设置仓储的版本控制、启动项注册、快捷键和适配器保存逻辑。

## 设计

### 窗口生命周期

`ApplicationController` 持有一个可复用的 `SettingsWindow` 引用。打开设置时：

- 若窗口已打开，则 `Activate()` 并恢复最小化状态。
- 若不存在，则以主窗口为 owner 创建并 `Show()`；不调用 `ShowDialog()`。
- 窗口关闭后清空引用。

`MainWindow.NavigateTo("settings")` 不再把设置视图放入 `ContentRegion`，而是调用独立窗口打开入口。

### 视图复用

现有 `SettingsView` 继续承载设置表单和 `SettingsViewModel`。它不再负责返回主窗口，而由 `SettingsWindow` 处理关闭、取消和保存后的关闭动作。这样可保持 Core/Platform.Windows 边界和现有保存测试不变。

### 视觉结构

`SettingsWindow` 使用现有 `TitleBar` 和 `WindowChrome`，窗口内容包含：

- 独立标题栏：显示“设置”，提供最小化、最大化、关闭。
- 设置主体：现有 `SettingsView`，内部使用 `ScrollViewer`。
- 底部操作栏：由 `SettingsView` 提供“取消”和“保存”，窗口关闭时由代码处理未保存确认。

窗口默认尺寸为 860×680，最小尺寸为 560×480，使用 `SizeToContent` 之外的固定内容区域，避免打开时因表单内容变化导致尺寸跳动。

### 动画与响应

窗口在 `Loaded` 时以轻量透明度/位移动画显示，关闭时先播放退出动画再 `Close()`。动画只作用于设置窗口自身，不阻塞主窗口；设置读取继续使用已有异步 `LoadAsync()`。

## 验收

- 点击话术库设置按钮：主窗口不切换内容，出现独立设置窗口。
- 设置窗口再次打开：只激活一个已有窗口。
- 保存后关闭并再次打开：已保存值从本地仓储恢复。
- 修改后直接关闭/取消：值不保存；存在未保存修改时显示确认。
- 设置窗口缩小到最小尺寸：主体可滚动，按钮和设置项不被裁切。
- `dotnet build QuickPhrase.sln` 通过；现有 Core/Desktop 测试通过。
