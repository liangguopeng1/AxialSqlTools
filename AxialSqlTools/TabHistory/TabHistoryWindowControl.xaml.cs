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
                    string text = vm.SelectedRecord.ContentDisplay ?? string.Empty;
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
