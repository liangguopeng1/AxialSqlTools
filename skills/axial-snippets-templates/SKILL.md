---
name: axial-snippets-templates
description: Manages AxialSqlTools query templates toolbar menu and SQL snippets with variable substitution. Use when changing templates folder, dynamic Query Templates menu, SnippetManager, SnippetService, KeypressCommandFilter, or snippet variables.
---

# Query Templates 与 Snippets

## Templates

- 默认目录：`%USERPROFILE%\Documents\AxialSqlToolsTemplates`（可由设置覆盖）
- 动态菜单：`AxialSqlToolsPackage` + `Modules/AuroraCore.cs`（`CommandRegistry`）
- 相关命令：`Commands/RefreshTemplatesCommand.cs`、`Commands/OpenTemplatesFolderCommand.cs`、`Commands/CommandProcessor.cs`
- 仓库另有 `query-library/` 作为社区查询集合（不一定等于用户模板目录）

改模板发现/刷新逻辑时走 Package/Aurora，而不是普通 VSCT 静态按钮。

## Snippets

- `SnippetManager/SnippetService.cs`、`SnippetItem.cs`、`SnippetVariableProcessor.cs`
- UI：`SnippetManager/SnippetManagerWindow*.cs` / `*Control.xaml(.cs)`
- 插入路径：`Modules/KeypressCommandFilter.cs` + `IVsTextView` / `IVsTextManager`

## 修改指引

1. 变量替换集中在 `SnippetVariableProcessor`。
2. 快捷键/扩展语法改动要兼顾编辑器焦点与 SSMS 文本视图生命周期。
3. 模板文件编码与子文件夹结构对照现有加载逻辑。
4. 设置项（路径、开关）经 `SettingsManager`。

## 验证

刷新模板菜单出现新文件；Snippet 插入与变量替换正确；换模板目录后仍可用。
