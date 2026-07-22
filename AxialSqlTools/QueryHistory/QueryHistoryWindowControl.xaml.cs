using System;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using AxialSqlTools.Properties;

namespace AxialSqlTools
{
    public partial class QueryHistoryWindowControl : UserControl
    {
        private readonly ToolWindowThemeController _themeController;

        public QueryHistoryWindowControl()
        {
            InitializeComponent();
            UiLocalization.Apply(this);
            LocalizeDataGridColumns();
            _themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);
            DataContext = new QueryHistoryViewModel();
        }

        private void LocalizeDataGridColumns()
        {
            DataGrid_QueryHistory.Columns[0].Header = Strings.Get("QueryHistory_ColId");
            DataGrid_QueryHistory.Columns[1].Header = Strings.Get("QueryHistory_ColStartTime");
            DataGrid_QueryHistory.Columns[2].Header = Strings.Get("QueryHistory_ColFinishTime");
            DataGrid_QueryHistory.Columns[3].Header = Strings.Get("QueryHistory_ColElapsed");
            DataGrid_QueryHistory.Columns[4].Header = Strings.Get("QueryHistory_ColRows");
            DataGrid_QueryHistory.Columns[5].Header = Strings.Get("QueryHistory_ColResult");
            DataGrid_QueryHistory.Columns[6].Header = Strings.Get("QueryHistory_ColServer");
            DataGrid_QueryHistory.Columns[7].Header = Strings.Get("QueryHistory_ColDatabase");
            DataGrid_QueryHistory.Columns[8].Header = Strings.Get("QueryHistory_ColLogin");
            DataGrid_QueryHistory.Columns[9].Header = Strings.Get("QueryHistory_ColWorkstation");
            DataGrid_QueryHistory.Columns[10].Header = Strings.Get("QueryHistory_ColQueryShort");
        }

        private void ApplyThemeBrushResources()
        {
            ToolWindowThemeResources.ApplySharedTheme(this);
        }

        private void WikiLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Uri?.AbsoluteUri))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });

            e.Handled = true;
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (DataContext is QueryHistoryViewModel vm && vm.RefreshCommand.CanExecute(null))
                {
                    vm.RefreshCommand.Execute(null);
                }
                e.Handled = true;
            }
        }

        private void DatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            // force a refresh right away:
            if (DataContext is QueryHistoryViewModel vm)
            {
                vm.RefreshCommand.Execute(null);
            }
        }

    }
}
