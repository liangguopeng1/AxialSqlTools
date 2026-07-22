---
name: axial-query-history
description: Handles AxialSqlTools query execution history capture and Statistics IO/TIME summary windows, including Package hooks and storage modes. Use when changing Query History, statistics capture, history JSON/SQL storage, or post-execute listeners in AxialSqlToolsPackage.
---

# Query History 与 Statistics Summary

## 采集入口

`AxialSqlToolsPackage` 在查询执行相关事件中采集：

- 起止时间、耗时、行数、SQL 文本、数据源/库/登录、成功失败等
- Messages 中的 `SET STATISTICS IO/TIME` 文本 → Statistics Summary

改采集字段或时机：优先改 Package 钩子 + 队列，再改 UI。

## Query History 文件

- `QueryHistory/QueryHistoryRecord.cs`、`QueryHistoryViewModel.cs`
- `QueryHistory/QueryHistoryWindow*.cs` / `*Control.xaml(.cs)`
- 存储模式（设置）：SQL 表 / JSON Lines（`%LOCALAPPDATA%\AxialSQL\QueryHistory\`）/ Disabled

## Statistics Summary 文件

- `StatisticsSummary/StatisticsSummary*.cs`、`StatisticsSummaryParser.cs`、`StatisticsSummaryStore.cs`
- `StatisticsSummary/StatisticsSummaryWindow*.cs`

## 修改指引

1. 存储模式与连接串：`SettingsManager`；密钥/连接串加密。
2. 写 SQL Server 历史表时注意建表脚本与标识符安全。
3. 解析 STATISTICS 文本时保持对英文消息格式的兼容；SSMS 本地化若影响格式需探测。
4. 高频执行下用现有队列/版本戳模式，避免 UI 卡死与错位配对。

## 验证

- History：启用/禁用/两种存储；失败查询也有记录（若设计如此）
- Statistics：开启 IO/TIME 后执行查询，汇总窗口数字与 Messages 一致
