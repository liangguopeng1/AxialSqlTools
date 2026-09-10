---
name: axial-intellisense
description: Use when changing the self-built SQL IntelliSense in AxialSqlTools (SSMS 22) — completion candidates, context detection, the WPF completion popup and its key/mouse routing, hover tooltip, per-database metadata cache, SSMS built-in IntelliSense disable/fallback, or the KeypressCommandFilter hook.
---

# SQL IntelliSense（自研）

为 SSMS 22 查询编辑器提供补全 + 悬停 ToolTip，安装后替代 SSMS 自带 IntelliSense。
设计文档：`docs/superpowers/specs/2026-07-28-sql-intellisense-design.md`。
细粒度踩坑与症状见 `references/pitfalls.md`；ScriptDom 通用陷阱见 `axial-scriptdom` skill。

## 关键文件

`AxialSqlTools/IntelliSense/`。`CompletionEngine` 是**同一类型的 partial class**，对外仍是 `CompletionEngine.GetCompletion`；分册在 `AxialSqlTools/IntelliSense/Completion/`，命名空间仍是 `AxialSqlTools.IntelliSense`。

| 文件 | 职责 |
|---|---|
| `CompletionEngine.cs` + `Completion/` | partial 主入口 `GetCompletion`（GO 分批 / AST 切片缓存）；分册 `LocalSymbols`（CTE/别名/临时表）、`Context`（上下文判定、FROM/EXEC 名解析）、`Items`（候选生成、过滤排序）、`CompletionMatcher.cs`（匹配打分/高亮下标，纯函数）、`CompletionModels.cs`（`CompletionResult` / `CteInfo` / `LocalTableInfo` / `TableRef`） |
| `CompletionItem.cs` | 补全项 + `CompletionKind` / `CompletionContext` |
| `MetadataModels.cs` | `IntelliSenseSettings` + 元数据模型（`MetadataCatalog` / `TableColumnInfo` / `RoutineInfo`） |
| `MetadataCatalogService.cs` / `MetadataCacheStore.cs` / `MetadataCacheRefreshService.cs` | 按 `(Server, Database)` 目录；磁盘缓存 `%APPDATA%\AxialSqlTools\intellisense-cache\{server}\meta.json` + `{database}.json`；按服务器刷新 |
| `IntelliSenseKeyHandler.cs` | 防抖 + 弹框态键路由 + Tab 优先级链 + 提交插入 |
| `CompletionListWindow.xaml(.cs)` / `WpfTreeWalk.cs` | WPF 无边框弹框、光标定位、鼠标选择；`WpfTreeWalk` 负责父节点遍历 |
| `QuickInfoProvider.cs` / `QuickInfoSqlContext.cs` / `QuickInfoTooltip.cs` / `QuickInfoDdlBuilder.cs` | 悬停：Token → 元数据 → 文本渲染 |
| `IntelliSenseTextViewExtension.cs` | 悬停监听（Win32 轮询 + DispatcherTimer） |
| `IntelliSenseDisableHelper.cs` / `IntelliSenseManager.cs` | 禁 SSMS 内建；总调度 + `AutoTriggerSuppressed` 降级 |

联动改动：`Modules/KeypressCommandFilter.cs`（优先级链分支）、`Modules/UiSettingsStore.cs`（`intelliSense` 节点）、
`AxialSqlToolsPackage.cs`（Filter 挂载条件 + Initialize 调 Ensure）、`WindowSettings/SettingsWindowControl.xaml(.cs)`（`TabIntelliSense`）。

## 数据流

```
按键 → KeypressCommandFilter.Exec
  1. 弹框开 → HandleSessionKey 路由（Up/Down/Tab/Enter/Esc/PageUp/Down/Home/End）→ 吞键
  2. Ctrl+Space / Ctrl+J → TriggerCompletion(true) → 吞键
  3. 弹框关 → snippet / asterisk（互斥）
  4. TYPECHAR → MaybeScheduleAutoTrigger → 防抖 timer（弹框关）/ BeginInvoke 刷新（弹框开）

TriggerCompletion:
  全文（一次 GetLineText）→ GO 批次；超 24KB 再切当前语句 → TSql170Parser.Parse（切片缓存）
  → GetContext → 对象上下文读 MetadataCatalogService（内存/磁盘）
  → Filter 候选 → GetPointOfLineColumn 取屏幕坐标 → Window.ShowAt
```

## 硬约束

| 约束 | 要点 |
|---|---|
| UI 线程 | 碰 DTE / 工具窗口 / 控件前 `JoinableTaskFactory.SwitchToMainThreadAsync`；后台回 UI 用 DispatcherTimer / `BeginInvoke` |
| 输入事件路径不抛异常 | 鼠标/键盘处理器必须 try/catch + 日志。项名称是 `Run` 内联，`OriginalSource` 不是 `Visual` → 遍历走 `WpfTreeWalk`，**禁止**裸 `VisualTreeHelper.GetParent` |
| 不同步提交 | 提交会 `Hide()` 弹框并 `ReplaceLines` 改宿主缓冲，必须 `Dispatcher.BeginInvoke` 推迟出路由事件 |
| 补全热路径禁止查库 | 只读内存/磁盘目录；缓存文件不与 Quick Search `objects.jsonl` 共用 |
| 新增文件登记 csproj | 传统 `AxialSqlTools.csproj` 不自动包含 `.cs` / `.xaml` |
| 挂载与降级 | Filter 挂载条件 `GetUseSnippets() \|\| GetIntelliSenseEnabled()`；`Package.InitializeAsync` 必须调 `EnsureSsmsIntelliSenseDisabled` |

## 设置项

`%APPDATA%\AxialSqlTools\settings.json` 的 `intelliSense` 节点（`UiSettingsStore` 统一加锁读写）：

`enabled` / `disableSsmsIntelliSense` / `autoTrigger` / `autoTriggerDelayMs`(200) / `hoverTooltipEnabled` /
`hoverTooltipDelayMs`(500) / `includeKeywords` / `includeSystemObjects` / `includeLocalTempTables` /
`includeLocalVariables` / `maxCompletionItems`(50) / `cacheRefreshDays`(7，0=关闭自动刷新)

模型放 `IntelliSense/MetadataModels.cs` 的 `IntelliSenseSettings`，**不放 `SettingsManager`**（避免注册表/JSON 后端混用）；与 `uiLanguage` 同文件、同锁。
设置 UI 在 `TabIntelliSense`（中文硬编码，`loc` Tag 待接入）。

## 修改指引

| 改什么 | 去哪 |
|---|---|
| 补全候选 | `Completion/CompletionEngine.Items.cs`（`BuildItems` / `Add*` / `FilterAndSort`） |
| 匹配打分 / 高亮下标 | `Completion/CompletionMatcher.cs`（`GetMatchScore`；档位与段匹配规则见 `references/pitfalls.md`） |
| 上下文判定 / FROM·EXEC 名解析 | `Completion/CompletionEngine.Context.cs`（`GetContext` / `ParseFromObjectName*` / `FromObjectNameContext`，§4 映射表） |
| CTE / 别名 / 临时表收集 | `Completion/CompletionEngine.LocalSymbols.cs` |
| 批次切分 / 解析缓存 | `CompletionEngine.cs`（`TryGetParseSlice` / `ParseSlice`） |
| 弹框行为 / 键路由 / 鼠标选择 | `IntelliSenseKeyHandler` + `CompletionListWindow`（先读 `references/pitfalls.md`） |
| 元数据查询 / 缓存 | `MetadataCatalogService` + `MetadataCacheStore` / `MetadataCacheRefreshService`；进度弹框 `Modules/IndexBuildProgressWindow` |
| 悬停 ToolTip | `QuickInfoProvider` / `QuickInfoSqlContext` / `QuickInfoTooltip` |
| 新增设置项 | `IntelliSenseSettings` + `UiSettingsStore` Get/Save + `TabIntelliSense` 控件 |

## 回归

- `tools/intellisense-scenario-tests.ps1` — 100 条上下文用例（反射 `CompletionEngine.GetCompletion`，类型名不变）。
- 改匹配打分 / 高亮 → 另跑 `tools/intellisense-matcher-tests/run-tests.ps1`（编译生产 `CompletionMatcher`，零外部依赖）。
- 改弹框 / 鼠标 / 提交 → 另跑 `tools/intellisense-mouse-tests/run-tests.ps1`（真实 WPF，驱动生产 `CompletionListWindow`）。
- 构建 / 安装见 `skills/axial-build-release/scripts/pack-release.ps1`；日志 `%APPDATA%\AxialSqlTools\logs\`。

## 已知 TODO

- `IntelliSenseDisableHelper` 的 SSMS 内建注册表精确键名待确认（不准则内建禁不掉，降级链仍保证不双弹框）。
- `TabIntelliSense` 的 `loc` Tag + `Strings.resx` 未接。
