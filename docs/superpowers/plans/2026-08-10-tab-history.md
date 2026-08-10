# Tab History（标签页历史 + SQL 内容快照）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 SSMS 22 扩展 AxialSqlTools 中新增「Tab History」功能：记录查询标签页生命周期事件（Opened/Activated/Closed/Executed）与关键时机的 SQL 全文快照，提供工具窗口检索回看。

**Architecture:** 独立 `TabHistory/` 模块（平行于 `QueryHistory/`）。Package 已挂载的 Window 事件 + 执行完成回调中调用静态采集器 `TabHistoryRecorder`，入队后由 `TabHistoryStore` 后台写 JSONL（按天分文件）；工具窗口 `TabHistoryWindow` 读文件 + 筛选展示。设置走 `UiSettingsStore`（settings.json），默认关闭。

**Tech Stack:** C# / WPF / VSSDK（SSMS 22, amd64, .NET Framework 4.7.2）、Newtonsoft.Json、NLog。设计文档：`docs/superpowers/specs/2026-08-10-tab-history-design.md`。

## Global Constraints

- 传统非 SDK csproj **不会自动包含新文件**：每个新增 `.cs` / `.xaml` 必须手工登记 `AxialSqlTools.csproj`（Compile / Page，参照 QueryHistory 条目）
- 设置一律走 `%APPDATA%\AxialSqlTools\settings.json`（`UiSettingsStore`），**不写入注册表**
- 所有 UI 文案加 `Tag="loc:xxx"` 并新增 `Strings.resx` + `Strings.zh-Hans.resx` 键（代码用 `Strings.Get("Key")`，无需改 Designer.cs）
- CommandId 用 `4156`；CommandSet GUID 沿用 `45457e02-6dec-4a4d-ab22-c9ee126d23c5`
- 事件采集在 UI 线程；写盘走后台队列，不得阻塞 UI；采集失败静默降级并记日志，不拖垮 Package
- 构建验证命令（每任务末执行）：
  `powershell -ExecutionPolicy Bypass -File "D:\IntelliJ-IDEA\github-workspace\AxialSqlTools\skills\axial-build-release\scripts\pack-release.ps1" -Configuration Release`
  预期：构建成功，`AxialSqlTools/bin/Release/AxialSqlTools.vsix` 生成
- **执行修正（2026-08-10）**：本机 Debug 配置下 WPF wpftmp 临时工程找不到 `obj\Debug\*.g.cs`（Release 正常、dev-reload.ps1 亦默认 Release），故所有任务验证一律用 Release 配置
- 安装到 SSMS 验证（最终任务）：`powershell -ExecutionPolicy Bypass -File "D:\IntelliJ-IDEA\github-workspace\AxialSqlTools\tools\dev-reload.ps1"`（默认 Release）

---

### Task 1: 设置后端 — UiSettingsStore 增加 TabHistorySettings + UserConfigPaths 目录

**Files:**
- Modify: `AxialSqlTools/Modules/UserConfigPaths.cs:25`（QueryHistoryDirectory 后加一行）
- Modify: `AxialSqlTools/Modules/UiSettingsStore.cs`（UiSettings 类 + 静态方法 + Normalize + Clone）

**Interfaces:**
- Consumes: 无
- Produces: `UserConfigPaths.TabHistoryDirectory`（string）；`UiSettingsStore.GetTabHistoryEnabled()`（bool，默认 false）；`UiSettingsStore.SaveTabHistoryEnabled(bool)`；`UiSettingsStore.GetTabHistoryRetentionDays()`（int，默认 90）；`UiSettingsStore.SaveTabHistoryRetentionDays(int)`

- [ ] **Step 1: UserConfigPaths 增加目录属性**

在 `UserConfigPaths.cs` 的 `QueryHistoryDirectory` 行后加：

```csharp
        public static string QueryHistoryDirectory => Path.Combine(Root, "query-history");
        public static string TabHistoryDirectory => Path.Combine(Root, "tab-history");
```

- [ ] **Step 2: UiSettings 增加 TabHistory 节点**

在 `UiSettings.cs` 的 `IntelliSense` 属性后加：

```csharp
        /// <summary>Tab History 设置节点。null 兼容旧 settings.json。</summary>
        [JsonProperty("tabHistory")]
        public TabHistorySettings TabHistory { get; set; }
```

在 `UiSettings` 类末尾（`}` 前）加：

```csharp
    public sealed class TabHistorySettings
    {
        /// <summary>是否记录标签页历史。默认关闭。</summary>
        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = false;

        /// <summary>历史文件保留天数；0 = 不清理。默认 90。</summary>
        [JsonProperty("retentionDays")]
        public int RetentionDays { get; set; } = 90;
    }
```

- [ ] **Step 3: UiSettingsStore 增加读写方法与 Normalize/Clone 同步**

在 `SaveIntelliSenseSettings` 方法后加：

```csharp
        public static TabHistorySettings GetTabHistorySettings()
        {
            var settings = Load();
            return settings.TabHistory ?? new TabHistorySettings();
        }

        public static bool GetTabHistoryEnabled()
        {
            return GetTabHistorySettings().Enabled;
        }

        public static void SaveTabHistoryEnabled(bool enabled)
        {
            var settings = Load();
            if (settings.TabHistory == null) settings.TabHistory = new TabHistorySettings();
            settings.TabHistory.Enabled = enabled;
            Save(settings);
        }

        public static int GetTabHistoryRetentionDays()
        {
            var s = GetTabHistorySettings();
            if (s.RetentionDays < 0) return 0;
            if (s.RetentionDays > 3650) return 3650;
            return s.RetentionDays;
        }

        public static void SaveTabHistoryRetentionDays(int retentionDays)
        {
            if (retentionDays < 0) retentionDays = 0;
            if (retentionDays > 3650) retentionDays = 3650;
            var settings = Load();
            if (settings.TabHistory == null) settings.TabHistory = new TabHistorySettings();
            settings.TabHistory.RetentionDays = retentionDays;
            Save(settings);
        }
```

`Normalize` 中 `IntelliSense` null 处理块后加：

```csharp
            // TabHistory 节点缺失时给默认值，保证读写安全。
            if (settings.TabHistory == null)
            {
                settings.TabHistory = new TabHistorySettings();
            }
```

`Clone` 方法改为：

```csharp
        private static UiSettings Clone(UiSettings source)
        {
            return new UiSettings
            {
                UiLanguage = source.UiLanguage,
                IntelliSense = source.IntelliSense ?? new IntelliSenseSettings(),
                TabHistory = source.TabHistory ?? new TabHistorySettings()
            };
        }
```

- [ ] **Step 4: 构建验证**

Run: 构建命令（见 Global Constraints）
Expected: BUILD SUCCEEDED，无编译错误

- [ ] **Step 5: Commit**

```bash
git add AxialSqlTools/Modules/UserConfigPaths.cs AxialSqlTools/Modules/UiSettingsStore.cs
git commit -m "feat: add TabHistory settings backend (enabled + retention days)"
```

---

### Task 2: 数据模型与存储 — TabHistoryRecord + TabHistoryStore

**Files:**
- Create: `AxialSqlTools/TabHistory/TabHistoryRecord.cs`
- Create: `AxialSqlTools/TabHistory/TabHistoryStore.cs`
- Modify: `AxialSqlTools/AxialSqlTools.csproj:210`（QueryHistory 条目后加 Compile）

**Interfaces:**
- Consumes: `UserConfigPaths.TabHistoryDirectory`、`UiSettingsStore.GetTabHistoryRetentionDays()`、`AxialSqlToolsPackage._logger`（public static Logger）
- Produces: `TabHistoryEventType` 枚举（Opened/Activated/Closed/Executed）；`TabHistoryRecord` 类（Id/Timestamp/EventType/DocumentName/DocumentPath/DataSource/DatabaseName/Content/ContentHash/CharCount/ContentShort）；`TabHistoryStore.Enqueue(TabHistoryRecord)`、`LoadRecent(int max = 1000)` → `List<TabHistoryRecord>`、`CleanupOldFiles()`

- [ ] **Step 1: 写 TabHistoryRecord.cs**

```csharp
using Newtonsoft.Json;
using System;

namespace AxialSqlTools
{
    public enum TabHistoryEventType
    {
        Opened,
        Activated,
        Closed,
        Executed
    }

    public class TabHistoryRecord
    {
        [JsonIgnore]
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public TabHistoryEventType EventType { get; set; }
        public string DocumentName { get; set; }
        public string DocumentPath { get; set; }
        public string DataSource { get; set; }
        public string DatabaseName { get; set; }
        public string Content { get; set; }
        public string ContentHash { get; set; }
        public int CharCount { get; set; }
        [JsonIgnore]
        public string ContentShort { get; set; }
    }
}
```

- [ ] **Step 2: 写 TabHistoryStore.cs**

```csharp
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AxialSqlTools
{
    /// <summary>
    /// Tab History 存储层：后台队列写 JSONL（按天分文件），支持读取与按保留天数清理。
    /// </summary>
    public static class TabHistoryStore
    {
        private static readonly ConcurrentQueue<TabHistoryRecord> WriteQueue = new ConcurrentQueue<TabHistoryRecord>();
        private static readonly object FileSyncRoot = new object();
        private static int _workerRunning;

        public static void Enqueue(TabHistoryRecord record)
        {
            if (record == null) return;
            WriteQueue.Enqueue(record);
            if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
            {
                _ = Task.Run(() => ProcessQueueAsync());
            }
        }

        private static void ProcessQueueAsync()
        {
            try
            {
                while (WriteQueue.TryDequeue(out TabHistoryRecord record))
                {
                    try
                    {
                        AppendLine(record);
                    }
                    catch (Exception ex)
                    {
                        AxialSqlToolsPackage._logger?.Error(ex, "[TabHistory] append failed");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _workerRunning, 0);
                // 写盘期间又有入队则再跑一轮，避免漏写
                if (!WriteQueue.IsEmpty &&
                    Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
                {
                    _ = Task.Run(() => ProcessQueueAsync());
                }
            }
        }

        private static void AppendLine(TabHistoryRecord record)
        {
            string folder = UserConfigPaths.TabHistoryDirectory;
            Directory.CreateDirectory(folder);
            string fileName = $"tab-history-{DateTime.Now:yyyy-MM-dd}.jsonl";
            string filePath = Path.Combine(folder, fileName);
            string json = JsonConvert.SerializeObject(record);
            lock (FileSyncRoot)
            {
                File.AppendAllText(filePath, json + Environment.NewLine);
            }
        }

        public static List<TabHistoryRecord> LoadRecent(int max = 1000)
        {
            var records = new List<TabHistoryRecord>();
            string folder = UserConfigPaths.TabHistoryDirectory;
            if (!Directory.Exists(folder)) return records;

            foreach (string filePath in Directory.GetFiles(folder, "tab-history-*.jsonl", SearchOption.TopDirectoryOnly))
            {
                foreach (string line in File.ReadLines(filePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = JsonConvert.DeserializeObject<TabHistoryRecord>(line);
                        if (rec == null) continue;
                        rec.ContentShort = BuildShortText(rec.Content);
                        records.Add(rec);
                    }
                    catch
                    {
                        // 忽略损坏行，继续
                    }
                }
            }

            return records
                .OrderByDescending(r => r.Timestamp)
                .Take(max)
                .ToList();
        }

        public static void CleanupOldFiles()
        {
            try
            {
                int retentionDays = UiSettingsStore.GetTabHistoryRetentionDays();
                if (retentionDays <= 0) return;

                string folder = UserConfigPaths.TabHistoryDirectory;
                if (!Directory.Exists(folder)) return;

                DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
                foreach (string filePath in Directory.GetFiles(folder, "tab-history-*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (File.GetLastWriteTime(filePath) < cutoff)
                        {
                            File.Delete(filePath);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Error(ex, "[TabHistory] cleanup failed");
            }
        }

        private static string BuildShortText(string content)
        {
            content = content ?? string.Empty;
            return content.Length > 100 ? content.Substring(0, 100) : content;
        }
    }
}
```

- [ ] **Step 3: csproj 登记 Compile**

在 `AxialSqlTools.csproj` 的 `<Compile Include="QueryHistory\RelayCommand.cs" />` 后加：

```xml
    <Compile Include="TabHistory\TabHistoryRecord.cs" />
    <Compile Include="TabHistory\TabHistoryStore.cs" />
```

- [ ] **Step 4: 构建验证**

Run: 构建命令
Expected: BUILD SUCCEEDED

- [ ] **Step 5: Commit**

```bash
git add AxialSqlTools/TabHistory/TabHistoryRecord.cs AxialSqlTools/TabHistory/TabHistoryStore.cs AxialSqlTools/AxialSqlTools.csproj
git commit -m "feat: add TabHistory record model and JSONL store"
```

---

### Task 3: 编辑器文本 helper — ScriptFactoryAccess.GetQueryWindowText

**Files:**
- Modify: `AxialSqlTools/Modules/ScriptFactoryAccess.cs:831`（GetActiveQueryWindowText 后加）

**Interfaces:**
- Consumes: 无
- Produces: `ScriptFactoryAccess.GetQueryWindowText(EnvDTE.Window window)` → string（指定窗口全文，失败返回空串）

- [ ] **Step 1: 在 GetActiveQueryWindowText 方法后新增重载**

```csharp
        public static string GetQueryWindowText(EnvDTE.Window window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (window == null) return string.Empty;
            try
            {
                if (window.Document == null) return string.Empty;
                TextDocument doc = window.Document.Object("TextDocument") as TextDocument;
                if (doc == null) return string.Empty;
                EditPoint start = doc.StartPoint.CreateEditPoint();
                return start.GetText(doc.EndPoint);
            }
            catch
            {
                return string.Empty;
            }
        }
```

- [ ] **Step 2: 构建验证**

Run: 构建命令
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```bash
git add AxialSqlTools/Modules/ScriptFactoryAccess.cs
git commit -m "feat: add ScriptFactoryAccess.GetQueryWindowText(window) helper"
```

---

### Task 4: 采集器 — TabHistoryRecorder

**Files:**
- Create: `AxialSqlTools/TabHistory/TabHistoryRecorder.cs`
- Modify: `AxialSqlTools/AxialSqlTools.csproj`（TabHistory 条目区加 Compile）

**Interfaces:**
- Consumes: `UiSettingsStore.GetTabHistoryEnabled()`、`ScriptFactoryAccess.GetQueryWindowText(EnvDTE.Window)`、`ScriptFactoryAccess.GetCurrentConnectionInfo()`、`TabHistoryStore.Enqueue`、`AxialSqlToolsPackage._logger`
- Produces: `TabHistoryRecorder.RecordWindowEvent(TabHistoryEventType eventType, EnvDTE.Window window)`；`TabHistoryRecorder.RecordExecuted(string content, string dataSource, string database)`

- [ ] **Step 1: 写 TabHistoryRecorder.cs**

```csharp
using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AxialSqlTools
{
    /// <summary>
    /// Tab History 采集入口。在 Package 已挂载的窗口事件与执行完成回调中调用；
    /// 内部做内容哈希去重（同一文档内容未变时 Content 存 null）。
    /// </summary>
    public static class TabHistoryRecorder
    {
        private static readonly ConcurrentDictionary<string, string> LastContentHashes =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void RecordWindowEvent(TabHistoryEventType eventType, EnvDTE.Window window)
        {
            try
            {
                if (!UiSettingsStore.GetTabHistoryEnabled()) return;
                if (window == null) return;
                if (!IsDocumentWindow(window)) return;

                string documentName = GetDocumentName(window);
                if (string.IsNullOrEmpty(documentName)) return;

                string content = ScriptFactoryAccess.GetQueryWindowText(window);
                string documentKey = GetDocumentKey(documentName, GetDocumentPath(window));
                string hash = ComputeHash(content);

                bool contentChanged = true;
                if (LastContentHashes.TryGetValue(documentKey, out string previousHash) &&
                    string.Equals(previousHash, hash, StringComparison.Ordinal))
                {
                    contentChanged = false;
                }
                LastContentHashes[documentKey] = hash;

                var record = new TabHistoryRecord
                {
                    Timestamp = DateTime.Now,
                    EventType = eventType,
                    DocumentName = documentName,
                    DocumentPath = GetDocumentPath(window),
                    DataSource = TryGetActiveDataSource(),
                    DatabaseName = TryGetActiveDatabase(),
                    Content = contentChanged ? content : null,
                    ContentHash = hash,
                    CharCount = contentChanged ? (content?.Length ?? 0) : 0
                };

                TabHistoryStore.Enqueue(record);
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Warn(ex, "[TabHistory] record window event failed");
            }
        }

        public static void RecordExecuted(string content, string dataSource, string database)
        {
            try
            {
                if (!UiSettingsStore.GetTabHistoryEnabled()) return;

                // Executed 不参与内容去重：执行内容必须保留
                string documentName = TryGetActiveDocumentName();
                string documentPath = TryGetActiveDocumentPath();
                LastContentHashes[GetDocumentKey(documentName, documentPath)] = ComputeHash(content);

                var record = new TabHistoryRecord
                {
                    Timestamp = DateTime.Now,
                    EventType = TabHistoryEventType.Executed,
                    DocumentName = documentName,
                    DocumentPath = documentPath,
                    DataSource = dataSource,
                    DatabaseName = database,
                    Content = content,
                    ContentHash = ComputeHash(content),
                    CharCount = content?.Length ?? 0
                };

                TabHistoryStore.Enqueue(record);
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Warn(ex, "[TabHistory] record executed failed");
            }
        }

        private static bool IsDocumentWindow(EnvDTE.Window window)
        {
            try
            {
                // 仅记录文档窗口（SQL 查询编辑器 Kind == "Document"），跳过工具窗/对象资源管理器等
                return string.Equals(window.Kind, "Document", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string GetDocumentName(EnvDTE.Window window)
        {
            try
            {
                if (window.Document != null && !string.IsNullOrEmpty(window.Document.Name))
                {
                    return window.Document.Name;
                }
            }
            catch
            {
            }
            try
            {
                return window.Caption ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetDocumentPath(EnvDTE.Window window)
        {
            try
            {
                return window.Document?.FullName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetDocumentKey(string documentName, string documentPath)
        {
            string key = string.IsNullOrEmpty(documentPath) ? documentName : documentPath;
            return string.IsNullOrEmpty(key) ? "(unknown)" : key;
        }

        private static string TryGetActiveDataSource()
        {
            try
            {
                var info = ScriptFactoryAccess.GetCurrentConnectionInfo();
                return info?.ServerName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TryGetActiveDatabase()
        {
            try
            {
                var info = ScriptFactoryAccess.GetCurrentConnectionInfo();
                return info?.Database ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TryGetActiveDocumentName()
        {
            try
            {
                var active = ServiceCache.ExtensibilityModel?.ActiveWindow;
                if (active == null) return string.Empty;
                return GetDocumentName(active);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TryGetActiveDocumentPath()
        {
            try
            {
                var active = ServiceCache.ExtensibilityModel?.ActiveWindow;
                if (active == null) return string.Empty;
                return GetDocumentPath(active);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ComputeHash(string content)
        {
            content = content ?? string.Empty;
            using (var sha1 = SHA1.Create())
            {
                byte[] bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(content));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
```

注意：`ConnectionInfo` 类（ScriptFactoryAccess.cs:23）已确认含 `ServerName` 与 `Database` 属性；`ServiceCache.ExtensibilityModel?.ActiveWindow` 与 Package 现有用法一致（AxialSqlToolsPackage.cs:521）。

- [ ] **Step 2: csproj 登记 Compile**

在 `<Compile Include="TabHistory\TabHistoryStore.cs" />` 后加：

```xml
    <Compile Include="TabHistory\TabHistoryRecorder.cs" />
```

- [ ] **Step 3: 构建验证**

Run: 构建命令
Expected: BUILD SUCCEEDED（若 ConnectionInfo 属性名不符会在此暴露，修正后重跑）

- [ ] **Step 4: Commit**

```bash
git add AxialSqlTools/TabHistory/TabHistoryRecorder.cs AxialSqlTools/AxialSqlTools.csproj
git commit -m "feat: add TabHistoryRecorder with content hash dedup"
```

---

### Task 5: 工具窗口 ViewModel 与控件 — TabHistoryViewModel + TabHistoryWindowControl

**Files:**
- Create: `AxialSqlTools/TabHistory/TabHistoryViewModel.cs`
- Create: `AxialSqlTools/TabHistory/TabHistoryWindowControl.xaml`
- Create: `AxialSqlTools/TabHistory/TabHistoryWindowControl.xaml.cs`
- Modify: `AxialSqlTools/AxialSqlTools.csproj`（TabHistory 条目区加 Compile + Page）

**Interfaces:**
- Consumes: `TabHistoryStore.LoadRecent`、`RelayCommand`（QueryHistory 目录已有）、`Strings.Get`、`UiLocalization.Apply`、`ToolWindowThemeController`/`ToolWindowThemeResources`（Modules/ToolWindowThemeSupport.cs）
- Produces: `TabHistoryViewModel`（TabHistoryRecords、SelectedRecord、FilterFromDate/FilterToDate/FilterServer/FilterText、RefreshCommand、ClearFilterCommand）；`TabHistoryWindowControl`（UserControl，x:Class=AxialSqlTools.TabHistoryWindowControl）

- [ ] **Step 1: 写 TabHistoryViewModel.cs**

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace AxialSqlTools
{
    public class TabHistoryViewModel : INotifyPropertyChanged
    {
        private List<TabHistoryRecord> _allRecords;

        public ObservableCollection<TabHistoryRecord> TabHistoryRecords { get; }

        private TabHistoryRecord _selectedRecord;
        public TabHistoryRecord SelectedRecord
        {
            get => _selectedRecord;
            set
            {
                if (_selectedRecord != value)
                {
                    _selectedRecord = value;
                    OnPropertyChanged(nameof(SelectedRecord));
                }
            }
        }

        private DateTime? _filterFromDate;
        public DateTime? FilterFromDate
        {
            get => _filterFromDate;
            set { if (_filterFromDate != value) { _filterFromDate = value; OnPropertyChanged(nameof(FilterFromDate)); } }
        }

        private DateTime? _filterToDate;
        public DateTime? FilterToDate
        {
            get => _filterToDate;
            set { if (_filterToDate != value) { _filterToDate = value; OnPropertyChanged(nameof(FilterToDate)); } }
        }

        private string _filterServer = string.Empty;
        public string FilterServer
        {
            get => _filterServer;
            set { if (_filterServer != value) { _filterServer = value; OnPropertyChanged(nameof(FilterServer)); } }
        }

        private string _filterText = string.Empty;
        public string FilterText
        {
            get => _filterText;
            set { if (_filterText != value) { _filterText = value; OnPropertyChanged(nameof(FilterText)); } }
        }

        public ICommand RefreshCommand { get; }
        public ICommand ClearFilterCommand { get; }

        public TabHistoryViewModel()
        {
            TabHistoryRecords = new ObservableCollection<TabHistoryRecord>();
            RefreshCommand = new RelayCommand(RefreshData);
            ClearFilterCommand = new RelayCommand(ClearAllFilters);
            RefreshData();
        }

        private void ClearAllFilters()
        {
            FilterFromDate = null;
            FilterToDate = null;
            FilterServer = string.Empty;
            FilterText = string.Empty;
            RefreshData();
        }

        public void RefreshData()
        {
            _allRecords = TabHistoryStore.LoadRecent(1000);

            IEnumerable<TabHistoryRecord> filtered = _allRecords;
            if (FilterFromDate.HasValue)
            {
                filtered = filtered.Where(r => r.Timestamp >= FilterFromDate.Value.Date);
            }
            if (FilterToDate.HasValue)
            {
                DateTime endOfDay = FilterToDate.Value.Date.AddDays(1).AddSeconds(-1);
                filtered = filtered.Where(r => r.Timestamp <= endOfDay);
            }
            if (!string.IsNullOrWhiteSpace(FilterServer))
            {
                filtered = filtered.Where(r =>
                    (r.DataSource ?? string.Empty).IndexOf(FilterServer, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (!string.IsNullOrWhiteSpace(FilterText))
            {
                filtered = filtered.Where(r =>
                    (r.DocumentName ?? string.Empty).IndexOf(FilterText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Content ?? string.Empty).IndexOf(FilterText, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            TabHistoryRecords.Clear();
            var ordered = filtered.OrderByDescending(r => r.Timestamp).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Id = i + 1;
                TabHistoryRecords.Add(ordered[i]);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}
```

- [ ] **Step 2: 写 TabHistoryWindowControl.xaml**

```xml
<UserControl x:Class="AxialSqlTools.TabHistoryWindowControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             mc:Ignorable="d"
             d:DesignHeight="800"
             d:DesignWidth="1000"
             Background="{DynamicResource AxialThemeBackgroundBrush}"
             Foreground="{DynamicResource AxialThemeForegroundBrush}">

    <UserControl.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="/AxialSqlTools;component/Themes/SharedToolWindowTheme.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </UserControl.Resources>

    <DockPanel Background="{DynamicResource AxialThemeBackgroundBrush}">
        <StackPanel DockPanel.Dock="Top"
                    Orientation="Horizontal"
                    Background="{DynamicResource AxialThemeHeaderBackgroundBrush}">
            <TextBlock Margin="5"
                       HorizontalAlignment="Center"
                       VerticalAlignment="Top"
                       FontWeight="Bold"
                       FontSize="18"
                       Tag="loc:TabHistory_HeaderTitle"
                       Text="Tab History" />
        </StackPanel>

        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>

            <!-- Row 0: FILTER CONTROLS -->
            <StackPanel Grid.Row="0" Margin="5" Orientation="Horizontal" VerticalAlignment="Center">
                <Label Content="From:" Tag="loc:TabHistory_From" Width="40" VerticalAlignment="Center" Margin="0,0,4,0"/>
                <DatePicker Width="132"
                            SelectedDate="{Binding FilterFromDate, Mode=TwoWay}"
                            SelectedDateChanged="DatePicker_SelectedDateChanged"/>
                <Label Content="To:" Tag="loc:TabHistory_To" Width="30" VerticalAlignment="Center" Margin="16,0,4,0"/>
                <DatePicker Width="132"
                            SelectedDate="{Binding FilterToDate, Mode=TwoWay}"
                            SelectedDateChanged="DatePicker_SelectedDateChanged"/>
                <Label Content="Server:" Tag="loc:TabHistory_Server" Width="60" VerticalAlignment="Center" Margin="16,0,4,0"/>
                <TextBox Width="140"
                         Text="{Binding FilterServer, UpdateSourceTrigger=PropertyChanged}"
                         KeyDown="TextBox_KeyDown"
                         VerticalContentAlignment="Center"/>
                <Label Content="Find:" Tag="loc:TabHistory_Find" Width="45" VerticalAlignment="Center" Margin="16,0,4,0"/>
                <TextBox Width="180"
                         Text="{Binding FilterText, UpdateSourceTrigger=PropertyChanged}"
                         KeyDown="TextBox_KeyDown"
                         VerticalContentAlignment="Center"/>
                <Button Content="Refresh"
                        Tag="loc:Common_Refresh"
                        Margin="16,0,4,0"
                        Width="90"
                        Style="{DynamicResource AxialPrimaryButtonStyle}"
                        Command="{Binding RefreshCommand}"/>
                <Button Content="Clear"
                        Tag="loc:Common_Clear"
                        Width="90"
                        Command="{Binding ClearFilterCommand}"/>
            </StackPanel>

            <!-- Row 1: DATA GRID -->
            <Grid Grid.Row="1">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                </Grid.RowDefinitions>
                <TextBlock Grid.Row="0"
                           Margin="5,0,0,6"
                           FontWeight="Bold"
                           Text="{Binding TabHistoryRecords.Count, StringFormat={}{0} Record(s)}"/>
                <DataGrid x:Name="DataGrid_TabHistory"
                          Grid.Row="1"
                          Margin="5"
                          IsReadOnly="True"
                          ItemsSource="{Binding TabHistoryRecords}"
                          SelectedItem="{Binding SelectedRecord, Mode=TwoWay}"
                          AutoGenerateColumns="False"
                          SelectionMode="Single"
                          Style="{DynamicResource AxialThemedDataGridStyle}"
                          ColumnHeaderStyle="{DynamicResource AxialThemedDataGridColumnHeaderStyle}"
                          CellStyle="{DynamicResource AxialThemedDataGridCellStyle}"
                          RowStyle="{DynamicResource AxialThemedDataGridRowStyle}">
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="Time" Binding="{Binding Timestamp, StringFormat=yyyy-MM-dd HH:mm:ss}" Width="150"/>
                        <DataGridTextColumn Header="Event" Binding="{Binding EventType}" Width="Auto"/>
                        <DataGridTextColumn Header="Document" Binding="{Binding DocumentName}" Width="160"/>
                        <DataGridTextColumn Header="Server" Binding="{Binding DataSource}" Width="140"/>
                        <DataGridTextColumn Header="Database" Binding="{Binding DatabaseName}" Width="140"/>
                        <DataGridTextColumn Header="Chars" Binding="{Binding CharCount}" Width="Auto"/>
                        <DataGridTextColumn Header="Preview" Binding="{Binding ContentShort}" Width="*" MaxWidth="600">
                            <DataGridTextColumn.ElementStyle>
                                <Style TargetType="TextBlock">
                                    <Setter Property="Foreground" Value="{DynamicResource AxialThemeForegroundBrush}"/>
                                    <Setter Property="TextTrimming" Value="CharacterEllipsis"/>
                                    <Setter Property="TextWrapping" Value="Wrap"/>
                                    <Setter Property="LineHeight" Value="16"/>
                                    <Setter Property="MaxHeight" Value="32"/>
                                </Style>
                            </DataGridTextColumn.ElementStyle>
                        </DataGridTextColumn>
                    </DataGrid.Columns>
                </DataGrid>
            </Grid>

            <!-- Row 2: FULL CONTENT VIEWER -->
            <DockPanel Grid.Row="2" Margin="5">
                <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,4">
                    <Button Content="Copy All" Tag="loc:TabHistory_CopyAll" Width="90" Click="CopyAll_Click"/>
                    <Button Content="Open File" Tag="loc:TabHistory_OpenFile" Width="90" Margin="8,0,0,0" Click="OpenFile_Click"/>
                </StackPanel>
                <TextBox x:Name="FullContentBox"
                         Text="{Binding SelectedRecord.Content}"
                         IsReadOnly="True"
                         TextWrapping="Wrap"
                         AcceptsReturn="True"
                         VerticalScrollBarVisibility="Auto"
                         FontFamily="Consolas"
                         FontSize="12"/>
            </DockPanel>
        </Grid>
    </DockPanel>
</UserControl>
```

- [ ] **Step 3: 写 TabHistoryWindowControl.xaml.cs**

```csharp
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AxialSqlTools.Properties;

namespace AxialSqlTools
{
    public partial class TabHistoryWindowControl : UserControl
    {
        private readonly ToolWindowThemeController _themeController;

        public TabHistoryWindowControl()
        {
            InitializeComponent();
            UiLocalization.Apply(this);
            LocalizeDataGridColumns();
            _themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);
            DataContext = new TabHistoryViewModel();
        }

        private void LocalizeDataGridColumns()
        {
            DataGrid_TabHistory.Columns[0].Header = Strings.Get("TabHistory_ColTimestamp");
            DataGrid_TabHistory.Columns[1].Header = Strings.Get("TabHistory_ColEvent");
            DataGrid_TabHistory.Columns[2].Header = Strings.Get("TabHistory_ColDocument");
            DataGrid_TabHistory.Columns[3].Header = Strings.Get("TabHistory_ColServer");
            DataGrid_TabHistory.Columns[4].Header = Strings.Get("TabHistory_ColDatabase");
            DataGrid_TabHistory.Columns[5].Header = Strings.Get("TabHistory_ColChars");
            DataGrid_TabHistory.Columns[6].Header = Strings.Get("TabHistory_ColPreview");
        }

        private void ApplyThemeBrushResources()
        {
            ToolWindowThemeResources.ApplySharedTheme(this);
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (DataContext is TabHistoryViewModel vm && vm.RefreshCommand.CanExecute(null))
                {
                    vm.RefreshCommand.Execute(null);
                }
                e.Handled = true;
            }
        }

        private void DatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is TabHistoryViewModel vm)
            {
                vm.RefreshCommand.Execute(null);
            }
        }

        private void CopyAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is TabHistoryViewModel vm && vm.SelectedRecord != null)
                {
                    string text = vm.SelectedRecord.Content ?? string.Empty;
                    if (text.Length == 0)
                    {
                        text = Strings.Get("TabHistory_ContentUnchanged");
                    }
                    Clipboard.SetText(text);
                }
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Warn(ex, "[TabHistory] copy failed");
            }
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is TabHistoryViewModel vm && vm.SelectedRecord != null &&
                    !string.IsNullOrEmpty(vm.SelectedRecord.DocumentPath) &&
                    System.IO.File.Exists(vm.SelectedRecord.DocumentPath))
                {
                    Process.Start(new ProcessStartInfo(vm.SelectedRecord.DocumentPath)
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Warn(ex, "[TabHistory] open file failed");
            }
        }
    }
}
```

- [ ] **Step 4: csproj 登记 Compile + Page**

在 `<Compile Include="TabHistory\TabHistoryRecorder.cs" />` 后加：

```xml
    <Compile Include="TabHistory\TabHistoryViewModel.cs" />
    <Compile Include="TabHistory\TabHistoryWindow.cs" />
    <Compile Include="TabHistory\TabHistoryWindowCommand.cs" />
    <Compile Include="TabHistory\TabHistoryWindowControl.xaml.cs">
      <DependentUpon>TabHistoryWindowControl.xaml</DependentUpon>
    </Compile>
```

在 `<Page Include="QueryHistory\QueryHistoryWindowControl.xaml">...` 条目后加：

```xml
    <Page Include="TabHistory\TabHistoryWindowControl.xaml">
      <SubType>Designer</SubType>
      <Generator>MSBuild:Compile</Generator>
    </Page>
```

注意：本任务 Step 4 提前登记了 Task 6 的 `TabHistoryWindow.cs` / `TabHistoryWindowCommand.cs`（尚未创建会致构建失败）。**本任务构建前先创建这两个占位不可行** —— 正确做法：本任务只登记 ViewModel + Control 的 Compile/Page，Window/Command 的 Compile 留到 Task 6 登记。修正如下：

在 `<Compile Include="TabHistory\TabHistoryRecorder.cs" />` 后只加：

```xml
    <Compile Include="TabHistory\TabHistoryViewModel.cs" />
    <Compile Include="TabHistory\TabHistoryWindowControl.xaml.cs">
      <DependentUpon>TabHistoryWindowControl.xaml</DependentUpon>
    </Compile>
```

- [ ] **Step 5: 构建验证**

Run: 构建命令
Expected: BUILD SUCCEEDED

- [ ] **Step 6: Commit**

```bash
git add AxialSqlTools/TabHistory/TabHistoryViewModel.cs AxialSqlTools/TabHistory/TabHistoryWindowControl.xaml AxialSqlTools/TabHistory/TabHistoryWindowControl.xaml.cs AxialSqlTools/AxialSqlTools.csproj
git commit -m "feat: add TabHistory tool window view model and control"
```

---

### Task 6: 工具窗口壳与命令 — TabHistoryWindow + TabHistoryWindowCommand + vsct + MenuTextLocalizer

**Files:**
- Create: `AxialSqlTools/TabHistory/TabHistoryWindow.cs`
- Create: `AxialSqlTools/TabHistory/TabHistoryWindowCommand.cs`
- Modify: `AxialSqlTools/AxialSqlToolsPackage.vsct`（按钮 + IDSymbol）
- Modify: `AxialSqlTools/Modules/MenuTextLocalizer.cs:32`（Entries 加一行）
- Modify: `AxialSqlTools/AxialSqlTools.csproj`（补 Window/Command Compile）

**Interfaces:**
- Consumes: `Strings.Get("Menu_TabHistory")`、`TabHistoryWindowControl`
- Produces: `TabHistoryWindow`（ToolWindowPane，Guid `9e4f2a81-5b1c-4d3e-8f60-2a7c9d4e5f61`）；`TabHistoryWindowCommand`（CommandId=4156）；vsct `AxialTabHistoryCommand`=4156

- [ ] **Step 1: 写 TabHistoryWindow.cs**

```csharp
using AxialSqlTools.Properties;
using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace AxialSqlTools
{
    [Guid("9e4f2a81-5b1c-4d3e-8f60-2a7c9d4e5f61")]
    public class TabHistoryWindow : ToolWindowPane
    {
        public TabHistoryWindow() : base(null)
        {
            this.Caption = Strings.Get("Menu_TabHistory");
            this.Content = new TabHistoryWindowControl();
        }
    }
}
```

- [ ] **Step 2: 写 TabHistoryWindowCommand.cs**

```csharp
using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace AxialSqlTools
{
    internal sealed class TabHistoryWindowCommand
    {
        public const int CommandId = 4156;
        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        private readonly AsyncPackage package;

        private TabHistoryWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static TabHistoryWindowCommand Instance { get; private set; }

        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider => this.package;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService =
                await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new TabHistoryWindowCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            this.package.JoinableTaskFactory.RunAsync(async delegate
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    ToolWindowPane window = await this.package.ShowToolWindowAsync(
                        typeof(TabHistoryWindow), 0, true, this.package.DisposalToken);
                    if ((null == window) || (null == window.Frame))
                    {
                        throw new NotSupportedException("Cannot create tool window");
                    }
                    IVsWindowFrame windowFrame = (IVsWindowFrame)window.Frame;
                    windowFrame.SetProperty((int)__VSFPROPID.VSFPROPID_FrameMode, VSFRAMEMODE.VSFM_MdiChild);
                }
                catch (Exception ex)
                {
                    VsShellUtilities.ShowMessageBox(
                        this.package,
                        "Failed to open Tab History. " + ex.Message,
                        "Axial SQL Tools",
                        OLEMSGICON.OLEMSGICON_CRITICAL,
                        OLEMSGBUTTON.OLEMSGBUTTON_OK,
                        OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
            });
        }
    }
}
```

- [ ] **Step 3: vsct 增加按钮**

在 `AxialSqlToolsPackage.vsct` 的 `AxialQueryHistoryCommand` Button（约 219-226 行）后加：

```xml
      <Button guid="guidAxialSqlToolsPackageCmdSet" id="AxialTabHistoryCommand" priority="0x0001" type="Button">
        <Parent guid="guidAxialSqlToolsPackageCmdSet" id="AxialToolsSubMenuGroup_Script2" />
        <Icon guid="guidQueryHistory" id="bmpQueryHistory" />
        <CommandFlag>IconAndText</CommandFlag>
        <Strings>
          <ButtonText>Tab History</ButtonText>
          <LocCanonicalName>AxialSqlTools.TabHistory</LocCanonicalName>
        </Strings>
      </Button>
```

在 `<IDSymbol name="AxialStatisticsSummaryCommand" value="4152" />` 前加 IDSymbol：

```xml
      <IDSymbol name="AxialTabHistoryCommand" value="4156" />
```

- [ ] **Step 4: MenuTextLocalizer Entries 加一行**

在 `("Query History", "Menu_QueryHistory"),` 后加：

```csharp
            ("Tab History", "Menu_TabHistory"),
```

- [ ] **Step 5: csproj 补 Window/Command Compile**

在 `<Compile Include="TabHistory\TabHistoryWindowControl.xaml.cs">` 条目后加：

```xml
    <Compile Include="TabHistory\TabHistoryWindow.cs" />
    <Compile Include="TabHistory\TabHistoryWindowCommand.cs" />
```

- [ ] **Step 6: 构建验证**

Run: 构建命令
Expected: BUILD SUCCEEDED

- [ ] **Step 7: Commit**

```bash
git add AxialSqlTools/TabHistory/TabHistoryWindow.cs AxialSqlTools/TabHistory/TabHistoryWindowCommand.cs AxialSqlTools/AxialSqlToolsPackage.vsct AxialSqlTools/Modules/MenuTextLocalizer.cs AxialSqlTools/AxialSqlTools.csproj
git commit -m "feat: add TabHistory tool window command and menu entry"
```

---

### Task 7: Package 钩子 — 事件采集 + ToolWindow 注册 + 命令初始化

**Files:**
- Modify: `AxialSqlTools/AxialSqlToolsPackage.cs:65`（ProvideToolWindow 注册）、`:341`（命令初始化）、`:771-792`（WindowCreated）、`:627-695`（WindowActivated）、`:755-767`（WindowClosing）、`:990-1021` 后（执行完成回调）

**Interfaces:**
- Consumes: `TabHistoryRecorder.RecordWindowEvent` / `RecordExecuted`、`TabHistoryWindowCommand.InitializeAsync`、`GridAccess.GetNonPublicField`、`ScriptFactoryAccess.GetActiveQueryWindowText`
- Produces: Package 启动即自动采集 Tab History

- [ ] **Step 1: 注册 ToolWindow + 初始化命令**

在 `[ProvideToolWindow(typeof(QueryHistoryWindow))]` 后加：

```csharp
    [ProvideToolWindow(typeof(TabHistoryWindow))]
```

在 `await QueryHistoryWindowCommand.InitializeAsync(this);` 后加：

```csharp
                await TabHistoryWindowCommand.InitializeAsync(this);
```

- [ ] **Step 2: WindowCreated_Event 记录 Opened**

在 `WindowCreated_Event` 的 try 块开头（`var sqlResultsControl = ...` 之前）加：

```csharp
            TabHistoryRecorder.RecordWindowEvent(TabHistoryEventType.Opened, Window);
```

- [ ] **Step 3: WindowActivated_Event 记录 Activated**

在 `WindowActivated_Event` 中 `if (GotFocus != null)` 的 connection-color try 块之前（约 680 行处）加：

```csharp
            // Tab History: 记录标签激活与当前内容快照
            try
            {
                TabHistoryRecorder.RecordWindowEvent(TabHistoryEventType.Activated, GotFocus);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to record tab activation history.");
            }
```

- [ ] **Step 4: WindowClosing_Event 记录 Closed**

在 `WindowClosing_Event` 的现有 try 块中（`GridAccess.ScheduleReapplyAllTabColors()` 前或后）加：

```csharp
            TabHistoryRecorder.RecordWindowEvent(TabHistoryEventType.Closed, Window);
```

- [ ] **Step 5: 执行完成回调记录 Executed**

在 `SQLResultsControl_ScriptExecutionCompleted` 的 query history 采集 try 块（约 990-1021 行）**之后**加：

```csharp
            // tab history: 执行后记录编辑器全文（含未执行草稿）
            try
            {
                var mConn = GridAccess.GetNonPublicField(QEOLESQLExec, "m_conn");
                string dataSource = string.Empty;
                string database = string.Empty;
                if (mConn != null)
                {
                    try { dataSource = (string)GridAccess.GetProperty(mConn, "DataSource"); } catch { }
                    try { database = (string)GridAccess.GetProperty(mConn, "Database"); } catch { }
                }
                TabHistoryRecorder.RecordExecuted(
                    ScriptFactoryAccess.GetActiveQueryWindowText(),
                    dataSource,
                    database);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred recording tab history after execute");
            }
```

- [ ] **Step 6: 初始化时清理旧文件**

在 `InitializeAsync` 中 `InitializeLogging();` 后加：

```csharp
            try
            {
                TabHistoryStore.CleanupOldFiles();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to clean old tab history files.");
            }
```

- [ ] **Step 7: 构建验证**

Run: 构建命令
Expected: BUILD SUCCEEDED

- [ ] **Step 8: Commit**

```bash
git add AxialSqlTools/AxialSqlToolsPackage.cs
git commit -m "feat: hook tab history recording into package window/execute events"
```

---

### Task 8: 设置窗口 UI — Tab History Tab

**Files:**
- Modify: `AxialSqlTools/WindowSettings/SettingsWindowControl.xaml`（TabItem 加入 Tab 集合）
- Modify: `AxialSqlTools/WindowSettings/SettingsWindowControl.xaml.cs`（LoadSavedSettings + 保存 handler）

**Interfaces:**
- Consumes: `UiSettingsStore.GetTabHistorySettings` / `SaveTabHistoryEnabled` / `SaveTabHistoryRetentionDays`
- Produces: 设置窗口「Tab History」Tab，勾选启用 + 保留天数输入

- [ ] **Step 1: XAML 加 TabItem**

在 `</TabItem>`（TabQueryHistory 结束，约 530 行前）后加：

```xml
            <TabItem x:Name="TabTabHistory" Header="Tab History">
                <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="8">
                    <StackPanel>
                        <TextBlock Margin="5,0,5,10" TextWrapping="Wrap" Opacity="0.85"
                                   Text="记录查询标签页的打开/切换/关闭与执行事件，并保存编辑器中的 SQL 内容快照。"/>
                        <StackPanel Margin="5,0,5,4">
                            <CheckBox x:Name="TabHistoryEnabled"
                                      Content="记录标签页历史（Tab History）"/>
                            <TextBlock Margin="22,2,0,0" Opacity="0.75" FontSize="11"
                                       Text="生效：实时。默认关闭。内容快照保存在 %APPDATA%\AxialSqlTools\tab-history\。"/>
                        </StackPanel>
                        <StackPanel Margin="5,0,5,4"
                                    IsEnabled="{Binding IsChecked, ElementName=TabHistoryEnabled}">
                            <StackPanel Orientation="Horizontal">
                                <TextBlock Text="保留天数（0 = 不清理）:" VerticalAlignment="Center" Margin="0,0,6,0"/>
                                <TextBox x:Name="TabHistoryRetentionDays" Width="60" VerticalAlignment="Center"/>
                            </StackPanel>
                            <TextBlock Margin="0,2,0,0" Opacity="0.75" FontSize="11" Text="生效：重启 SSMS 后清理旧文件。"/>
                        </StackPanel>
                        <Button x:Name="button_SaveTabHistorySettings"
                                Click="Button_SaveTabHistorySettings_Click"
                                Width="80" Height="24" Margin="5,8,0,0"
                                HorizontalAlignment="Left" FontWeight="Bold" Content="保存"/>
                    </StackPanel>
                </ScrollViewer>
            </TabItem>
```

- [ ] **Step 2: code-behind 加载**

在 `LoadSavedSettings` 中 IntelliSense 加载 try 块后加：

```csharp
            // Tab History 设置加载
            try
            {
                var th = UiSettingsStore.GetTabHistorySettings();
                TabHistoryEnabled.IsChecked = th.Enabled;
                TabHistoryRetentionDays.Text = th.RetentionDays.ToString();
            }
            catch
            {
            }
```

- [ ] **Step 3: code-behind 保存 handler**

在 `Button_SaveQueryHistory_Click` 附近加：

```csharp
        private void Button_SaveTabHistorySettings_Click(object sender, RoutedEventArgs e)
        {
            bool enabled = TabHistoryEnabled.IsChecked.GetValueOrDefault();
            UiSettingsStore.SaveTabHistoryEnabled(enabled);
            int retentionDays = int.TryParse(TabHistoryRetentionDays.Text, out var rd) ? rd : 90;
            UiSettingsStore.SaveTabHistoryRetentionDays(retentionDays);
            SavedMessage();
        }
```

- [ ] **Step 4: 构建验证**

Run: 构建命令
Expected: BUILD SUCCEEDED

- [ ] **Step 5: Commit**

```bash
git add AxialSqlTools/WindowSettings/SettingsWindowControl.xaml AxialSqlTools/WindowSettings/SettingsWindowControl.xaml.cs
git commit -m "feat: add Tab History settings tab"
```

---

### Task 9: 本地化字符串 — resx 键

**Files:**
- Modify: `tools/i18n-new-keys.json`
- 运行工具：`tools/append-i18n-keys.ps1`（更新 `Strings.resx` + `Strings.zh-Hans.resx`）

**Interfaces:**
- Consumes: 无
- Produces: 以下键同时存在于 `Strings.resx`（en）与 `Strings.zh-Hans.resx`（zh）：Menu_TabHistory、Settings_TabHistoryEnabled、Settings_TabHistoryRetentionDays、TabHistory_HeaderTitle、TabHistory_From/To/Server/Find、TabHistory_ColTimestamp/ColEvent/ColDocument/ColServer/ColDatabase/ColChars/ColPreview、TabHistory_CopyAll/OpenFile、TabHistory_ContentUnchanged

- [ ] **Step 1: 编辑 i18n-new-keys.json**

将 `tools/i18n-new-keys.json` 内容（先读现有内容，保留已有键）追加以下键：

```json
{
  "Menu_TabHistory": { "en": "Tab History", "zh": "标签页历史" },
  "Settings_TabHistoryEnabled": { "en": "Record tab history (Tab History)", "zh": "记录标签页历史（Tab History）" },
  "Settings_TabHistoryRetentionDays": { "en": "Retention days (0 = never clean):", "zh": "保留天数（0 = 不清理）：" },
  "TabHistory_HeaderTitle": { "en": "Tab History", "zh": "标签页历史" },
  "TabHistory_From": { "en": "From:", "zh": "从：" },
  "TabHistory_To": { "en": "To:", "zh": "至：" },
  "TabHistory_Server": { "en": "Server:", "zh": "服务器：" },
  "TabHistory_Find": { "en": "Find:", "zh": "查找：" },
  "TabHistory_ColTimestamp": { "en": "Time", "zh": "时间" },
  "TabHistory_ColEvent": { "en": "Event", "zh": "事件" },
  "TabHistory_ColDocument": { "en": "Document", "zh": "文档" },
  "TabHistory_ColServer": { "en": "Server", "zh": "服务器" },
  "TabHistory_ColDatabase": { "en": "Database", "zh": "数据库" },
  "TabHistory_ColChars": { "en": "Chars", "zh": "字符数" },
  "TabHistory_ColPreview": { "en": "Preview", "zh": "预览" },
  "TabHistory_CopyAll": { "en": "Copy All", "zh": "复制全文" },
  "TabHistory_OpenFile": { "en": "Open File", "zh": "打开文件" },
  "TabHistory_ContentUnchanged": { "en": "(content unchanged)", "zh": "（内容未变化）" }
}
```

注意：若 JSON 中已有键则跳过（脚本按 name 去重），重复写入无副作用。

- [ ] **Step 2: 运行追加脚本**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File "D:\IntelliJ-IDEA\github-workspace\AxialSqlTools\tools\append-i18n-keys.ps1"
```
Expected: 输出 `Appended EN=18 ZH=18`（数量以实际新增为准）与 `XML OK`

- [ ] **Step 3: 构建验证**

Run: 构建命令
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add AxialSqlTools/Properties/Strings.resx AxialSqlTools/Properties/Strings.zh-Hans.resx tools/i18n-new-keys.json
git commit -m "i18n: add Tab History strings (en/zh-Hans)"
```

---

### Task 10: 端到端安装验证

**Files:**
- 无代码改动

**Interfaces:**
- Consumes: 全部已完成任务产物

- [ ] **Step 1: 完整构建**

Run: 构建命令（Debug）
Expected: BUILD SUCCEEDED，生成 `AxialSqlTools/bin/Debug/AxialSqlTools.vsix`

- [ ] **Step 2: 安装到 SSMS 并重启**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File "D:\IntelliJ-IDEA\github-workspace\AxialSqlTools\tools\dev-reload.ps1" -Configuration Debug
```
Expected: 构建→关闭 SSMS→安装（direct copy 或 VSIXInstaller）→SSMS 重启并恢复之前的查询窗口

- [ ] **Step 3: 手动验证清单**

逐项确认（设置窗口开启 Tab History 后）：
1. 设置 → Tab History 默认不勾选；勾选并保存 → `%APPDATA%\AxialSqlTools\settings.json` 出现 `"tabHistory": {"enabled": true, "retentionDays": 90}`
2. 新建标签页（Ctrl+N）→ 菜单 Axial SQL Tools → Tab History 打开窗口，出现 Event=Opened 记录
3. 在标签页输入 `SELECT 1`（不执行）→ 切换/关闭标签页 → 出现 Activated/Closed 记录且 Content 含 `SELECT 1`
4. 同一标签页内容未变时多次切换 → 出现 Content=null 的记录（UI 显示「（内容未变化）」）
5. 执行查询 → 出现 Event=Executed 记录，Server/Database 与连接一致
6. 工具窗口筛选（日期/服务器/查找）、复制全文、打开文件按钮正常
7. 关闭 Tab History 开关 → 不再产生新记录；打开开关 → 恢复
8. 设置 `retentionDays=2` 且有 3 天前 JSONL → 重启 SSMS 后旧文件被删除；设为 0 → 不删除
9. 回归：Query History 窗口仍正常记录执行历史

- [ ] **Step 4: 若有问题，回到对应任务修复并重跑 Step 1-3；全部通过后无额外提交（改动已在各任务提交）**

---

## 附：实现顺序与依赖

```
Task 1 (设置后端) → Task 2 (模型+存储) → Task 3 (文本 helper) → Task 4 (采集器)
  → Task 5 (ViewModel+Control) → Task 6 (窗口壳+命令+vsct) → Task 7 (Package 钩子)
  → Task 8 (设置 UI) → Task 9 (本地化) → Task 10 (端到端验证)
```

依赖说明：Task 4 依赖 1/2/3；Task 5 依赖 2（Store）；Task 7 依赖 4/5/6；Task 8 依赖 1；Task 10 依赖全部。Task 9（resx 键）可在 Task 5-8 任意时刻之后执行，不影响编译（`Strings.Get` 运行期查键，缺键返回 null 不崩）。
