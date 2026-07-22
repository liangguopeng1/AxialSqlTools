---
name: axial-quick-search
description: Works on AxialSqlTools Quick Search tool window for finding SQL object definitions across one or all databases, including AvalonEdit highlighting and result markers. Use when changing QuickSearch UI, search SQL, object types, wildcards, or text marking in search results.
---

# Quick Search

## 关键文件

- `QuickSearch/QuickSearchWindow.cs`
- `QuickSearch/QuickSearchWindowCommand.cs`
- `QuickSearch/QuickSearchWindowControl.xaml(.cs)`
- `QuickSearch/TextMarkerService.cs`
- `QuickSearch/sql.xshd` — AvalonEdit 语法高亮

## 行为要点

- 连接来自 Object Explorer / `ScriptFactoryAccess`
- 搜索范围：存储过程、视图、函数、表、SQL Agent Job Step 等（以控件逻辑为准）
- 支持单库或实例上可访问的全部数据库；全词/通配
- 结果展示 + 编辑器标记

## 修改指引

1. 改搜索 SQL/对象类型：集中在 Control 的查询构造处，注意标识符转义与权限不足时的跳过策略。
2. 全库扫描可能很慢：保持可取消、进度或状态提示。
3. 高亮/标记问题优先查 `TextMarkerService` 与 AvalonEdit 生命周期（窗口关闭释放）。
4. 部分能力可能仍标 WIP——改前读控件内注释/TODO。

## 验证

单库命中、全库命中、无权限库不崩溃、标记可清除、再搜索不泄漏旧标记。
