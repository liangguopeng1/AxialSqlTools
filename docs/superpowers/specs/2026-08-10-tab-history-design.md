# Tab History（标签页历史 + SQL 内容快照）— 设计规格书

**日期**: 2026-08-10
**状态**: 设计评审通过，待实现
**项目**: AxialSqlTools — SSMS 22 扩展

---

## 1. 目标

记录 SSMS 查询编辑器中**所有打开过的标签页**及其**生命周期事件**，并在关键时机对编辑器中的 **SQL 内容做全文快照**（含未执行的草稿），提供工具窗口按时间/服务器/文档检索回看"那天打开过什么、写过什么 SQL"。

与现有 `QueryHistory` 的区别：QueryHistory 只记录**已执行**的查询（含耗时、行数、统计信息）；Tab History 记录**标签页生命周期 + 编辑内容快照**，两者互不干扰。

## 2. 核心决策

| 决策项 | 选择 |
|---|---|
| 实现方式 | 新建独立 `TabHistory/` 目录，平行于 `QueryHistory/` |
| 采集时机 | 仅关键时机：窗口创建 / 激活 / 关闭 / 查询执行后（**不做定时轮询、不做按键级记录**） |
| 存储 | JSONL 按天分文件：`%APPDATA%\AxialSqlTools\tab-history\tab-history-yyyy-MM-dd.jsonl` |
| 内容去重 | 同一文档内容哈希与上一条相同 → 内容字段存空（UI 显示"无变化"） |
| 查看方式 | 新增 Tool Window「Tab History」，含筛选 + 列表 + 全文查看 |
| 启用策略 | **默认关闭**，设置窗口加开关（settings.json 的 `tabHistory.enabled`） |
| 设置后端 | `UiSettingsStore`（`%APPDATA%\AxialSqlTools\settings.json`），扩展 `UiSettings` 加 `TabHistorySettings` 节点 |
| 命令 ID | `4156`（vsct 未占用） |

## 3. 架构总览

```
SSMS 查询编辑器窗口事件（UI 线程）
│  WindowCreated   → TabHistoryRecorder.Record(Opened, window)
│  WindowActivated → TabHistoryRecorder.Record(Activated, window)
│  WindowClosing   → TabHistoryRecorder.Record(Closed, window)
│  SQLResultsControl_ScriptExecutionCompleted → TabHistoryRecorder.Record(Executed, 执行上下文)
│
▼
TabHistoryRecorder（静态，采集入口，仅 GetTabHistoryEnabled() 时干活）
│  ├── 读窗口文本：ScriptFactoryAccess.GetQueryWindowText(window)（新增 helper）
│  ├── 读连接信息：Executed 事件用执行上下文真实连接；其余事件用活动连接或留空
│  └── 内容哈希去重（per-document 上次哈希缓存）
▼
TabHistoryStore（静态，存储层）
│  ├── ConcurrentQueue + Task.Run 后台写盘（复用 QueryHistory 队列模式）
│  ├── AppendAsync：JSONL 追加 + 文件锁
│  └── LoadRecent / CleanupOldFiles（启动清理 >90 天）
▼
TabHistoryWindow（ToolWindowPane）
   └── TabHistoryWindowControl.xaml(.cs) + TabHistoryViewModel
       ├── 筛选：日期 From/To、服务器、文档名/内容关键词
       ├── DataGrid：时间 / 事件 / 文档 / 服务器 / 库 / 字符数 / 预览
       └── 下方只读全文区 + 复制 + 打开文件
```

### 3.1 采集入口（改 Package 已有事件，不新增事件体系）

`AxialSqlToolsPackage` 已挂载 `WindowCreated_Event` / `WindowActivated_Event` / `WindowClosing_Event`，并在 `SQLResultsControl_ScriptExecutionCompleted`（静态方法，QueryHistory 已在此采集）中能拿到执行上下文。在四处现有处理器中追加调用 `TabHistoryRecorder.Record(...)` 即可，**不新增任何 Shell 事件挂载**。

## 4. 数据模型与存储

### 4.1 记录模型（`TabHistoryRecord.cs`）

```csharp
public enum TabHistoryEventType { Opened, Activated, Closed, Executed }

public class TabHistoryRecord
{
    public long Id;                  // 加载时按倒序重编号（同 QueryHistory 做法）
    public DateTime Timestamp;       // 事件发生时间（本地时间）
    public TabHistoryEventType EventType;
    public string DocumentName;      // 如 SQLQuery1.sql（无扩展名拼接）
    public string DocumentPath;      // 未保存文件为空
    public string DataSource;        // 服务器；Executed 为真实连接，其余尽力而为
    public string DatabaseName;      // 同上
    public string Content;           // SQL 全文；与上一条同文档同哈希则为 null
    public string ContentHash;       // SHA1（Content 的），用于去重
    public int CharCount;            // Content 长度（null 时取 0）
    public string ContentShort;      // UI 预览前 100 字符（不落盘）
}
```

### 4.2 磁盘格式

JSONL，一行一条记录，按天分文件：

```
{"Timestamp":"2026-08-10T14:30:00","EventType":"Opened","DocumentName":"SQLQuery1.sql","DocumentPath":"","DataSource":"SRV01","DatabaseName":"db1","Content":"SELECT * FROM t","ContentHash":"ab12...","CharCount":15}
```

- 文件夹：`UserConfigPaths.TabHistoryDirectory`（新增属性，`Path.Combine(Root, "tab-history")`）
- 文件名：`tab-history-yyyy-MM-dd.jsonl`（本地日期）
- 启动清理：删除 `LastWriteTime` 早于 90 天前的 `tab-history-*.jsonl`
- 读取：`LoadRecent(int max = 1000)` 从所有文件按行反序列化，损坏行跳过，按 Timestamp 倒序取最近 max 条

### 4.3 去重规则

`TabHistoryRecorder` 维护 `ConcurrentDictionary<string /*documentKey*/, string /*lastHash*/>`（documentKey = 文档路径或标题）。当前内容哈希与上次相同 → 写入时 `Content=null`，但仍记录事件（保证标签页生命周期完整）。`Executed` 事件**不做内容去重**（执行内容必须保留，即使与快照重复）。

## 5. 采集层细节

### 5.1 窗口文本获取（新增 helper）

`ScriptFactoryAccess` 已有 `GetActiveQueryWindowText()`（只取**活动**文档）。新增针对指定窗口的重载：

```csharp
public static string GetQueryWindowText(EnvDTE.Window window)
```

实现：`window?.Document?.Object("TextDocument") as TextDocument` → `StartPoint.CreateEditPoint().GetText(EndPoint)`；失败返回空串。`WindowClosing` 时文档通常仍可读，取不到则为空。

### 5.2 各事件采集内容

| 事件 | EventType | Content | DataSource/Database |
|---|---|---|---|
| `WindowCreated_Event` | Opened | 窗口初始内容 | 活动连接（尽力而为，可能为空） |
| `WindowActivated_Event` | Activated | 当前内容（去重） | 活动连接（尽力而为） |
| `WindowClosing_Event` | Closed | 最终内容（去重） | 不取连接（关闭时不可靠），可为空 |
| `SQLResultsControl_ScriptExecutionCompleted` | Executed | 编辑器当前内容（不去重） | 执行上下文真实连接（静态方法参数 `QEOLESQLExec` → `m_conn` → DataSource/Database） |

### 5.3 文档识别

仅记录 SQL 查询文档：`window.Kind == "Document"` 且能成功取到 `TextDocument` 即视为查询窗口（SSMS 查询编辑器满足此条件）。对象资源管理器、结果窗等非文档窗口跳过。

### 5.4 线程与错误处理

- 事件在 UI 线程触发；`Record` 内快速构造记录后入队，`Task.Run` 后台写盘（复用 QueryHistory 的 `EnqueueDataForProcessing` 模式），不卡 UI
- 写盘失败仅 `_logger.Error`，不影响 Package 运行
- `Record` 全程 try/catch，采集失败静默降级（不拖垮 Package 加载/事件链）
- 文件写入用 `lock` 保证并发安全

## 6. UI 层

### 6.1 菜单与命令

- `AxialSqlToolsPackage.vsct`：新增 `AxialTabHistoryCommand`（value=4156），Button 放在 `AxialToolsSubMenuGroup_Script2`（与 Query History 相邻），复用 `guidQueryHistory` 图标（避免新增 png）；ButtonText 走 `LocCanonicalName` 本地化
- `TabHistoryWindowCommand.cs`：CommandId=4156，CommandSet 同 QueryHistory（`45457e02-6dec-4a4d-ab22-c9ee126d23c5`）
- `TabHistoryWindow.cs`：`ToolWindowPane`，Caption 走 `Strings.Get("Menu_TabHistory")`
- `[ProvideToolWindow(typeof(TabHistoryWindow))]` 注册到 Package

### 6.2 控件布局（`TabHistoryWindowControl.xaml`）

```
Row 0（Auto）: 标题「Tab History」+ 筛选行
  筛选：From 日期 | To 日期 | Server | 文档/内容关键词 | [刷新] [清空]
Row 1（*）  : DataGrid
  列：时间 | 事件(Opened/Activated/Closed/Executed 中文标签) | 文档 | 服务器 | 库 | 字符数 | 预览(前100字符)
Row 2（*）  : 只读全文区 + 顶部工具条 [复制全文] [打开文件](DocumentPath 非空时可用)
```

- 主题：`SharedToolWindowTheme.xaml`（`AxialThemeBackgroundBrush` 等）
- 所有文案加 `Tag="loc:xxx"`，走 `MenuTextLocalizer` 本地化（zh-Hans / en）
- `DataGrid` 选中行 → 下方显示全文（Content 为 null 时显示本地化占位文案「(内容未变化)」）
- ViewModel 复用 `RelayCommand`（QueryHistory 已有）

## 7. 设置

### 7.1 UiSettings 扩展（`UiSettingsStore.cs`）

```csharp
[JsonProperty("tabHistory")]
public TabHistorySettings TabHistory { get; set; }

public sealed class TabHistorySettings
{
    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = false;   // 默认关闭
}
```

新增方法：`GetTabHistoryEnabled()` / `SaveTabHistoryEnabled(bool)`，`Normalize`/`Clone` 同步处理该节点（null 兼容旧 settings.json）。

### 7.2 设置窗口（`WindowSettingsControl.xaml`）

在现有设置 Tab 中新增一项 CheckBox「记录标签页历史（Tab History）」，默认不勾选；勾选即开始记录（无需重启）。文案 `Tag="loc:xxx"`。

## 8. 本地化

新增字符串（`Strings.resx` + `Strings.zh-Hans.resx` + `Properties/Resources` 相关）：

- `Menu_TabHistory`（窗口标题/菜单）
- `TabHistory_HeaderTitle`、`TabHistory_EventOpened/Activated/Closed/Executed`（事件中文标签）
- `TabHistory_From/To/Server/Filter`（筛选标签）
- `TabHistory_ContentUnchanged`（"(内容未变化)"占位）
- `TabHistory_CopyAll/OpenFile/Refresh/ClearFilters`
- 设置项 `Settings_TabHistoryEnabled`

工具：`tools/append-i18n-keys.ps1` / `rebuild-zh-hans-resx.ps1` 可辅助批量登记。

## 9. 涉及文件清单

**新增（均需在 `AxialSqlTools.csproj` 手工登记 Compile/Page）：**

| 文件 | 职责 |
|---|---|
| `TabHistory/TabHistoryRecord.cs` | 记录模型 + 事件枚举 |
| `TabHistory/TabHistoryStore.cs` | JSONL 写读、清理、加载 |
| `TabHistory/TabHistoryRecorder.cs` | 采集入口、去重、文档/连接信息提取 |
| `TabHistory/TabHistoryViewModel.cs` | 加载 + 筛选 + 选中记录 |
| `TabHistory/TabHistoryWindow.cs` | ToolWindowPane |
| `TabHistory/TabHistoryWindowCommand.cs` | 菜单命令（4156） |
| `TabHistory/TabHistoryWindowControl.xaml` + `.xaml.cs` | 控件 + code-behind |

**修改：**

| 文件 | 改动 |
|---|---|
| `AxialSqlToolsPackage.cs` | 三个 Window 事件 + `SQLResultsControl_ScriptExecutionCompleted` 中调用 Recorder；注册 ToolWindow；初始化命令 |
| `AxialSqlToolsPackage.vsct` | 新增按钮 4156 + IDSymbol |
| `Modules/ScriptFactoryAccess.cs` | 新增 `GetQueryWindowText(Window)` |
| `Modules/UiSettingsStore.cs` | `TabHistorySettings` 节点 + 读写方法 |
| `Modules/UserConfigPaths.cs` | `TabHistoryDirectory` |
| `WindowSettings/SettingsWindowControl.xaml(.cs)` | 设置开关项 |
| `Properties/Strings.resx` + `Strings.zh-Hans.resx` | 本地化字符串 |

## 10. 验证

1. **开关**：设置窗口默认不勾选 → 无文件产生；勾选后新建/切换/关闭标签页、执行查询 → `%APPDATA%\AxialSqlTools\tab-history\` 出现当天 JSONL
2. **内容**：在标签页输入草稿但不执行 → 切换/关闭标签页后记录中包含全文；内容未变时出现 `Content=null` 记录
3. **执行**：执行查询后产生 Executed 记录，Data Source/Database 与连接一致
4. **UI**：Tab History 窗口筛选（日期/服务器/关键词）、选中看全文、复制、打开文件均正常
5. **兼容**：snippets / IntelliSense 开关任意组合下功能正常；历史文件损坏行不影响加载
6. **回归**：Query History 原有执行历史记录不受影响
