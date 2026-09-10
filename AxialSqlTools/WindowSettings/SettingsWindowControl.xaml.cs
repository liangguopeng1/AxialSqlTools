namespace AxialSqlTools
{
    using Microsoft.Data.SqlClient;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.IO.Compression;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Collections.ObjectModel;
    using System.Windows.Documents;
    using System.Windows.Media;
    using System.Windows.Navigation;
    using Microsoft.VisualBasic;
    using UiStrings = AxialSqlTools.Properties.Strings;
    using static AxialSqlTools.AxialSqlToolsPackage;

    /// <summary>
    /// Interaction logic for SettingsWindowControl.
    /// </summary>
    public partial class SettingsWindowControl : UserControl
    {
        private const string QueryHistoryStorageModeDatabase = "Database";
        private const string QueryHistoryStorageModeTextFiles = "TextFiles";
        private const string QueryHistoryStorageModeDisabled = "Disabled";

        private string _queryHistoryConnectionString;
        private readonly ToolWindowThemeController _themeController;
        private bool updateResultSubscribed;
        private ObservableCollection<SettingsManager.ConnectionColorRule> _connectionColorRules;

        private string tsqlFormatExample = @"while (1=0) 
begin 
select top 10
    c.CustomerID, getDate(),
    CASE WHEN o.TotalAmount > 1000 THEN 'High' ELSE 'Low' END AS OrderSize
FROM Customers c
JOIN Orders o ON c.CustomerID = o.CustomerID CROSS JOIN Regions r
WHERE c.IsActive = 1;

SELECT dbo.func(p.ProductID), p.ProductName FROM Products p; EXEC dbo.test @a = 0, @b = 1;
end
if 1=0 begin select 1; declare @a int, @b varchar(10) = ''
end
go
create procedure dbo.test @a int, @b int = 0
as select 1;
";
        /// <summary>
        /// Initializes a new instance of the <see cref="SettingsWindowControl"/> class.
        /// </summary>
        public SettingsWindowControl()
        {
            this.InitializeComponent();

            _connectionColorRules = new ObservableCollection<SettingsManager.ConnectionColorRule>();
            ConnectionColorRulesListView.ItemsSource = _connectionColorRules;

            _themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);

            this.Loaded += UserControl_Loaded;
            this.Unloaded += UserControl_Unloaded;
            this.IsVisibleChanged += (s, e) =>
            {
                if (this.IsVisible)
                {
                    try { AxialSqlTools.IntelliSense.IntelliSenseManager.CloseAllPopups(); } catch { }
                }
            };

            SourceQueryPreview.Text = tsqlFormatExample;

            formatTSqlExample();
            ApplyLocalizedTexts();
            LoadLanguageCombo();
            LoadLogLevelCombo();
            ShowBuildTime();
        }

        private void ApplyLocalizedTexts()
        {
            SettingsTitleText.Text = UiStrings.Settings_WindowTitle;
            TabGeneral.Header = UiStrings.Settings_Tab_General;
            TabQueryTemplates.Header = UiStrings.Settings_Tab_QueryTemplates;
            TabCodeSnippets.Header = UiStrings.Settings_Tab_CodeSnippets;
            TabQueryHistory.Header = UiStrings.Settings_Tab_QueryHistory;
            TabCodeFormat.Header = UiStrings.Settings_Tab_CodeFormat;
            TabExcelExport.Header = UiStrings.Settings_Tab_ExcelExport;
            TabGoogleSheets.Header = UiStrings.Settings_Tab_GoogleSheets;
            TabConnectionColors.Header = UiStrings.Settings_Tab_ConnectionColors;
            TabSmtp.Header = UiStrings.Settings_Tab_Smtp;
            TabUpdates.Header = UiStrings.Settings_Tab_Updates;
            LanguageLabelText.Text = UiStrings.Settings_LanguageLabel;
            LanguageHintText.Text = UiStrings.Settings_LanguageHint;
            UpdateSettingsWindowCaption();
            UiLocalization.Apply(this);
            LocalizeWikiDescriptions();
            LocalizeGoogleSheetsAuth();
            LocalizeUsefulTsqlScripts();
            ApplyQueryHistoryStorageComboItems();
            ApplyConnectionColorGridHeaders();
            RefreshGoogleSheetsStatusText();
            if (UpdateReleasesLinkRun != null)
                UpdateReleasesLinkRun.Text = UiStrings.Get("Settings_Updates_ViewReleases");
            ShowBuildTime();
            UpdateUpdateStatus();
        }

        private void LocalizeWikiDescriptions()
        {
            LocalizeWikiDescription(WikiQueryTemplatesTextBlock, "https://github.com/liangguopeng1/AxialSqlTools/wiki/Query-Templates-and-Snippets");
            LocalizeWikiDescription(WikiCodeSnippetsTextBlock, "https://github.com/liangguopeng1/AxialSqlTools/wiki/Query-Templates-and-Snippets");
            LocalizeWikiDescription(WikiQueryHistoryTextBlock, "https://github.com/liangguopeng1/AxialSqlTools/wiki/Query-History");
            LocalizeWikiDescription(WikiCodeFormatTextBlock, "https://github.com/liangguopeng1/AxialSqlTools/wiki/TSQL-Code-Formatting-with-Microsoft-ScriptDOM-library");
            LocalizeWikiDescription(WikiExcelExportTextBlock, "https://github.com/liangguopeng1/AxialSqlTools/wiki/Export-Grid-To-Excel");
            LocalizeWikiDescription(WikiSmtpTextBlock, "https://github.com/liangguopeng1/AxialSqlTools/wiki/Export-Grid-to-Email");
        }

        private void LocalizeWikiDescription(TextBlock textBlock, string uri)
        {
            if (textBlock == null)
            {
                return;
            }
            textBlock.Inlines.Clear();
            textBlock.Inlines.Add(new Run(UiStrings.Get("Common_FeatureDescriptionIn")));
            textBlock.Inlines.Add(new Run(" "));
            var wikiLink = new Hyperlink(new Run(UiStrings.Get("Common_Wiki")))
            {
                NavigateUri = new Uri(uri)
            };
            wikiLink.RequestNavigate += buttonWikiPage_Click;
            textBlock.Inlines.Add(wikiLink);
        }

        private void LocalizeGoogleSheetsAuth()
        {
            if (GoogleSheetsAuthTextBlock == null)
            {
                return;
            }
            GoogleSheetsAuthTextBlock.Inlines.Clear();
            GoogleSheetsAuthTextBlock.Inlines.Add(new Run(UiStrings.Get("Settings_GoogleAuthPrefix")));
            var consoleLink = new Hyperlink(new Run(UiStrings.Get("Settings_GoogleCloudConsole")))
            {
                NavigateUri = new Uri("https://console.cloud.google.com/apis/credentials")
            };
            consoleLink.RequestNavigate += buttonWikiPage_Click;
            GoogleSheetsAuthTextBlock.Inlines.Add(consoleLink);
            GoogleSheetsAuthTextBlock.Inlines.Add(new Run(UiStrings.Get("Settings_GoogleAuthSuffix")));
        }

        private void LocalizeUsefulTsqlScripts()
        {
            if (UsefulTsqlScriptsTextBlock == null)
            {
                return;
            }
            const string libraryUri = "https://github.com/liangguopeng1/AxialSqlTools/tree/main/query-library";
            UsefulTsqlScriptsTextBlock.Inlines.Clear();
            UsefulTsqlScriptsTextBlock.Inlines.Add(new Run(UiStrings.Get("Settings_UsefulTsqlScripts")));
            UsefulTsqlScriptsTextBlock.Inlines.Add(new Run(" "));
            var libraryLink = new Hyperlink(new Run(libraryUri))
            {
                NavigateUri = new Uri(libraryUri)
            };
            libraryLink.RequestNavigate += buttonWikiPage_Click;
            UsefulTsqlScriptsTextBlock.Inlines.Add(libraryLink);
        }

        private void RefreshGoogleSheetsStatusText()
        {
            try
            {
                UpdateGoogleSheetsStatus(SettingsManager.GetGoogleSheetsSettings().refreshToken);
            }
            catch
            {
            }
        }

        private void ApplyQueryHistoryStorageComboItems()
        {
            foreach (ComboBoxItem item in QueryHistoryStorageType.Items)
            {
                var tag = item.Tag as string;
                if (tag == QueryHistoryStorageModeDisabled)
                {
                    item.Content = UiStrings.Get("Settings_StorageDisabled");
                }
                else if (tag == QueryHistoryStorageModeDatabase)
                {
                    item.Content = UiStrings.Get("Settings_StorageDatabase");
                }
                else if (tag == QueryHistoryStorageModeTextFiles)
                {
                    item.Content = UiStrings.Get("Settings_StorageTextFiles");
                }
            }
        }

        private void ApplyConnectionColorGridHeaders()
        {
            if (ConnectionColorRulesListView?.View is GridView gridView && gridView.Columns.Count >= 4)
            {
                gridView.Columns[1].Header = UiStrings.Get("Settings_ServerContains");
                gridView.Columns[2].Header = UiStrings.Get("Settings_DatabaseContains");
                gridView.Columns[3].Header = UiStrings.Get("Settings_Color");
            }
        }

        private void UpdateSettingsWindowCaption()
        {
            try
            {
                var package = AxialSqlToolsPackage.PackageInstance;
                if (package == null)
                {
                    return;
                }
                var window = package.FindToolWindow(typeof(SettingsWindow), 0, false) as SettingsWindow;
                if (window != null)
                {
                    window.Caption = UiStrings.Settings_ToolWindowCaption;
                }
            }
            catch
            {
            }
        }

        private void ShowBuildTime()
        {
            if (TextBlock_BuildTime == null)
            {
                return;
            }

            string raw = BuildInfo.BuildTime;
            if (string.IsNullOrWhiteSpace(raw) || raw == "BUILD_TIME_PLACEHOLDER")
            {
                TextBlock_BuildTime.Text = UiStrings.Get("Settings_BuildTimeUnknown");
                return;
            }
            TextBlock_BuildTime.Text = raw;
        }

        private void LoadLanguageCombo()
        {
            UiLanguageCombo.Items.Clear();
            UiLanguageCombo.Items.Add(new ComboBoxItem
            {
                Content = UiStrings.Settings_Language_Chinese,
                Tag = UiSettingsStore.LanguageZhCn
            });
            UiLanguageCombo.Items.Add(new ComboBoxItem
            {
                Content = UiStrings.Settings_Language_English,
                Tag = UiSettingsStore.LanguageEn
            });
            var current = UiSettingsStore.GetUiLanguage();
            foreach (ComboBoxItem item in UiLanguageCombo.Items)
            {
                if (string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase))
                {
                    UiLanguageCombo.SelectedItem = item;
                    break;
                }
            }
            if (UiLanguageCombo.SelectedItem == null && UiLanguageCombo.Items.Count > 0)
            {
                UiLanguageCombo.SelectedIndex = 0;
            }
        }

        private void LoadLogLevelCombo()
        {
            LogLevelCombo.Items.Clear();
            LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Debug（详细）", Tag = UiSettingsStore.LogLevelDebug });
            LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Info（默认）", Tag = UiSettingsStore.LogLevelInfo });
            LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Warn", Tag = UiSettingsStore.LogLevelWarn });
            LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Error", Tag = UiSettingsStore.LogLevelError });
            var current = UiSettingsStore.GetLogLevel();
            foreach (ComboBoxItem item in LogLevelCombo.Items)
            {
                if (string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase))
                {
                    LogLevelCombo.SelectedItem = item;
                    break;
                }
            }
            if (LogLevelCombo.SelectedItem == null)
                LogLevelCombo.SelectedIndex = 1;
        }

        private void Button_SaveGeneral_Click(object sender, RoutedEventArgs e)
        {
            var logItem = LogLevelCombo.SelectedItem as ComboBoxItem;
            var level = logItem?.Tag as string ?? UiSettingsStore.LogLevelInfo;
            UiSettingsStore.SaveLogLevel(level);
            AxialSqlToolsPackage.ApplyLogMinLevel(level);
            var langItem = UiLanguageCombo.SelectedItem as ComboBoxItem;
            var language = langItem?.Tag as string ?? UiSettingsStore.LanguageZhCn;
            UiSettingsStore.SaveUiLanguage(language);
            UiCultureService.Apply(language);
            ApplyLocalizedTexts();
            LoadLanguageCombo();
            LoadLogLevelCombo();
            // Re-apply toolbar/menu captions for the new language (VSCT starts English; Chinese overlays must be reversible).
            MenuTextLocalizer.ApplyFromIde();
            SavedMessage();
        }

        private void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            try { AxialSqlTools.IntelliSense.IntelliSenseManager.CloseAllPopups(); } catch { }
            SubscribeToUpdateResultChanges();
            LoadSavedSettings();
        }

        private void UserControl_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            UnsubscribeFromUpdateResultChanges();
        }

        private void SubscribeToUpdateResultChanges()
        {
            if (updateResultSubscribed)
            {
                return;
            }

            UpdateChecker.LastUpdateResultChanged += UpdateChecker_LastUpdateResultChanged;
            updateResultSubscribed = true;
        }

        private void UnsubscribeFromUpdateResultChanges()
        {
            if (!updateResultSubscribed)
            {
                return;
            }

            UpdateChecker.LastUpdateResultChanged -= UpdateChecker_LastUpdateResultChanged;
            updateResultSubscribed = false;
        }

        private void ApplyThemeBrushResources()
        {
            ToolWindowThemeResources.ApplySharedTheme(this);

            ApplyGoogleSheetsAuthorizationBrush();
        }

        private Brush GetThemedStatusBrush(bool isSuccess)
        {
            string key = isSuccess ? "AxialThemeStatusSuccessBrush" : "AxialThemeStatusErrorBrush";
            return Resources[key] as Brush
                ?? (isSuccess ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)) : new SolidColorBrush(Color.FromRgb(0xA1, 0x26, 0x0D)));
        }

        private void ApplyGoogleSheetsAuthorizationBrush()
        {
            if (GoogleSheetsRefreshTokenLabel == null)
            {
                return;
            }

            bool isAuthorized = string.Equals(GoogleSheetsRefreshTokenLabel.Text, UiStrings.Get("Settings_Authorized"), StringComparison.OrdinalIgnoreCase);
            GoogleSheetsRefreshTokenLabel.Foreground = GetThemedStatusBrush(isAuthorized);
        }

        private void LoadSavedSettings()
        {
            try
            {

                ScriptFolder.Text = SettingsManager.GetTemplatesFolder();

                var snippetSettings = SettingsManager.GetSnippetSettings();
                UseSnippets.IsChecked = snippetSettings.useSnippets;
                SnippetFolder.Text = snippetSettings.snippetFolder;
                SnippetReplaceMode.SelectedValue = snippetSettings.replaceKey.ToString();
                var asteriskSettings = SettingsManager.GetAsteriskExpansionSettings();
                UseAsteriskExpansion.IsChecked = asteriskSettings.useAsteriskExpansion;
                AsteriskExpansionTriggerMode.SelectedValue = asteriskSettings.triggerKey.ToString();

                _queryHistoryConnectionString = SettingsManager.GetQueryHistoryConnectionString();
                QueryHistoryTableName.Text = SettingsManager.GetQueryHistoryTableName();
                QueryHistoryTextFilesInfo.Text = SettingsManager.GetQueryHistoryTextFileFolder();
                SelectQueryHistoryStorageType(SettingsManager.GetQueryHistoryStorageMode());
                UpdateQueryHistoryStorageControls();
                UpdateQueryHistoryConnectionDetails();

                RefreshQueryHistoryCreateScript();

                MyEmailAddress.Text = SettingsManager.GetMyEmail();

                SettingsManager.SmtpSettings smtpSettings = SettingsManager.GetSmtpSettings();

                SMTP_Server.Text = smtpSettings.ServerName;
                SMTP_Port.Text = smtpSettings.Port.ToString();
                SMTP_UserName.Text = smtpSettings.Username;
                SMTP_Password.Password = smtpSettings.Password;
                SMTP_EnableSSL.IsChecked = smtpSettings.EnableSsl;

                var tsqlCodeFormatSettings = SettingsManager.GetTSqlCodeFormatSettings();
                PreserveComments.IsChecked = tsqlCodeFormatSettings.preserveComments;
                RemoveNewLineAfterJoin.IsChecked = tsqlCodeFormatSettings.removeNewLineAfterJoin;
                AddTabAfterJoinOn.IsChecked = tsqlCodeFormatSettings.addTabAfterJoinOn;
                MoveCrossJoinToNewLine.IsChecked = tsqlCodeFormatSettings.moveCrossJoinToNewLine;
                FormatCaseAsMultiline.IsChecked = tsqlCodeFormatSettings.formatCaseAsMultiline;
                AddNewLineBetweenStatementsInBlocks.IsChecked = tsqlCodeFormatSettings.addNewLineBetweenStatementsInBlocks;
                BreakSprocParametersPerLine.IsChecked = tsqlCodeFormatSettings.breakSprocParametersPerLine;
                UppercaseBuiltInFunctions.IsChecked = tsqlCodeFormatSettings.uppercaseBuiltInFunctions;
                UnindentBeginEndBlocks.IsChecked = tsqlCodeFormatSettings.unindentBeginEndBlocks;
                BreakVariableDefinitionsPerLine.IsChecked = tsqlCodeFormatSettings.breakVariableDefinitionsPerLine;  
                BreakSprocDefinitionParametersPerLine.IsChecked = tsqlCodeFormatSettings.breakSprocDefinitionParametersPerLine;
                // BreakSelectFieldsAfterTopAndUnindent.IsChecked = tsqlCodeFormatSettings.breakSelectFieldsAfterTopAndUnindent;

                OpenAiApiKey.Password = SettingsManager.GetOpenAiApiKey();

                // Excel export settings
                var excelSettings = SettingsManager.GetExcelExportSettings();
                ExcelExportIncludeSourceQuery.IsChecked = excelSettings.includeSourceQuery;
                ExcelExportAddAutoFilter.IsChecked = excelSettings.addAutofilter;
                ExcelExportBoolsAsNumbers.IsChecked = excelSettings.exportBoolsAsNumbers;
                ExcelExportDefaultDirectory.Text = excelSettings.defaultDirectory;
                ExcelExportDefaultFilename.Text = excelSettings.defaultFileName;

                var googleSettings = SettingsManager.GetGoogleSheetsSettings();
                GoogleSheetsIncludeSourceQuery.IsChecked = googleSettings.includeSourceQuery;
                GoogleSheetsExportBoolsAsNumbers.IsChecked = googleSettings.exportBoolsAsNumbers;
                GoogleSheetsDefaultSpreadsheetName.Text = googleSettings.defaultSpreadsheetName;
                GoogleSheetsClientId.Text = googleSettings.clientId;
                GoogleSheetsClientSecret.Password = googleSettings.clientSecret;
                UpdateGoogleSheetsStatus(googleSettings.refreshToken);

                EnableUpdateChecks.IsChecked = SettingsManager.GetEnableUpdateChecks();
                UpdateUpdateStatus();

                LoadConnectionColorRules();

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred while loading settings");

                string msg = string.Format(UiStrings.Get("Msg_Settings_LoadError"), ex.Message, ex.InnerException);
                MessageBox.Show(msg, UiStrings.Common_Error);
            }

            try
            {
                GitHubToken.Password = WindowsCredentialHelper.LoadToken("AxialSqlTools_GitHubToken");
            }
            catch 
            {
                // ??
            }

        }

        private void UpdateQueryHistoryConnectionDetails()
        {

            if (string.IsNullOrWhiteSpace(_queryHistoryConnectionString))
            {
                Label_QueryHistoryConnectionInfo.Text = UiStrings.Get("Settings_NotConfigured");
            }
            else
            {
                try
                {

                    SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(_queryHistoryConnectionString);

                    string msg = string.Format("Server: {0}; Database: {1}; User ID: {2}", builder.DataSource, builder.InitialCatalog, builder.UserID);

                    Label_QueryHistoryConnectionInfo.Text = msg;

                }
                catch (Exception ex)
                {
                    Label_QueryHistoryConnectionInfo.Text = ex.Message;
                }
            }

            // IntelliSense 设置加载
            try
            {
                var isettings = UiSettingsStore.GetIntelliSenseSettings();
                IntelliSenseEnabled.IsChecked = isettings.enabled;
                IntelliSenseHoverTooltip.IsChecked = isettings.hoverTooltipEnabled;
                IntelliSenseIncludeKeywords.IsChecked = isettings.includeKeywords;
                IntelliSenseIncludeSystemObjects.IsChecked = isettings.includeSystemObjects;
                IntelliSenseBracketIdentifiers.IsChecked = isettings.bracketIdentifiers;
                IntelliSenseAutoTriggerDelay.Text = isettings.autoTriggerDelayMs.ToString();
                IntelliSenseHoverDelay.Text = isettings.hoverTooltipDelayMs.ToString();
                IntelliSenseMaxItems.Text = isettings.maxCompletionItems.ToString();
                IntelliSenseCacheRefreshDays.Text = isettings.cacheRefreshDays.ToString();
            }
            catch { }

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

        }

        private void Button_SaveScriptFolder_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.SaveTemplatesFolder(ScriptFolder.Text);

            SavedMessage();
        }

        private void Button_SaveIntelliSenseSettings_Click(object sender, RoutedEventArgs e)
        {
            bool axialEnabled = IntelliSenseEnabled.IsChecked.GetValueOrDefault();
            var current = UiSettingsStore.GetIntelliSenseSettings() ?? new AxialSqlTools.IntelliSense.IntelliSenseSettings();
            current.enabled = axialEnabled;
            current.disableSsmsIntelliSense = axialEnabled;
            current.autoTrigger = axialEnabled;
            current.hoverTooltipEnabled = IntelliSenseHoverTooltip.IsChecked.GetValueOrDefault();
            current.includeKeywords = IntelliSenseIncludeKeywords.IsChecked.GetValueOrDefault();
            current.includeSystemObjects = IntelliSenseIncludeSystemObjects.IsChecked.GetValueOrDefault();
            current.bracketIdentifiers = IntelliSenseBracketIdentifiers.IsChecked.GetValueOrDefault();
            current.autoTriggerDelayMs = int.TryParse(IntelliSenseAutoTriggerDelay.Text, out var d1) ? d1 : 200;
            current.hoverTooltipDelayMs = int.TryParse(IntelliSenseHoverDelay.Text, out var d2) ? d2 : 500;
            current.maxCompletionItems = int.TryParse(IntelliSenseMaxItems.Text, out var mi) ? mi : 50;
            int days = int.TryParse(IntelliSenseCacheRefreshDays.Text, out var d3) ? d3 : 7;
            if (days < 0) days = 0;
            if (days > 365) days = 365;
            current.cacheRefreshDays = days;
            UiSettingsStore.SaveIntelliSenseSettings(current);
            AxialSqlTools.IntelliSense.IntelliSenseKeyHandler.EnsureAllHoverTimers();
            bool ssmsIntelliSenseEnabled = !axialEnabled;
            if (!IntelliSense.IntelliSenseDisableHelper.TrySetSsmsIntelliSenseEnabled(ssmsIntelliSenseEnabled, out string ssmsError))
            {
                string action = ssmsIntelliSenseEnabled ? "恢复" : "禁用";
                MessageBox.Show(
                    "插件设置已保存，但未能" + action + " SSMS settings.json 中的系统 IntelliSense。\n" +
                    (string.IsNullOrEmpty(ssmsError)
                        ? "请手动修改 languages.sql.intelliSense.enableIntellisense 后重启 SSMS。"
                        : ssmsError),
                    UiStrings.Common_Error,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                IntelliSense.IntelliSenseDisableHelper.ScheduleSyncRetries(ssmsIntelliSenseEnabled);
            }
            SavedMessage();
        }

        private async void Button_RefreshIntelliSenseCache_Click(object sender, RoutedEventArgs e)
        {
            ScriptFactoryAccess.ConnectionInfo conn = null;
            try
            {
                conn = ScriptFactoryAccess.GetConnectionInfoForCacheRefresh();
            }
            catch
            {
            }
            if (conn == null || string.IsNullOrWhiteSpace(conn.ServerName))
            {
                MessageBox.Show("请先在对象资源管理器中选中服务器，或打开已连接的查询标签页。", UiStrings.Common_Error, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var button = sender as Button;
            if (button != null)
                button.IsEnabled = false;
            try
            {
                await AxialSqlTools.IntelliSense.MetadataCacheRefreshService.Instance.RefreshWithProgressWindowAsync(conn);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, UiStrings.Common_Error, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (button != null)
                    button.IsEnabled = true;
            }
        }

        private void Button_SaveSnippetFolder_Click(object sender, RoutedEventArgs e)
        {
            var snippetSettings = SettingsManager.GetSnippetSettings();
            snippetSettings.useSnippets = UseSnippets.IsChecked.GetValueOrDefault();
            snippetSettings.snippetFolder = SnippetFolder.Text;
            snippetSettings.replaceKey = GetSelectedSnippetReplaceKey();

            SettingsManager.SaveSnippetSettings(snippetSettings);

            SettingsManager.SaveAsteriskExpansionSettings(new SettingsManager.AsteriskExpansionSettings
            {
                useAsteriskExpansion = UseAsteriskExpansion.IsChecked.GetValueOrDefault(),
                triggerKey = GetSelectedAsteriskExpansionTriggerKey()
            });

            SavedMessage();
        }

        private void buttonDownloadAxialScripts_Click(object sender, RoutedEventArgs e)
        {
            string repoUrl = "https://github.com/liangguopeng1/AxialSqlTools/archive/main.zip";
            string targetFolderPath = "AxialSqlTools-main/query-library"; // Relative path inside the zip
            string targetPath = SettingsManager.GetTemplatesFolder();

            try
            {
                // Download the repo zip
                string tempZipPath = DownloadGitHubRepoZip(repoUrl);

                // Extract the specific folder from the zip
                ExtractSpecificFolderFromZip(tempZipPath, targetFolderPath, targetPath);

                MessageBox.Show(UiStrings.Get("Msg_Settings_QueryLibraryDownloaded"), UiStrings.Common_Done);

            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(System.Globalization.CultureInfo.CurrentUICulture, UiStrings.Get("Msg_Settings_DownloadFailed"), ex.Message),
                    UiStrings.Common_Error);
            }

        }

        private SettingsManager.SnippetReplaceKey GetSelectedSnippetReplaceKey()
        {
            var selectedValue = SnippetReplaceMode.SelectedValue as string;
            if (Enum.TryParse(selectedValue, out SettingsManager.SnippetReplaceKey key))
            {
                return key;
            }

            return SettingsManager.SnippetReplaceKey.Enter;
        }

        private SettingsManager.SnippetReplaceKey GetSelectedAsteriskExpansionTriggerKey()
        {
            var selectedValue = AsteriskExpansionTriggerMode.SelectedValue as string;
            if (Enum.TryParse(selectedValue, out SettingsManager.SnippetReplaceKey key))
            {
                return key;
            }

            return SettingsManager.SnippetReplaceKey.Tab;
        }

        static string DownloadGitHubRepoZip(string url)
        {
            using (HttpClient client = new HttpClient())
            {              
                // Mimic a browser's User-Agent string
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3");
                client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.5");

                string tempPath = Path.GetTempFileName() + ".zip";
                byte[] data = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
                File.WriteAllBytes(tempPath, data);
                return tempPath;
            }
        }

        static void ExtractSpecificFolderFromZip(string zipPath, string folderPath, string destinationPath)
        {
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        string path = Path.Combine(destinationPath, entry.FullName.Substring(folderPath.Length + 1));

                        // Create subdirectory structure in destination, if needed
                        if (entry.FullName.EndsWith("/"))
                        {
                            Directory.CreateDirectory(path);
                        }
                        else
                        {
                            // Ensure directory exists
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            // Check if file exists to avoid IOException
                            if (File.Exists(path))
                            {
                                File.Delete(path); // Delete the file if it exists.
                            }
                            entry.ExtractToFile(path, true);
                        }
                    }
                }
            }
            // Delete the temporary zip file after extraction
            File.Delete(zipPath);
        }

        private void SavedMessage()
        {
            MessageBox.Show(UiStrings.Settings_Saved, UiStrings.Settings_Saved);
        }

        private void buttonWikiPage_Click(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void ButtonSaveSmtpSettings_Click(object sender, RoutedEventArgs e)
        {
            
            SettingsManager.SmtpSettings smtpSettings = new SettingsManager.SmtpSettings()
            {
                ServerName = SMTP_Server.Text,
                Username = SMTP_UserName.Text,
                Password = SMTP_Password.Password,
                EnableSsl = SMTP_EnableSSL.IsChecked.GetValueOrDefault()
            };

            int smptPort = 587;
            bool success = int.TryParse(SMTP_Port.Text, out smptPort);
            smtpSettings.Port = smptPort;

            SettingsManager.SaveSmtpSettings(smtpSettings);

            SettingsManager.SaveMyEmail(MyEmailAddress.Text);

            SavedMessage();

        }

        private void Button_SaveApplyAdditionalFormat_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsManager.TSqlCodeFormatSettings
            {
                preserveComments = PreserveComments.IsChecked.GetValueOrDefault(false),
                removeNewLineAfterJoin = RemoveNewLineAfterJoin.IsChecked.GetValueOrDefault(false),
                addTabAfterJoinOn = AddTabAfterJoinOn.IsChecked.GetValueOrDefault(false),
                moveCrossJoinToNewLine = MoveCrossJoinToNewLine.IsChecked.GetValueOrDefault(false),
                formatCaseAsMultiline = FormatCaseAsMultiline.IsChecked.GetValueOrDefault(false),
                addNewLineBetweenStatementsInBlocks = AddNewLineBetweenStatementsInBlocks.IsChecked.GetValueOrDefault(false),
                breakSprocParametersPerLine = BreakSprocParametersPerLine.IsChecked.GetValueOrDefault(false),
                uppercaseBuiltInFunctions = UppercaseBuiltInFunctions.IsChecked.GetValueOrDefault(false),
                unindentBeginEndBlocks = UnindentBeginEndBlocks.IsChecked.GetValueOrDefault(false),
                breakVariableDefinitionsPerLine = BreakVariableDefinitionsPerLine.IsChecked.GetValueOrDefault(false),
                breakSprocDefinitionParametersPerLine = BreakSprocDefinitionParametersPerLine.IsChecked.GetValueOrDefault(false),
                // breakSelectFieldsAfterTopAndUnindent = BreakSelectFieldsAfterTopAndUnindent.IsChecked.GetValueOrDefault(false)
            };

            SettingsManager.SaveTSqlCodeFormatSettings(settings);
            SavedMessage();
        }

        private void button_SaveExcelExportSettings_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsManager.ExcelExportSettings
            {
                includeSourceQuery = ExcelExportIncludeSourceQuery.IsChecked.GetValueOrDefault(false),
                addAutofilter = ExcelExportAddAutoFilter.IsChecked.GetValueOrDefault(false),
                exportBoolsAsNumbers = ExcelExportBoolsAsNumbers.IsChecked.GetValueOrDefault(false),
                defaultDirectory = ExcelExportDefaultDirectory.Text,
                defaultFileName = ExcelExportDefaultFilename.Text
            };

            SettingsManager.SaveExcelExportSettings(settings);
            SavedMessage();
        }

        private void button_SaveGoogleSheetsSettings_Click(object sender, RoutedEventArgs e)
        {
            var settings = BuildGoogleSheetsSettings();
            SettingsManager.SaveGoogleSheetsSettings(settings);
            UpdateGoogleSheetsStatus(settings.refreshToken);
            SavedMessage();
        }

        private void EnableUpdateChecks_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.SaveEnableUpdateChecks(EnableUpdateChecks.IsChecked.GetValueOrDefault(true));
        }

        private void button_CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (button_CheckUpdates != null)
                button_CheckUpdates.IsEnabled = false;
            ApplyUpdateStatusUi("checking", UiStrings.Get("Settings_Updates_Checking"), null, null);
            UpdateChecker.CheckNow(AxialSqlToolsPackage.PackageInstance, ignoreSettings: true);
        }

        private void UpdateChecker_LastUpdateResultChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(UpdateUpdateStatus));
            }
            catch
            {
            }
        }

        private void UpdateUpdateStatus()
        {
            if (UpdateCurrentVersion != null)
                UpdateCurrentVersion.Text = FormatInstalledVersion();
            string raw = UpdateChecker.LastUpdateResult ?? string.Empty;
            string time = null;
            string message = raw;
            int split = raw.IndexOf(" - ", StringComparison.Ordinal);
            if (split > 0)
            {
                time = raw.Substring(0, split);
                message = raw.Substring(split + 3);
            }
            string kind;
            string headline;
            string detail;
            ClassifyUpdateStatus(message, out kind, out headline, out detail);
            ApplyUpdateStatusUi(kind, headline, detail, time);
            if (button_CheckUpdates != null)
                button_CheckUpdates.IsEnabled = kind != "checking";
        }

        private static void ClassifyUpdateStatus(string message, out string kind, out string headline, out string detail)
        {
            string text = message ?? string.Empty;
            if (ContainsAny(text, "No update check has run", "has not run"))
            {
                kind = "idle";
                headline = UiStrings.Get("Settings_Updates_Idle");
                detail = null;
                return;
            }
            if (ContainsAny(text, "Manual update check started", "Startup update check scheduled", "Running startup update check"))
            {
                kind = "checking";
                headline = UiStrings.Get("Settings_Updates_Checking");
                detail = null;
                return;
            }
            if (ContainsAny(text, "release info was unavailable", "current version could not", "version could not be parsed", "fetch failed"))
            {
                kind = "error";
                headline = UiStrings.Get("Settings_Updates_Failed");
                detail = null;
                return;
            }
            if (ContainsAny(text, "Up to date"))
            {
                kind = "ok";
                headline = UiStrings.Get("Settings_Updates_UpToDate");
                detail = null;
                return;
            }
            if (ContainsAny(text, "Update available"))
            {
                kind = "available";
                headline = UiStrings.Get("Settings_Updates_Available");
                int colon = text.IndexOf(':');
                detail = colon >= 0 ? text.Substring(colon + 1).Trim() : null;
                return;
            }
            if (ContainsAny(text, "Downloading update package"))
            {
                kind = "checking";
                headline = UiStrings.Get("Settings_Updates_Downloading");
                detail = null;
                return;
            }
            if (ContainsAny(text, "will install when SSMS closes", "Ready to install on close", "downloaded and verified", "downloaded without checksum"))
            {
                kind = "ok";
                headline = UiStrings.Get("Settings_Updates_ReadyOnClose");
                detail = null;
                return;
            }
            if (ContainsAny(text, "download failed", "Update download failed", "Opened release page", "could not launch VSIXInstaller", "Deferred update skipped"))
            {
                kind = "error";
                headline = UiStrings.Get("Settings_Updates_DownloadFailed");
                detail = null;
                return;
            }
            if (ContainsAny(text, "Startup update check failed", "Manual update check failed", "Update check failed", "Update check skipped"))
            {
                kind = "error";
                headline = UiStrings.Get("Settings_Updates_Failed");
                detail = null;
                return;
            }
            kind = "idle";
            headline = UiStrings.Get(string.IsNullOrWhiteSpace(text) ? "Settings_Updates_Idle" : "Settings_Updates_Unknown");
            detail = null;
        }

        private static bool ContainsAny(string text, params string[] parts)
        {
            foreach (string part in parts)
            {
                if (text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private void ApplyUpdateStatusUi(string kind, string headline, string detail, string time)
        {
            if (UpdateStatusHeadline != null)
                UpdateStatusHeadline.Text = headline ?? string.Empty;
            if (UpdateCheckStatus != null)
            {
                UpdateCheckStatus.Text = detail ?? string.Empty;
                UpdateCheckStatus.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
            }
            if (UpdateStatusTime != null)
            {
                UpdateStatusTime.Text = time ?? string.Empty;
                UpdateStatusTime.Visibility = string.IsNullOrWhiteSpace(time) ? Visibility.Collapsed : Visibility.Visible;
            }
            if (UpdateStatusAccent == null)
                return;
            string brushKey = "AxialThemeSubtleBorderBrush";
            if (kind == "ok")
                brushKey = "AxialThemeStatusSuccessBrush";
            else if (kind == "error")
                brushKey = "AxialThemeStatusErrorBrush";
            else if (kind == "available" || kind == "checking")
                brushKey = "AxialThemeAccentBrush";
            var brush = TryFindResource(brushKey) as Brush;
            if (brush != null)
                UpdateStatusAccent.Background = brush;
        }

        private static string FormatInstalledVersion()
        {
            Version v = UpdateChecker.GetCurrentVersion();
            if (v == null)
                return "—";
            if (v.Revision > 0)
                return v.ToString(4);
            return v.ToString(3);
        }

        private async void button_AuthorizeGoogleSheets_Click(object sender, RoutedEventArgs e)
        {
            var settings = BuildGoogleSheetsSettings();

            if (!settings.HasClientConfiguration())
            {
                MessageBox.Show(UiStrings.Get("Msg_Settings_GoogleClientRequired"), UiStrings.Get("Msg_Settings_GoogleSheetsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string authorizationUrl = GoogleSheetsExport.BuildAuthorizationUrl(settings);
                Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });

                string authorizationCode = Interaction.InputBox("Paste the authorization code provided by Google after granting access.", "Google Sheets Authorization");
                if (string.IsNullOrWhiteSpace(authorizationCode))
                {
                    return;
                }

                var authResult = await GoogleSheetsExport.ExchangeAuthorizationCodeAsync(settings, authorizationCode.Trim(), CancellationToken.None);

                if (!string.IsNullOrWhiteSpace(authResult.RefreshToken))
                {
                    settings.refreshToken = authResult.RefreshToken;
                }

                SettingsManager.SaveGoogleSheetsSettings(settings);
                UpdateGoogleSheetsStatus(settings.refreshToken);
                SavedMessage();
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(System.Globalization.CultureInfo.CurrentUICulture, UiStrings.Get("Msg_Settings_AuthFailed"), ex.Message), UiStrings.Get("Msg_Settings_GoogleSheetsTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private SettingsManager.GoogleSheetsSettings BuildGoogleSheetsSettings()
        {
            return new SettingsManager.GoogleSheetsSettings
            {
                includeSourceQuery = GoogleSheetsIncludeSourceQuery.IsChecked.GetValueOrDefault(false),
                exportBoolsAsNumbers = GoogleSheetsExportBoolsAsNumbers.IsChecked.GetValueOrDefault(false),
                defaultSpreadsheetName = GoogleSheetsDefaultSpreadsheetName.Text,
                clientId = GoogleSheetsClientId.Text,
                clientSecret = GoogleSheetsClientSecret.Password,
                refreshToken = SettingsManager.GetGoogleSheetsSettings().refreshToken
            };
        }

        private void UpdateGoogleSheetsStatus(string refreshToken)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                GoogleSheetsRefreshTokenLabel.Text = UiStrings.Get("Settings_NotAuthorized");
            }
            else
            {
                GoogleSheetsRefreshTokenLabel.Text = UiStrings.Get("Settings_Authorized");
            }
            ApplyGoogleSheetsAuthorizationBrush();
        }

        private void Hyperlink_RequestNavigateFormatQueryWiki(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void Button_SaveOpenAi_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.SaveOpenAiApiKey(OpenAiApiKey.Password);

            SavedMessage();
        }

        private void Button_SaveQueryHistory_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.SaveQueryHistoryConnectionString(_queryHistoryConnectionString);
            SettingsManager.SaveQueryHistoryTableName(QueryHistoryTableName.Text);
            SettingsManager.SaveQueryHistoryStorageMode(GetSelectedQueryHistoryStorageType());

            SavedMessage();

            RefreshQueryHistoryCreateScript();

        }

        private void Button_SaveTabHistorySettings_Click(object sender, RoutedEventArgs e)
        {
            bool enabled = TabHistoryEnabled.IsChecked.GetValueOrDefault();
            UiSettingsStore.SaveTabHistoryEnabled(enabled);
            int retentionDays = int.TryParse(TabHistoryRetentionDays.Text, out var rd) ? rd : 90;
            UiSettingsStore.SaveTabHistoryRetentionDays(retentionDays);
            SavedMessage();
        }

        private void Button_SelectDatabaseFromObjectExplorer_Click(object sender, RoutedEventArgs e)
        {
            var ci = ScriptFactoryAccess.GetCurrentConnectionInfoFromObjectExplorer();
            if (ci == null || string.IsNullOrWhiteSpace(ci.FullConnectionString))
            {
                MessageBox.Show(UiStrings.Get("Msg_QuickSearch_SelectOeNode"), UiStrings.Settings_Tab_QueryHistory);
                return;
            }

            _queryHistoryConnectionString = SettingsManager.EnsureTrustServerCertificate(ci.FullConnectionString);
            UpdateQueryHistoryConnectionDetails();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select templates folder";
                dialog.ShowNewFolderButton = true;

                // Show the dialog and check if the user selected a folder
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    // Set the selected folder path to the TextBox
                    ScriptFolder.Text = dialog.SelectedPath;
                }
            }
        }

        private void SnippetsBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select snippets folder";
                dialog.ShowNewFolderButton = true;

                // Show the dialog and check if the user selected a folder
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    // Set the selected folder path to the TextBox
                    SnippetFolder.Text = dialog.SelectedPath;
                }
            }
        }

        private string GetSelectedQueryHistoryStorageType()
        {
            if (QueryHistoryStorageType.SelectedItem is ComboBoxItem item)
            {
                return item.Tag?.ToString() ?? QueryHistoryStorageModeTextFiles;
            }

            return QueryHistoryStorageModeTextFiles;
        }

        private void SelectQueryHistoryStorageType(string storageType)
        {
            string mode = string.IsNullOrWhiteSpace(storageType) ? QueryHistoryStorageModeTextFiles : storageType;

            foreach (var obj in QueryHistoryStorageType.Items)
            {
                if (obj is ComboBoxItem item && string.Equals(item.Tag?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
                {
                    QueryHistoryStorageType.SelectedItem = item;
                    return;
                }
            }

            QueryHistoryStorageType.SelectedIndex = 0;
        }

        private void UpdateQueryHistoryStorageControls()
        {
            bool isDisabledStorage = string.Equals(GetSelectedQueryHistoryStorageType(), QueryHistoryStorageModeDisabled, StringComparison.OrdinalIgnoreCase);
            bool isDatabaseStorage = string.Equals(GetSelectedQueryHistoryStorageType(), QueryHistoryStorageModeDatabase, StringComparison.OrdinalIgnoreCase);
            Label_QueryHistoryConnectionInfoTitle.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            Label_QueryHistoryConnectionInfo.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            button_SelectDatabaseFromObjectExplorer.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            Label_QueryHistoryTargetTableName.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            QueryHistoryTableName.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            Label_QueryHistoryTargetTableHint.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            Group_QueryHistoryCreateScript.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            QueryHistoryTextFilesPanel.Visibility = (!isDatabaseStorage && !isDisabledStorage) ? Visibility.Visible : Visibility.Collapsed;
            Label_QueryHistoryTextFilesInfo.Visibility = (!isDatabaseStorage && !isDisabledStorage) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void QueryHistoryStorageType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateQueryHistoryStorageControls();
        }

        private void Button_OpenQueryHistoryFolder_Click(object sender, RoutedEventArgs e)
        {
            string folderPath = SettingsManager.GetQueryHistoryTextFileFolder();
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
        }

        private void formatTSqlExample()
        {
            var settings = new SettingsManager.TSqlCodeFormatSettings
            {
                preserveComments = PreserveComments.IsChecked.GetValueOrDefault(false),
                removeNewLineAfterJoin = RemoveNewLineAfterJoin.IsChecked.GetValueOrDefault(false),
                addTabAfterJoinOn = AddTabAfterJoinOn.IsChecked.GetValueOrDefault(false),
                moveCrossJoinToNewLine = MoveCrossJoinToNewLine.IsChecked.GetValueOrDefault(false),
                formatCaseAsMultiline = FormatCaseAsMultiline.IsChecked.GetValueOrDefault(false),
                addNewLineBetweenStatementsInBlocks = AddNewLineBetweenStatementsInBlocks.IsChecked.GetValueOrDefault(false),
                breakSprocParametersPerLine = BreakSprocParametersPerLine.IsChecked.GetValueOrDefault(false),
                uppercaseBuiltInFunctions = UppercaseBuiltInFunctions.IsChecked.GetValueOrDefault(false),
                unindentBeginEndBlocks = UnindentBeginEndBlocks.IsChecked.GetValueOrDefault(false),
                breakVariableDefinitionsPerLine = BreakVariableDefinitionsPerLine.IsChecked.GetValueOrDefault(false),
                breakSprocDefinitionParametersPerLine = BreakSprocDefinitionParametersPerLine.IsChecked.GetValueOrDefault(false),
                // breakSelectFieldsAfterTopAndUnindent = BreakSelectFieldsAfterTopAndUnindent.IsChecked.GetValueOrDefault(false)
            };

            FormattedQueryPreview.Text = TSqlFormatter.FormatCode(SourceQueryPreview.Text, settings);
        }

        private void formatSetting_Checked(object sender, RoutedEventArgs e)
        {
            formatTSqlExample();
        }

        private void formatSetting_Unchecked(object sender, RoutedEventArgs e)
        {
            formatTSqlExample();
        }

        private void buttonSaveGitHubSettings_Click(object sender, RoutedEventArgs e)
        {

            WindowsCredentialHelper.SaveToken("AxialSqlTools_GitHubToken", "AxialSqlTools_GitHubToken", GitHubToken.Password);

            SavedMessage();

        }


        private static string DefaultQueryHistoryTableName => QueryHistoryTableHelper.DefaultTableName;

        private void QueryHistoryTableName_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshQueryHistoryCreateScript();
        }

        private string EffectiveQueryHistoryTableName()
        {
            return QueryHistoryTableHelper.ResolveTableName(QueryHistoryTableName?.Text);
        }

        private string GenerateQueryHistoryCreateTableScript(string tableName)
        {
            return QueryHistoryTableHelper.BuildEnsureTableSql(tableName).Trim();
        }

        private void RefreshQueryHistoryCreateScript()
        {
            try
            {
                QueryHistoryCreateScript.Text = GenerateQueryHistoryCreateTableScript(EffectiveQueryHistoryTableName());
            }
            catch (Exception ex)
            {
                QueryHistoryCreateScript.Text = $"-- Failed to generate script: {ex.Message}";
            }
        }

        private void LoadConnectionColorRules()
        {
            _connectionColorRules = new ObservableCollection<SettingsManager.ConnectionColorRule>(SettingsManager.GetConnectionColorRules());
            ConnectionColorRulesListView.ItemsSource = _connectionColorRules;
        }

        private void EnsureConnectionColorRulesLoaded()
        {
            if (_connectionColorRules == null)
            {
                _connectionColorRules = new ObservableCollection<SettingsManager.ConnectionColorRule>();
                ConnectionColorRulesListView.ItemsSource = _connectionColorRules;
            }
        }

        private string PickColor(string currentHex)
        {
            var dialog = new System.Windows.Forms.ColorDialog();
            try
            {
                dialog.Color = System.Drawing.ColorTranslator.FromHtml(currentHex);
            }
            catch { }
            dialog.FullOpen = true;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                return System.Drawing.ColorTranslator.ToHtml(dialog.Color);
            }
            return null;
        }

        private void NewRuleColorPreview_Click(object sender, RoutedEventArgs e)
        {
            var currentBrush = NewRuleColorPreview.Background as SolidColorBrush;
            string currentHex = currentBrush != null
                ? string.Format("#{0:X2}{1:X2}{2:X2}", currentBrush.Color.R, currentBrush.Color.G, currentBrush.Color.B)
                : "#FF4444";

            string picked = PickColor(currentHex);
            if (picked != null)
            {
                try
                {
                    var color = System.Drawing.ColorTranslator.FromHtml(picked);
                    NewRuleColorPreview.Background = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
                }
                catch { }
            }
        }

        private void NewRuleColorPreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            NewRuleColorPreview_Click(sender, (RoutedEventArgs)e);
        }

        private void RuleColorPreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SettingsManager.ConnectionColorRule rule)
            {
                string picked = PickColor(rule.StatusBarColor);
                if (picked != null)
                {
                    rule.StatusBarColor = picked;
                    ConnectionColorRulesListView.Items.Refresh();
                }
            }
        }

        private void ButtonAddColorRule_Click(object sender, RoutedEventArgs e)
        {
            EnsureConnectionColorRulesLoaded();

            string serverPattern = NewRuleServerPattern.Text?.Trim();
            string databasePattern = NewRuleDatabasePattern.Text?.Trim();

            if (string.IsNullOrEmpty(serverPattern) && string.IsNullOrEmpty(databasePattern))
            {
                MessageBox.Show(UiStrings.Get("Msg_Settings_ConnectionColorsFill"), UiStrings.Get("Msg_Settings_ConnectionColorsTitle"));
                return;
            }

            var brush = NewRuleColorPreview.Background as SolidColorBrush;
            string hex = "#FF4444";
            if (brush != null)
            {
                hex = string.Format("#{0:X2}{1:X2}{2:X2}", brush.Color.R, brush.Color.G, brush.Color.B);
            }

            _connectionColorRules.Add(new SettingsManager.ConnectionColorRule
            {
                ServerNamePattern = serverPattern ?? string.Empty,
                DatabaseNamePattern = databasePattern ?? string.Empty,
                StatusBarColor = hex,
                IsEnabled = true
            });

            NewRuleServerPattern.Text = "";
            NewRuleDatabasePattern.Text = "";
        }

        private void ButtonEditColorRule_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectionColorRulesListView.SelectedItem is SettingsManager.ConnectionColorRule selectedRule)
            {
                NewRuleServerPattern.Text = selectedRule.ServerNamePattern;
                NewRuleDatabasePattern.Text = selectedRule.DatabaseNamePattern;

                try
                {
                    var color = System.Drawing.ColorTranslator.FromHtml(selectedRule.StatusBarColor);
                    NewRuleColorPreview.Background = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
                }
                catch { }

                _connectionColorRules.Remove(selectedRule);
            }
        }

        private void ButtonRemoveColorRule_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectionColorRulesListView.SelectedItem is SettingsManager.ConnectionColorRule selectedRule)
            {
                _connectionColorRules.Remove(selectedRule);
            }
        }

        private void Button_SaveConnectionColorRules_Click(object sender, RoutedEventArgs e)
        {
            var rules = new System.Collections.Generic.List<SettingsManager.ConnectionColorRule>(_connectionColorRules);
            SettingsManager.SaveConnectionColorRules(rules);
            GridAccess.ColorAllDocumentTabs();
            GridAccess.ScheduleReapplyAllTabColors();
            SavedMessage();
        }

    }
}
