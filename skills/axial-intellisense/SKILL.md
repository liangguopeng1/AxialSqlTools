---
name: axial-intellisense
description: AxialSqlTools 的自研 SQL IntelliSense（补全 + ToolTip + 参数提示），覆盖 ScriptDOM 解析、按库元数据缓存、WPF Popup 弹框、键盘路由、SSMS 内建禁用降级。用 when 改补全候选/上下文判定、设置项、弹框行为、双弹框降级、ScriptDom 解析、ToolTip、Filter 挂载。
---

# SQL IntelliSense（自研）

为 SSMS 22 查询编辑器提供补全 + 悬停 ToolTip，安装后替代 SSMS 自带 IntelliSense。设计文档：`docs/superpowers/specs/2026-07-28-sql-intellisense-design.md`（含评审修订记录）。

## 关键文件

`AxialSqlTools/IntelliSense/`：

| 文件 | 职责 |
|---|---|
| `MetadataModels.cs` | `IntelliSenseSettings` + 元数据模型（`MetadataCatalog`/`TableColumnInfo`/`RoutineInfo` 等） |
| `CompletionItem.cs` | 补全项 + `CompletionKind`/`CompletionContext` 枚举 |
| `CompletionEngine.cs` | `partial` 主入口：`GetCompletion` + GO 分批 / TSql170Parser AST 缓存 |
| `MetadataCatalogService.cs` | 按 `(Server, Database)` 内存目录；补全只读内存/磁盘，不查库 |
| `MetadataCacheStore.cs` | `%APPDATA%\AxialSqlTools\intellisense-cache\{server}\meta.json` + `{database}.json` |
| `MetadataCacheRefreshService.cs` | 按服务器刷全部用户库；静默/手动；进度弹框 |
| `IntelliSenseKeyHandler.cs` | 防抖 DispatcherTimer + 弹框态键路由 + Tab 优先级链 + 光标坐标锚定 + 提交插入 |
| `CompletionListWindow.xaml(.cs)` | WPF 无边框 Window，定位到光标屏幕坐标 |
| `QuickInfoProvider.cs` | Token → 元数据对象 → 信息文本 |
| `QuickInfoTooltip.cs` | WPF ToolTip 渲染 |
| `IntelliSenseTextViewExtension.cs` | 悬停监听（Win32 轮询 + DispatcherTimer；勿 AssignHandle 子类化） |
| `IntelliSenseDisableHelper.cs` | 写 SSMS 注册表禁用内建 |
| `IntelliSenseManager.cs` | 总调度入口 + `AutoTriggerSuppressed` 降级 |

`AxialSqlTools/IntelliSense/Completion/`（引擎 partial 分册 + 结果模型；命名空间仍为 `AxialSqlTools.IntelliSense`）：

| 文件 | 职责 |
|---|---|
| `CompletionModels.cs` | `CompletionResult` / `LocalSymbols` / `CteInfo` / `LocalTableInfo` / `TableRef` |
| `CompletionEngine.LocalSymbols.cs` | CTE / 临时表 / 表变量 / FROM 别名收集 |
| `CompletionEngine.Context.cs` | 上下文判定（§4 映射表）、FROM/EXEC 限定名解析、`FromObjectNameContext` |
| `CompletionEngine.Items.cs` | 候选生成（`BuildItems`）、过滤排序（`FilterAndSort`/`GetMatchScore`）、关键字与内建函数表 |

`CompletionEngine` 为 **同一类型的 partial class**（对外仍是 `CompletionEngine.GetCompletion`）。改逻辑时按职责进对应文件；新增 `.cs` 须登记 `AxialSqlTools.csproj`。

修改文件：
- `Modules/KeypressCommandFilter.cs` — 加 IntelliSense 优先级链分支
- `Modules/UiSettingsStore.cs` — 扩展 `UiSettings` 加 `intelliSense` 节点
- `AxialSqlToolsPackage.cs` — Filter 挂载条件解耦 + Initialize 调 Ensure
- `WindowSettings/SettingsWindowControl.xaml(.cs)` — `TabIntelliSense`

## 架构与数据流

```
按键 → KeypressCommandFilter.Exec
  1. 弹框开 → HandleSessionKey 路由（Up/Down/Tab/Enter/Esc/PageUp/Down/Home/End）→ 吞键
  2. Ctrl+Space / Ctrl+J → TriggerCompletion(true) → 吞键
  3. 弹框关 → snippet / asterisk（互斥）
  4. TYPECHAR → MaybeScheduleAutoTrigger → 防抖 timer（弹框关）/ BeginInvoke 立即刷新（弹框开）

TriggerCompletion:
  IVsTextBuffer 全文（一次 GetLineText）→ GO 批次；超 24KB 再切当前语句 → TSql170Parser.Parse（进程内切片缓存）
  → CompletionEngine.GetContext → 对象上下文读 MetadataCatalogService（内存/磁盘）
  → Filter 候选 → IVsTextView.GetPointOfLineColumn 取光标屏幕坐标 → Window.ShowAt
```

## 关键约束（踩坑必查）

### Filter 挂载
- `KeypressCommandFilter` 原仅在 `GetUseSnippets()==true` 时挂载（`Package.cs:593`）。已改为 `GetUseSnippets() || GetIntelliSenseEnabled()`。
- `KeypressCommandFilter` 构造里按 `GetIntelliSenseEnabled()` 创建 `IntelliSenseKeyHandler`。

### Tab 优先级链（互斥）
1. 弹框开 → Tab/Enter 提交补全（吞键；不因弹框刚出现而忽略）
2. 弹框未开但防抖/后台计算中 → Tab 立即出候选并提交第一项（快打 `s`+Tab → SELECT）
3. 弹框关 + 命中 snippet → snippet 替换
4. 弹框关 + 命中 asterisk → asterisk 扩展
5. 否则放行编辑器

### 双弹框降级（spec §6.1）
- SSMS 内建禁用写 SSMS 自身注册表（非插件配置），**需重启 SSMS 才生效**。
- `IntelliSenseManager.EnsureSsmsIntelliSenseDisabled()`：内建已禁→不抑制；需要写注册表→设 `AutoTriggerSuppressed=true`。
- `MaybeScheduleAutoTrigger` 检查 `AutoTriggerSuppressed`，为 true 时 TYPECHAR 不自动弹，仅 Ctrl+Space 手动可用。
- `Package.InitializeAsync` 必须调 `Ensure`，否则 SSMS 内建始终开着 → 双弹框 → 弹框闪关。
- 弹框 `Window` 必须设 Owner（`WindowInteropHelper` + `Process.MainWindowHandle`），否则被宿主焦点切换 deactivate 而隐藏（"闪关"根因）。

### ScriptDom / VS API 陷阱
详见 `axial-scriptdom` skill「ScriptDom API 陷阱」节。要点：
- `IVsTextView.GetPointOfLineColumn` 第三参数是 `POINT[]`（单元素数组），非 `out POINT`。
- `VSConstants.VSStd2KCmdID` 无 `ESCAPE`，Escape 键用 `CANCEL`。
- `FROM 192.` 会被点拆成多段；提交链接服务器必须整段替换为 `[192.168.1.148]`，不能只替换最后一个点后面。
- `[server].db..table` 是省略架构的四段名，必须带 `LinkedServer` 去链接服务器缓存找表/列；不能当成当前库的 `db.schema.table` 或 `schema.table alias`。
- 行首 `ex` 同时匹配 EXEC 与 EXCEPT：按新语句（`LooksLikeNewBatchPrefix` 对 exec 前缀放行），否则会落成 WhereClause 只出 EXCEPT/EXISTS。
- 片段前缀支持包含匹配：`ss` 同时出 `ssf`（前缀）和 `sess`（包含）；关键字/函数仍只做前缀。
- 字符串字面量 / `--` / `/* */` 内不补全（`IsInsideStringOrComment`）；`'b'` 不出 Beizhu/BETWEEN。
- `autoTriggerDelayMs`（设置名「补全列表弹出延迟」）在每次防抖启动时读取，保存后当前标签立即生效。
- 脚本 `USE db`（即使未执行）覆盖连接当前库：补全/QuickInfo/缓存预热走该库。`GetActiveUseDatabase` 跨 GO，忽略注释和字符串。`AddTablesAndViews` 不得因 `connInfo.Database != catalog.Database` 丢掉 USE 库的表。
- 悬停 `alias.col` 禁止在当前库扫所有表的同名列；找不到别名所属表就不出提示，避免 master 里随便一张带 `id` 的表。`GetQuickInfoByWord` 点号后同样按别名解析，不得把列名当表名。
- FROM 多个 JOIN：扫表段时第一个 `ON` 不能结束整段 FROM，否则后面的 `db.schema.table` 悬停不到。`CollectAliasesFromTokens` 同样不能在 `ON` 处 return，否则 `where k.` 只剩前两张表。`AS alias WITH (NOLOCK)` 后 look-ahead 必须跳过 WITH 提示，否则后续 JOIN 丢光。跨库补全 `GetCatalogNonBlocking` 在 `EnsureCatalogBuilding` 读完磁盘后要再取一次缓存。
- 派生表 `(SELECT ...) AS KCZ`：按括号深度选当前查询的 FROM，外层 WHERE 只能提示 KCZ/外层 JOIN 别名，不能漏出子查询里的 KC。`(SELECT…)` 不能当 (nolock) 丢掉后把 KCZ 登记成物理表名。未限定表名不得继承其它 FROM 表的库名（`USE master` 时 `BK_KuFang` 不得显示成 rt_storage）。悬停 KCZ 显示子查询原文；悬停 `stock` / `cartshuliang` 显示 SELECT 列表原文（`sum(stock)` / `sum(stock) AS stock`）。`sum(stock)` 无 AS 仍要把列名收成 `stock`。标量子查询 WHERE 里未限定的 `j_id` / `status` / `H_ID` 按该子查询 FROM 表解析，不能因为光标不在表名上就丢 FROM。走 `CollectQueryLocalsFromTokens`（传入 slice），不要去目录里找物理表 KCZ。
- 大脚本：`TryGetParseSlice` 先 GO 再按语句切（24KB 以上）；QuickInfo 与补全共用 `ParseSlice` 缓存，禁止每次悬停 Parse 全文。热路径读缓冲用一次 `GetLineText(0,0,last)`，不要按行拼。切片后用 `CollectLocalSymbolsFromText` 扫本批次光标前的 `CREATE TABLE #` / `INTO #` / `DECLARE @`（含表变量列）。`IsInsideStringOrComment` 必须用全文，否则前面未闭合 `/*` 会把注释当代码。`t.` 别名要跟到 `#tmp`/`@tv` 本地列，不能只按别名本身当对象名。
- 悬停热路径：`GetCurrentConnectionInfo` 反射扫连接对象很贵，必须短 TTL 缓存。悬停不要每次 `EnsureCatalogsReferencedInSql`；QuickInfo 只需 `GetTokensForSlice`（词法），不要为悬停 `Parse` 整句 AST。
- 内建函数悬停（COUNT / ROUND / SUM / ISNULL / GETDATE 等）：后接 `(` 时优先当函数，避免同名表抢走。无括号兜底只给 `CURRENT_TIMESTAMP` 这类签名不含 `(` 的，避免 `LEFT JOIN` 的 LEFT 被当成函数。文案走 `CompletionEngine.TryGetBuiltInFunction`，不要另建函数表。
- WHERE 别名补全：`AS SS_PiCi` 与表名相同时仍要提示 `SS_PiCi`（插入 `SS_PiCi.` 再出列）。仅当已有**不同**短别名时才跳过表名本身（`FROM t AS a` 只出 `a`）。
- JOIN ON / `AND KC`：ON 条件是表达式不是下一张表，上下文用 WhereClause 出别名；`KC.` 仍走 MemberAccess。相关子查询 `Y_GHSID = KC.` 要收外层 FROM 别名；光标落在外层 FROM 的派生表里时不能把 KCZ/后续 JOIN 漏进内层。悬停 `KC` / `KC.h_id`（如 cartshuliang 标量子查询）走 `locals.Aliases`，不能只扫当前 FROM 表段。`ON … and g` 单字母不当 GROUP/JOIN 关键字，要出 `GHS`；`in`/`gr`/`wh` 仍走 FromClause。
- `CREATE OR ALTER PROC/FUNC/VIEW`：ScriptDOM 常解析不出 AST（script=null），但 token 仍在。`GetCompletion` 不得因 script==null 直接 Unknown；有 token 就继续 `GetContext` / `CollectAliasesFromTokens`。过程体里 `and b1` / `where pr` 与普通 SELECT 一样出别名和列。
- 悬停四段名 `server.db.schema.table`（如 `[192.168.1.108].jichushuju.dbo.t_products`）：`dbo.t_products` 的 owner 是架构不是别名。`ResolveTable` 必须带 `LinkedServer`，不能当成本地 `dbo.t_products`。`GetQuickInfoByWord` 在 HasOwner 未命中别名时不得 `return null`，要再走 `TryResolveTableByHoverName`。

## 设置项

`%APPDATA%\AxialSqlTools\settings.json` 的 `intelliSense` 节点（由 `UiSettingsStore` 统一加锁读写，与 `uiLanguage` 同文件）：

```json
"intelliSense": {
  "enabled": true,
  "disableSsmsIntelliSense": true,
  "autoTrigger": true,
  "autoTriggerDelayMs": 200,
  "hoverTooltipEnabled": true,
  "hoverTooltipDelayMs": 500,
  "includeKeywords": true,
  "includeSystemObjects": true,
  "includeLocalTempTables": true,
  "includeLocalVariables": true,
  "maxCompletionItems": 50,
  "cacheRefreshDays": 7
}
```

- `IntelliSenseSettings` 模型放 `IntelliSense/MetadataModels.cs`，**不放进 `SettingsManager`**（避免注册表/JSON 后端混用）。
- 设置 UI 在 `SettingsWindowControl` 的 `TabIntelliSense`（当前中文硬编码，`loc` Tag 待接入）。

## 修改指引

1. 改补全候选 → `Completion/CompletionEngine.Items.cs`（`BuildItems` / `Add*` / `FilterAndSort` / `GetMatchScore`）。
2. 改上下文判定 / FROM·EXEC 名解析 → `Completion/CompletionEngine.Context.cs`（`GetContext` / `ParseFromObjectName*` / §4 映射表）。
3. 改 CTE/别名/临时表收集 → `Completion/CompletionEngine.LocalSymbols.cs`。
4. 改批次切分或解析缓存 → `CompletionEngine.cs`（`TryGetParseSlice` / `ParseSlice`）。
5. 改弹框行为/键路由 → `IntelliSenseKeyHandler` + `CompletionListWindow`。
6. 改元数据查询/缓存 → `MetadataCatalogService` + `MetadataCacheStore` / `MetadataCacheRefreshService`。补全热路径禁止查库。不要与 Quick Search `objects.jsonl` 共用文件。进度弹框在 `Modules/IndexBuildProgressWindow`。
7. 新增设置项 → `MetadataModels.IntelliSenseSettings` + `UiSettingsStore` Get/Save + `TabIntelliSense` 控件 + 加载/保存。`cacheRefreshDays` 默认 7，0 关闭自动刷新。
8. 新增 `.cs`/`.xaml` 必须**登记 csproj**（传统 csproj 不自动包含）。
9. UI 线程切换用 `JoinableTaskFactory`；后台查询回 UI 用 DispatcherTimer/BeginInvoke。

回归：`tools/intellisense-scenario-tests.ps1`（反射 `CompletionEngine.GetCompletion`，类型名不变）。

## 已知 TODO（待 SSMS 实测）

- `IntelliSenseDisableHelper` 的 SSMS 内建注册表路径 `Software\Microsoft\SQL Server Management Studio\22.0\Text Editor\Transact-SQL\IntelliSense` 精确键名待确认（不准则内建禁不掉，但降级链保证不双弹框）。
- `IntelliSenseTextViewExtension` 悬停用 Win32 屏幕坐标 + 活动视图门闩（`GetActiveView`）；勿对编辑器 `AssignHandle`（新建标签会卡死）。
- 设置 UI 本地化 `loc` Tag + `Strings.resx` 未接（当前中文硬编码）。

## 验证

构建用 `skills/axial-build-release/scripts/pack-release.ps1`（SSMS 在 D 盘会自动 remap HintPath）。
SSMS 实测回归矩阵见 spec §9（snippet×IntelliSense×SSMS内建 开关组合 5 种）。
日志：`%LOCALAPPDATA%\AxialSQL\AxialSQLToolsLog\`，找 IntelliSense 相关 Info/Warn。
