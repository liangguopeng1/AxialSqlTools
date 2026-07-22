---
name: axial-ssms-compat
description: Diagnoses and fixes AxialSqlTools SSMS private-API reflection adapters (GridAccess, ScriptFactoryAccess, query execution hooks) when grids, status bar, history, or statistics break after SSMS updates. Use for SSMS compatibility, reflection field mismatches, result grid access failures, tab coloring, or "works in older SSMS" issues.
---

# SSMS 内部 API 兼容

## 为何脆弱

扩展通过反射读取 SSMS 私有字段（结果网格、执行对象、状态栏等）。SSMS 小版本也可能改字段名/集合类型。

## 关键文件

- `Modules/GridAccess.cs` — 网格、`DataTable`、对齐、状态栏、统计消息
- `Modules/ScriptFactoryAccess.cs` — 编辑器/OE 连接
- `Modules/ResultGridControlAdaptor.cs`
- `AxialSqlToolsPackage.cs` — `Query.Execute` 等事件、历史/统计采集
- `Modules/KeypressCommandFilter.cs` — 编辑器按键

## 排障步骤

1. 复现并记下 SSMS 精确版本。
2. 查日志：`%LOCALAPPDATA%\AxialSQL\AxialSQLToolsLog\`。
3. 定位失败 API：网格？连接？执行后钩子？
4. 在对应适配类中核对反射字段；注意代码里已有 **SSMS 21 `CollectionBase` vs SSMS 22 `List<T>`** 分支模式——新差异照此扩展，而不是删旧分支。
5. 失败路径保持降级（功能不可用但 Package 可加载），并打日志。

## 修改原则

- 集中改适配层，避免在各功能里复制反射。
- 新增字段探测时先能力检测再访问。
- 不要假设公开文档存在；以运行时类型为准。

## 验证

在目标 SSMS 版本上跑：执行查询、看状态栏耗时、导出网格、Query History/Statistics（若相关）。
