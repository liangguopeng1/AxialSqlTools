using Aurora;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.CommandBars;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using Task = System.Threading.Tasks.Task;
using Microsoft.SqlServer.Management.UI.Grid;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio;
using System.Collections;
using NLog;
using NLog.Targets;
using NLog.Targets.Wrappers;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Linq;
using AxialSqlTools.Properties;
using AxialSqlTools.IntelliSense;

namespace AxialSqlTools
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(AxialSqlToolsPackage.PackageGuidString)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionHasMultipleProjects_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionHasSingleProject_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(SettingsWindow))]
    [ProvideToolWindow(typeof(AboutWindow))]
    [ProvideToolWindow(typeof(ToolWindowGridToEmail))]
    [ProvideToolWindow(typeof(HealthDashboard_Server))]
    [ProvideToolWindow(typeof(DataTransferWindow))]
    [ProvideToolWindow(typeof(SqlServerBuildsWindow))]
    [ProvideToolWindow(typeof(QueryHistoryWindow))]
    [ProvideToolWindow(typeof(TabHistoryWindow))]
    [ProvideToolWindow(typeof(StatisticsSummaryWindow))]
    [ProvideToolWindow(typeof(DatabaseScripterToolWindow))]
    [ProvideToolWindow(typeof(DataImportWindow))]
    [ProvideToolWindow(typeof(QuickSearchWindow))]
    [ProvideToolWindow(typeof(SnippetManagerWindow))]
    public sealed class AxialSqlToolsPackage : AsyncPackage
    {

        public class SQLVersionInfo
        {
            public string SqlVersion { get; set; }    // e.g. "SQL Server 2022"
            public Version BuildNumber { get; set; }   // e.g. "16.0.1000"
            public DateTime ReleaseDate { get; set; }
            public string UpdateName { get; set; }    // e.g. "CU5" or "Security Update XYZ"
            public string KbNumber { get; set; }    // e.g. "CU5" or "Security Update XYZ"
            public string Url { get; set; }
        }

        public class SQLBuildsData
        {
            public Dictionary<string, List<SQLVersionInfo>> Builds { get; set; } = new Dictionary<string, List<SQLVersionInfo>>();
        }

        public SQLBuildsData SQLBuildsDataInfo;

        #region QueryHistory
        private class QueryHistoryEntry
        {
            public DateTime StartTime;
            public DateTime FinishTime;
            public string ElapsedTime;
            public long TotalRowsReturned;
            public string ExecResult;
            public string QueryText;
            public string DataSource;
            public string DatabaseName;
            public string LoginName;
            public string WorkstationId;
        }

        private const string QueryHistoryStorageModeTextFiles = "TextFiles";
        private const string QueryHistoryStorageModeDisabled = "Disabled";

        private static ConcurrentQueue<QueryHistoryEntry> _queryHistoryQueue = new ConcurrentQueue<QueryHistoryEntry>();
        private static int _statisticsCaptureVersion;
        private static int _pendingStatisticsCaptureVersion;
        private static readonly object _statisticsCaptureSyncRoot = new object();
        private static CancellationTokenSource _statisticsCaptureCancellationTokenSource;
        private System.Windows.Threading.DispatcherTimer _connectionColorRetryTimer;
        private System.Windows.Threading.DispatcherTimer _activeConnectionMonitorTimer;
        private int _connectionColorRetryCount;
        private string _lastObservedConnectionColorKey;
        private string _lastObservedConnectionWindowKey;
        public static Logger _logger;

        private void InitializeLogging()
        {

            var logDirectory = UserConfigPaths.LogsDirectory;
            Directory.CreateDirectory(logDirectory);

            // If using the NLog.config approach:
            LogManager.Setup()
                  .LoadConfiguration(builder =>
                  {
                      // Create a file target
                      var fileTarget = new FileTarget("fileLog")
                      {
                          FileName = Path.Combine(logDirectory, "log_${shortdate}.log"),
                          Layout = "${longdate}|${level}|${logger}|${message}${exception:format=ToString}",
                          ArchiveFileName = Path.Combine(logDirectory, "archive/log.{###}.txt"),
                          ArchiveAboveSize = 1024 * 1024 * 5, // 5 MB, for example
                          MaxArchiveFiles = 5,
                          KeepFileOpen = true,
                          AutoFlush = true
                      };
                      // 异步写盘，避免补全热路径同步 Flush 卡 UI
                      var asyncFile = new AsyncTargetWrapper("asyncFileLog", fileTarget)
                      {
                          QueueLimit = 10000,
                          OverflowAction = AsyncTargetWrapperOverflowAction.Discard,
                          TimeToSleepBetweenBatches = 50,
                          BatchSize = 100
                      };

                      // Create a rule: min level from settings (Debug/Info/Warn/Error)
                      builder.ForLogger()
                             .FilterMinLevel(ToNLogLevel(UiSettingsStore.GetLogLevel()))
                             .WriteTo(asyncFile);
                  });

            _logger = LogManager.GetCurrentClassLogger();
            // If needed, create directories here if they do not exist
            // Or do nothing if the config is specifying a folder that NLog will create automatically
        }

        /// <summary>后台线程刷盘，不阻塞 UI。热路径不要同步 Flush。</summary>
        public static void FlushLogsAsync()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { LogManager.Flush(); }
                catch { }
            });
        }

        /// <summary>按设置调整 NLog 最低级别，保存后立即生效。</summary>
        public static void ApplyLogMinLevel(string levelName)
        {
            var min = ToNLogLevel(levelName);
            var config = LogManager.Configuration;
            if (config == null) return;
            foreach (var rule in config.LoggingRules)
            {
                rule.SetLoggingLevels(min, LogLevel.Fatal);
            }
            LogManager.ReconfigExistingLoggers();
        }

        private static LogLevel ToNLogLevel(string levelName)
        {
            string name = UiSettingsStore.NormalizeLogLevel(levelName);
            if (name == UiSettingsStore.LogLevelDebug) return LogLevel.Debug;
            if (name == UiSettingsStore.LogLevelWarn) return LogLevel.Warn;
            if (name == UiSettingsStore.LogLevelError) return LogLevel.Error;
            return LogLevel.Info;
        }

        private static void EnqueueDataForProcessing(QueryHistoryEntry data)
        {
            _queryHistoryQueue.Enqueue(data);
            _ = Task.Run(() => ProcessDataAsync());
        }

        private static async Task ProcessDataAsync()
        {
            while (_queryHistoryQueue.TryDequeue(out QueryHistoryEntry data))
            {
                await PersistDataAsync(data);
            }

        }

        private static async Task PersistDataAsync(QueryHistoryEntry data)
        {
            try
            {
                string storageMode = SettingsManager.GetQueryHistoryStorageMode();
                if (string.Equals(storageMode, QueryHistoryStorageModeDisabled, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (string.Equals(storageMode, QueryHistoryStorageModeTextFiles, StringComparison.OrdinalIgnoreCase))
                {
                    await PersistDataAsJsonLineAsync(data);
                    return;
                }

                string connectionString = SettingsManager.GetQueryHistoryConnectionString();
                string qhTableName = SettingsManager.GetQueryHistoryTableNameOrDefault();

                if (string.IsNullOrEmpty(connectionString))
                {
                    return;
                }

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync();
                    QueryHistoryTableHelper.EnsureTableExists(connection, qhTableName);

                    string sql = $@"
                        INSERT INTO {qhTableName}
                            (StartTime, FinishTime, ElapsedTime, TotalRowsReturned, 
                                ExecResult, QueryText, DataSource, DatabaseName, LoginName, WorkstationId) 
                        VALUES (@StartTime, @FinishTime, @ElapsedTime, @TotalRowsReturned, 
                                    @ExecResult, @QueryText, @DataSource, @DatabaseName, @LoginName, @WorkstationId)
                        ";

                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@StartTime", data.StartTime);
                        command.Parameters.AddWithValue("@FinishTime", data.FinishTime);
                        command.Parameters.AddWithValue("@ElapsedTime", data.ElapsedTime);
                        command.Parameters.AddWithValue("@TotalRowsReturned", data.TotalRowsReturned);
                        command.Parameters.AddWithValue("@ExecResult", data.ExecResult);
                        command.Parameters.AddWithValue("@QueryText", data.QueryText?.Trim() ?? string.Empty);
                        command.Parameters.AddWithValue("@DataSource", data.DataSource ?? string.Empty);
                        command.Parameters.AddWithValue("@DatabaseName", data.DatabaseName ?? string.Empty);
                        command.Parameters.AddWithValue("@LoginName", data.LoginName ?? string.Empty);
                        command.Parameters.AddWithValue("@WorkstationId", data.WorkstationId ?? string.Empty);
                        await command.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[QueryHistory-PersistDataAsync]: An exception occurred");
            }

        }

        private static Task PersistDataAsJsonLineAsync(QueryHistoryEntry data)
        {
            string folderPath = SettingsManager.GetQueryHistoryTextFileFolder();
            Directory.CreateDirectory(folderPath);

            string fileName = $"query-history-{DateTime.UtcNow:yyyy-MM-dd}.jsonl";
            string filePath = Path.Combine(folderPath, fileName);
            string json = JsonConvert.SerializeObject(data);

            File.AppendAllText(filePath, json + Environment.NewLine);
            return Task.CompletedTask;
        }
        #endregion

        public const string PackageGuidString = "82ff597d-c4bc-469f-b990-637219074984";
        public const string PackageGuidGroup = "d8ef26a8-e88c-4ad1-85fd-ddc48a207530";

        private Plugin m_plugin = null;
        private CommandRegistry m_commandRegistry = null;
        private CommandBar m_commandBarQueryTemplates = null;

        public CommandEvents m_queryExecuteEvent { get; private set; }

        private int numberOfWindowsOpen = 0;

        public int GetNextToolWindowId()
        {
            numberOfWindowsOpen += 1;

            return numberOfWindowsOpen;
        }

        public Dictionary<string, string> globalSnippets = new Dictionary<string, string>();
        private readonly List<KeypressCommandFilter> _commandFilters = new List<KeypressCommandFilter>();
        private readonly HashSet<IVsTextView> _registeredTextViews = new HashSet<IVsTextView>();

        public static AxialSqlToolsPackage PackageInstance { get; private set; }

        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
        /// <param name="progress">A provider for progress updates.</param>
        /// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // When initialized asynchronously, the current thread may be a background thread at this point.
            // Do any initialization that requires the UI thread after switching to the UI thread.
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            PackageInstance = this;

            UiCultureService.ApplyFromSettings();
            UserConfigPaths.EnsureRootExists();

            InitializeLogging();

            // Tab History: 启动时清理过期快照文件
            try
            {
                TabHistoryStore.CleanupOldFiles();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to clean old tab history files.");
            }

            // IntelliSense: 尝试禁用 SSMS 自带 IntelliSense（防双弹框）。
            // 禁用需重启 SSMS 才生效；本次会话若内建仍开，Manager 会抑制自动触发，仅 Ctrl+Space 可用。
            try
            {
                if (!IntelliSenseManager.EnsureSsmsIntelliSenseDisabled())
                {
                    _logger?.Warn("IntelliSense: 写 SSMS settings.json 失败，内建未能禁用。本会话自动补全被抑制，仅 Ctrl+Space 手动可用，重启 SSMS 后恢复。");
                }
                else if (IntelliSenseManager.AutoTriggerSuppressed)
                {
                    if (!IntelliSense.IntelliSenseDisableHelper.IsSsmsIntelliSenseDisabled())
                    {
                        _logger?.Warn("IntelliSense: 写 settings.json 成功但读回仍为启用，请确认 SSMS.isolation.ini 的 InstallationID 与 %LOCALAPPDATA%\\Microsoft\\SSMS\\22.0_* 目录一致。当前自动补全被抑制。");
                    }
                    else
                    {
                        _logger?.Info("IntelliSense: 已在 settings.json 禁用 SSMS 内建 IntelliSense，需重启 SSMS 生效。本会话自动补全被抑制，仅 Ctrl+Space 手动可用，重启后恢复。");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "IntelliSense: EnsureSsmsIntelliSenseDisabled threw.");
            }

            try
            {
                IntelliSense.MetadataCacheRefreshService.Instance.KickoffFromPackage();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "IntelliSense: cache kickoff failed.");
            }

            try
            {
                await FormatQueryCommand.InitializeAsync(this);
                await RefreshTemplatesCommand.InitializeAsync(this);
                await OpenTemplatesFolderCommand.InitializeAsync(this);
                await ExportGridToExcelCommand.InitializeAsync(this);
                await ExportGridToGoogleSheetCommand.InitializeAsync(this);
                await SettingsWindowCommand.InitializeAsync(this);
                await AboutWindowCommand.InitializeAsync(this);
                await ScriptSelectedObject.InitializeAsync(this);
                await ExportGridToAsInsertsCommand.InitializeAsync(this);
                await ToolWindowGridToEmailCommand.InitializeAsync(this);
                await HealthDashboard_ServerCommand.InitializeAsync(this);
                await DataTransferWindowCommand.InitializeAsync(this);
                await DataImportWindowCommand.InitializeAsync(this);
                await ResultGridCopyAsInsertCommand.InitializeAsync(this);
                await SqlServerBuildsWindowCommand.InitializeAsync(this);
                await QueryHistoryWindowCommand.InitializeAsync(this);
                await TabHistoryWindowCommand.InitializeAsync(this);
                await StatisticsSummaryWindowCommand.InitializeAsync(this);
                await DatabaseScripterToolWindowCommand.InitializeAsync(this);
                await QuickSearchWindowCommand.InitializeAsync(this);
                await SnippetManagerWindowCommand.InitializeAsync(this);
                await SelectCurrentStatementCommand.InitializeAsync(this);
                await ToggleBlockCommentCommand.InitializeAsync(this);
                await RefreshIntelliSenseCacheCommand.InitializeAsync(this);

                UpdateChecker.ScheduleCheck(this, SettingsManager.GetEnableUpdateChecks());

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred");
            }

            try
            {

                DTE2 application = GetGlobalService(typeof(DTE)) as DTE2;
                IVsProfferCommands3 profferCommands3 = await base.GetServiceAsync(typeof(SVsProfferCommands)) as IVsProfferCommands3;
                OleMenuCommandService oleMenuCommandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

                HookExecuteCommand(application, "Query.Execute", hookAfterExecute: true);
                // SSMS 选中执行也走 Query.Execute，无独立 Query.ExecuteSelection 命令
                HookExecuteCommand(application, "Query.Parse");

                EnvDTE80.Events2 events = (EnvDTE80.Events2)application.Events;
                EnvDTE.WindowEvents windowEvents = events.WindowEvents;

                windowEvents.WindowCreated += new _dispWindowEvents_WindowCreatedEventHandler(WindowCreated_Event);
                windowEvents.WindowActivated += new _dispWindowEvents_WindowActivatedEventHandler(WindowActivated_Event);
                windowEvents.WindowClosing += new _dispWindowEvents_WindowClosingEventHandler(WindowClosing_Event);

                StartActiveWindowConnectionMonitor();

                // "File.ConnectObjectExplorer"
                // "Query.Connect"
                // 

                //---------------------------------------------------------------------------
                // Query Templates
                ImageList icons = new ImageList();
                icons.Images.Add(Resources.script);
                icons.Images.Add(Resources.open_folder);
                icons.Images.Add(Resources.refresh);

                m_plugin = new Plugin(application, profferCommands3, icons, oleMenuCommandService, "AxialSqlTools", "Aurora.Connect");

                CommandBar commandBar = m_plugin.AddCommandBar("Axial SQL Tools", MsoBarPosition.msoBarTop);
                m_commandRegistry = new CommandRegistry(m_plugin, commandBar, new Guid(PackageGuidString), new Guid(PackageGuidGroup));

                m_commandBarQueryTemplates = m_plugin.AddCommandBarMenu("Query Templates", MsoBarPosition.msoBarTop, null);

                //---------------------------------------------------------------------------
                LoadGlobalSnippets();
                SnippetService.ReloadSnippets();

                //---------------------------------------------------------------------------
                RefreshTemplatesList();

                MenuTextLocalizer.Apply(application);

                // Tab History：SSMS 22 经常合并不了新增 vsct 按钮，显式挂到「工具」子菜单（与查询历史同级）
                EnsureTabHistoryInToolsMenu(commandBar);
                EnsureRefreshCacheInToolsMenu(commandBar);

            }
            catch (Exception ex)
            {

                _logger.Error(ex, "An exception occurred");

                // Show a message box to prove we were here
                VsShellUtilities.ShowMessageBox(
                    this,
                    ex.Message,
                    "Oops! Something went wrong. Please report this issue on our GitHub repository and attach error log.",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            }

            try
            {
                SQLBuildsDataInfo = await Task.Run(() => SQLBuilds.DownloadSqlServerBuildInfo());

                MenuCommand CmdSqlServerBuilds = m_plugin.MenuCommandService.FindCommand(new CommandID(SqlServerBuildsWindowCommand.CommandSet, SqlServerBuildsWindowCommand.CommandId));
                CmdSqlServerBuilds.Visible = true;

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred");
            }

            // needed for the OxyPlot library
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);
            
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (UiSettingsStore.GetIntelliSenseEnabled())
                    {
                        IntelliSense.IntelliSenseDisableHelper.TryDisableSsmsIntelliSense();
                    }
                    else
                    {
                        IntelliSense.IntelliSenseDisableHelper.TryEnableSsmsIntelliSense();
                    }
                }
                catch
                {
                }
                UpdateChecker.LaunchDeferredUpdateOnClose();
            }

            base.Dispose(disposing);
        }

        #endregion

        private bool TryApplyConnectionColorForWindow(EnvDTE.Window window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string windowKind = null;
            try { windowKind = window?.Kind; } catch { }

            if (window == null || windowKind != "Document")
            {
                return false;
            }

            var connectionInfo = ScriptFactoryAccess.GetCurrentConnectionInfo();
            if (connectionInfo == null || string.IsNullOrWhiteSpace(connectionInfo.ServerName))
            {
                return false;
            }

            GridAccess.ApplyConnectionColor(connectionInfo.ServerName, connectionInfo.Database);
            GridAccess.ColorAllDocumentTabs();
            return true;
        }

        private void StartActiveWindowConnectionMonitor()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_activeConnectionMonitorTimer != null)
            {
                return;
            }

            _activeConnectionMonitorTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };

            _activeConnectionMonitorTimer.Tick += (sender, args) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                try
                {
                    RefreshActiveWindowConnectionColorIfChanged();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to monitor active window connection changes.");
                }
            };

            _activeConnectionMonitorTimer.Start();
        }

        private void RefreshActiveWindowConnectionColorIfChanged(bool force = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var activeWindow = ServiceCache.ExtensibilityModel?.ActiveWindow;
            string windowKind = null;
            try { windowKind = activeWindow?.Kind; } catch { }

            if (activeWindow == null || windowKind != "Document")
            {
                _lastObservedConnectionColorKey = null;
                _lastObservedConnectionWindowKey = null;
                return;
            }

            var connectionInfo = ScriptFactoryAccess.GetCurrentConnectionInfo();
            if (connectionInfo == null || string.IsNullOrWhiteSpace(connectionInfo.ServerName))
            {
                _lastObservedConnectionColorKey = null;
                _lastObservedConnectionWindowKey = null;
                return;
            }

            string connectionKey = $"{connectionInfo.ServerName}|{connectionInfo.Database}";
            string windowKey = GetWindowTrackingKey(activeWindow);

            if (!force &&
                string.Equals(connectionKey, _lastObservedConnectionColorKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(windowKey, _lastObservedConnectionWindowKey, StringComparison.Ordinal))
            {
                return;
            }

            GridAccess.ApplyConnectionColor(connectionInfo.ServerName, connectionInfo.Database);
            GridAccess.ColorAllDocumentTabs();

            _lastObservedConnectionColorKey = connectionKey;
            _lastObservedConnectionWindowKey = windowKey;
        }

        private static string GetWindowTrackingKey(EnvDTE.Window window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (window == null)
            {
                return string.Empty;
            }

            try
            {
                if (window.Document != null)
                {
                    return window.Document.FullName ?? window.Caption ?? string.Empty;
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

        private void ScheduleActiveWindowConnectionColorRefresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_connectionColorRetryTimer == null)
            {
                _connectionColorRetryTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };

                _connectionColorRetryTimer.Tick += (sender, args) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();

                    _connectionColorRetryCount++;

                    bool applied = false;
                    try
                    {
                        applied = TryApplyConnectionColorForWindow(ServiceCache.ExtensibilityModel?.ActiveWindow);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, "Failed to refresh connection color.");
                    }

                    if (applied || _connectionColorRetryCount >= 6)
                    {
                        _connectionColorRetryTimer.Stop();
                    }
                };
            }

            RefreshActiveWindowConnectionColorIfChanged(force: true);
            _connectionColorRetryCount = 0;
            _connectionColorRetryTimer.Stop();
            _connectionColorRetryTimer.Start();
        }

        private void WindowActivated_Event(EnvDTE.Window GotFocus, EnvDTE.Window LostFocus)
        {

            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (ShouldCloseIntelliSensePopups(GotFocus, LostFocus))
                    IntelliSenseManager.CloseAllPopups();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "IntelliSense: CloseAllPopups on window activate failed.");
            }

            try
            {
                EnsureStatisticsExecutionHookForActiveWindow("window-activated");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to reattach statistics handler during window activation.");
            }

            // IntelliSense 必须独立于 snippets 可用：任一启用即挂载 KeypressCommandFilter
            // 仅挂轻量 Filter；KeyHandler/WPF 延后到首次按键。
            // 注册本身也延后到 ApplicationIdle，避开 Ctrl+N 激活瞬间的编辑器创建窗口期。
            if (SettingsManager.GetUseSnippets() || UiSettingsStore.GetIntelliSenseEnabled())
            {
                var activated = GotFocus;
                try
                {
                    var dispatcher = System.Windows.Application.Current?.Dispatcher
                        ?? Dispatcher.CurrentDispatcher;
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            TryRegisterIntelliSenseOnActivated(activated);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Deferred IntelliSense register failed");
                        }
                    }), DispatcherPriority.ApplicationIdle);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "An exception occurred");
                    try { TryRegisterIntelliSenseOnActivated(GotFocus); } catch { }
                }
            }

            // Tab History: 失焦窗口先缓存全文；激活事件本身不写盘，只刷新缓存
            try
            {
                if (LostFocus != null)
                    TabHistoryRecorder.RememberWindowContent(LostFocus);
                TabHistoryRecorder.RecordWindowEvent(TabHistoryEventType.Activated, GotFocus);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to record tab activation history.");
            }

            // Apply connection-based coloring (document tab + status bar)
            try
            {
                if (GotFocus != null)
                {
                    TryApplyConnectionColorForWindow(GotFocus);
                    GridAccess.ScheduleReapplyAllTabColors();
                    ScheduleActiveWindowConnectionColorRefresh();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred applying connection color");
            }

        }

        private void TryRegisterIntelliSenseOnActivated(EnvDTE.Window gotFocus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (gotFocus == null) return;
            if (!SettingsManager.GetUseSnippets() && !UiSettingsStore.GetIntelliSenseEnabled()) return;
            try
            {
                object winObj = null;
                try { winObj = gotFocus.Object; } catch { return; }
                if (winObj == null) return;
                var DocData = GridAccess.GetProperty(winObj, "DocData");
                if (DocData == null) return;
                var txtMgr = (IVsTextManager)GridAccess.GetProperty(DocData, "TextManager");
                if (txtMgr == null) return;
                IVsTextView textView;
                if (txtMgr.GetActiveView(0, null, out textView) != VSConstants.S_OK || textView == null)
                    return;
                if (_registeredTextViews.Contains(textView))
                    return;
                _registeredTextViews.Add(textView);
                var CommandFilter = new KeypressCommandFilter(this, textView);
                CommandFilter.AddToChain();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "TryRegisterIntelliSenseOnActivated failed");
            }
        }

        private static bool ShouldCloseIntelliSensePopups(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            try
            {
                if (gotFocus != null && lostFocus != null && gotFocus == lostFocus)
                    return false;
                // 设置等工具窗：即使 LostFocus 为空也要关（菜单打开时常见）
                if (gotFocus != null)
                {
                    string kind = null;
                    try { kind = gotFocus.Kind; } catch { }
                    if (!string.Equals(kind, "Document", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                // 文档 ↔ 文档切换
                if (lostFocus != null && gotFocus != null)
                    return true;
                // 文档失焦
                if (lostFocus != null)
                    return true;
            }
            catch
            {
                return true;
            }
            // lostFocus 为空且仍是文档：可能是补全窗焦点抖动，不关
            return false;
        }

        private void WindowClosing_Event(EnvDTE.Window Window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Re-color remaining tabs after a tab closes
            try
            {
                // 关闭回调里 Document 可能已拆，先尽量抓一次全文进缓存
                TabHistoryRecorder.RememberWindowContent(Window);
                TabHistoryRecorder.RecordWindowEvent(TabHistoryEventType.Closed, Window);
                GridAccess.ScheduleReapplyAllTabColors();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred in WindowClosing_Event");
            }
        }

        private void WindowCreated_Event(EnvDTE.Window Window)
        {

            ThreadHelper.ThrowIfNotOnUIThread();

            // subscribe to the execution completed event
            try
            {
                TabHistoryRecorder.RecordWindowEvent(TabHistoryEventType.Opened, Window);

                var sqlResultsControl = GridAccess.GetNonPublicField(Window.Object, "m_sqlResultsControl");
                AttachStatisticsExecutionCompletedHandler(sqlResultsControl);
                TryApplyConnectionColorForWindow(Window);
                GridAccess.ScheduleReapplyAllTabColors();
                ScheduleActiveWindowConnectionColorRefresh();

            }
            catch (Exception ex) 
            {
                _logger.Error(ex, "An exception occurred");
            }

            // 注意：不要在 WindowCreated 里挂 IntelliSense/AssignHandle。
            // Ctrl+N 新建标签时窗口尚未完全就绪，同步挂载会导致 SSMS 卡死退出。
            // 挂载统一在 WindowActivated 中完成。
        }

        private static void AttachStatisticsExecutionCompletedHandler(object sqlResultsControl)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (sqlResultsControl == null)
            {
                return;
            }

            EventHandler eventHandler = SQLResultsControl_ScriptExecutionCompleted;

            EventInfo eventInfo = sqlResultsControl.GetType().GetEvent("ScriptExecutionCompleted");
            if (eventInfo == null)
            {
                return;
            }

            Delegate handlerDelegate = Delegate.CreateDelegate(eventInfo.EventHandlerType, eventHandler.Target, eventHandler.Method);
            eventInfo.RemoveEventHandler(sqlResultsControl, handlerDelegate);
            eventInfo.AddEventHandler(sqlResultsControl, handlerDelegate);
        }

        public static void EnsureStatisticsExecutionHookForActiveWindow(string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var sqlResultsControl = GridAccess.GetSQLResultsControl();
                AttachStatisticsExecutionCompletedHandler(sqlResultsControl);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to ensure statistics execution hook ({reason}).");
            }
        }

        public void LoadGlobalSnippets()
        {

            if (SettingsManager.GetUseSnippets())
            {

                var snippetFolder = SettingsManager.GetSnippetFolder();

                if (Directory.Exists(snippetFolder))
                {
                    var allFiles = Directory.EnumerateFiles(snippetFolder, "*.sql");

                    foreach (var file in allFiles)
                    {

                        FileInfo fi = new FileInfo(file);

                        if (fi.Length < 1024 * 1024)
                        {
                            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fi.Name);

                            globalSnippets.Add(fileNameWithoutExtension.ToUpper(), System.IO.File.ReadAllText(fi.FullName));
                        }

                    }


                }

            }



        }

        // I don't understand the purpose, but it works
        private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            // add this into main module -> AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);

            if (args.Name.Contains("OxyPlot"))
                return AppDomain.CurrentDomain.Load(args.Name);
            else return null;
        }
        //----------------


        // This method aligns all numeric values to the right
        public static void SQLResultsControl_ScriptExecutionCompleted(object QEOLESQLExec, object b)
        {

            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                //1. Align numeric types to the right
                CollectionBase gridContainers = GridAccess.GetGridContainers();

                foreach (var gridContainer in gridContainers)
                {
                    var grid = GridAccess.GetNonPublicField(gridContainer, "m_grid") as GridControl;
                    var gridStorage = grid.GridStorage;
                    var schemaTable = GridAccess.GetNonPublicField(gridStorage, "m_schemaTable") as DataTable;

                    var gridColumns = GridAccess.GetNonPublicField(grid, "m_Columns") as GridColumnCollection;
                    if (gridColumns != null)
                    {
                        //Why no "flot"? Because it cannot be aligned "good" due to the varying number of digits in the decimal part.
                        string[] typeToAlignRight = new string[] { "tinyint", "smallint", "int", "bigint", "money", "smallmoney", "decimal", "numeric" };

                        List<int> columnsToAlignRight = new List<int> { };

                        for (int c = 0; c < schemaTable.Rows.Count; c++)
                        {
                            int columnOrdinal = (int)schemaTable.Rows[c][1];
                            var sqlDataTypeName = schemaTable.Rows[c][24];

                            if (typeToAlignRight.Contains(sqlDataTypeName))
                            {
                                columnsToAlignRight.Add(columnOrdinal);
                            }
                        }

                        foreach (Microsoft.SqlServer.Management.UI.Grid.GridColumn gridColumn in gridColumns)
                        {

                            if (columnsToAlignRight.Contains(gridColumn.ColumnIndex - 1) || gridColumn.ColumnIndex == 0)
                            {
                                // not needed
                                //var textAlignField = GridAccess.GetNonPublicFieldInfo(gridColumn, "TextAlign");
                                //if (textAlignField != null)
                                //{
                                //    textAlignField.SetValue(gridColumn, System.Windows.Forms.HorizontalAlignment.Right);
                                //}

                                // applies to the row number column 
                                var textAlignField2 = GridAccess.GetNonPublicFieldInfo(gridColumn, "m_myAlign");
                                if (textAlignField2 != null)
                                {
                                    textAlignField2.SetValue(gridColumn, System.Windows.Forms.HorizontalAlignment.Right);
                                }

                                var textAlignField3 = GridAccess.GetNonPublicFieldInfo(gridColumn, "m_textFormat");
                                if (textAlignField3 != null)
                                {
                                    System.Windows.Forms.TextFormatFlags flags = (System.Windows.Forms.TextFormatFlags)GridAccess.GetNonPublicField(gridColumn, "m_textFormat");
                                    textAlignField3.SetValue(gridColumn, flags | System.Windows.Forms.TextFormatFlags.Right);
                                }
                            }

                        }
                    }

                    grid.Refresh();

                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred");
            }

            try
            {
                // 2. Get open transaction info
                int openTranCount = 0;

                var SQLResultsControl = GridAccess.GetSQLResultsControl();
                var m_SqlExec = GridAccess.GetNonPublicField(SQLResultsControl, "m_sqlExec");

                Microsoft.Data.SqlClient.SqlConnection connection = GridAccess.GetNonPublicField(m_SqlExec, "m_conn") as Microsoft.Data.SqlClient.SqlConnection;
                if (connection.State == ConnectionState.Open)
                {
                    using (Microsoft.Data.SqlClient.SqlCommand command = new Microsoft.Data.SqlClient.SqlCommand("SELECT @@TRANCOUNT", connection))
                    {
                        var result = command.ExecuteScalar();
                        openTranCount = result != DBNull.Value ? Convert.ToInt32(result) : 0;
                    }
                }

                SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(connection.ConnectionString);
                bool isColumnEncryptionSettingOn = builder.ColumnEncryptionSetting == SqlConnectionColumnEncryptionSetting.Enabled;

                var editorProperties = GridAccess.GetNonPublicField(m_SqlExec, "editorProperties");
                var editorProperties_ElapsedTime = (string)GridAccess.GetProperty(editorProperties, "ElapsedTime");

                GridAccess.ChangeStatusBarContent(openTranCount, isColumnEncryptionSettingOn, editorProperties_ElapsedTime);

                // Re-apply connection color after status bar update
                string dataSource = (string)GridAccess.GetProperty(connection, "DataSource");
                string database = (string)GridAccess.GetProperty(connection, "Database");
                GridAccess.ApplyConnectionColor(dataSource, database);

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred");
            }

            // query history
            try
            {

                var editorProperties = GridAccess.GetNonPublicField(QEOLESQLExec, "editorProperties");

                var textSpan = GridAccess.GetNonPublicField(QEOLESQLExec, "textSpan");

                var mConn = GridAccess.GetNonPublicField(QEOLESQLExec, "m_conn");

                var QueryHistoryObj = new QueryHistoryEntry();
                QueryHistoryObj.StartTime = (DateTime)GridAccess.GetNonPublicField(editorProperties, "startTime");
                QueryHistoryObj.FinishTime = (DateTime)GridAccess.GetNonPublicField(editorProperties, "finishTime");
                QueryHistoryObj.ElapsedTime = (string)GridAccess.GetProperty(editorProperties, "ElapsedTime");
                QueryHistoryObj.TotalRowsReturned = (long)GridAccess.GetProperty(editorProperties, "TotalRowsReturned");

                // Success, Failure -> not really clear how to track batch execution results...
                QueryHistoryObj.ExecResult = GridAccess.GetNonPublicField(QEOLESQLExec, "m_execResult").ToString();

                QueryHistoryObj.QueryText = (string)GridAccess.GetProperty(textSpan, "Text");
                QueryHistoryObj.DataSource = (string)GridAccess.GetProperty(mConn, "DataSource");
                QueryHistoryObj.DatabaseName = (string)GridAccess.GetProperty(mConn, "Database");
                QueryHistoryObj.LoginName = (string)GridAccess.GetProperty(editorProperties, "ChildLoginName");
                QueryHistoryObj.WorkstationId = (string)GridAccess.GetProperty(mConn, "WorkstationId");

                EnqueueDataForProcessing(QueryHistoryObj);

                try
                {
                    string qtext = QueryHistoryObj.QueryText;
                    if (IntelliSense.MetadataCatalogService.ContainsDdl(qtext))
                    {
                        var conn = ScriptFactoryAccess.GetCurrentConnectionInfo();
                        if (conn != null)
                        {
                            IntelliSense.MetadataCatalogService.Instance.InvalidateDdlTargets(conn, qtext);
                            var dbs = IntelliSense.MetadataCatalogService.ExtractDdlTargetDatabases(
                                qtext, QueryHistoryObj.DatabaseName ?? conn.Database);
                            IntelliSense.MetadataCacheRefreshService.Instance.RefreshDatabases(conn, dbs);
                        }
                    }
                }
                catch (Exception ddlEx)
                {
                    _logger.Warn(ddlEx, "IntelliSense DDL cache refresh after execute failed");
                }

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred");
            }

            // tab history: 执行后记录编辑器全文（整页 SQL，不是本次执行的 textSpan 片段）
            try
            {
                string content = string.Empty;
                try
                {
                    var active = ServiceCache.ExtensibilityModel?.ActiveWindow;
                    if (active != null)
                        content = ScriptFactoryAccess.GetQueryWindowText(active) ?? string.Empty;
                }
                catch { }

                // 拿不到全文时再回退到本次执行片段，避免丢记录
                if (string.IsNullOrEmpty(content))
                {
                    var textSpan = GridAccess.GetNonPublicField(QEOLESQLExec, "textSpan");
                    content = GridAccess.GetProperty(textSpan, "Text") as string ?? string.Empty;
                }

                var mConn = GridAccess.GetNonPublicField(QEOLESQLExec, "m_conn");
                string dataSource = string.Empty;
                string database = string.Empty;
                if (mConn != null)
                {
                    try { dataSource = (string)GridAccess.GetProperty(mConn, "DataSource"); } catch { }
                    try { database = (string)GridAccess.GetProperty(mConn, "Database"); } catch { }
                }
                TabHistoryRecorder.RecordExecuted(content, dataSource, database);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred recording tab history after execute");
            }

            if (!StatisticsSummaryStore.IsWindowOpen())
            {
                ThreadHelper.Generic.BeginInvoke(() => EnsureStatisticsExecutionHookForActiveWindow("post-skip"));

                return;
            }

            var captureVersion = Interlocked.Exchange(ref _pendingStatisticsCaptureVersion, 0);
            if (captureVersion == 0)
            {
                captureVersion = Interlocked.Increment(ref _statisticsCaptureVersion);
            }

            if (!StatisticsSummaryStore.BeginCapture(captureVersion))
            {
                return;
            }

            var captureCancellationTokenSource = CreateStatisticsCaptureCancellationTokenSource();

            _ = Task.Run(async delegate
            {
                try
                {
                    await CaptureStatisticsSummaryAsync(QEOLESQLExec, captureVersion, captureCancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    StatisticsSummaryStore.MarkUnavailable(captureVersion);
                    _logger.Error(ex, "Failed to capture statistics summary.");
                }
                finally
                {
                    ReleaseStatisticsCaptureCancellationTokenSource(captureCancellationTokenSource);
                }
            });


        }

        public static void CancelStatisticsCapture(bool updateStore = true)
        {
            CancellationTokenSource captureCancellationTokenSource;

            Interlocked.Exchange(ref _pendingStatisticsCaptureVersion, 0);

            lock (_statisticsCaptureSyncRoot)
            {
                captureCancellationTokenSource = _statisticsCaptureCancellationTokenSource;
                _statisticsCaptureCancellationTokenSource = null;
            }

            captureCancellationTokenSource?.Cancel();

            if (updateStore)
            {
                StatisticsSummaryStore.CancelCapture();
            }
        }

        private static CancellationTokenSource CreateStatisticsCaptureCancellationTokenSource()
        {
            CancellationTokenSource previousCancellationTokenSource;
            CancellationTokenSource nextCancellationTokenSource;

            lock (_statisticsCaptureSyncRoot)
            {
                previousCancellationTokenSource = _statisticsCaptureCancellationTokenSource;
                nextCancellationTokenSource = new CancellationTokenSource();
                _statisticsCaptureCancellationTokenSource = nextCancellationTokenSource;
            }

            previousCancellationTokenSource?.Cancel();
            return nextCancellationTokenSource;
        }

        private static void ReleaseStatisticsCaptureCancellationTokenSource(CancellationTokenSource captureCancellationTokenSource)
        {
            lock (_statisticsCaptureSyncRoot)
            {
                if (ReferenceEquals(_statisticsCaptureCancellationTokenSource, captureCancellationTokenSource))
                {
                    _statisticsCaptureCancellationTokenSource = null;
                }
            }

            captureCancellationTokenSource.Dispose();
        }

        private static async Task CaptureStatisticsSummaryAsync(object sqlExecutionContext, int captureVersion, CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;

            cancellationToken.ThrowIfCancellationRequested();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var statisticsOutputOptions = GetStatisticsSummaryOutputOptions(sqlExecutionContext);
            if (!statisticsOutputOptions.HasStatisticsOutput)
            {
                StatisticsSummaryStore.MarkUnavailable(captureVersion, StatisticsSummaryCaptureStatus.StatisticsDisabled);
                return;
            }

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!StatisticsSummaryStore.IsWindowOpen())
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                GridAccess.TryFlushStatisticsMessages();

                var statisticsText = GridAccess.TryGetStatisticsMessagesText();
                if (TryStoreStatisticsSummary(sqlExecutionContext, statisticsText, captureVersion, out var summary))
                {
                    return;
                }

                if (attempt < maxAttempts - 1)
                {
                    await Task.Delay(150, cancellationToken);
                }
            }

            StatisticsSummaryStore.MarkUnavailable(captureVersion);
        }

        private sealed class StatisticsSummaryOutputOptions
        {
            public bool StatisticsIoEnabled { get; set; }
            public bool StatisticsTimeEnabled { get; set; }
            public bool HasStatisticsOutput => StatisticsIoEnabled || StatisticsTimeEnabled;
        }

        private static StatisticsSummaryOutputOptions GetStatisticsSummaryOutputOptions(object sqlExecutionContext)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (!(GridAccess.GetNonPublicField(sqlExecutionContext, "m_conn") is SqlConnection connection)
                    || connection.State != ConnectionState.Open)
                {
                    return new StatisticsSummaryOutputOptions
                    {
                        StatisticsIoEnabled = true,
                        StatisticsTimeEnabled = true,
                    };
                }

                using (var command = new SqlCommand("DBCC USEROPTIONS WITH NO_INFOMSGS;", connection))
                using (var reader = command.ExecuteReader())
                {
                    var options = new StatisticsSummaryOutputOptions();

                    while (reader.Read())
                    {
                        if (reader.FieldCount < 2)
                        {
                            continue;
                        }

                        var optionName = reader.IsDBNull(0) ? null : reader.GetString(0);
                        var optionValue = reader.IsDBNull(1) ? null : reader.GetString(1);
                        if (string.IsNullOrWhiteSpace(optionName))
                        {
                            continue;
                        }

                        if (optionName.Equals("statistics io", StringComparison.OrdinalIgnoreCase))
                        {
                            options.StatisticsIoEnabled = IsUserOptionEnabled(optionValue);
                        }
                        else if (optionName.Equals("statistics time", StringComparison.OrdinalIgnoreCase))
                        {
                            options.StatisticsTimeEnabled = IsUserOptionEnabled(optionValue);
                        }
                    }

                    return options;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to inspect session statistics options.");
                return new StatisticsSummaryOutputOptions
                {
                    StatisticsIoEnabled = true,
                    StatisticsTimeEnabled = true,
                };
            }
        }

        private static bool IsUserOptionEnabled(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Equals("on", StringComparison.OrdinalIgnoreCase)
                || value.Equals("set", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryStoreStatisticsSummary(object sqlExecutionContext, string statisticsText, int captureVersion, out StatisticsSummary summary)
        {
            summary = null;

            if (string.IsNullOrWhiteSpace(statisticsText))
            {
                return false;
            }

            summary = StatisticsSummaryParser.Parse(statisticsText);
            if (summary == null)
            {
                return false;
            }

            var textSpan = GridAccess.GetNonPublicField(sqlExecutionContext, "textSpan");
            var mConn = GridAccess.GetNonPublicField(sqlExecutionContext, "m_conn");

            summary.QueryText = GridAccess.GetProperty(textSpan, "Text") as string;
            summary.DataSource = GridAccess.GetProperty(mConn, "DataSource") as string;
            summary.DatabaseName = GridAccess.GetProperty(mConn, "Database") as string;

            StatisticsSummaryStore.Set(summary, captureVersion);
            return true;
        }

        private void HookExecuteCommand(DTE2 application, string commandName, bool hookAfterExecute = false)
        {
            if (application == null || string.IsNullOrWhiteSpace(commandName))
                return;
            try
            {
                EnvDTE.Command cmd = null;
                try
                {
                    cmd = application.Commands.Item(commandName);
                }
                catch (ArgumentException)
                {
                    // 当前 SSMS 版本无此命令名（如旧代码里的 Query.ExecuteSelection）
                    _logger?.Info("IntelliSense: skip missing command {0}.", commandName);
                    return;
                }
                if (cmd == null || string.IsNullOrEmpty(cmd.Guid))
                {
                    _logger?.Info("IntelliSense: skip unavailable command {0}.", commandName);
                    return;
                }
                var cmdGuid = new Guid(cmd.Guid);
                IntelliSenseManager.RegisterExecuteCommand(cmdGuid, (uint)cmd.ID);
                var evt = application.Events.get_CommandEvents(cmd.Guid, cmd.ID);
                evt.BeforeExecute += this.CommandEvents_BeforeExecute;
                if (hookAfterExecute)
                {
                    m_queryExecuteEvent = evt;
                    evt.AfterExecute += this.CommandEvents_AfterExecute;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "IntelliSense: failed to hook execute command {0}.", commandName);
            }
        }

        private void CommandEvents_BeforeExecute(string Guid, int ID, object CustomIn, object CustomOut, ref bool CancelDefault)
        {
            try
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    IntelliSenseManager.CloseAllPopups();
                });
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "IntelliSense: CloseAllPopups on query execute failed.");
            }

            try
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    PrepareStatisticsCaptureBeforeExecute();
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to prepare statistics capture before query execution.");
            }
        }

        private void PrepareStatisticsCaptureBeforeExecute()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            EnsureStatisticsExecutionHookForActiveWindow("query-execute-before");

            if (!StatisticsSummaryStore.IsWindowOpen())
            {
                return;
            }

            var captureVersion = Interlocked.Increment(ref _statisticsCaptureVersion);
            Interlocked.Exchange(ref _pendingStatisticsCaptureVersion, captureVersion);

            StatisticsSummaryStore.BeginCapture(captureVersion);
        }

        //it has been executed, but the Grid hasn't been created yet...
        private void CommandEvents_AfterExecute(string Guid, int ID, object CustomIn, object CustomOut)
        {
            //ThreadHelper.ThrowIfNotOnUIThread();
        }

        public void RefreshTemplatesList()
        {
            if (m_commandBarQueryTemplates is null)
            {
                return;
            }

            //Delete existing autogenerated controls
            for (int idx = m_commandBarQueryTemplates.Controls.Count; idx >= 3; idx--)
            {
                CommandBarControl control = m_commandBarQueryTemplates.Controls[idx];
                if (control == null) continue;

                control.Delete();
            }

            Dictionary<string, string> fileNamesCache = new Dictionary<string, string>();

            string Folder = SettingsManager.GetTemplatesFolder();
            int i = 2;
            CreateCommands(ref i, ref fileNamesCache, Folder, m_commandRegistry, m_commandBarQueryTemplates);

            UpdateRenamedTemplatesControls(m_commandBarQueryTemplates, fileNamesCache);

        }

        private void EnsureTabHistoryInToolsMenu(CommandBar toolbar)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // 去掉此前误挂在工具栏根上的按钮（Aurora onlyToolbar 残留）
                RemoveCommandBarControlByNames(toolbar,
                    "AxialSqlTools.TabHistory", "Tab History", "标签页历史");

                CommandBar toolsMenu = FindToolsSubMenu(toolbar);
                if (toolsMenu == null)
                {
                    _logger?.Warn("Tools submenu not found; Tab History menu item was not registered.");
                    return;
                }

                // 先删掉菜单里已有的（可能在末尾），再按「查询历史」后的位置重新插入
                RemoveCommandBarControlByNames(toolsMenu,
                    "AxialSqlTools.TabHistory", "Tab History", "标签页历史");

                var queryHistory = FindCommandBarControl(toolsMenu, "Query History", "查询历史");
                // CommandBar Index 为 1-based；插到查询历史后面 = Index + 1
                uint insertPos = queryHistory != null
                    ? (uint)(queryHistory.Index + 1)
                    : (uint)(toolsMenu.Controls.Count + 1);

                m_commandRegistry.RegisterCommand(
                    doBindings: false,
                    handler: new TabHistoryCommandProcessor(m_plugin, this, toolsMenu, insertPos),
                    onlyToolbar: true,
                    menuParent: toolsMenu);

                // 再保险一次：若仍不在查询历史后，强制 Move
                PlaceControlAfter(toolsMenu,
                    new[] { "AxialSqlTools.TabHistory", "Tab History", "标签页历史" },
                    new[] { "Query History", "查询历史" });
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to place Tab History into Tools menu.");
            }
        }

        private void EnsureRefreshCacheInToolsMenu(CommandBar toolbar)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                RemoveCommandBarControlByNames(toolbar,
                    "AxialSqlTools.RefreshIntelliSenseCache", "Refresh Cache", "刷新缓存");
                CommandBar toolsMenu = FindToolsSubMenu(toolbar);
                if (toolsMenu == null)
                {
                    _logger?.Warn("Tools submenu not found; Refresh Cache menu item was not registered.");
                    return;
                }
                RemoveCommandBarControlByNames(toolsMenu,
                    "AxialSqlTools.RefreshIntelliSenseCache", "Refresh Cache", "刷新缓存");
                var snippet = FindCommandBarControl(toolsMenu, "Snippet Manager", "片段管理器");
                uint insertPos = snippet != null
                    ? (uint)(snippet.Index + 1)
                    : (uint)(toolsMenu.Controls.Count + 1);
                m_commandRegistry.RegisterCommand(
                    doBindings: false,
                    handler: new RefreshIntelliSenseCacheCommandProcessor(m_plugin, this, toolsMenu, insertPos),
                    onlyToolbar: true,
                    menuParent: toolsMenu);
                PlaceControlAfter(toolsMenu,
                    new[] { "AxialSqlTools.RefreshIntelliSenseCache", "Refresh Cache", "刷新缓存" },
                    new[] { "Snippet Manager", "片段管理器" });
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to place Refresh Cache into Tools menu.");
            }
        }

        private static CommandBar FindToolsSubMenu(CommandBar toolbar)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (toolbar == null) return null;
            foreach (CommandBarControl control in toolbar.Controls)
            {
                if (!(control is CommandBarPopup popup) || popup.CommandBar == null)
                    continue;
                string caption = NormalizeCommandCaption(popup.Caption);
                string name = NormalizeCommandCaption(popup.CommandBar.Name);
                if (caption == "tools" || caption == "工具"
                    || name == "tools" || name == "工具")
                {
                    return popup.CommandBar;
                }
            }
            return null;
        }

        private static void RemoveCommandBarControlByNames(CommandBar bar, params string[] names)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (bar == null) return;
            var match = FindCommandBarControl(bar, names);
            if (match != null)
            {
                try { match.Delete(false); } catch { }
            }
        }

        private static bool HasCommandBarControl(CommandBar bar, params string[] names)
        {
            return FindCommandBarControl(bar, names) != null;
        }

        private static CommandBarControl FindCommandBarControl(CommandBar bar, params string[] names)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (bar == null || names == null || names.Length == 0) return null;
            var normalized = new HashSet<string>(
                names.Select(NormalizeCommandCaption).Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.OrdinalIgnoreCase);
            foreach (CommandBarControl control in bar.Controls)
            {
                try
                {
                    string caption = NormalizeCommandCaption(control.Caption);
                    string tag = NormalizeCommandCaption(control.Tag as string);
                    // Controls[canonicalName] lookup path — some Aurora buttons use AccName/Id
                    if (normalized.Contains(caption) || normalized.Contains(tag))
                        return control;
                }
                catch { }
            }
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                try
                {
                    var existing = bar.Controls[name];
                    if (existing != null) return existing;
                }
                catch { }
            }
            return null;
        }

        private static void PlaceControlAfter(CommandBar bar, string[] targetNames, string[] afterNames)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var target = FindCommandBarControl(bar, targetNames);
            var after = FindCommandBarControl(bar, afterNames);
            if (target == null || after == null) return;
            try
            {
                bool afterSeen = false;
                bool alreadyPlaced = false;
                CommandBarControl insertBefore = null;
                foreach (CommandBarControl c in bar.Controls)
                {
                    if (afterSeen)
                    {
                        if (ReferenceEquals(c, target))
                            alreadyPlaced = true;
                        else
                            insertBefore = c;
                        break;
                    }
                    if (ReferenceEquals(c, after))
                        afterSeen = true;
                }
                if (alreadyPlaced) return;
                if (insertBefore != null)
                    target.Move(insertBefore, false);
                else
                    target.Move(System.Reflection.Missing.Value, false);
            }
            catch { }
        }

        private static string NormalizeCommandCaption(string caption)
        {
            if (string.IsNullOrEmpty(caption)) return string.Empty;
            return caption.Replace("&", "").Trim().ToLowerInvariant();
        }

        private void UpdateRenamedTemplatesControls(CommandBar commandBarFolder, Dictionary<string, string> fileNamesCache)
        {
            foreach (CommandBarControl control in commandBarFolder.Controls)
            {
                string keyToFind = control.Caption;
                if (fileNamesCache.TryGetValue(keyToFind, out string value))
                {
                    control.Caption = value;
                    control.Tag = keyToFind;
                }
                else if (fileNamesCache.TryGetValue(control.Tag, out string value2)) //This is the case when the file was renamed
                {
                    control.Caption = value2;
                }
            }
        }

        private void CreateCommands(ref int i, ref Dictionary<string, string> fileNamesCache, string Folder,
                CommandRegistry m_commandRegistry, CommandBar commandBarFolder)
        {

            var dirs = Directory.GetDirectories(Folder);

            foreach (var dirStr in dirs)
            {
                var di = new FileInfo(dirStr);

                string controlName = "Folder_" + i;

                fileNamesCache.Add(controlName, di.Name);
                i = i + 1;

                CommandBar commandBarFolderNext = m_plugin.AddCommandBarMenu(controlName, MsoBarPosition.msoBarMenuBar, commandBarFolder);

                CreateCommands(ref i, ref fileNamesCache, Path.Combine(Folder, dirStr), m_commandRegistry, commandBarFolderNext);

            }

            var files = Directory.GetFiles(Folder);

            foreach (var file in files)
            {
                var fi = new FileInfo(file);

                string controlName = "Template_" + i;
                fileNamesCache.Add(controlName, fi.Name);

                var nc = new CommandProcessor(m_plugin, controlName, controlName, "");
                nc.package = this;
                nc.FullFileName = fi.FullName;
                m_commandRegistry.RegisterCommand(true, nc, true, commandBarFolder);

                i = i + 1;

            }

            UpdateRenamedTemplatesControls(commandBarFolder, fileNamesCache);

        }

    }
}
