using System;
using System.Windows;
using System.Windows.Controls;
using AxialSqlTools.Properties;

namespace AxialSqlTools
{
    public partial class SnippetManagerWindowControl : UserControl
    {
        public SnippetManagerWindowControl()
        {
            InitializeComponent();
            UiLocalization.Apply(this);
            LocalizeDataGridColumns();
            var vm = new SnippetManagerViewModel();
            DataContext = vm;

            // Set the ComboBox to the current ReplaceKey value
            cmbReplaceKey.SelectedValue = vm.ReplaceKey.ToString();
        }

        private void LocalizeDataGridColumns()
        {
            DataGrid_Snippets.Columns[0].Header = Strings.Get("Snippet_ColPrefix");
            DataGrid_Snippets.Columns[1].Header = Strings.Get("Snippet_ColDescription");
            DataGrid_Snippets.Columns[2].Header = Strings.Get("Snippet_ColBodyPreview");
        }

        private void cmbReplaceKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is SnippetManagerViewModel vm && cmbReplaceKey.SelectedValue is string selectedValue)
            {
                if (Enum.TryParse(selectedValue, out SettingsManager.SnippetReplaceKey key))
                {
                    vm.ReplaceKey = key;
                }
            }
        }
    }
}
