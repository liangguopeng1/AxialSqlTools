using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace AxialSqlTools
{
    public class TabHistoryViewModel : INotifyPropertyChanged
    {
        private List<TabHistoryRecord> _allRecords;

        public ObservableCollection<TabHistoryRecord> TabHistoryRecords { get; }

        private TabHistoryRecord _selectedRecord;
        public TabHistoryRecord SelectedRecord
        {
            get => _selectedRecord;
            set
            {
                if (_selectedRecord != value)
                {
                    _selectedRecord = value;
                    OnPropertyChanged(nameof(SelectedRecord));
                }
            }
        }

        private DateTime? _filterFromDate;
        public DateTime? FilterFromDate
        {
            get => _filterFromDate;
            set { if (_filterFromDate != value) { _filterFromDate = value; OnPropertyChanged(nameof(FilterFromDate)); } }
        }

        private DateTime? _filterToDate;
        public DateTime? FilterToDate
        {
            get => _filterToDate;
            set { if (_filterToDate != value) { _filterToDate = value; OnPropertyChanged(nameof(FilterToDate)); } }
        }

        private string _filterServer = string.Empty;
        public string FilterServer
        {
            get => _filterServer;
            set { if (_filterServer != value) { _filterServer = value; OnPropertyChanged(nameof(FilterServer)); } }
        }

        private string _filterText = string.Empty;
        public string FilterText
        {
            get => _filterText;
            set { if (_filterText != value) { _filterText = value; OnPropertyChanged(nameof(FilterText)); } }
        }

        public ICommand RefreshCommand { get; }
        public ICommand ClearFilterCommand { get; }

        public TabHistoryViewModel()
        {
            TabHistoryRecords = new ObservableCollection<TabHistoryRecord>();
            RefreshCommand = new RelayCommand(RefreshData);
            ClearFilterCommand = new RelayCommand(ClearAllFilters);
            RefreshData();
        }

        private void ClearAllFilters()
        {
            FilterFromDate = null;
            FilterToDate = null;
            FilterServer = string.Empty;
            FilterText = string.Empty;
            RefreshData();
        }

        public void RefreshData()
        {
            try
            {
                _allRecords = TabHistoryStore.LoadRecent(1000);
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Warn(ex, "[TabHistory] load failed");
                _allRecords = new List<TabHistoryRecord>();
            }

            IEnumerable<TabHistoryRecord> filtered = _allRecords;
            if (FilterFromDate.HasValue)
            {
                filtered = filtered.Where(r => r.Timestamp >= FilterFromDate.Value.Date);
            }
            if (FilterToDate.HasValue)
            {
                DateTime endOfDay = FilterToDate.Value.Date.AddDays(1).AddSeconds(-1);
                filtered = filtered.Where(r => r.Timestamp <= endOfDay);
            }
            if (!string.IsNullOrWhiteSpace(FilterServer))
            {
                filtered = filtered.Where(r =>
                    (r.DataSource ?? string.Empty).IndexOf(FilterServer, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (!string.IsNullOrWhiteSpace(FilterText))
            {
                filtered = filtered.Where(r =>
                    (r.DocumentName ?? string.Empty).IndexOf(FilterText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Content ?? string.Empty).IndexOf(FilterText, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            TabHistoryRecords.Clear();
            var ordered = filtered.OrderByDescending(r => r.Timestamp).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Id = i + 1;
                TabHistoryRecords.Add(ordered[i]);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}
