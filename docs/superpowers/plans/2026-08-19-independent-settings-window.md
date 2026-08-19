# 独立设置窗口 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 QuickPhrase 设置页从 MainWindow 内嵌内容改为可复用、非模态的独立 WPF 设置窗口。

**Architecture:** `ApplicationController` 负责设置窗口的单实例生命周期，`MainWindow` 和话术库只发出打开请求；`SettingsWindow` 复用现有 `SettingsView`/`SettingsViewModel`，不改变 Core 设置契约和 SQLite 保存链路。窗口使用纯 WPF `WindowChrome`、现有主题资源和异步加载。

**Tech Stack:** .NET 10、Pure WPF、CommunityToolkit.Mvvm、现有 `ICommandService`、SQLite settings repository。

---

### Task 1: 增加设置窗口的行为测试

**Files:**
- Modify: `tests/QuickPhrase.Desktop.Tests/`
- Test: 新增针对窗口打开入口的单元测试（如果现有测试项目能引用 Desktop 类型）；否则用静态源码契约测试覆盖 `Show()`、单实例和不再切换 `ContentRegion`。

- [ ] **Step 1: 先检查测试项目是否可引用 WPF Desktop**

运行：

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore
```

预期：确认现有测试可运行及其引用边界；若不存在 WPF 测试基础设施，不创建仅为窗口实例化而引入的测试框架。

- [ ] **Step 2: 添加最小可验证契约**

优先测试不依赖真实 UI 线程的窗口入口策略：

```csharp
[Fact]
public void OpenSettings_ReusesExistingVisibleWindow()
{
    var registry = new SettingsWindowRegistry();
    var first = registry.GetOrCreate(CreateWindow);
    var second = registry.GetOrCreate(CreateWindow);

    Assert.Same(first, second);
}
```

如果当前工程没有适合的窗口抽象，则不增加生产测试专用 API，改为通过构建和源码检查验证窗口生命周期。

### Task 2: 新增独立 SettingsWindow

**Files:**
- Create: `desktop/QuickPhrase.Desktop/SettingsWindow.xaml`
- Create: `desktop/QuickPhrase.Desktop/SettingsWindow.xaml.cs`
- Modify: `desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj`（仅当 SDK 未自动包含新文件时）

- [ ] **Step 1: 创建纯 WPF 窗口骨架**

窗口使用现有主题和 `TitleBar`：

```xml
<Window x:Class="QuickPhrase.Desktop.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:QuickPhrase.Desktop"
        xmlns:shell="clr-namespace:System.Windows.Shell;assembly=PresentationFramework"
        Title="闪语 · 设置"
        Width="860" Height="680" MinWidth="560" MinHeight="480"
        WindowStartupLocation="CenterOwner"
        Background="{StaticResource AppBackgroundBrush}"
        FontFamily="{StaticResource UiFontFamily}">
    <shell:WindowChrome.WindowChrome>
        <shell:WindowChrome CaptionHeight="32" ResizeBorderThickness="6"
                            GlassFrameThickness="0" UseAeroCaptionButtons="False" />
    </shell:WindowChrome.WindowChrome>
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="32" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>
        <local:TitleBar Grid.Row="0" PageTitle="设置" />
        <ContentControl x:Name="ContentRegion" Grid.Row="1" />
    </Grid>
</Window>
```

- [ ] **Step 2: 注入并加载现有 SettingsView**

构造函数接收 `ICommandService`，创建 `SettingsView`，订阅 `CloseRequested`，并在 `Loaded` 后异步调用 `ViewModel.LoadAsync()`。窗口关闭时解除事件订阅。

- [ ] **Step 3: 实现无阻塞开关动画**

使用 `DoubleAnimation` 操作 `Opacity` 和 `TranslateTransform.Y`；动画完成后再关闭窗口。动画失败不影响关闭路径，错误以中文日志输出。

### Task 3: 将主窗口和托盘入口改为打开独立窗口

**Files:**
- Modify: `desktop/QuickPhrase.Desktop/MainWindow.xaml.cs`
- Modify: `desktop/QuickPhrase.Desktop/ApplicationController.cs`
- Modify: `desktop/QuickPhrase.Desktop/Views/LibraryView.xaml.cs`（仅在事件转发需要调整时）

- [ ] **Step 1: 让 MainWindow 暴露设置打开请求**

将 `NavigateTo("settings")` 改为调用注入的 `Action`/事件，不再执行 `SwitchToAsync(EnsureSettings(), ...)`；保留其他场景导航不变。

- [ ] **Step 2: 在 ApplicationController 中维护单个设置窗口**

新增 `_settingsWindow` 字段和 `OpenSettings()` 方法：窗口已存在时恢复并激活；不存在时创建、设置 owner、订阅 `Closed` 清空引用、调用 `Show()`。

- [ ] **Step 3: 托盘菜单直接调用独立设置入口**

将托盘菜单“设置”处理器从 `OpenManagement("settings")` 改为 `OpenSettings()`；若主窗口不存在，设置窗口仍以应用主窗口创建流程中的 owner 或无 owner 打开。

### Task 4: 保持保存/取消和关闭确认语义

**Files:**
- Modify: `desktop/QuickPhrase.Desktop/SettingsWindow.xaml.cs`
- Modify: `desktop/QuickPhrase.Desktop/Views/SettingsView.xaml.cs`（仅移除主窗口内嵌导航耦合）
- Test: `tests/QuickPhrase.Desktop.Tests/` 中现有设置保存测试

- [ ] **Step 1: 保留 `SettingsViewModel.SaveAsync()` 本地持久化**

不修改 `AppSettings` 结构；保存继续调用 `ICommandService.UpdateSettingsAsync()`，成功后由 `ApplicationController` 应用快捷键/启动项变化。

- [ ] **Step 2: 关闭前处理未保存修改**

窗口关闭请求先检查 `ViewModel.HasUnsavedChanges`：有修改时显示现有 `NavigationConfirmDialog`，选择“放弃改动”才继续关闭，选择“保存并离开”则等待 `SaveAsync()` 后关闭。

- [ ] **Step 3: 运行设置回归测试**

运行：

```powershell
dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj --no-restore
```

预期：所有既有设置和导航测试通过。

### Task 5: 构建和人工验收

**Files:**
- Verify: `QuickPhrase.sln`
- Verify: `desktop/QuickPhrase.Desktop/SettingsWindow.xaml`

- [ ] **Step 1: 编译正式解决方案**

运行：

```powershell
dotnet build QuickPhrase.sln --no-restore
```

预期：退出码 0，无 XAML 编译错误。

- [ ] **Step 2: 检查正式项目边界**

确认 `QuickPhrase.Desktop.csproj` 未新增 WebView2、React、JavaScript runtime 或网页资源引用。

- [ ] **Step 3: 人工检查窗口行为**

启动 WPF 程序并确认：点击底部设置按钮后主窗口不切换；设置窗口只创建一个；窗口可拖动/最小化/最大化/关闭；窄窗口滚动正常；保存后重新打开能恢复值。
