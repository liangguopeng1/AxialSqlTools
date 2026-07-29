# AxialSqlTools 架构速查

## 项目类型

- SSMS 22 专用 VSIX（`Microsoft.VisualStudio.Ssms`，amd64，版本见 `source.extension.vsixmanifest`）
- .NET Framework 4.7.2，`AsyncPackage`（`AxialSqlToolsPackage`）
- 单项目：`AxialSqlTools/AxialSqlTools.csproj`（传统非 SDK 风格）

## 分层

1. **Shell 集成**：`AxialSqlToolsPackage.cs`、`AxialSqlToolsPackage.vsct`、`source.extension.vsixmanifest`
2. **SSMS 适配**：`Modules/GridAccess.cs`、`Modules/ScriptFactoryAccess.cs`、`Modules/ResultGridControlAdaptor.cs`、`Modules/KeypressCommandFilter.cs`
3. **SQL 连接/引擎**：`Microsoft.Data.SqlClient`、SMO、DacFx、ScriptDOM（引用多来自 SSMS 安装目录绝对路径）
4. **功能模块**：各目录 `*Command` / `*Window` / `*Control.xaml(.cs)`
5. **配置**：`Modules/SettingsManager.cs`（注册表，历史）、JSON（`%APPDATA%\AxialSqlTools\`，统一新位置，见 `_shared/conventions.md`）、DPAPI、Credential Manager

## 双命令体系

- 标准：VSCT + `OleMenuCommandService` + `MenuCommand`
- 动态：`Modules/AuroraCore.cs`（`Plugin` / `CommandRegistry` / `IVsProfferCommands3`）— Query Templates 等

## 功能目录地图

| 目录 | 能力 |
|---|---|
| `Commands/` | 格式化、网格导出、脚本对象、快捷键命令 |
| `Modules/` | 适配层、设置、导出实现、格式化、指标 |
| `HealthDashboards/` | 服务器健康面板 |
| `DataTransfer/` | 跨库 BULK 传输 |
| `SyncToGitHub/` | DacFx/SMO 脚本化 + GitHub |
| `QueryHistory/` | 查询历史 |
| `StatisticsSummary/` | STATISTICS IO/TIME 汇总 |
| `QuickSearch/` | 跨库对象搜索 |
| `SnippetManager/` | Snippet 管理 |
| `WindowSettings/` / `WindowAbout/` | 设置 / 关于 |
| `query-library/` | SQL 运维模板集合（非 C#） |

## 关键类

| 类 | 角色 |
|---|---|
| `AxialSqlToolsPackage` | 包入口、事件、历史/统计钩子、动态模板菜单 |
| `GridAccess` | 反射访问结果网格、状态栏、统计消息 |
| `ScriptFactoryAccess` | 当前编辑器/OE 连接与文本 |
| `SettingsManager` | 设置读写与加密字段 |
| `TsqlFormatter` | ScriptDOM 格式化 |
| `MetricsService` | 健康面板指标查询 |

## 日志

`%LOCALAPPDATA%\AxialSQL\AxialSQLToolsLog\log_yyyy-MM-dd.log`（NLog）
