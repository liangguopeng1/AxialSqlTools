# SQL IntelliSense — 设计规格书

**日期**: 2026-07-28（2026-07-29 评审修订）  
**状态**: 设计评审完成，已纳入修订，待实现  
**项目**: AxialSqlTools — SSMS 22 扩展

---

## 0. 评审修订记录（2026-07-29）

本次评审对照实际代码（`KeypressCommandFilter` / `AxialSqlToolsPackage` / `SettingsManager` / `ScriptFactoryAccess` / `UiSettingsStore` / `csproj`）与项目 skills 约定（`axial-settings-secrets`、`_shared/conventions.md`、`axial-ssms-compat`）后修订如下：

| # | 原方案问题 | 修订 |
|---|---|---|
| 1 | Filter 挂载与 snippets 开关耦合（`Package.cs:593` 仅 `GetUseSnippets()` 为 true 才挂载），snippets 关闭则 IntelliSense 失效 | 挂载条件改为 `GetUseSnippets() \|\| GetIntelliSenseEnabled()`；见 §3.1、§10 |
| 2 | 存储后端不统一（SettingsManager 走注册表，方案却用 JSON） | 一律走 `%APPDATA%\AxialSqlTools\settings.json`，扩展 `UiSettings` + `UiSettingsStore`；注册表仅用于禁用 SSMS 内建 IntelliSense（操作的是 SSMS 自身设置，非插件配置）；见 §8 |
| 3 | 拦截 SHOWMEMBERLIST 阻止不了 SSMS 自动弹框，注册表写失败会双弹框 | 补首次启用引导 + 注册表路径 + 降级时禁用本插件自动弹（避免双弹）；见 §6.1 |
| 4 | 元数据缓存无失效策略 | 补手动刷新（Ctrl+Shift+R）+ 连接/库切换失效 + 可选定时刷新；见 §5.4 |
| 5 | WPF Popup 锚定到光标坐标的技术细节缺失 | 补 `IVsTextView.GetPointOfLineColumn` → 屏幕坐标 → `Popup.Placement` 步骤；见 §5.1 |
| 6 | MouseHover 实现路径未说明（IVsTextView 无原生 hover 事件） | 补能力检测：优先 `IWpfTextView`，降级 `IVsTextViewFilter` / Win32 钩子；见 §5.2 |
| 7 | Tab 优先级链未定义（补全 / snippet / asterisk 互斥） | 明确：弹框打开 → Tab/Enter 补全；弹框关闭 → 走原 snippet/asterisk 链；见 §5.1、§10 |
| 8 | ScriptDOM 全量解析性能（大脚本每次按键重解析） | 按 `GO` 分割只解析光标所在批次 + 缓存上次解析结果；见 §5.1 |
| 9 | 本地化标记缺失（checkbox 无 `loc` Tag） | UI 全部加 `Tag="loc:..."` 走 `MenuTextLocalizer`；见 §8.2 |
| 10 | 设置塞进 General Tab 拥挤 | 改为新建独立 `TabIntelliSense`；见 §8.2 |
| 11 | 键位拦截不全（缺 Up/Down/PageUp/PageDown/Home/End） | 补完整拦截列表；见 §5.1、§10 |
| 12 | UI 未覆盖全部设置项（delayMs 等） | 补 autoTriggerDelayMs / hoverTooltipDelayMs / maxCompletionItems 控件；见 §8.2 |
| 13 | 跨库元数据连接复用策略未定 | 按 `(Server, Database)` 缓存独立 SqlConnection，不污染查询会话；见 §5.4 |
| 14 | 缺集成回归清单 | 补 snippet×IntelliSense×SSMS内建 开关组合矩阵；见 §9 |

---

## 1. 目标

为 SSMS 22 查询编辑器提供完整的 SQL IntelliSense（补全 + ToolTip + 参数提示），安装后默认替代 SSMS 自带 IntelliSense。

## 2. 核心决策

| 决策项       | 选择                                                |
| --------- | ------------------------------------------------- |
| 实现路径      | 全部自己实现（WPF + ScriptDOM + SqlClient）               |
| 触发策略      | 自动弹（200ms 防抖）+ Ctrl+Space 强制弹 + 完全替代 SSMS 内建      |
| 元数据来源     | 按库内存缓存（首次查 sys.*, 后续命中缓存）                         |
| 弹框 UI     | WPF 自渲染 Popup + 右侧详情面板                            |
| ToolTip   | 插件自渲染 WPF ToolTip, 监看 MouseHover                  |
| SSMS 内建禁用 | 插件开关 + 写 SSMS 注册表 + 首次启用引导 + 拦截 SHOWMEMBERLIST    |
| 排序        | 前缀匹配 → 类型分组 → 字母序                                 |
| 系统对象      | 默认提示 sys.\* / INFORMATION_SCHEMA.\* / sp\_\*, 可关闭 |
| 参数提示      | 选中函数/过程后自动补 `(` 并弹内嵌参数面板                          |
| Schema 前缀 | 自动补齐 dbo., 用户已有前缀时省略                              |
| **设置存储**  | **`%APPDATA%\AxialSqlTools\settings.json`（扩展 `UiSettings`/`UiSettingsStore`）；不再用注册表存插件设置** |
| **Filter 挂载** | **与 snippets 开关解耦：snippets 或 IntelliSense 任一启用即挂载** |

## 3. 架构总览

```
SSMS 查询编辑器 (IVsTextView)
├── KeypressCommandFilter (已有) → IntelliSenseKeyHandler (新增分支)
│   ├── 拦截 COMPLETEWORD / SHOWMEMBERLIST + 方向键 / PageUp / Home / End
│   ├── 防抖 200ms → CompletionEngine（后台任务，JoinableTaskFactory 切回 UI）
│   └── 弹框打开时: Tab/Enter→补全; Up/Down→选候选; Esc→关
│       弹框关闭时: 走原 snippet / asterisk 链 (互斥优先级见 §5.1)
├── IntelliSenseTextViewExtension (新增)
│   └── MouseHover (IWpfTextView 优先, 降级 IVsTextViewFilter/Win32) → QuickInfoProvider
│
└── 功能层
    ├── CompletionEngine (ScriptDOM 解析 + 上下文判定)
    │   └── 按 GO 分割, 仅解析光标所在批次; 缓存上次 AST
    ├── QuickInfoProvider (Token → 对象 → 信息)
    ├── MetadataCatalogService (单例, 内存缓存)
    │   ├── 缓存在 ConcurrentDictionary<(Server, Database), MetadataCatalog>
    │   ├── 跨库凭据用连接指纹区分
    │   └── 按 (Server, Database) 缓存独立 SqlConnection, 不复用查询会话
    ├── IntelliSenseSettingsStore (JSON, 扩展 UiSettingsStore)
    └── UI 层
        ├── CompletionListWindow.xaml (WPF Popup, ListBox + 详情面板)
        │   └── Placement = 光标屏幕坐标 (IVsTextView.GetPointOfLineColumn → POINT)
        ├── QuickInfoTooltip.cs (WPF ToolTip 风格)
        └── IntelliSenseDisableHelper.cs (写 SSMS 注册表 + settings.json 标记)

┌─ 依赖关系 ──────────────────────────────────────┐
│  ScriptFactoryAccess → 获取当前连接 (复用, 勿新写反射)   │
│  TSql170Parser (ScriptDOM, 已引用) → 解析 SQL AST      │
│  SqlConnection → 查询 sys.objects/columns/params  │
│  UiSettingsStore → 读写 settings.json 中 intelliSense │
│  UserConfigPaths → %APPDATA%\AxialSqlTools\       │
│  ToolWindowThemeSupport → Popup/ToolTip 主题一致    │
└──────────────────────────────────────────────────┘
```

### 3.1 Filter 挂载解耦（关键）

现有 `AxialSqlToolsPackage.cs` 中 `KeypressCommandFilter` 仅在 `SettingsManager.GetUseSnippets()` 为 true 时挂载（约 `Package.cs:593`）。IntelliSense 必须独立可用，故改为：

```csharp
bool needFilter = SettingsManager.GetUseSnippets()
                || IntelliSenseSettingsStore.GetEnabled();
if (needFilter)
{
    var CommandFilter = new KeypressCommandFilter(this, textView);
    CommandFilter.AddToChain();
}
```

## 4. SQL 语法上下文映射表

`CompletionEngine` 基于 ScriptDOM AST + TokenStream 严格判定光标所属 Clause，按以下规则给出候选：

### 4.1 关键字上下文（无需连接）

| 光标位置             | 提示内容                                                                                                                |
| ---------------- | ------------------------------------------------------------------------------------------------------------------- |
| 新批次 / GO 后 / 空白行 | SELECT, WITH, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, EXEC, USE, DECLARE, SET, IF, BEGIN, END, TRUNCATE, MERGE |
| CREATE 后         | TABLE, VIEW, PROCEDURE, FUNCTION, INDEX, SCHEMA, TYPE                                                               |
| ALTER 后          | TABLE, VIEW, PROCEDURE, FUNCTION, INDEX, SCHEMA                                                                     |

### 4.2 数据对象上下文（需连接 + 元数据）

| 判定条件                              | 提示内容                                                |
| --------------------------------- | --------------------------------------------------- |
| SELECT 后、FROM 前（SelectElements 内） | 列名：`*`、CTE/表变量/#t 列、聚合/标量/窗口函数、CASE                 |
| FROM / JOIN / ,（FromClause 内）     | 仅表/视图/表值函数 + 同义词 + `(` 子查询；跨库三段名补全                  |
| `.` 前有表名/别名（匹配 FromClause）        | 仅该表的列名 + MS_Description                             |
| EXEC / EXECUTE 后                  | 仅存储过程 + 标量函数                                        |
| INSERT INTO 后                     | 仅表/视图                                               |
| UPDATE 后（UpdateStatement, SET 前）  | 仅表/视图 + 别名                                          |
| DELETE FROM 后                     | 仅表/视图                                               |
| WHERE / ON / HAVING / AND / OR 后  | 仅列名 + 运算符 + 标量函数                                    |
| SET（UPDATE 内）后                    | 仅被 UPDATE 的表列名                                      |
| ORDER BY / GROUP BY 后             | 仅列名                                                 |
| USE 后                             | 仅数据库名（sys.databases）                                |
| `@` 符号后                           | 当前脚本 DECLARE @var / SET @var / SELECT @var = 的局部变量名 |

### 4.3 本地脚本上下文（无需连接）

| 判定条件           | 提示内容                                                   |
| -------------- | ------------------------------------------------------ |
| CTE 名 + `.`    | CTE 列名（WITH 子句解析）                                      |
| `#t.` / `##t.` | 临时表列名（ScriptDOM 从 CREATE TABLE #t / SELECT INTO #t 解析） |
| `@t.`          | 表变量列名（从 DECLARE @t TABLE(...) 解析）                      |

### 4.4 跨库 / Schema / 变量特殊规则

- **三段名**: `FROM jichushuju.dbo.t` → 元数据查询以 `jichushuju` 为目标库，缓存 key 为 `(Server, jichushuju)`，使用按库缓存的独立连接（不复用查询编辑器会话，避免 `USE` 污染）
- **缺 schema**: `FROM t` → 补全结果为 `[dbo].[t]`, 选中后插入 `[dbo].[t]`
- **有 schema**: `FROM dbo.` → 补全结果只显示 `[name]`, 选中后插入 `[name]`
- **变量**: `@` 后提示同脚本 DECLARE 的局部变量名

## 5. 数据流

### 5.1 打字 → 弹框

```
用户按键
  → KeypressCommandFilter.Exec
  → IntelliSenseKeyHandler.ExecIfEnabled
    ├─ 弹框已打开?
    │   ├─ Up/Down/PageUp/PageDown/Home/End → 移动选中, 吞键
    │   ├─ Tab/Enter → 插入 InsertText, 关弹框, 吞键
    │   ├─ "(" → 若函数/过程, 切参数面板, 吞键
    │   ├─ "." → 刷新为该对象列, 吞键
    │   ├─ Escape → 关弹框, 吞键
    │   └─ 其它可见字符 → 放行按键 + 重置防抖
    │   (注: 弹框打开时绝不走 snippet/asterisk 分支)
    └─ 弹框关闭 → 防抖 200ms（新按键重置）→ 后台任务:
        1. IVsTextBuffer → 全文
        2. 按 GO 分批 → 仅取光标所在批次
           (缓存: 若批次文本与上次相同则复用 AST, 否则 TSql170Parser.Parse)
        3. CompletionEngine.GetContext(position) → 上下文类型
        4. 对象上下文 → MetadataCatalogService.GetOrBuildCatalog(connInfo, dbOverride)
           ├─ 命中 → 返回缓存
           └─ 未命中 → 独立 SqlConnection 查 sys.* (5s 超时) → 缓存
        5. CompletionEngine.Filter(catalog, prefix, context) → List<CompletionItem>
        6. 关键字上下文 → GetKeywords(context) → 同
    → JoinableTaskFactory 切回 UI 线程:
        7. 取光标屏幕坐标:
           IVsTextView.GetPointOfLineColumn(line, col, out POINT pt)
           → Popup.Placement = PlacementMode.Absolute / PlacementTarget
        8. CompletionListWindow.Show(pt, items)

用户操作(弹框关闭态):
    Tab/Enter → 走原 TryReplaceSnippet / AsteriskExpansionService (互斥优先级)
    Ctrl+Space → 强制弹补全
```

**Tab/Enter 优先级链（互斥）**：
1. 弹框打开 → 选中补全项（吞键）
2. 弹框关闭 + 命中 snippet → snippet 替换（吞键）
3. 弹框关闭 + 命中 asterisk → asterisk 扩展（吞键）
4. 否则 → 放行给编辑器

### 5.2 悬停 → ToolTip

```
鼠标停在 token 上 ≥ hoverTooltipDelayMs (默认 500ms)
  → IntelliSenseTextViewExtension
      能力检测顺序:
        a) 取 IWpfTextView (SqlEditor 可能暴露 WPF 层) → 其 MouseHover 事件
        b) 降级: IVsTextViewFilter + SetTrigger (VSStd2KCmdID)
        c) 再降级: Win32 SetCapture + WM_MOUSEMOVE on 编辑器 HWND
  → ScriptDOM 解析 → 定位 token
  → 匹配元数据对象:
    ├─ 表名 → CREATE TABLE 脚本 + MS_Description
    ├─ 列名 → 数据类型 + Nullable + 默认值 + MS_Description
    └─ 函数/过程 → 参数列表 + CREATE 脚本 + MS_Description
  → QuickInfoTooltip 渲染 (主题跟随 ToolWindowThemeSupport)
```

### 5.3 参数提示

```
用户选中函数/存储过程补全项 (Tab/Enter)
  → CompletionEngine 自动追加 InsertText + "("
  → CompletionListWindow 切换为参数模式
  → 显示参数名 + 类型 + 默认值 + 当前位置高亮
  → 用户打 ")" 或 Escape → 退出参数模式
```

### 5.4 元数据缓存与失效

- **缓存粒度**：`ConcurrentDictionary<(Server, Database), MetadataCatalog>`，单例
- **连接复用**：按 `(Server, Database)` 缓存独立 `SqlConnection`（带连接指纹区分凭据），不复用查询编辑器会话，避免 `USE` 改变上下文
- **失效时机**：
  1. 手动刷新：`Ctrl+Shift+R`（复用 SSMS 刷新语义）清空当前库缓存
  2. 连接切换：`CurrentlyActiveWndConnectionInfo` 变化时，旧库缓存保留但不再用于当前视图
  3. 数据库切换（`USE db` / 下拉切换）：目标库缓存按需重建
  4. 可选：每 N 分钟后台刷新（默认关闭，设 `autoRefreshMinutes`）

## 6. 错误处理与降级

- **任何 IntelliSense 故障不能导致编辑器无法打字、文本丢失、或 Package 崩溃**
- 元数据查询超时/权限不足 → 返回空 Catalog, 不弹候选, 关键字/本地补全照常
- ScriptDOM 解析失败 → 返回 Unknown 上下文, 不弹候选
- WPF Popup 创建异常 → 吞异常（记 NLog）, 放行按键
- SSMS 升级反射变更 → Try/Catch 降级, 不拖垮 Package（见 `axial-ssms-compat`：保留旧分支，按运行时类型能力检测）

### 6.1 SSMS 内建 IntelliSense 禁用（防双弹框）

禁用 SSMS 内建是操作 **SSMS 自身的注册表设置**（非插件配置），插件设置仍走 JSON：

- **注册表路径**（写 SSMS 选项，需实测确认精确键名）：
  `HKCU\Software\Microsoft\SQL Server Management Studio\22.0\Text Editor\Transact-SQL\IntelliSense`
  设 `EnableIntelliSense=0`（或对应 DWORD）
- **首次启用引导**：插件 `enabled=true` 且检测到内建仍开启时，弹 InfoBar 提示"已尝试禁用 SSMS 内建 IntelliSense，需重启 SSMS 生效"
- **降级链**（避免双弹框）：
  1. 写注册表成功 → 重启后内建关闭
  2. 写注册表失败 → 记 NLog，并**临时关闭本插件自动弹**（仅保留 Ctrl+Space 手动触发），直到用户手动关闭 SSMS 内建或重启
  3. 拦截 SHOWMEMBERLIST/COMPLETEWORD 始终生效（阻止 Ctrl+Space/Ctrl+J 触发内建）

## 7. 文件结构

```
AxialSqlTools/IntelliSense/
├── IntelliSenseManager.cs          # 总调度: AttachToView / Detach
├── IntelliSenseKeyHandler.cs       # 键盘拦截 + 防抖 + 弹框态路由
├── IntelliSenseTextViewExtension.cs # MouseHover → ToolTip (能力检测)
├── CompletionEngine.cs             # ScriptDOM 解析 + 上下文判定 + 批次缓存
├── QuickInfoProvider.cs            # Token → 对象 → 信息
├── MetadataCatalogService.cs       # 按库内存缓存 (SqlClient, 连接复用)
├── MetadataModels.cs               # 数据模型 + IntelliSenseSettings 模型
├── IntelliSenseSettingsStore.cs    # JSON 读写 (扩展 UiSettingsStore)
├── CompletionListWindow.xaml       # WPF Popup 弹框
├── CompletionListWindow.xaml.cs
├── QuickInfoTooltip.cs             # WPF ToolTip 渲染
├── IntelliSenseDisableHelper.cs    # 写 SSMS 注册表 (禁用内建)
└── CompletionItem.cs               # 补全项模型

修改文件:
├── KeypressCommandFilter.cs        # 加 IntelliSense 分支 + 弹框态路由 + 优先级链
├── AxialSqlToolsPackage.cs         # Filter 挂载条件解耦 (snippets || intelliSense)
├── UiSettingsStore.cs / UiSettings # 新增 intelliSense 节点 + Get/Save 方法
└── WindowSettings/                 # 新增独立 TabIntelliSense
```

> **必做**：传统 csproj 不会自动包含新文件。新增每个 `.cs` 加 `<Compile Include="IntelliSense\Xxx.cs" />`，每个 `.xaml` 加 `<Page Include="IntelliSense\Xxx.xaml">`，对照现有条目格式（见 `_shared/conventions.md`）。

## 8. 设置项

### 8.1 JSON 持久化（统一走 settings.json）

扩展 `UiSettings` 类，在 `%APPDATA%\AxialSqlTools\settings.json` 中新增 `intelliSense` 节点（与现有 `uiLanguage` 同文件，由 `UiSettingsStore` 统一加锁读写）：

```json
{
  "uiLanguage": "zh-CN",
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
    "autoRefreshMinutes": 0
  }
}
```

**`UiSettings` 扩展**：

```csharp
public sealed class UiSettings
{
    [JsonProperty("uiLanguage")]
    public string UiLanguage { get; set; } = UiSettingsStore.LanguageZhCn;

    [JsonProperty("intelliSense")]
    public IntelliSenseSettings IntelliSense { get; set; } = new IntelliSenseSettings();
}

public class IntelliSenseSettings
{
    public bool enabled = true;
    public bool disableSsmsIntelliSense = true;
    public bool autoTrigger = true;
    public int autoTriggerDelayMs = 200;
    public bool hoverTooltipEnabled = true;
    public int hoverTooltipDelayMs = 500;
    public bool includeKeywords = true;
    public bool includeSystemObjects = true;
    public bool includeLocalTempTables = true;
    public bool includeLocalVariables = true;
    public int maxCompletionItems = 50;
    public int autoRefreshMinutes = 0; // 0=关闭后台刷新
}
```

**`UiSettingsStore` 新增**（仿现有 `SaveUiLanguage` 模式，复用其锁与缓存）：

```csharp
public static IntelliSenseSettings GetIntelliSenseSettings()
{
    return Load().IntelliSense ?? new IntelliSenseSettings();
}

public static bool GetIntelliSenseEnabled()
{
    return GetIntelliSenseSettings().enabled;
}

public static void SaveIntelliSenseSettings(IntelliSenseSettings settings)
{
    var s = Load();
    s.IntelliSense = settings ?? new IntelliSenseSettings();
    Save(s); // 复用 UiSettingsStore.Save, 不会丢失 uiLanguage
}
```

> `IntelliSenseSettings` 模型类放在 `IntelliSense/MetadataModels.cs`，不放进 `SettingsManager`（避免注册表/JSON 后端混用）。`SettingsManager` 仍只管注册表类旧设置，新功能一律走 Store。
>
> `disableSsmsIntelliSense` 勾选后会弹提示"需要重启 SSMS 生效"。

### 8.2 设置 UI — 独立 IntelliSense Tab

在 `WindowSettings/SettingsWindowControl.xaml` 的 `TabControl` 中新增独立 `TabItem`（与 General / Code Snippets 等平级），所有控件加 `Tag="loc:..."` 走 `MenuTextLocalizer` 本地化：

```xml
<TabItem x:Name="TabIntelliSense" Header="IntelliSense" Tag="loc:Settings_TabIntelliSense">
    <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="8">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <CheckBox Grid.Row="0" x:Name="IntelliSenseEnabled"
                      Content="Enable Axial IntelliSense (replaces SSMS built-in)"
                      Tag="loc:Settings_IntelliSenseEnabled" Margin="5"/>
            <CheckBox Grid.Row="1" x:Name="IntelliSenseDisableSsms"
                      Content="Disable SSMS built-in IntelliSense (takes effect after SSMS restart)"
                      Tag="loc:Settings_IntelliSenseDisableSsms"
                      IsEnabled="{Binding IsChecked, ElementName=IntelliSenseEnabled}" Margin="5"/>
            <CheckBox Grid.Row="2" x:Name="IntelliSenseAutoTrigger"
                      Content="Auto-trigger completion while typing"
                      Tag="loc:Settings_IntelliSenseAutoTrigger"
                      IsEnabled="{Binding IsChecked, ElementName=IntelliSenseEnabled}" Margin="5"/>
            <CheckBox Grid.Row="3" x:Name="IntelliSenseHoverTooltip"
                      Content="Show hover tooltip for tables, columns, and procedures"
                      Tag="loc:Settings_IntelliSenseHoverTooltip"
                      IsEnabled="{Binding IsChecked, ElementName=IntelliSenseEnabled}" Margin="5"/>
            <CheckBox Grid.Row="4" x:Name="IntelliSenseIncludeKeywords"
                      Content="Include SQL keywords in completion"
                      Tag="loc:Settings_IntelliSenseIncludeKeywords"
                      IsEnabled="{Binding IsChecked, ElementName=IntelliSenseEnabled}" Margin="5"/>
            <CheckBox Grid.Row="5" x:Name="IntelliSenseIncludeSystemObjects"
                      Content="Include system objects (sys.*, INFORMATION_SCHEMA.*, sp_*)"
                      Tag="loc:Settings_IntelliSenseIncludeSystemObjects"
                      IsEnabled="{Binding IsChecked, ElementName=IntelliSenseEnabled}" Margin="5"/>

            <!-- 高级参数 (补全原方案缺失的 UI 入口) -->
            <StackPanel Grid.Row="6" Orientation="Horizontal" Margin="5,8,0,0">
                <TextBlock Text="Auto-trigger delay (ms):" VerticalAlignment="Center"
                           Tag="loc:Settings_IntelliSenseAutoTriggerDelay" Margin="0,0,6,0"/>
                <TextBox x:Name="IntelliSenseAutoTriggerDelay" Width="60" VerticalAlignment="Center"/>
            </StackPanel>
            <StackPanel Grid.Row="7" Orientation="Horizontal" Margin="5,4,0,0">
                <TextBlock Text="Hover tooltip delay (ms):" VerticalAlignment="Center"
                           Tag="loc:Settings_IntelliSenseHoverDelay" Margin="0,0,6,0"/>
                <TextBox x:Name="IntelliSenseHoverDelay" Width="60" VerticalAlignment="Center"/>
            </StackPanel>
            <StackPanel Grid.Row="8" Orientation="Horizontal" Margin="5,4,0,0">
                <TextBlock Text="Max completion items:" VerticalAlignment="Center"
                           Tag="loc:Settings_IntelliSenseMaxItems" Margin="0,0,6,0"/>
                <TextBox x:Name="IntelliSenseMaxItems" Width="60" VerticalAlignment="Center"/>
            </StackPanel>
        </Grid>
    </ScrollViewer>
</TabItem>
```

底部保存按钮沿用现有模式（参考其它 Tab 的 `SavedMessage()`），或在独立 Tab 内放 Save 按钮。Code-behind：

```csharp
// LoadSavedSettings() 中新增:
var is = UiSettingsStore.GetIntelliSenseSettings();
IntelliSenseEnabled.IsChecked = is.enabled;
IntelliSenseDisableSsms.IsChecked = is.disableSsmsIntelliSense;
IntelliSenseAutoTrigger.IsChecked = is.autoTrigger;
IntelliSenseHoverTooltip.IsChecked = is.hoverTooltipEnabled;
IntelliSenseIncludeKeywords.IsChecked = is.includeKeywords;
IntelliSenseIncludeSystemObjects.IsChecked = is.includeSystemObjects;
IntelliSenseAutoTriggerDelay.Text = is.autoTriggerDelayMs.ToString();
IntelliSenseHoverDelay.Text = is.hoverTooltipDelayMs.ToString();
IntelliSenseMaxItems.Text = is.maxCompletionItems.ToString();

// Save:
private void Button_SaveIntelliSenseSettings_Click(object sender, RoutedEventArgs e)
{
    UiSettingsStore.SaveIntelliSenseSettings(new IntelliSenseSettings
    {
        enabled = IntelliSenseEnabled.IsChecked.GetValueOrDefault(),
        disableSsmsIntelliSense = IntelliSenseDisableSsms.IsChecked.GetValueOrDefault(),
        autoTrigger = IntelliSenseAutoTrigger.IsChecked.GetValueOrDefault(),
        hoverTooltipEnabled = IntelliSenseHoverTooltip.IsChecked.GetValueOrDefault(),
        includeKeywords = IntelliSenseIncludeKeywords.IsChecked.GetValueOrDefault(),
        includeSystemObjects = IntelliSenseIncludeSystemObjects.IsChecked.GetValueOrDefault(),
        autoTriggerDelayMs = int.TryParse(IntelliSenseAutoTriggerDelay.Text, out var d1) ? d1 : 200,
        hoverTooltipDelayMs = int.TryParse(IntelliSenseHoverDelay.Text, out var d2) ? d2 : 500,
        maxCompletionItems = int.TryParse(IntelliSenseMaxItems.Text, out var mi) ? mi : 50
    });
    SavedMessage(); // 复用已有保存提示
}
```

> 本地化：新增的 `loc:` 键需同步加入 `Properties/Strings.resx` 与 `Properties/Strings.zh-Hans.resx`（项目已支持中英双语）。

## 9. 测试策略

- **单元** — CompletionEngine.GetContext(): 30+ 上下文组合, Mock MetadataCatalog
- **单元** — 上下文映射表: 验证 SELECT/JOIN/WHERE/EXEC/INSERT 不交叉提示
- **单元** — MetadataCatalogService 缓存: Mock DataReader 验证命中/未命中/失效
- **单元** — IntelliSenseSettingsStore: settings.json 读写, 验证不丢 uiLanguage
- **单元** — IntelliSenseDisableHelper: SSMS 注册表读写 + 降级
- **手工** — SSMS 22 回归矩阵（开关组合）:

| # | snippets | IntelliSense | SSMS 内建 | 预期 |
|---|---|---|---|---|
| 1 | 开 | 开 | 关 | Tab 命中 snippet→替换; 否则弹补全 |
| 2 | 关 | 开 | 关 | Tab 弹补全; snippet 不触发 |
| 3 | 开 | 关 | 关 | 原行为不变 (仅 snippet/asterisk) |
| 4 | 关 | 关 | 开 | 完全回退 SSMS 自带 |
| 5 | 开 | 开 | 开 | 不应出现双弹框 (降级链生效) |

- **手工** — 功能用例:
  - `USE db` → `SELECT * FROM` 选表 → `.` 选列
  - `EXEC` → 选过程 → `(` 参数提示
  - CTE / `#t` / `@t` 的 `.` 列补全
  - 跨库 `FROM jichushuju.dbo.`
  - 鼠标悬停 ToolTip
  - `Ctrl+Shift+R` 刷新缓存后 DDL 变更可见
  - 大脚本（>2000 行）打字无明显卡顿（批次解析生效）

## 10. 实施顺序

1. `MetadataModels.cs`（含 `IntelliSenseSettings`）— 纯数据模型
2. `CompletionItem.cs` — 补全项模型
3. `IntelliSenseSettingsStore.cs` + 扩展 `UiSettings`/`UiSettingsStore` — JSON 读写
4. `MetadataCatalogService.cs` — 依赖 ScriptFactoryAccess + 连接复用 + 缓存失效
5. `CompletionEngine.cs` — 依赖 ScriptDOM + 批次缓存 + 1,2,3
6. `IntelliSenseKeyHandler.cs` — 依赖 4,5；含弹框态路由 + Tab 优先级链 + 完整键位拦截
7. `CompletionListWindow.xaml(.cs)` — WPF 弹框 + 光标坐标锚定
8. `IntelliSenseTextViewExtension.cs` + `QuickInfoTooltip.cs` — ToolTip（能力检测）
9. `QuickInfoProvider.cs` — 依赖 4
10. `IntelliSenseDisableHelper.cs` — 写 SSMS 注册表 + 降级
11. `IntelliSenseManager.cs` — 总调度
12. 修改 `KeypressCommandFilter.cs` — 加 IntelliSense 分支
13. 修改 `AxialSqlToolsPackage.cs` — **Filter 挂载条件解耦**（snippets || intelliSense）
14. 修改 `WindowSettings/` — 新增 `TabIntelliSense` + `loc` Tag + Strings.resx
15. **登记 csproj**（每个新 .cs/.xaml）
16. 构建 + 手工验证回归矩阵

> 第 13 步是阻断级修复，必须在功能可用前完成；否则 snippets 关闭时整个 Filter 不挂载，IntelliSense 无从触发。
