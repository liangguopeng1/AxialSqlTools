# Quick Search：按服务器跟踪索引状态 + 七天自动刷新

**日期：** 2026-07-27
**状态：** 已确认
**前置：** 2026-07-22-quicksearch-connections-cache-design.md（本地索引初版）

## 目标

1. 刷新索引期间切换服务器时，状态栏必须显示**当前选中服务器**的状态，而不是继续显示被索引服务器的进度。
2. 索引任务允许在**后台继续跑**；切回原服务器时能再次看到它的实时进度；多台服务器可并行各自建索引。
3. 打开快速搜索窗口时检查当前选中服务器的索引缓存：**无索引或索引时间超过 7 天** → 自动后台静默刷新。

## 非目标

- 切换服务器时也做七天检查（只在窗口打开时检查一次，符合需求描述）。
- 索引任务的进度条 / 百分比（仍沿用状态栏文本）。
- 改动索引存储格式与搜索逻辑（`QuickSearchIndexStore` 不变）。

## 现状与问题

`QuickSearchWindowControl.xaml.cs` 中索引刷新用单一字段 `indexCancellationTokenSource` 管理：

- 进度回调 `Progress<string>` 直接写 `TextBlock_IndexStatus.Text`，不判断当前选中的是哪台服务器——A 服务器索引中切到 B，状态栏仍被 A 的进度覆盖。
- 索引完成/失败后无条件 `UpdateIndexStatus(server.ServerName)`（旧服务器），同样覆盖新选中服务器的状态显示。
- 单一取消源导致 A 索引中点击 B 的"刷新索引"实际是取消 A 的任务，两台服务器无法并行建索引。
- 打开窗口时不检查索引新旧，过期索引长期被使用（搜索优先走本地索引）。

## 设计

### 1. 按服务器的索引状态

新增私有嵌套类与字典（仅 UI 线程访问，无需锁）：

```csharp
private sealed class ServerIndexingState
{
    public CancellationTokenSource CancellationSource;
    public string StatusText;   // 最近一次进度文本
    public bool Silent;         // 自动刷新为 true：不弹完成/失败消息框
}

private readonly Dictionary<string, ServerIndexingState> activeIndexingByServer =
    new Dictionary<string, ServerIndexingState>(StringComparer.OrdinalIgnoreCase);
```

替代现有 `indexCancellationTokenSource` 字段。所有访问点（事件处理、`Progress<T>` 回调、await 续体）均在 UI 线程。

### 2. 状态栏显示规则

`UpdateIndexStatus(string serverName)` 改为：

1. 该服务器在 `activeIndexingByServer` 中 → 显示其 `StatusText`（初始为"正在建立索引..."，随后为"正在索引 [db]..."）。
2. 否则读 `meta.json` 显示"索引：{时间}（{数} 个对象）"或"索引：未建立"。

进度回调只更新 `state.StatusText`，然后调用 `RefreshIndexStatusIfSelected(serverName)`——仅当被索引服务器是当前选中服务器时才写状态栏。任务结束（成功/取消/失败）从字典移除后，再调一次同一方法回退到 meta 显示。

### 3. 刷新按钮行为

- `StartIndexing(ConnectionInfo server, bool silent)`：统一入口，登记字典、刷新按钮与状态栏、后台 `await BuildIndexAsync`、结束清理。
- `Button_RefreshIndex_Click`：当前选中服务器在索引中 → 取消其任务；否则 `StartIndexing(server, silent: false)`。
- `UpdateRefreshIndexButton()`：选中服务器在索引中 → 按钮显示"取消"，否则"刷新索引"；在 `LoadDatabasesForSelectedServer`、索引开始/结束时调用。
- 手动刷新（`silent: false`）保留现有的"本地索引已建立"/失败弹窗；取消不弹窗。
- 多台服务器可同时各自建索引（任务相互独立，存储按服务器分目录，无冲突）。

### 4. 七天自动刷新

`QuickSearchControl_Loaded` 在 `RefreshServerList()` 后调用 `AutoRefreshStaleIndexIfNeeded()`：

- 当前选中服务器为 null → 跳过。
- `TryGetMeta` 失败（无索引）→ 自动刷新（用户已确认该行为）。
- `DateTime.UtcNow - meta.IndexedAtUtc > 7 天` → 自动刷新。
- 否则不动作。

自动刷新用 `StartIndexing(server, silent: true)`：失败不弹窗，状态栏回退显示旧索引状态；进度照常显示在状态栏。常量 `IndexAutoRefreshMaxAge = TimeSpan.FromDays(7)`。

### 5. 本地化

新增（中/英）：

- `QuickSearch_IndexAutoRefreshing`："索引不存在或已过期，正在后台自动刷新..." / "Index missing or stale, refreshing in background..."（静默任务初始 `StatusText`）

复用现有 `QuickSearch_Indexing`、`QuickSearch_IndexingDatabase`、`QuickSearch_IndexNone`、`QuickSearch_IndexReady`、`Common_Cancel`、`Msg_QuickSearch_IndexBuilt`、`Msg_QuickSearch_IndexFailed`。

### 6. 主要改动文件

- `QuickSearch/QuickSearchWindowControl.xaml.cs`：全部逻辑改动（约 ±80 行）
- `Properties/Strings.resx`、`Properties/Strings.zh-Hans.resx`：新增 1 条文案

## 验收标准

1. A 服务器索引中切到 B：状态栏立即显示 B 的索引状态（时间/未建立），不再被 A 的进度覆盖；A 的任务在后台继续。
2. 切回 A：状态栏恢复显示 A 的实时进度；A 完成后状态栏显示新的索引时间。
3. A 索引中切到 B 点"刷新索引"：B 开始建索引，A 不被取消；按钮文案随选中服务器状态正确切换（刷新索引/取消）。
4. 索引超过 7 天的服务器，打开窗口后自动开始后台刷新，无弹窗打扰；无索引服务器打开窗口时同样自动建索引。
5. 索引未过期（< 7 天）的服务器，打开窗口不触发刷新。

## 风险与兼容

- 并行建索引会增加目标服务器瞬时负载：仍是用户显式触发或每台一次自动触发，可接受。
- 窗口卸载（SSMS 工具窗隐藏/关闭）时进行中的任务不强制取消：任务持有的是独立 SqlConnection，写盘为原子 move，中途断开最多留下 `.tmp` 文件，下次建索引前会清理（现有逻辑已处理）。
- 自动刷新期间用户手动点刷新：字典去重，同一服务器不会重复建。
