---
name: axial-add-command
description: Adds SSMS menu/toolbar commands to AxialSqlTools via VSCT, MenuCommand, and Package InitializeAsync. Use when creating a new command, button, keyboard shortcut, Tools menu item, or toolbar action—even if the user says "add a button" or "wire up a menu handler" without naming VSCT.
---

# 添加命令（VSCT + MenuCommand）

## 参考实现

对照 `Commands/FormatQueryCommand.cs`（或同目录其他 `*Command.cs`）。

## 步骤

1. **分配 ID**：在 `AxialSqlToolsPackage.vsct` 增加 `IDSymbol`（未占用整数），在合适 `MenuGroup` 下增加 `Button`（父菜单、图标、可选 `KeyBinding`）。
2. **命令类**：新建 `Commands/YourCommand.cs`：
   - `CommandSet` = `45457e02-6dec-4a4d-ab22-c9ee126d23c5`
   - `CommandId` 与 VSCT 一致
   - `InitializeAsync`：切主线程 → 取 `OleMenuCommandService` → 注册 `MenuCommand`/`OleMenuCommand`
   - `Execute`：业务逻辑
3. **Package 挂钩**：在 `AxialSqlToolsPackage.InitializeAsync` 调用 `YourCommand.InitializeAsync(this)`。
4. **csproj**：`<Compile Include="Commands\YourCommand.cs" />`（见 `_shared/conventions.md`）。
5. **依赖**：需要连接/文本用 `ScriptFactoryAccess`；需要网格用 `GridAccess`。

## 注意

- 动态模板菜单走 `Aurora`（`Modules/AuroraCore.cs`），不是这条 VSCT 路径；见 `axial-snippets-templates`。
- UI 操作必须在主线程。
- 改 VSCT 后需完整重建扩展再在 SSMS 验证。

## 验证

关闭 SSMS → 构建/重装（`axial-build-release`）→ 确认菜单可见且 Execute 触发。
