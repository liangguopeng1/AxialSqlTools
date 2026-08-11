using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AxialSqlTools.Properties;
using EnvDTE;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;
using Microsoft.Data.SqlClient;

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
                    string text = vm.SelectedContentDisplay ?? string.Empty;
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
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!(DataContext is TabHistoryViewModel vm) || vm.SelectedRecord == null)
                    return;

                var record = vm.SelectedRecord;
                string path = record.DocumentPath ?? string.Empty;
                string name = record.DocumentName ?? string.Empty;

                // 文档还在（已打开标签 / 磁盘文件）→ 直接打开原文，不改内容
                if (TryActivateOpenDocument(path, name))
                    return;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    OpenExistingDocument(path);
                    return;
                }

                // 文档已不存在 → 用历史快照新建查询标签页
                string sql = vm.SelectedContentDisplay;
                string placeholder = Strings.Get("TabHistory_ContentUnchanged");
                if (string.IsNullOrWhiteSpace(sql) || string.Equals(sql, placeholder, StringComparison.Ordinal))
                    return;

                OpenSqlInNewQueryTab(sql, record.DatabaseName);
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Warn(ex, "[TabHistory] open document failed");
            }
        }

        private static bool TryActivateOpenDocument(string documentPath, string documentName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = ServiceCache.ExtensibilityModel;
                if (dte?.Documents == null) return false;

                foreach (Document doc in dte.Documents)
                {
                    if (doc == null) continue;
                    try
                    {
                        string fullName = doc.FullName ?? string.Empty;
                        string name = doc.Name ?? string.Empty;
                        bool pathMatch = !string.IsNullOrEmpty(documentPath) &&
                            string.Equals(fullName, documentPath, StringComparison.OrdinalIgnoreCase);
                        bool nameMatch = string.IsNullOrEmpty(documentPath) &&
                            !string.IsNullOrEmpty(documentName) &&
                            string.Equals(name, documentName, StringComparison.OrdinalIgnoreCase);
                        if (pathMatch || nameMatch)
                        {
                            doc.Activate();
                            return true;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Debug(ex, "[TabHistory] activate open document failed");
            }
            return false;
        }

        private static void OpenExistingDocument(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = ServiceCache.ExtensibilityModel;
            if (dte == null) throw new InvalidOperationException("DTE unavailable");

            try
            {
                dte.ItemOperations.OpenFile(path);
            }
            catch
            {
                dte.Documents.Open(path, "Auto", false);
            }
        }

        private static void OpenSqlInNewQueryTab(string sql, string database)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var connInfo = ScriptFactoryAccess.GetCurrentConnectionInfo();
            if (connInfo != null && !string.IsNullOrWhiteSpace(database))
                connInfo = ScriptFactoryAccess.CloneWithDatabase(connInfo, database) ?? connInfo;

            if (connInfo?.ActiveConnectionInfo != null)
            {
                ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, connInfo.ActiveConnectionInfo, null);
            }
            else
            {
                var ui = TryBuildUiConnection(connInfo);
                if (ui != null)
                    ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, ui, null);
                else
                    ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql);
            }

            var doc = (TextDocument)ServiceCache.ExtensibilityModel.Application.ActiveDocument.Object(null);
            doc.EndPoint.CreateEditPoint().Insert(sql);
        }

        private static Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo TryBuildUiConnection(
            ScriptFactoryAccess.ConnectionInfo connInfo)
        {
            if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.FullConnectionString))
                return null;
            try
            {
                var builder = new SqlConnectionStringBuilder(connInfo.FullConnectionString);
                return ScriptFactoryAccess.TryCreateUiConnectionInfo(
                    builder,
                    builder.UserID,
                    builder.Password,
                    string.Empty);
            }
            catch
            {
                return null;
            }
        }
    }
}
