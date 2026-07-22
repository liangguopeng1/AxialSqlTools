---
name: axial-overview
description: Maps AxialSqlTools architecture, entry points, feature folders, and where to start changes in this SSMS 22 VSIX repo. Use whenever exploring the codebase, onboarding, locating features, or when the task is unclear which module owns the behavior—even if the user only asks "where is X" or "how does this extension work".
---

# AxialSqlTools Overview

## 先读

1. `skills/_shared/architecture.md`
2. `skills/_shared/conventions.md`
3. 仓库根 `README.md`（功能列表与 Wiki 链接）

## 启动链路

1. VSIX：`AxialSqlTools/source.extension.vsixmanifest`（目标 SSMS 22，版本号在此）
2. Package：`AxialSqlTools/AxialSqlToolsPackage.cs`（`AsyncPackage`，AutoLoad，注册全部 ToolWindow）
3. 菜单：`AxialSqlTools/AxialSqlToolsPackage.vsct`
4. 命令初始化：Package `InitializeAsync` 内显式调用各 `*Command.InitializeAsync`

## 改代码前的定位法

| 症状/需求 | 先看 |
|---|---|
| 菜单/工具栏按钮 | `.vsct` + `Commands/` 或对应 `*WindowCommand.cs` |
| 结果网格/状态栏/执行时间 | `Modules/GridAccess.cs` |
| 当前连接/选中 SQL | `Modules/ScriptFactoryAccess.cs` |
| 设置项 | `WindowSettings/` + `Modules/SettingsManager.cs` |
| 构建安装 | `skills/axial-build-release`、`.vscode/axial-extension.ps1` |

## 输出期望

回答结构问题时：给**目录/类路径**，说明属于哪一层，并指向应使用的专用 skill（命令、兼容、构建等）。
