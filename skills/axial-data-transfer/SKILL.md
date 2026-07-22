---
name: axial-data-transfer
description: Implements AxialSqlTools BULK data transfer between SQL Server, PostgreSQL, and MySQL, including saved connections and type mapping. Use when changing DataTransfer window, SqlBulkCopy pipelines, Npgsql/MySqlConnector paths, auto-create table, or saved connection store.
---

# BULK Data Transfer

## 关键文件

- `DataTransfer/DataTransferWindow*.cs` / `*Control.xaml(.cs)`
- `DataTransfer/SavedConnectionStore.cs`
- `DataTransfer/SavedConnectionManagerWindow*.cs`、`SavedConnectionPickerWindow*.cs`
- 包：`Microsoft.Data.SqlClient`、`Npgsql`、`MySqlConnector`

## 支持方向（以控件为准）

SQL Server ↔ SQL Server / PostgreSQL / MySQL（含自动建表、批量进度、取消）。

## 修改指引

1. 传输管道：保持异步读取 + bulk 写入 + `CancellationToken`。
2. 类型映射/建表：改映射表时覆盖常见类型与边界（NULL、长度、二进制）。
3. 连接保存：`SavedConnectionStore` + 密码加密；UI 走 Manager/Picker。
4. 标识符与拼接 SQL：沿用现有引用风格，防止注入与保留字问题。
5. 进度与错误信息要可操作（哪一行/哪一列失败）。

## 验证

小表往返、取消中途、目标表已存在/不存在、错误密码提示；确认密码未以明文进 JSON。
