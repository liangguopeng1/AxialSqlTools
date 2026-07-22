---
name: axial-tool-window
description: Creates AxialSqlTools ToolWindowPane + WPF UserControl + WindowCommand registration for SSMS tool windows. Use when adding or changing a dockable tool window, WPF pane, ProvideToolWindow, or any UI that should open as a VS/SSMS tool window.
---

# 添加工具窗口

## 参考实现

任选成熟三件套，例如：

- `QuickSearch/QuickSearchWindow.cs` + `QuickSearchWindowCommand.cs` + `QuickSearchWindowControl.xaml(.cs)`
- 或 `WindowSettings/`、`QueryHistory/` 同构文件

## 步骤

1. **Window**：`ToolWindowPane` 子类，设置 `Caption`、`Content = new XxxControl()`。
2. **Control**：WPF `UserControl`；主题可复用 `Modules/ToolWindowThemeSupport.cs` 与 `Themes/`。
3. **Command**：打开窗口用 `package.JoinableTaskFactory` + `FindToolWindow`/`ShowToolWindow`（对照现有 `*WindowCommand`）。
4. **Package 特性**：在 `AxialSqlToolsPackage` 上加 `[ProvideToolWindow(typeof(XxxWindow))]`。
5. **VSCT**：按钮指向该 CommandId。
6. **InitializeAsync**：Package 中初始化对应 Command。
7. **csproj**：登记 `.cs` 与 `.xaml`（`Compile` + `Page`）。

## 注意

- 窗口内长时间 SQL 查询放到后台，结果用 `JoinableTaskFactory` 回 UI。
- 支持取消时传入 `CancellationToken`，窗口关闭时取消循环（对照 Health Dashboard）。
- 不要在工具窗口里再发明一套连接获取；用 `ScriptFactoryAccess`。

## 验证

SSMS 中从菜单打开窗口、停靠/关闭/再开，确认无二次创建异常与线程异常。
