---
name: axial-scriptdom
description: Formats and validates T-SQL in AxialSqlTools using Microsoft ScriptDOM (TSql170Parser/Sql170ScriptGenerator), including comment preservation. Use when changing SQL formatting, FormatQueryCommand, parser/generator options, or ScriptDOM AST handling.
---

# T-SQL ScriptDOM 格式化

## 关键文件

- `Commands/FormatQueryCommand.cs` — 命令；Shift 可出选项对话框
- `Modules/TsqlFormatter.cs` — 解析/生成
- `Modules/TsqlFormatterCommentInterleaver.cs` — 注释交错保留
- `Commands/Dialogs/FormatOptionsDialog.xaml(.cs)` — 选项 UI
- 设置：`SettingsManager` 中的格式化相关项

## 现状约束

- 当前固定 `TSql170Parser` / `Sql170ScriptGenerator` / `SqlVersion.Sql170`
- 代码中有 TODO：应按连接的 SQL Server 版本选择 parser/generator——改版本策略时保留降级到 170 的路径

## 修改指引

1. 格式化算法变更放在 `TsqlFormatter`（及 comment interleaver），命令类只负责选区/全文与写回编辑器。
2. 选项需持久化时走 `SettingsManager`，并在 Settings UI 暴露（若已有对应控件）。
3. 解析失败应向用户显示可理解错误，不要静默清空文档。

## 验证

- 含注释、字符串、批次（`GO` 若涉及）的样例
- 仅选中片段 vs 全文
- 选项开关切换后输出符合预期
