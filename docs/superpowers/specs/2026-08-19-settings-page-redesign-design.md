# QuickPhrase 设置页 UI/UX 统一重构设计

## 目标

在不改变现有设置数据结构、保存链路、一级导航和设置功能的前提下，将五个设置模块统一为 Windows 原生桌面设置系统：固定侧栏、稳定内容栅格、页面标题与设置组分离、设置行共享视觉规则。

## 设计决定

- 侧栏使用 176px；设置内容最大宽度 640px；内容区使用 40px 页面安全边距。
- 页面标题和描述独立于设置组；设置组只包裹实际设置行，禁止使用固定高度填充空白。
- 使用共享 WPF ResourceDictionary Style/Template，不新增自定义 UserControl。
- `SettingsHeaderTitle`、`SettingsHeaderDescription`、`SettingsSectionTitle`、`SettingsGroup`、`SettingRow` 和 `SettingAction` 由 `Controls.xaml` 提供。
- `SettingsView.xaml` 只组合现有绑定和命令；五个模块、快捷键编辑、适配器切换、导入导出和即时保存保持不变。
- 窗口尺寸和独立设置窗口生命周期保持当前工作区实现；原型链路不参与本次生产 UI 判断。

## 验收

- 五个模块切换时标题、内容左边界、右侧控件列和滚动骨架稳定。
- 100% 到 200% DPI 及默认/中等/最小窗口宽度下无横向裁切、Switch 重叠和按钮挤压。
- 设置页契约测试覆盖共享 Token、共享 Style、统一宽度、无页面级大卡片和既有绑定入口。
- 通过针对性测试、Desktop 测试和解决方案构建；真实 GUI/DPI 验收单独记录。

## 自审

- 未引入新的业务设置或数据字段。
- 未修改 Core、Platform.Windows、SQLite、原型、WebView2 或 IPC 边界。
- 未重置或清理工作区中与本任务无关的未提交改动。
