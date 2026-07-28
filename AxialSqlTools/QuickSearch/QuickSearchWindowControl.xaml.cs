using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;
using AxialSqlTools.Properties;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Xml;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using static AxialSqlTools.ScriptFactoryAccess;

namespace AxialSqlTools
{
    public partial class QuickSearchWindowControl : UserControl
    {
        private const int MaxParallelDatabaseSearches = 4;
        private static readonly TimeSpan IndexAutoRefreshMaxAge = TimeSpan.FromDays(7);

        private readonly ToolWindowThemeController themeController;
        private CancellationTokenSource searchCancellationTokenSource;
        private readonly Dictionary<string, ServerIndexingState> activeIndexingByServer =
            new Dictionary<string, ServerIndexingState>(StringComparer.OrdinalIgnoreCase);
        private TextMarkerService textMarkerService;
        private bool suppressSelectionEvents;

        private sealed class ServerIndexingState
        {
            public CancellationTokenSource CancellationSource { get; set; }
            public string StatusText { get; set; }
            public bool Silent { get; set; }
        }

        public QuickSearchWindowControl()
        {
            this.InitializeComponent();
            UiLocalization.Apply(this);
            LocalizeDataGridColumns();
            LocalizeWikiDescription();
            themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);

            CheckBox_WholeWord.IsChecked = true;

            using (var stream = typeof(QuickSearchWindowControl).Assembly.GetManifestResourceStream("AxialSqlTools.QuickSearch.sql.xshd"))
            using (var reader = new XmlTextReader(stream))
            {
                SqlEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }

            if (textMarkerService == null)
            {
                textMarkerService = new TextMarkerService(SqlEditor);
            }
        }

        private void ApplyThemeBrushResources()
        {
            ToolWindowThemeResources.ApplySharedTheme(this);
        }

        private void LocalizeDataGridColumns()
        {
            DataGrid_SearchResults.Columns[0].Header = Strings.Get("QuickSearch_ColDatabase");
            DataGrid_SearchResults.Columns[1].Header = Strings.Get("QuickSearch_ColType");
            DataGrid_SearchResults.Columns[2].Header = Strings.Get("QuickSearch_ColSchema");
            DataGrid_SearchResults.Columns[3].Header = Strings.Get("QuickSearch_ColObject");
            DataGrid_SearchResults.Columns[4].Header = Strings.Get("QuickSearch_ColLocation");
            DataGrid_SearchResults.Columns[5].Header = Strings.Get("QuickSearch_ColMatchPreview");
            DataGrid_SearchResults.Columns[6].Header = Strings.Get("QuickSearch_ColScript");
        }

        private void LocalizeWikiDescription()
        {
            WikiDescriptionTextBlock.Inlines.Clear();
            WikiDescriptionTextBlock.Inlines.Add(new Run(Strings.Get("Common_FeatureDescriptionIn")));
            WikiDescriptionTextBlock.Inlines.Add(new Run(" "));
            var wikiLink = new Hyperlink(new Run(Strings.Get("Common_Wiki")))
            {
                NavigateUri = new Uri("https://github.com/liangguopeng1/AxialSqlTools/wiki/Quick-Search")
            };
            wikiLink.RequestNavigate += WikiLink_RequestNavigate;
            WikiDescriptionTextBlock.Inlines.Add(wikiLink);
        }

        private void WikiLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            ToolWindowNavigation.HandleRequestNavigate(e);
        }

        private void QuickSearchControl_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshServerList();
            AutoRefreshStaleIndexForAllServers();
        }

        private void AutoRefreshStaleIndexForAllServers()
        {
            if (!(ComboBox_Servers.ItemsSource is IList<ConnectionInfo> servers) || servers.Count == 0)
            {
                return;
            }

            foreach (ConnectionInfo server in servers)
            {
                if (server == null || string.IsNullOrWhiteSpace(server.ServerName))
                {
                    continue;
                }

                if (activeIndexingByServer.ContainsKey(server.ServerName))
                {
                    continue;
                }

                bool needsRefresh = !QuickSearchIndexStore.TryGetMeta(server.ServerName, out QuickSearchIndexMeta meta)
                    || (DateTime.UtcNow - meta.IndexedAtUtc) > IndexAutoRefreshMaxAge;

                if (needsRefresh)
                {
                    StartIndexing(server, silent: true);
                }
            }
        }

        private void AutoRefreshStaleIndexIfNeeded()
        {
            var server = ComboBox_Servers.SelectedItem as ConnectionInfo;
            if (server == null || string.IsNullOrWhiteSpace(server.ServerName))
            {
                return;
            }

            bool needsRefresh = !QuickSearchIndexStore.TryGetMeta(server.ServerName, out QuickSearchIndexMeta meta)
                || (DateTime.UtcNow - meta.IndexedAtUtc) > IndexAutoRefreshMaxAge;

            if (needsRefresh)
            {
                StartIndexing(server, silent: true);
            }
        }

        private void Button_RefreshConnections_Click(object sender, RoutedEventArgs e)
        {
            RefreshServerList(keepSelection: true);
        }

        private void RefreshServerList(bool keepSelection = false)
        {
            string previousServer = (ComboBox_Servers.SelectedItem as ConnectionInfo)?.ServerName;

            suppressSelectionEvents = true;
            try
            {
                List<ConnectionInfo> sessions = ScriptFactoryAccess.GetConnectedObjectExplorerSessions();
                ComboBox_Servers.ItemsSource = sessions;

                if (sessions.Count == 0)
                {
                    ComboBox_Servers.SelectedItem = null;
                    ComboBox_Databases.ItemsSource = null;
                    SearchInputsGrid.IsEnabled = false;
                    UpdateIndexStatus(null);
                    UpdateRefreshIndexButton();
                    return;
                }

                ConnectionInfo selected = null;
                if (keepSelection && !string.IsNullOrWhiteSpace(previousServer))
                {
                    selected = sessions.FirstOrDefault(s =>
                        string.Equals(s.ServerName, previousServer, StringComparison.OrdinalIgnoreCase));
                }

                ComboBox_Servers.SelectedItem = selected ?? sessions[0];
            }
            finally
            {
                suppressSelectionEvents = false;
            }

            LoadDatabasesForSelectedServer();
        }

        private void ComboBox_Servers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressSelectionEvents)
            {
                return;
            }

            LoadDatabasesForSelectedServer();
        }

        private void ComboBox_Databases_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSearchEnabled();
        }

        private void LoadDatabasesForSelectedServer()
        {
            var server = ComboBox_Servers.SelectedItem as ConnectionInfo;
            if (server == null)
            {
                ComboBox_Databases.ItemsSource = null;
                SearchInputsGrid.IsEnabled = false;
                UpdateIndexStatus(null);
                UpdateRefreshIndexButton();
                return;
            }

            try
            {
                List<string> databases = ScriptFactoryAccess.GetDatabases(server);
                var items = new List<DatabaseOptionItem>
                {
                    new DatabaseOptionItem(null, Strings.Get("QuickSearch_AllDatabasesOption"))
                };
                items.AddRange(databases.Select(name => new DatabaseOptionItem(name, name)));

                suppressSelectionEvents = true;
                ComboBox_Databases.ItemsSource = items;
                ComboBox_Databases.SelectedIndex = 0;
                suppressSelectionEvents = false;

                UpdateSearchEnabled();
                UpdateIndexStatus(server.ServerName);
                UpdateRefreshIndexButton();
            }
            catch (Exception ex)
            {
                ComboBox_Databases.ItemsSource = null;
                SearchInputsGrid.IsEnabled = false;
                UpdateRefreshIndexButton();
                MessageBox.Show(
                    string.Format(Strings.Get("Msg_QuickSearch_LoadDatabasesFailed"), ex.Message),
                    Strings.Get("Menu_QuickSearch"));
            }
        }

        private void UpdateSearchEnabled()
        {
            SearchInputsGrid.IsEnabled = ComboBox_Servers.SelectedItem is ConnectionInfo;
        }

        private void UpdateIndexStatus(string serverName)
        {
            if (!string.IsNullOrWhiteSpace(serverName) &&
                activeIndexingByServer.TryGetValue(serverName, out ServerIndexingState state))
            {
                TextBlock_IndexStatus.Text = string.IsNullOrEmpty(state.StatusText)
                    ? Strings.Get("QuickSearch_Indexing")
                    : state.StatusText;
                return;
            }

            if (string.IsNullOrWhiteSpace(serverName) || !QuickSearchIndexStore.TryGetMeta(serverName, out QuickSearchIndexMeta meta))
            {
                TextBlock_IndexStatus.Text = Strings.Get("QuickSearch_IndexNone");
                return;
            }

            DateTime local = meta.IndexedAtUtc.ToLocalTime();
            TextBlock_IndexStatus.Text = string.Format(
                Strings.Get("QuickSearch_IndexReady"),
                local.ToString("yyyy-MM-dd HH:mm"),
                meta.ObjectCount);
        }

        private void RefreshIndexStatusIfSelected(string serverName)
        {
            var selected = ComboBox_Servers.SelectedItem as ConnectionInfo;
            if (selected != null && string.Equals(selected.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
            {
                UpdateIndexStatus(serverName);
            }
        }

        private void UpdateRefreshIndexButton()
        {
            var server = ComboBox_Servers.SelectedItem as ConnectionInfo;
            bool indexing = server != null && activeIndexingByServer.ContainsKey(server.ServerName);
            Button_RefreshIndex.Content = indexing
                ? Strings.Get("Common_Cancel")
                : Strings.Get("QuickSearch_RefreshIndex");
        }

        private ConnectionInfo GetSelectedServerConnection()
        {
            return ComboBox_Servers.SelectedItem as ConnectionInfo;
        }

        private string GetSelectedDatabaseNameOrNull()
        {
            var item = ComboBox_Databases.SelectedItem as DatabaseOptionItem;
            return item?.Name;
        }

        private async void Button_Search_Click(object sender, RoutedEventArgs e)
        {
            await RunSearchAsync();
        }

        private async Task RunSearchAsync()
        {
            if (searchCancellationTokenSource != null)
            {
                searchCancellationTokenSource.Cancel();
                return;
            }

            ConnectionInfo server = GetSelectedServerConnection();
            if (server == null)
            {
                MessageBox.Show(Strings.Get("Msg_QuickSearch_SelectConnection"), Strings.Get("Menu_QuickSearch"));
                return;
            }

            string searchText = TextBox_SearchText.Text?.Trim();
            if (string.IsNullOrEmpty(searchText))
            {
                MessageBox.Show(Strings.Get("Msg_QuickSearch_EnterText"), Strings.Get("Menu_QuickSearch"));
                return;
            }

            if (!AnyTypeSelected())
            {
                MessageBox.Show(Strings.Get("Msg_QuickSearch_SelectObjectType"), Strings.Get("Menu_QuickSearch"));
                return;
            }

            searchCancellationTokenSource = new CancellationTokenSource();

            try
            {
                Button_Search.Content = Strings.Get("Common_Cancel");
                DataGrid_SearchResults.ItemsSource = null;
                SqlEditor.Text = string.Empty;
                TextBlock_ResultCount.Text = "Searching...";

                bool useWildcards = CheckBox_UseWildcards.IsChecked == true;
                bool includeProcs = CheckBox_StoredProcedures.IsChecked == true;
                bool includeViews = CheckBox_Views.IsChecked == true;
                bool includeFunctions = CheckBox_Functions.IsChecked == true;
                bool includeTables = CheckBox_Tables.IsChecked == true;
                bool includeAgentJobSteps = CheckBox_AgentJobSteps.IsChecked == true;
                string selectedDatabase = GetSelectedDatabaseNameOrNull();

                CancellationToken cancellationToken = searchCancellationTokenSource.Token;
                var progress = new Progress<string>(databaseName =>
                {
                    TextBlock_ResultCount.Text = $"Searching [{databaseName}]...";
                });

                DataTable results;
                if (QuickSearchIndexStore.HasIndex(server.ServerName))
                {
                    TextBlock_ResultCount.Text = Strings.Get("QuickSearch_SearchingIndex");
                    results = await Task.Run(() => SearchFromIndex(
                        server.ServerName,
                        searchText,
                        useWildcards,
                        selectedDatabase,
                        includeProcs,
                        includeViews,
                        includeFunctions,
                        includeTables,
                        includeAgentJobSteps), cancellationToken);
                }
                else
                {
                    results = await Task.Run(() => ExecuteSearchAsync(
                        server,
                        searchText,
                        selectedDatabase,
                        useWildcards,
                        includeProcs,
                        includeViews,
                        includeFunctions,
                        includeTables,
                        includeAgentJobSteps,
                        progress,
                        cancellationToken), cancellationToken);
                }

                DataGrid_SearchResults.ItemsSource = results.DefaultView;
                TextBlock_ResultCount.Text = $"{results.Rows.Count} result(s)";
            }
            catch (OperationCanceledException)
            {
                TextBlock_ResultCount.Text = "Search canceled";
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Operation cancelled by user."))
                {
                    TextBlock_ResultCount.Text = "Search canceled";
                }
                else
                {
                    MessageBox.Show(string.Format(Strings.Get("Msg_QuickSearch_SearchFailed"), ex.Message), Strings.Get("Menu_QuickSearch"));
                    TextBlock_ResultCount.Text = "Search failed";
                }
            }
            finally
            {
                searchCancellationTokenSource?.Dispose();
                searchCancellationTokenSource = null;
                Button_Search.Content = Strings.Get("Common_Search");
            }
        }

        private async void TextBox_SearchText_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            await RunSearchAsync();
        }

        private void Button_RefreshIndex_Click(object sender, RoutedEventArgs e)
        {
            ConnectionInfo server = GetSelectedServerConnection();
            if (server == null)
            {
                MessageBox.Show(Strings.Get("Msg_QuickSearch_SelectConnection"), Strings.Get("Menu_QuickSearch"));
                return;
            }

            if (activeIndexingByServer.TryGetValue(server.ServerName, out ServerIndexingState existing))
            {
                existing.CancellationSource.Cancel();
                return;
            }

            StartIndexing(server, silent: false);
        }

        private async void StartIndexing(ConnectionInfo server, bool silent)
        {
            string serverName = server.ServerName;
            if (string.IsNullOrWhiteSpace(serverName) || activeIndexingByServer.ContainsKey(serverName))
            {
                return;
            }

            var state = new ServerIndexingState
            {
                CancellationSource = new CancellationTokenSource(),
                StatusText = Strings.Get(silent ? "QuickSearch_IndexAutoRefreshing" : "QuickSearch_Indexing"),
                Silent = silent
            };
            activeIndexingByServer[serverName] = state;
            RefreshIndexStatusIfSelected(serverName);
            UpdateRefreshIndexButton();

            try
            {
                List<string> databases = ScriptFactoryAccess.GetDatabases(server);
                var progress = new Progress<string>(db =>
                {
                    state.StatusText = string.Format(Strings.Get("QuickSearch_IndexingDatabase"), db);
                    RefreshIndexStatusIfSelected(serverName);
                });

                await QuickSearchIndexStore.BuildIndexAsync(server, databases, progress, state.CancellationSource.Token);

                if (!silent)
                {
                    MessageBox.Show(Strings.Get("Msg_QuickSearch_IndexBuilt"), Strings.Get("Menu_QuickSearch"));
                }
            }
            catch (OperationCanceledException)
            {
                // 取消：状态栏在 finally 中回退到该服务器自身的索引状态
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show(string.Format(Strings.Get("Msg_QuickSearch_IndexFailed"), ex.Message), Strings.Get("Menu_QuickSearch"));
                }
            }
            finally
            {
                activeIndexingByServer.Remove(serverName);
                state.CancellationSource.Dispose();
                RefreshIndexStatusIfSelected(serverName);
                UpdateRefreshIndexButton();
            }
        }

        private DataTable SearchFromIndex(
            string serverName,
            string searchText,
            bool useWildcards,
            string selectedDatabase,
            bool includeProcs,
            bool includeViews,
            bool includeFunctions,
            bool includeTables,
            bool includeAgentJobSteps)
        {
            DataTable allResults = BuildResultTable();
            List<QuickSearchIndexEntry> entries = QuickSearchIndexStore.Search(
                serverName,
                searchText,
                useWildcards,
                selectedDatabase,
                includeProcs,
                includeViews,
                includeFunctions,
                includeTables,
                includeAgentJobSteps);

            foreach (QuickSearchIndexEntry entry in entries)
            {
                string preview = BuildPreview(entry.SourceText ?? string.Empty, searchText, useWildcards);
                allResults.Rows.Add(
                    entry.DatabaseName,
                    entry.ObjectType,
                    entry.SchemaName,
                    entry.ObjectName,
                    entry.MatchLocation,
                    preview,
                    entry.ScriptDatabaseName,
                    entry.ScriptSchemaName,
                    entry.ScriptObjectName,
                    entry.SourceText);
            }

            return allResults;
        }

        private async Task<DataTable> ExecuteSearchAsync(
            ConnectionInfo server,
            string searchText,
            string selectedDatabase,
            bool useWildcards,
            bool includeProcs,
            bool includeViews,
            bool includeFunctions,
            bool includeTables,
            bool includeAgentJobSteps,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            List<string> databases = GetDatabasesToSearch(server, selectedDatabase);
            DataTable allResults = BuildResultTable();
            var sync = new object();

            using (var gate = new SemaphoreSlim(MaxParallelDatabaseSearches))
            {
                var tasks = databases.Select(async dbName =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(dbName);

                        DataTable rows;
                        try
                        {
                            rows = await SearchDatabaseAsync(
                                server,
                                dbName,
                                searchText,
                                useWildcards,
                                includeProcs,
                                includeViews,
                                includeFunctions,
                                includeTables,
                                includeAgentJobSteps,
                                cancellationToken);
                        }
                        catch (SqlException)
                        {
                            return;
                        }

                        lock (sync)
                        {
                            foreach (DataRow row in rows.Rows)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                string sourceText = row["SourceText"]?.ToString() ?? string.Empty;
                                string preview = BuildPreview(sourceText, searchText, useWildcards);
                                allResults.Rows.Add(
                                    row["DatabaseName"],
                                    row["ObjectType"],
                                    row["SchemaName"],
                                    row["ObjectName"],
                                    row["MatchLocation"],
                                    preview,
                                    row["ScriptDatabaseName"],
                                    row["ScriptSchemaName"],
                                    row["ScriptObjectName"],
                                    sourceText);
                            }
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
            }

            return allResults;
        }

        private List<string> GetDatabasesToSearch(ConnectionInfo server, string selectedDatabase)
        {
            if (!string.IsNullOrWhiteSpace(selectedDatabase))
            {
                return new List<string> { selectedDatabase };
            }

            return ScriptFactoryAccess.GetDatabases(server);
        }

        private async Task<DataTable> SearchDatabaseAsync(
            ConnectionInfo server,
            string databaseName,
            string searchText,
            bool useWildcards,
            bool includeProcs,
            bool includeViews,
            bool includeFunctions,
            bool includeTables,
            bool includeAgentJobSteps,
            CancellationToken cancellationToken)
        {
            DataTable result = BuildResultTable();
            string pattern = BuildPattern(searchText, useWildcards);

            string combinedSql = @"
SELECT
    DB_NAME() AS DatabaseName,
    CASE
        WHEN o.[type] = 'P' THEN 'Stored Procedure'
        WHEN o.[type] = 'V' THEN 'View'
        ELSE 'Function'
    END AS ObjectType,
    s.[name] AS SchemaName,
    o.[name] AS ObjectName,
    'Definition' AS MatchLocation,
    m.[definition] AS SourceText,
    DB_NAME() AS ScriptDatabaseName,
    s.[name] AS ScriptSchemaName,
    o.[name] AS ScriptObjectName
FROM sys.objects o
INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
INNER JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE (
        (@includeProcs = 1 AND o.[type] = 'P') OR
        (@includeViews = 1 AND o.[type] = 'V') OR
        (@includeFunctions = 1 AND o.[type] IN ('FN', 'IF', 'TF'))
      )
  AND m.[definition] LIKE @pattern ESCAPE '!'
  AND o.is_ms_shipped = 0
UNION ALL
SELECT
    DB_NAME(),
    'Table',
    s.[name],
    t.[name],
    'Table Name',
    t.[name],
    DB_NAME(),
    s.[name],
    t.[name]
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE @includeTables = 1
  AND t.[name] LIKE @pattern ESCAPE '!'
UNION ALL
SELECT
    DB_NAME(),
    'Table',
    s.[name],
    t.[name],
    'Column',
    c.[name],
    DB_NAME(),
    s.[name],
    t.[name]
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
INNER JOIN sys.columns c ON c.object_id = t.object_id
WHERE @includeTables = 1
  AND c.[name] LIKE @pattern ESCAPE '!'
UNION ALL
SELECT
    DB_NAME(),
    CASE
        WHEN o.[type] = 'P' THEN 'Stored Procedure'
        WHEN o.[type] = 'V' THEN 'View'
        ELSE 'Function'
    END,
    s.[name],
    o.[name],
    'Parameter',
    p.[name],
    DB_NAME(),
    s.[name],
    o.[name]
FROM sys.parameters p
INNER JOIN sys.objects o ON o.object_id = p.object_id
INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE p.parameter_id > 0
  AND (
        (@includeProcs = 1 AND o.[type] = 'P') OR
        (@includeViews = 1 AND o.[type] = 'V') OR
        (@includeFunctions = 1 AND o.[type] IN ('FN', 'IF', 'TF'))
      )
  AND p.[name] LIKE @pattern ESCAPE '!';";

            string agentJobsSql = @"
SELECT
    'msdb' AS DatabaseName,
    'SQL Agent Job Step' AS ObjectType,
    'dbo' AS SchemaName,
    j.[name] + N' / Step ' + CONVERT(varchar(12), js.step_id) + N' - ' + js.step_name AS ObjectName,
    'JobStep' AS MatchLocation,
    js.[command] AS SourceText,
    N'msdb' AS ScriptDatabaseName,
    N'dbo' AS ScriptSchemaName,
    j.[name] AS ScriptObjectName
FROM dbo.sysjobs j
INNER JOIN dbo.sysjobsteps js ON js.job_id = j.job_id
WHERE js.[command] LIKE @pattern ESCAPE '!'
   OR js.step_name LIKE @pattern ESCAPE '!'
   OR j.[name] LIKE @pattern;";

            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(server.FullConnectionString)
            {
                InitialCatalog = databaseName
            };

            using (var conn = new SqlConnection(builder.ConnectionString))
            {
                await conn.OpenAsync(cancellationToken);
                await ExecuteSearchQueryAsync(conn, combinedSql, pattern, includeProcs, includeViews, includeFunctions, includeTables, result, cancellationToken);

                if (databaseName == "msdb" && includeAgentJobSteps)
                {
                    await ExecuteSearchQueryAsync(conn, agentJobsSql, pattern, includeProcs, includeViews, includeFunctions, includeTables, result, cancellationToken);
                }
            }

            return result;
        }

        private static async Task ExecuteSearchQueryAsync(SqlConnection conn, string sql, string pattern, bool includeProcs, bool includeViews, bool includeFunctions, bool includeTables, DataTable aggregateResult, CancellationToken cancellationToken)
        {
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.CommandTimeout = 120;
                cmd.Parameters.AddWithValue("@pattern", pattern);
                cmd.Parameters.AddWithValue("@includeProcs", includeProcs ? 1 : 0);
                cmd.Parameters.AddWithValue("@includeViews", includeViews ? 1 : 0);
                cmd.Parameters.AddWithValue("@includeFunctions", includeFunctions ? 1 : 0);
                cmd.Parameters.AddWithValue("@includeTables", includeTables ? 1 : 0);

                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                {
                    if (reader.HasRows)
                    {
                        var chunk = new DataTable();
                        chunk.Load(reader);
                        aggregateResult.Merge(chunk, true, MissingSchemaAction.Add);
                    }
                }
            }
        }

        private static string BuildPattern(string text, bool useWildcards)
        {
            string escaped = text.Replace("!", "!!");

            if (useWildcards)
            {
                return escaped;
            }

            escaped = escaped
                .Replace("%", "!%")
                .Replace("_", "!_")
                .Replace("[", "![");

            return $"%{escaped}%";
        }

        private static string BuildPreview(string sourceText, string searchText, bool useWildcards)
        {
            if (string.IsNullOrEmpty(sourceText))
            {
                return string.Empty;
            }

            if (!useWildcards)
            {
                int index = sourceText.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    int start = Math.Max(0, index - 40);
                    int length = Math.Min(sourceText.Length - start, searchText.Length + 80);
                    string snippet = sourceText.Substring(start, length).Replace(Environment.NewLine, " ");
                    int snippetIndex = snippet.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
                    if (snippetIndex >= 0)
                    {
                        return snippet.Substring(0, snippetIndex) + "[" + snippet.Substring(snippetIndex, searchText.Length) + "]" + snippet.Substring(snippetIndex + searchText.Length);
                    }

                    return snippet;
                }
            }

            return sourceText.Length > 120
                ? sourceText.Substring(0, 120).Replace(Environment.NewLine, " ") + "..."
                : sourceText.Replace(Environment.NewLine, " ");
        }

        private void CheckBox_WholeWord_Checked(object sender, RoutedEventArgs e)
        {
            if (CheckBox_WholeWord.IsChecked == true)
            {
                CheckBox_UseWildcards.IsChecked = false;
            }
        }

        private void CheckBox_UseWildcards_Checked(object sender, RoutedEventArgs e)
        {
            if (CheckBox_UseWildcards.IsChecked == true)
            {
                CheckBox_WholeWord.IsChecked = false;
            }
        }

        private bool AnyTypeSelected()
        {
            return CheckBox_StoredProcedures.IsChecked == true
                || CheckBox_Views.IsChecked == true
                || CheckBox_Functions.IsChecked == true
                || CheckBox_Tables.IsChecked == true
                || CheckBox_AgentJobSteps.IsChecked == true;
        }

        private void Button_ScriptResult_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(sender is Button button) || !(button.DataContext is DataRowView rowView))
            {
                return;
            }

            try
            {
                ConnectionInfo server = GetSelectedServerConnection();
                if (server == null)
                {
                    MessageBox.Show(Strings.Get("Msg_QuickSearch_SelectConnection"), Strings.Get("Menu_QuickSearch"));
                    return;
                }

                string databaseName = rowView["ScriptDatabaseName"]?.ToString();
                string schemaName = rowView["ScriptSchemaName"]?.ToString();
                string objectName = rowView["ScriptObjectName"]?.ToString();
                string matchLocation = rowView["MatchLocation"]?.ToString();

                if (matchLocation == "JobStep")
                {
                    MessageBox.Show(Strings.Get("Msg_QuickSearch_Wip"), Strings.Get("Menu_QuickSearch"));
                    return;
                }

                ConnectionInfo scriptConnection = ScriptFactoryAccess.CloneWithDatabase(server, databaseName);
                string selectedObjectName = $"[{databaseName}].[{schemaName}].[{objectName}]";
                string fullScriptResult = ScriptObjectDefinition.GetText(
                    AxialSqlToolsPackage.PackageInstance,
                    selectedObjectName,
                    scriptConnection);

                if (string.IsNullOrEmpty(fullScriptResult))
                {
                    return;
                }

                var uiConnection = scriptConnection.ActiveConnectionInfo;
                if (uiConnection == null)
                {
                    var builder = new SqlConnectionStringBuilder(scriptConnection.FullConnectionString);
                    uiConnection = ScriptFactoryAccess.TryCreateUiConnectionInfo(
                        builder,
                        builder.UserID,
                        builder.Password,
                        string.Empty);
                }

                if (uiConnection != null)
                {
                    ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, uiConnection, null);
                }
                else
                {
                    ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql);
                }

                EnvDTE.TextDocument doc = (EnvDTE.TextDocument)ServiceCache.ExtensibilityModel.Application.ActiveDocument.Object(null);
                doc.EndPoint.CreateEditPoint().Insert(fullScriptResult);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Strings.Get("Msg_QuickSearch_ScriptFailed"), ex.Message), Strings.Get("Msg_QuickSearch_ScriptObjectTitle"));
            }
        }

        private void DataGrid_SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(DataGrid_SearchResults.SelectedItem is DataRowView rowView))
            {
                return;
            }

            try
            {
                SqlEditor.Text = rowView["SourceText"]?.ToString();
                textMarkerService.RemoveAll();

                string input = TextBox_SearchText.Text ?? string.Empty;
                Regex regex;

                if (CheckBox_UseWildcards.IsChecked == true)
                {
                    string regexPattern = Regex.Escape(input)
                        .Replace(@"\%", ".*")
                        .Replace(@"\_", ".");
                    regex = new Regex(regexPattern, RegexOptions.IgnoreCase);
                }
                else
                {
                    regex = new Regex(Regex.Escape(input), RegexOptions.IgnoreCase);
                }

                foreach (Match match in regex.Matches(SqlEditor.Text))
                {
                    var marker = textMarkerService.Create(match.Index, match.Length);
                    marker.BackgroundColor = System.Windows.Media.Colors.Yellow;
                    marker.ForegroundColor = System.Windows.Media.Colors.Black;
                }
            }
            catch
            {
                SqlEditor.Text = string.Empty;
            }
        }

        private static DataTable BuildResultTable()
        {
            var table = new DataTable();
            table.Columns.Add("DatabaseName", typeof(string));
            table.Columns.Add("ObjectType", typeof(string));
            table.Columns.Add("SchemaName", typeof(string));
            table.Columns.Add("ObjectName", typeof(string));
            table.Columns.Add("MatchLocation", typeof(string));
            table.Columns.Add("MatchPreview", typeof(string));
            table.Columns.Add("ScriptDatabaseName", typeof(string));
            table.Columns.Add("ScriptSchemaName", typeof(string));
            table.Columns.Add("ScriptObjectName", typeof(string));
            table.Columns.Add("SourceText", typeof(string));
            return table;
        }

        private sealed class DatabaseOptionItem
        {
            public DatabaseOptionItem(string name, string displayName)
            {
                Name = name;
                DisplayName = displayName;
            }

            public string Name { get; }
            public string DisplayName { get; }
        }
    }
}
