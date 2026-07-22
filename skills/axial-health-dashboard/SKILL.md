---
name: axial-health-dashboard
description: Maintains AxialSqlTools Server Health Dashboard polling, DMV/metric queries, and OxyPlot charts. Use when changing health metrics, MetricsService, Always On/wait stats/jobs/backup tiles, refresh loops, or HealthDashboard WPF UI.
---

# Server Health Dashboard

## 关键文件

- `HealthDashboards/HealthDashboard_Server*.cs` / `*Control.xaml(.cs)`
- `HealthDashboards/HealthDashboard_Servers*.cs`（多服务器视图）
- `Modules/MetricsService.cs` — 指标 SQL
- 图表：`OxyPlot.Wpf`

## 行为要点

- 后台轮询（现有约 3s）+ `JoinableTaskFactory` 回 UI
- 指标含 CPU、连接、阻塞、内存、PLE、文件/日志、Always On、等待、Agent Job、备份、磁盘、`sp_WhoIsActive` 探测等（以 `MetricsService` 为准）

## 修改指引

1. **新指标**：先在 `MetricsService` 增加查询与模型字段，再在 Control 绑定/图表。
2. **轮询**：窗口关闭必须取消循环，避免对已 dispose 控件更新。
3. **权限**：监控账号可能缺 VIEW SERVER STATE；单块失败不应让整个面板空白——沿用现有每块容错。
4. 重查询注意实例负载；避免比现有更激进的无节流查询。

## 验证

连接后数据刷新、断开/关窗后无后台异常、缺权限时有降级展示。
