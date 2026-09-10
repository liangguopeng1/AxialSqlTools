# IntelliSense 踩坑清单（细粒度）

按需查阅。SKILL.md 只保留硬约束与入口；这里放历史踩坑的具体症状与修法。
ScriptDom 通用陷阱另见 `axial-scriptdom` skill。

## 弹框与键路由

### 鼠标命中目标是 Run，不是 Visual（编辑器卡死）
- 项名称文本由 `MatchHighlight` 注入 **`Run` 内联**组成，鼠标事件 `OriginalSource` 就是 `Run` ——
  `ContentElement`，**不是 `Visual`**。旧 `FindAncestor<T>` 直接 `VisualTreeHelper.GetParent(Run)` 抛
  `InvalidOperationException: "System.Windows.Documents.Run"不是 Visual 或 Visual3D`。
- 异常发生在 WPF 输入分派里 → 鼠标路由中断、弹框仍持有捕获 → 编辑器失去响应（卡死）。
  命中文字才触发、命中行内留白/字形 Border 正常 → **症状呈间歇性**。
- 必须走 `WpfTreeWalk`（`IntelliSense/WpfTreeWalk.cs`）：Visual 走视觉树，`ContentElement` 走
  `ContentOperations.GetParent` → `FrameworkContentElement.Parent`。弹框代码里**禁止**裸 `VisualTreeHelper.GetParent`。
- 入口处理器（mouse-down/up → `SelectItemUnderMouse`）必须 try/catch 并记日志：输入事件路径上异常绝不能逃出 WPF 路由。

### 提交时机
- 不要在 WPF 路由事件内同步提交：mouse-up 只 `e.Handled = true` + `ScheduleMouseCommit()`，
  由 `Dispatcher.BeginInvoke(..., DispatcherPriority.Input)` 推迟到路由返回后；`_mouseCommitScheduled` 保证只排队一次。
  同步提交会在鼠标捕获/路由未结束时 `Hide()` 弹框并 `IVsTextLines.ReplaceLines` 改写宿主缓冲，造成输入状态重入。
- `HidePopup()` 隐藏前释放本窗口捕获 `Mouse.Capture(null)`：NOACTIVATE 窗口不走激活/失活，
  隐藏后可能仍持有 OS 捕获 → 编辑器收不到鼠标消息。
- `IntelliSenseKeyHandler.CommitSelected` 曾用空 `catch {}` 吞掉插入异常 → 现场无任何日志。
  现为 `Error` + flush，并在提交路径加 `Info` 行。

### Filter 挂载
- `KeypressCommandFilter` 原仅在 `GetUseSnippets()==true` 时挂载（`Package.cs:593`）。
  已改为 `GetUseSnippets() || GetIntelliSenseEnabled()`。
- `KeypressCommandFilter` 构造里按 `GetIntelliSenseEnabled()` 创建 `IntelliSenseKeyHandler`。

### Tab 优先级链（互斥）
1. 弹框开 → Tab/Enter 提交补全（吞键；不因弹框刚出现而忽略）
2. 弹框未开但防抖/后台计算中 → Tab 立即出候选并提交第一项（快打 `s`+Tab → SELECT）
3. 弹框关 + 命中 snippet → snippet 替换
4. 弹框关 + 命中 asterisk → asterisk 扩展
5. 否则放行编辑器

### 双弹框降级（spec §6.1）
- SSMS 内建禁用写 SSMS 自身注册表（非插件配置），**需重启 SSMS 才生效**。键路径：
  `Software\Microsoft\SQL Server Management Studio\22.0\Text Editor\Transact-SQL\IntelliSense`（精确键名待确认）。
- `IntelliSenseManager.EnsureSsmsIntelliSenseDisabled()`：内建已禁→不抑制；需要写注册表→设 `AutoTriggerSuppressed=true`。
- `MaybeScheduleAutoTrigger` 检查 `AutoTriggerSuppressed`，为 true 时 TYPECHAR 不自动弹，仅 Ctrl+Space 手动可用。
- `Package.InitializeAsync` 必须调 `Ensure`，否则 SSMS 内建始终开着 → 双弹框 → 弹框闪关。
- 弹框 `Window` 必须设 Owner（`WindowInteropHelper` + `Process.MainWindowHandle`），
  否则被宿主焦点切换 deactivate 而隐藏（"闪关"根因）。

## 解析与上下文

- `IVsTextView.GetPointOfLineColumn` 第三参数是 `POINT[]`（单元素数组），非 `out POINT`。
- `VSConstants.VSStd2KCmdID` 无 `ESCAPE`，Escape 键用 `CANCEL`。
- `FROM 192.` 会被点拆成多段；提交链接服务器必须整段替换为 `[192.168.1.148]`，不能只替换最后一个点后面。
- `[server].db..table` 是省略架构的四段名，必须带 `LinkedServer` 去链接服务器缓存找表/列；
  不能当成当前库的 `db.schema.table` 或 `schema.table alias`。
- 行首 `ex` 同时匹配 EXEC 与 EXCEPT：按新语句（`LooksLikeNewBatchPrefix` 对 exec 前缀放行），
  否则会落成 WhereClause 只出 EXCEPT/EXISTS。
- 片段前缀支持包含匹配：`ss` 同时出 `ssf`（前缀）和 `sess`（包含）；关键字/函数仍只做前缀。
- 字符串字面量 / `--` / `/* */` 内不补全（`IsInsideStringOrComment`）；`'b'` 不出 Beizhu/BETWEEN。
- 脚本 `USE db`（即使未执行）覆盖连接当前库：补全/QuickInfo/缓存预热走该库。`GetActiveUseDatabase` 跨 GO，
  忽略注释和字符串。`AddTablesAndViews` 不得因 `connInfo.Database != catalog.Database` 丢掉 USE 库的表。
- FROM 多个 JOIN：扫表段时第一个 `ON` 不能结束整段 FROM，否则后面的 `db.schema.table` 悬停不到。
  `CollectAliasesFromTokens` 同样不能在 `ON` 处 return，否则 `where k.` 只剩前两张表。
  `AS alias WITH (NOLOCK)` 后 look-ahead 必须跳过 WITH 提示，否则后续 JOIN 丢光。
  跨库补全 `GetCatalogNonBlocking` 在 `EnsureCatalogBuilding` 读完磁盘后要再取一次缓存。
- 派生表 `(SELECT ...) AS KCZ`：按括号深度选当前查询的 FROM，外层 WHERE 只能提示 KCZ/外层 JOIN 别名，
  不能漏出子查询里的 KC。`(SELECT…)` 不能当 (nolock) 丢掉后把 KCZ 登记成物理表名。
  未限定表名不得继承其它 FROM 表的库名（`USE master` 时 `BK_KuFang` 不得显示成 rt_storage）。
  悬停 KCZ 显示子查询原文；悬停 `stock` / `cartshuliang` 显示 SELECT 列表原文
  （`sum(stock)` / `sum(stock) AS stock`）；`sum(stock)` 无 AS 仍要把列名收成 `stock`。
  标量子查询 WHERE 里未限定的 `j_id` / `status` / `H_ID` 按该子查询 FROM 表解析，
  不能因为光标不在表名上就丢 FROM。走 `CollectQueryLocalsFromTokens`（传入 slice），不要去目录里找物理表 KCZ。
- 大脚本：`TryGetParseSlice` 先 GO 再按语句切（24KB 以上）；QuickInfo 与补全共用 `ParseSlice` 缓存，
  禁止每次悬停 Parse 全文。热路径读缓冲用一次 `GetLineText(0,0,last)`，不要按行拼。
  切片后用 `CollectLocalSymbolsFromText` 扫本批次光标前的 `CREATE TABLE #` / `INTO #` / `DECLARE @`（含表变量列）。
  `IsInsideStringOrComment` 必须用全文，否则前面未闭合 `/*` 会把注释当代码。
  `t.` 别名要跟到 `#tmp`/`@tv` 本地列，不能只按别名本身当对象名。
- WHERE 别名补全：`AS SS_PiCi` 与表名相同时仍要提示 `SS_PiCi`（插入 `SS_PiCi.` 再出列）。
  仅当已有**不同**短别名时才跳过表名本身（`FROM t AS a` 只出 `a`）。
- JOIN ON / `AND KC`：ON 条件是表达式不是下一张表，上下文用 WhereClause 出别名；`KC.` 仍走 MemberAccess。
  相关子查询 `Y_GHSID = KC.` 要收外层 FROM 别名；光标落在外层 FROM 的派生表里时不能把 KCZ/后续 JOIN 漏进内层。
  悬停 `KC` / `KC.h_id`（如 cartshuliang 标量子查询）走 `locals.Aliases`，不能只扫当前 FROM 表段。
  `ON … and g` 单字母不当 GROUP/JOIN 关键字，要出 `GHS`；`in`/`gr`/`wh` 仍走 FromClause。
- `CREATE OR ALTER PROC/FUNC/VIEW`：ScriptDOM 常解析不出 AST（script=null），但 token 仍在。
  `GetCompletion` 不得因 script==null 直接 Unknown；有 token 就继续 `GetContext` / `CollectAliasesFromTokens`。
  过程体里 `and b1` / `where pr` 与普通 SELECT 一样出别名和列。

## 悬停（QuickInfo）

- 悬停 `alias.col` 禁止在当前库扫所有表的同名列；找不到别名所属表就不出提示，
  避免 master 里随便一张带 `id` 的表。`GetQuickInfoByWord` 点号后同样按别名解析，不得把列名当表名。
- 悬停热路径：`GetCurrentConnectionInfo` 反射扫连接对象很贵，必须短 TTL 缓存。
  悬停不要每次 `EnsureCatalogsReferencedInSql`；QuickInfo 只需 `GetTokensForSlice`（词法），
  不要为悬停 `Parse` 整句 AST。
- 内建函数悬停（COUNT / ROUND / SUM / ISNULL / GETDATE 等）：后接 `(` 时优先当函数，避免同名表抢走。
  无括号兜底只给 `CURRENT_TIMESTAMP` 这类签名不含 `(` 的，避免 `LEFT JOIN` 的 LEFT 被当成函数。
  文案走 `CompletionEngine.TryGetBuiltInFunction`，不要另建函数表。
- 悬停四段名 `server.db.schema.table`（如 `[192.168.1.108].jichushuju.dbo.t_products`）：
  `dbo.t_products` 的 owner 是架构不是别名。`ResolveTable` 必须带 `LinkedServer`，不能当成本地 `dbo.t_products`。
  `GetQuickInfoByWord` 在 HasOwner 未命中别名时不得 `return null`，要再走 `TryResolveTableByHoverName`。
- 悬停用 Win32 屏幕坐标 + 活动视图门闩（`GetActiveView`）；**勿对编辑器 `AssignHandle`**（新建标签会卡死）。

## 设置与生效时机

- `autoTriggerDelayMs`（设置名「补全列表弹出延迟」）在每次防抖启动时读取，保存后当前标签立即生效。

## 测试与工具链

- `tools/intellisense-mouse-tests/run-tests.ps1` —— 真实 WPF 回归测试，编译并驱动生产
  `CompletionListWindow`（真实布局 + `InputHitTest`）。断言命中目标为 `Run`、`WpfTreeWalk` 不抛且能选中、
  Visual 命中照常、提交被推迟且只提交一次。10 项断言，退出码即结果。
- 弹框/鼠标的行为断言**全部收敛到上面这一套**：曾有 `tools/intellisense-mouse-regression-test.ps1`
  做正则静态检查（禁止裸 `VisualTreeHelper.GetParent`、mouse-up 不同步提交等），已被行为测试逐条覆盖且更严格，
  2026-09-10 删除。正则方案还有副作用：它靠「方法紧邻」定位，会强迫生产代码的排列顺序和注释位置。
- `tools/intellisense-scenario-tests.ps1` —— 反射 `CompletionEngine.GetCompletion` 跑 100 条上下文用例，类型名不变。
- 日志在 `%APPDATA%\AxialSqlTools\logs\`（旧文档写的 `%LOCALAPPDATA%\AxialSQL\AxialSQLToolsLog\` 是错的，已更正）。
- 仓库 `.ps1` 一律写**英文注释**：文件无 BOM，PS 5.1 按 ANSI 解码，中文会让括号错位导致解析失败。
- 沙箱里 PowerShell 工具会静默阻止外部 exe（`dotnet`/`vswhere`）；跑 dotnet 测试要走 Bash，
  PS 侧只可用 `Parser::ParseFile` 做语法校验。
