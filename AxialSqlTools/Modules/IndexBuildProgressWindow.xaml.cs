using System;
using System.Windows;
using System.Windows.Interop;

namespace AxialSqlTools
{
    /// <summary>索引/缓存构建进度。可复用（IntelliSense 手动刷新；后续快速搜索可直接 Show）。</summary>
    public sealed class IndexBuildProgress
    {
        public string ServerName { get; set; }
        public string Title { get; set; }
        public string CurrentItem { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
        public string Message { get; set; }
    }

    public partial class IndexBuildProgressWindow : Window
    {
        public event Action CancelRequested;

        public IndexBuildProgressWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => PositionBottomRight();
        }

        public void ShowAtBottomRight()
        {
            try
            {
                var helper = new WindowInteropHelper(this);
                IntPtr owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (owner != IntPtr.Zero)
                    helper.Owner = owner;
            }
            catch
            {
            }
            Show();
            PositionBottomRight();
        }

        public void Update(IndexBuildProgress progress)
        {
            if (progress == null) return;
            int total = progress.Total < 0 ? 0 : progress.Total;
            int completed = progress.Completed < 0 ? 0 : progress.Completed;
            if (total > 0)
            {
                Bar.Maximum = total;
                Bar.Value = Math.Min(completed, total);
            }
            else
            {
                Bar.Maximum = 100;
                Bar.Value = 0;
            }
            if (!string.IsNullOrEmpty(progress.Title))
                TitleText.Text = progress.Title;
            string item = progress.CurrentItem;
            if (!string.IsNullOrEmpty(progress.Message))
                StatusText.Text = progress.Message;
            else if (!string.IsNullOrEmpty(item) && total > 0)
                StatusText.Text = completed + "/" + total + "  " + item;
            else if (total > 0)
                StatusText.Text = completed + "/" + total;
        }

        public void CloseSafe()
        {
            try
            {
                Close();
            }
            catch
            {
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CancelButton.IsEnabled = false;
            StatusText.Text = "正在取消...";
            CancelRequested?.Invoke();
        }

        private void PositionBottomRight()
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth - 16;
            Top = work.Bottom - ActualHeight - 16;
        }
    }
}
