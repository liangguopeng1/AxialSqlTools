# Quick Search：连接选择、Script 修复与搜索加速

**日期：** 2026-07-22  
**状态：** 已确认，实施中  
**方案：** C（UI 下拉 + Script 修复 + 并行优化 + 可选本地索引）

## 目标

1. 从 SSMS Object Explorer **当前已连接会话**中选择一台服务器；可选数据库。选了库只搜该库，不选则搜该服务器全部可访问用户库。
2. 修复结果行点击 **Script** 时的 SSL 证书不受信任登录错误，并保证脚本连到搜索目标服务器。
3. 搜索加速：默认路径做并行/SQL 优化；并提供**可选本地索引**，有索引时优先本地搜。

## 非目标

- 一次跨多台服务器搜索（本期限单服务器）。
- 注册服务器 / 连接历史 MRU（只枚举 Object Explorer 已连接会话）。
- 作业步骤 Script（仍保持 WIP 提示）。
- 全局全文检索引擎或服务端部署索引。

## 现状与根因

### 连接

- 现用 `GetCurrentConnectionInfoFromObjectExplorer()`，只取 OE **当前选中节点**。
- 「所有用户数据库」勾选仅在该单连接上展开库列表。
- 公开 `IObjectExplorerService` 无「枚举全部已连接服务器」API。

### Script 报错

- `Button_ScriptResult_Click` 调用 `ScriptObjectDefinition.GetText`，内部用 `GetCurrentConnectionInfo()`（**活动查询窗口**连接），不是快速搜索选中的服务器。
- 活动窗口连接常缺 `TrustServerCertificate`，Encrypt 开启时出现：证书链由不受信任的颁发机构颁发。

### 性能

- 按库串行执行多条 `LIKE '%keyword%'`（定义 / 表 / 列 / 参数 / 作业），库多则慢。

## 设计

### 1. 连接选择 UI

替换顶部「从对象资源管理器选择目标」按钮 + 连接 Label，以及「服务器上所有用户数据库」勾选。

新控件：

| 控件 | 行为 |
|------|------|
| 服务器下拉 | 列出 OE 当前已连接服务器；只能选一台 |
| 数据库下拉 | 选中服务器后加载；首项「（全部用户数据库）」；选具体库则只搜该库 |
| 刷新按钮 | 重新枚举已连接服务器；若已选服务器则刷新库列表 |

打开工具窗时自动加载服务器列表；仅一台时默认选中。未选服务器时搜索区禁用或搜索时提示。

本地化：新增字符串资源（中/英），沿用现有 `Strings` / `UiLocalization`。

### 2. 枚举已连接会话

在 `ScriptFactoryAccess`（或独立小助手）增加：

- `GetConnectedObjectExplorerSessions()`：反射读取 `IObjectExplorerService` 内部 `Tree` → `Hierarchies`，从每个 hierarchy 根节点取 `INodeInformation.Connection`，构建与现有一致的 `ConnectionInfo`（含 Encrypt / TrustServerCertificate / 认证）。
- `GetDatabases(ConnectionInfo)`：连 `master` 查询 `sys.databases`（ONLINE、MULTI_USER、`HAS_DBACCESS=1`，排除 tempdb），供数据库下拉使用。

反射失败时：降级为仅返回当前选中节点连接（若有），并记录日志；不崩溃。

### 3. Script 修复

- `ScriptObjectDefinition.GetText` 增加重载，显式传入 `ConnectionInfo`（或连接串 + 可选 `UIConnectionInfo`）。
- Quick Search 的 Script 按钮传入**当前搜索目标**连接，不再用活动查询窗口。
- 创建新查询窗口时使用同一目标的 `UIConnectionInfo`（若可得）；否则用目标连接串打开脚本，避免写到错误服务器。
- 连接串保留 OE 会话中的 `Encrypt` / `TrustServerCertificate`。不在全局强制 `TrustServerCertificate=true`（仅使用会话真实设置；若 OE 本身已信任证书则 Script 随之成功）。

若目标连接仍因证书失败：错误信息提示检查连接的「信任服务器证书」设置。

### 4. 搜索加速

#### 4.1 即时优化（默认实时路径）

- 多库并行：`SemaphoreSlim` 限制并发（默认 4，可常量配置）。
- 同库尽量合并查询往返（定义/表/列/参数可合并或减少顺序等待）；作业步骤仍仅在 `msdb`。
- 进度：报告已完成库数 / 当前库名。
- 取消：`CancellationToken` 贯穿并行任务。

#### 4.2 可选本地索引

存储根目录（与项目统一配置一致）：

`%APPDATA%\AxialSqlTools\quick-search-index\`

按服务器键分子目录（规范化 `DataSource`，非法路径字符替换），例如：

- `meta.json`：服务器名、索引时间、对象条数、连接指纹（不含密码明文）
- `objects.jsonl`：每行一个对象（类型、库、架构、名、匹配位置、源文本）；不引入 SQLite 依赖，与现有 `%APPDATA%\AxialSqlTools\` JSON 风格一致

行为：

| 场景 | 行为 |
|------|------|
| 有索引且未手动要求刷新 | 搜索优先走本地索引 |
| 无索引 / 索引损坏 | 走实时并行搜索；不强制先建索引 |
| 用户点「刷新索引」 | 后台按当前服务器拉取对象文本写入本地；可取消；完成后更新状态栏 |

UI：

- 「刷新索引」按钮（仅在已选服务器时可用）。
- 状态栏显示：`索引：yyyy-MM-dd HH:mm` 或 `未建索引`。

索引内容范围与当前搜索对象类型一致：存储过程/视图/函数定义、表名、列名、参数名、SQL Agent 作业步骤（msdb）。

安全：索引文件仅存对象元数据与定义文本，**不存密码**；连接指纹只用服务器名 + 认证类型等非敏感字段。

### 5. 主要改动文件（预期）

- `QuickSearch/QuickSearchWindowControl.xaml` / `.xaml.cs`：UI 与搜索/Script 流程
- `Modules/ScriptFactoryAccess.cs`：枚举已连接会话、建连接信息
- `Modules/ScriptObjectDefinition.cs`：按传入连接 Script
- 新增：`QuickSearch/QuickSearchIndexStore.cs`（或同级）负责索引读写与本地搜索
- `Properties/Strings*.resx`：新文案

### 6. 风险与兼容

- OE `Tree.Hierarchies` 为未文档化 API，SSMS 大版本可能变；需 try/catch + 降级 + 日志。
- 本地索引可能过期：依赖用户「刷新索引」；状态栏展示时间以便判断。
- 并行扫库会增加服务器瞬时负载：并发上限控制。

## 验收标准

1. Object Explorer 有多台已连接服务器时，服务器下拉能列出它们；选一台后可搜；换服务器后结果针对新目标。
2. 数据库选具体库时只出该库结果；选「全部」时覆盖可访问用户库（含 msdb 作业，若勾选作业类型）。
3. 在 Encrypt 且需信任证书的环境下，对搜索目标点 Script 不再因「证书链不受信任」失败（在 OE 连接本身已带 TrustServerCertificate 时）。
4. 多库实时搜索明显快于改前串行（同环境可比）。
5. 「刷新索引」成功后，同服务器再次搜索可走本地索引；状态栏显示索引时间。

## 实现顺序建议

1. 枚举已连接会话 + UI 下拉（服务器/数据库/刷新）
2. Script 使用搜索目标连接
3. 并行实时搜索优化
4. 本地索引存储 + 刷新 + 优先本地搜
