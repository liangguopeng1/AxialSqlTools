using AxialSqlTools.Properties;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace AxialSqlTools
{
    public partial class SavedConnectionPickerWindow : Window
    {
        public SettingsManager.DataTransferSavedConnection SelectedConnection { get; private set; }

        public SavedConnectionPickerWindow(IEnumerable<SettingsManager.DataTransferSavedConnection> connections, string title)
        {
            InitializeComponent();
            UiLocalization.Apply(this);
            HeaderTextBlock.Text = title;
            ConnectionsListBox.ItemsSource = connections.ToList();
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectionsListBox.SelectedItem is SettingsManager.DataTransferSavedConnection selected)
            {
                SelectedConnection = selected;
                DialogResult = true;
            }
            else
            {
                MessageBox.Show(Strings.Get("Msg_DataTransfer_SelectSavedConnection"), Strings.Get("Menu_DataTransfer"));
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
