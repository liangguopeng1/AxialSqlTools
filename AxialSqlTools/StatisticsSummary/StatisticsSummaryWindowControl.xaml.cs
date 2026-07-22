using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using AxialSqlTools.Properties;

namespace AxialSqlTools
{
    public partial class StatisticsSummaryWindowControl : UserControl
    {
        private readonly ToolWindowThemeController _themeController;
        private readonly StatisticsSummaryViewModel _viewModel;
        private bool _isSummarySubscribed;

        public StatisticsSummaryWindowControl()
        {
            InitializeComponent();
            UiLocalization.Apply(this);
            LocalizeWikiDescription();
            _themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);
            _viewModel = new StatisticsSummaryViewModel();
            DataContext = _viewModel;
            EnsureSummarySubscription();

            Loaded += OnLoaded;
            IsVisibleChanged += OnIsVisibleChanged;
            ResultsItemsControl.ItemContainerGenerator.StatusChanged += OnResultItemsContainerStatusChanged;
        }

        private void ApplyThemeBrushResources()
        {
            ToolWindowThemeResources.ApplySharedTheme(this);
        }

        private void LocalizeWikiDescription()
        {
            WikiDescriptionTextBlock.Inlines.Clear();
            WikiDescriptionTextBlock.Inlines.Add(new Run(Strings.Get("Common_FeatureDescriptionIn")));
            WikiDescriptionTextBlock.Inlines.Add(new Run(" "));
            var wikiLink = new Hyperlink(new Run(Strings.Get("Common_Wiki")))
            {
                NavigateUri = new Uri("https://github.com/liangguopeng1/AxialSqlTools/wiki/Statistics-Summary")
            };
            wikiLink.RequestNavigate += WikiLink_RequestNavigate;
            WikiDescriptionTextBlock.Inlines.Add(wikiLink);
        }

        private void OnResultItemsContainerStatusChanged(object sender, EventArgs e)
        {
            if (ResultsItemsControl.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
            {
                return;
            }

            LocalizeGeneratedResultItems();
        }

        private void LocalizeGeneratedResultItems()
        {
            var generator = ResultsItemsControl.ItemContainerGenerator;
            for (int i = 0; i < _viewModel.Results.Count; i++)
            {
                if (generator.ContainerFromIndex(i) is FrameworkElement container)
                {
                    UiLocalization.Apply(container);
                    LocalizeResultDataGrid(FindVisualChild<DataGrid>(container));
                }
            }
        }

        private static void LocalizeResultDataGrid(DataGrid dataGrid)
        {
            if (dataGrid == null || dataGrid.Columns.Count < 3)
            {
                return;
            }

            dataGrid.Columns[0].Header = Strings.Get("Stats_ColTable");
            dataGrid.Columns[1].Header = Strings.Get("Stats_ColScans");
            dataGrid.Columns[2].Header = Strings.Get("Stats_ColLogicalReads");
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    return match;
                }

                var nested = FindVisualChild<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            EnsureSummarySubscription();
            StatisticsSummaryStore.SetWindowOpen(true);
            _viewModel.RefreshFromStore();
            LocalizeGeneratedResultItems();
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible)
            {
                return;
            }

            StatisticsSummaryStore.SetWindowOpen(true);
            EnsureSummarySubscription();
            _viewModel.RefreshFromStore();
        }

        private void EnsureSummarySubscription()
        {
            if (_isSummarySubscribed)
            {
                return;
            }

            StatisticsSummaryStore.SummaryChanged += StatisticsSummaryStore_SummaryChanged;
            _isSummarySubscribed = true;
        }

        private void StatisticsSummaryStore_SummaryChanged(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess())
            {
                _viewModel.RefreshFromStore();
                LocalizeGeneratedResultItems();
                return;
            }

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _viewModel.RefreshFromStore();
                LocalizeGeneratedResultItems();
            });
        }

        private void WikiLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            ToolWindowNavigation.HandleRequestNavigate(e);
        }

        private void CancelLoading_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AxialSqlToolsPackage.CancelStatisticsCapture();
        }

        private void CopyAsButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(sender is Button button) || !(button.ContextMenu is ContextMenu contextMenu))
            {
                return;
            }

            contextMenu.PlacementTarget = button;
            contextMenu.Placement = PlacementMode.Bottom;
            contextMenu.IsOpen = true;
        }

        private void CopyAsCsvMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CopyResultTableToClipboard(sender, BuildCsvText);
        }

        private void CopyAsMarkdownMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CopyResultTableToClipboard(sender, BuildMarkdownText);
        }

        private void CopyAsJsonMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CopyResultTableToClipboard(sender, BuildJsonText);
        }

        private static void CopyResultTableToClipboard(object sender, Func<StatisticsSummaryResultViewModel, string> formatter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = GetResultFromMenuItem(sender);
            if (result == null || !result.HasTableData)
            {
                return;
            }

            CopyTextToClipboard(formatter(result));
        }

        private static StatisticsSummaryResultViewModel GetResultFromMenuItem(object sender)
        {
            return (sender as MenuItem)?.CommandParameter as StatisticsSummaryResultViewModel;
        }

        private static string BuildCsvText(StatisticsSummaryResultViewModel result)
        {
            var lines = new List<string>
            {
                string.Join(",", new[] { Strings.Get("Stats_ColTable"), Strings.Get("Stats_ColScans"), Strings.Get("Stats_ColLogicalReads") })
            };

            lines.AddRange(result.Tables.Select(table => string.Join(",",
                EscapeCsv(table.TableName),
                EscapeCsv(table.ScanCount.ToString()),
                EscapeCsv(table.TotalReads.ToString()))));

            return string.Join(Environment.NewLine, lines);
        }

        private static string BuildMarkdownText(StatisticsSummaryResultViewModel result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("| " + Strings.Get("Stats_ColTable") + " | " + Strings.Get("Stats_ColScans") + " | " + Strings.Get("Stats_ColLogicalReads") + " |");
            sb.AppendLine("| --- | ---: | ---: |");

            foreach (var table in result.Tables)
            {
                sb.Append("| ");
                sb.Append(EscapeMarkdown(table.TableName));
                sb.Append(" | ");
                sb.Append(table.ScanCount);
                sb.Append(" | ");
                sb.Append(table.TotalReads);
                sb.AppendLine(" |");
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildJsonText(StatisticsSummaryResultViewModel result)
        {
            var rows = result.Tables.Select(table => new Dictionary<string, object>
            {
                { Strings.Get("Stats_ColTable"), table.TableName ?? string.Empty },
                { Strings.Get("Stats_ColScans"), table.ScanCount },
                { Strings.Get("Stats_ColLogicalReads"), table.TotalReads },
            });

            return JsonConvert.SerializeObject(rows, Formatting.Indented);
        }

        private static string EscapeCsv(string value)
        {
            value = value ?? string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string EscapeMarkdown(string value)
        {
            return (value ?? string.Empty).Replace("|", "\\|");
        }

        private static void CopyTextToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            Clipboard.SetDataObject(text);
        }
    }
}
