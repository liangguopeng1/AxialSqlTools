using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AxialSqlTools.Properties;

namespace AxialSqlTools
{
    public partial class ScriptObjectPickerDialog : Window
    {
        public ScriptObjectSelectionItem SelectedObject { get; set; }

        public ScriptObjectPickerDialog(IEnumerable<ScriptObjectSelectionItem> matches)
        {
            InitializeComponent();
            UiLocalization.Apply(this);
            HeaderTextBlock.Text = Strings.Get("Msg_ScriptObject_SelectPrompt");
            ObjectsListBox.ItemsSource = matches.ToList();
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (ObjectsListBox.SelectedItem is ScriptObjectSelectionItem selected)
            {
                SelectedObject = selected;
                DialogResult = true;
            }
            else
            {
                MessageBox.Show(Strings.Get("Msg_ScriptObject_SelectRequired"), Strings.Get("Msg_QuickSearch_ScriptObjectTitle"));
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
