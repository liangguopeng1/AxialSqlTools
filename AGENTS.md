# AxialSqlTools — Agent 说明

SSMS 22 专用 VSIX 生产力扩展（`Microsoft.VisualStudio.Ssms`，amd64，Identity Version 见 `AxialSqlTools/source.extension.vsixmanifest`）。单项目、传统非 SDK csproj、.NET Framework 4.7.2、`AsyncPackage`。Apache License 2.0。

本文件是全局入口。场景细节按需打开 `skills/<name>/SKILL.md`，不要一次读完所有 skill。共享背景：`skills/_shared/architecture.md`、`skills/_shared/conventions.md`。用户功能列表见仓库根 `README.md`。

## 启动链路

1. `AxialSqlTools/source.extension.vsixmanifest` — 目标 SSMS 22、版本号
2. `AxialSqlTools/AxialSqlToolsPackage.cs` — `AsyncPackage`，AutoLoad，注册 ToolWindow，查询执行钩子
3. `AxialSqlTools/AxialSqlToolsPackage.vsct` — 菜单/工具栏/快捷键
4. `InitializeAsync` 内显式调用各 `*Command.InitializeAsync`

## 分层

1. **Shell**：Package、VSCT、vsixmanifest
2. **SSMS 适配（脆弱）**：`Modules/GridAccess.cs`、`ScriptFactoryAccess.cs`、`ResultGridControlAdaptor.cs`、`KeypressCommandFilter.cs` — 反射私有 API，失败必须降级，勿拖垮 Package
3. **SQL 引擎**：`Microsoft.Data.SqlClient`、SMO、DacFx、ScriptDOM（HintPath 多指向本机 SSMS 安装目录）
4. **功能模块**：各目录 `*Command` / `*Window` / `*Control.xaml(.cs)`
5. **配置**：`Modules/UserConfigPaths.cs` + `SettingsManager` / `UiSettingsStore`；敏感项 DPAPI 或凭据管理器

双命令体系：标准 VSCT + `OleMenuCommandService`；动态菜单走 `Modules/AuroraCore.cs`（Query Templates）。

## 功能目录

| 目录 | 能力 |
|---|---|
| `Commands/` | 格式化、网格导出、脚本对象、快捷键 |
| `Modules/` | 适配层、设置、导出、格式化、指标 |
| `IntelliSense/` | 自研补全 / ToolTip / 元数据缓存 |
| `HealthDashboards/` | 服务器健康面板 |
| `DataTransfer/` | 跨库 BULK（SQL / PostgreSQL / MySQL） |
| `DataImport/` | 数据导入 |
| `SyncToGitHub/` | DacFx/SMO 脚本化 + GitHub |
| `QueryHistory/` | 查询历史 |
| `TabHistory/` | 标签页历史 |
| `StatisticsSummary/` | STATISTICS IO/TIME 汇总 |
| `QuickSearch/` | 跨库对象搜索 |
| `SnippetManager/` | Snippet 管理 |
| `GridToEmail/` | 网格发邮件 |
| `SqlServerBuilds/` | SQL Server 构建版本信息 |
| `WindowSettings/` / `WindowAbout/` | 设置 / 关于 |
| `query-library/` | SQL 运维模板（非 C#） |

## 定位

| 需求 | 先看 |
|---|---|
| 菜单 / 工具栏 / 快捷键 | `.vsct` + `Commands/` 或 `*WindowCommand.cs` |
| 结果网格 / 状态栏 / 执行时间 | `Modules/GridAccess.cs` |
| 当前连接 / 选中 SQL | `Modules/ScriptFactoryAccess.cs` |
| 设置项 / 凭据 | `WindowSettings/` + `SettingsManager` + `UserConfigPaths` |
| 查询执行后钩子（历史/统计） | `AxialSqlToolsPackage.cs` |
| 编辑器按键（snippet / 补全） | `Modules/KeypressCommandFilter.cs` |
| 构建 / 安装 / 发版 | `skills/axial-build-release`、`.vscode/axial-extension.ps1` |

## Skill 路由

任务对上后再读对应 `skills/<name>/SKILL.md`。

| 你在做什么 | Skill |
|---|---|
| 不熟悉仓库 / 找入口 | `axial-overview` |
| 加菜单命令、快捷键 | `axial-add-command` |
| 加 Tool Window / WPF UI | `axial-tool-window` |
| 网格/状态栏/查询事件坏了、SSMS 升级 | `axial-ssms-compat` |
| 取当前连接、认证、Encrypt | `axial-sql-connection` |
| T-SQL 格式化 / ScriptDOM | `axial-scriptdom` |
| 网格导出 Excel/Sheets/Email/INSERT | `axial-grid-export` |
| Quick Search | `axial-quick-search` |
| Health Dashboard / DMV | `axial-health-dashboard` |
| Query History / Statistics Summary | `axial-query-history` |
| Data Transfer / BULK | `axial-data-transfer` |
| Sync to GitHub / DacFx / SMO | `axial-sync-github` |
| Query Templates / Snippets | `axial-snippets-templates` |
| 设置窗口、注册表、凭据加密 | `axial-settings-secrets` |
| SQL IntelliSense 补全/ToolTip | `axial-intellisense` |
| 构建、安装、改版本、发 ZIP | `axial-build-release` |

`DataImport/`、`TabHistory/`、`SqlServerBuilds/` 暂无专用 skill：对照同构 Tool Window，约定仍以本文件为准。

## 硬约定

### UI 线程

操作 DTE / 工具窗口 / 控件前：`await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken)`。后台查询后回 UI 用 Package 的 `JoinableTaskFactory`，不要跨线程碰控件。

### 新文件必须登记 csproj

传统 csproj **不会**自动包含新文件。新增 `.cs` / `.xaml` 后在 `AxialSqlTools/AxialSqlTools.csproj` 登记：

- Compile：`<Compile Include="Path\File.cs" />`
- WPF Page：`<Page Include="Path\File.xaml" ... />`（对照现有条目）

### 命令 ID

Command Set GUID：`45457e02-6dec-4a4d-ab22-c9ee126d23c5`。新 `CommandId` 在 VSCT 的 `IDSymbol` 分配未占用整数，命令类使用同一常量。动态模板菜单走 Aurora，不是这条 VSCT 路径。

### 复用优先

| 需求 | 复用 |
|---|---|
| 当前连接 / 编辑器文本 | `ScriptFactoryAccess` |
| 结果网格 DataTable / 对齐 / 状态栏 | `GridAccess` |
| 配置路径 | `UserConfigPaths` |
| 设置读写 | `SettingsManager` / `UiSettingsStore` |
| 工具窗口主题 | `Modules/ToolWindowThemeSupport.cs`、`Themes/` |
| UI 文案 | `Properties/Strings.resx` + `UiLocalization`（`Tag="loc:Key"`） |

不要在功能模块里复制 SSMS 反射。不要从 DTE 另拼一套连接。

### 用户配置

统一根目录：`%APPDATA%\AxialSqlTools\`（`UserConfigPaths.Root`）。路径用 `Environment.SpecialFolder.ApplicationData`，不要硬编码用户名。

| 内容 | 路径 |
|---|---|
| 通用设置 | `settings.json` |
| 代码片段 | `snippets.json` |
| Data Transfer 连接 | `data-transfer-connections.json` |
| GitHub Sync 配置 | `github-sync-profiles.json` |
| Query History（文件模式） | `query-history\` |
| Tab History | `tab-history\` |
| 查询模板 | `templates\` |
| 日志 | `logs\`（`log_yyyy-MM-dd.log`） |
| Quick Search 索引 | `quick-search-index\` |
| IntelliSense 缓存 | `intellisense-cache\` |

新代码不要写入：`HKCU\AxialSqlTools\Settings`、`%LOCALAPPDATA%\AxialSQL\`、`文档\AxialSqlToolsTemplates`（仅兼容/迁移）。

敏感信息：SMTP / API Key / 连接串密码走 DPAPI；GitHub Token 走 `WindowsCredentialHelper`。审查时确认未写入明文 JSON。

### SSMS 兼容

反射字段名随 SSMS 小版本变化。集中改适配层；已有 SSMS 21 `CollectionBase` vs SSMS 22 `List<T>` 分支——新差异按此扩展，不要删旧分支。失败打日志并降级。

### 代码风格

- 不要加无意义空行
- 复用已有代码时保留原有注释
- 改动范围只覆盖任务需要的文件
- 避免空 `catch { }` 吞掉新代码失败

### 本地验证

1. 关闭 SSMS
2. Release 构建或 `.vscode/axial-extension.ps1`
3. 重启 SSMS，检查工具栏/菜单/目标窗口

日志：`%APPDATA%\AxialSqlTools\logs\`。热路径不要同步 `LogManager.Flush()`。
