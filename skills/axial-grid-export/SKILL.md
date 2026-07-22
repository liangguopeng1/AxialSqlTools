---
name: axial-grid-export
description: Implements AxialSqlTools results-grid export and clipboard formats (Excel, Google Sheets, Email, temp-table INSERTs, CSV/JSON/XML/HTML, column names). Use when changing grid export, copy-as, OpenXml Excel, Sheets API, or any feature that reads SSMS result grids via GridAccess.
---

# 结果网格导出与复制

## 数据入口

一律经 `Modules/GridAccess.cs` 取网格/`DataTable`。不要直接反射 SSMS 控件（兼容问题集中在适配层，见 `axial-ssms-compat`）。

## 功能与文件

| 能力 | 主要文件 |
|---|---|
| Excel | `Commands/ExportGridToExcelCommand.cs`、`Modules/ExcelExport.cs`（OpenXml） |
| Google Sheets | `Commands/ExportGridToGoogleSheetCommand.cs`、`Modules/GoogleSheetsExport.cs` |
| Email | `GridToEmail/` |
| INSERT / temp table | `Commands/ExportGridToAsInsertsCommand.cs` |
| 多格式复制 / 列名 | `Commands/ResultGridCommands.cs` 等 |

## 修改指引

1. 新导出格式：命令（VSCT）+ 纯转换模块；转换模块接收 `DataTable`/`DataSet`。
2. 大结果集注意 UI 线程：导出走后台，完成后再弹成功对话框（对照 Excel 成功对话框）。
3. Google Sheets / SMTP 依赖设置项——改配置键时同步 `SettingsManager` 与 Settings UI。
4. 生成 SQL（INSERT）时注意标识符引用与类型字面量，对照现有实现勿引入注入式拼接回归。

## 验证

多结果集、空网格、含 NULL/Unicode 的列；Excel 能否打开；复制格式粘贴到目标工具正确。
