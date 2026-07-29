using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NLog;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 补全列表弹框。无边框 WPF Window，定位到光标屏幕坐标。
    /// ShowActivated=False 避免抢编辑器焦点；键路由由 IntelliSenseKeyHandler 驱动。
    /// </summary>
    public partial class CompletionListWindow : Window
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private const int GwlExStyle = -20;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

        private static void SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newValue)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(hWnd, nIndex, newValue);
            else
                SetWindowLong32(hWnd, nIndex, newValue.ToInt32());
        }

        private readonly ObservableCollection<CompletionItem> _items = new ObservableCollection<CompletionItem>();
        private const int PageSize = 8;

        public bool IsOpen { get; private set; }

        public CompletionListWindow()
        {
            InitializeComponent();
            ItemsList.ItemsSource = _items;
            ItemsList.PreviewMouseLeftButtonUp += ItemsList_PreviewMouseLeftButtonUp;
            Deactivated += (s, e) => _logger.Debug("CompletionListWindow.Deactivated (IsOpen={0})", IsOpen);
            Closed += (s, e) => _logger.Debug("CompletionListWindow.Closed");
            SourceInitialized += OnSourceInitialized;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                IntPtr style = GetWindowLongPtr(hwnd, GwlExStyle);
                long newStyle = style.ToInt64() | WsExNoActivate | WsExToolWindow;
                SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(newStyle));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "CompletionListWindow: WS_EX_NOACTIVATE failed");
            }
        }

        public void ShowAt(double deviceScreenX, double deviceScreenY, List<CompletionItem> items, IntPtr editorHwnd)
        {
            UpdateItems(items);
            EnsureOwner(editorHwnd);
            IsHitTestVisible = false;
            ApplyScreenPosition(deviceScreenX, deviceScreenY, editorHwnd);
            IsOpen = true;
            try
            {
                if (!IsVisible)
                    this.Show();
                Dispatcher.BeginInvoke(new Action(() => IsHitTestVisible = true),
                    DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "CompletionListWindow.Show failed");
                IsOpen = false;
            }
        }

        public void RepositionAt(double deviceScreenX, double deviceScreenY, IntPtr editorHwnd)
        {
            if (!IsOpen) return;
            EnsureOwner(editorHwnd);
            ApplyScreenPosition(deviceScreenX, deviceScreenY, editorHwnd);
        }

        private void EnsureOwner(IntPtr editorHwnd)
        {
            try
            {
                var helper = new WindowInteropHelper(this);
                IntPtr ownerHwnd = editorHwnd != IntPtr.Zero
                    ? editorHwnd
                    : System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (ownerHwnd != IntPtr.Zero && helper.Owner != ownerHwnd)
                    helper.Owner = ownerHwnd;
            }
            catch
            {
            }
        }

        private void ApplyScreenPosition(double deviceScreenX, double deviceScreenY, IntPtr editorHwnd)
        {
            double dipX = deviceScreenX;
            double dipY = deviceScreenY;
            try
            {
                IntPtr dpiHwnd = editorHwnd != IntPtr.Zero ? editorHwnd : new WindowInteropHelper(this).Owner;
                if (dpiHwnd != IntPtr.Zero)
                {
                    var src = HwndSource.FromHwnd(dpiHwnd);
                    if (src?.CompositionTarget != null)
                    {
                        var m = src.CompositionTarget.TransformFromDevice;
                        dipX = deviceScreenX * m.M11;
                        dipY = deviceScreenY * m.M22;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "ApplyScreenPosition DPI conversion failed");
            }
            UpdateLayout();
            double left = dipX;
            double top = dipY;
            double workBottom = SystemParameters.WorkArea.Bottom;
            double workRight = SystemParameters.WorkArea.Right;
            if (top + ActualHeight > workBottom)
            {
                top = Math.Max(0, workBottom - ActualHeight - 4);
            }
            if (left + ActualWidth > workRight)
            {
                left = Math.Max(0, workRight - ActualWidth - 4);
            }
            Left = left;
            Top = top;
        }

        public void UpdateItems(List<CompletionItem> items)
        {
            _items.Clear();
            if (items != null)
            {
                foreach (var it in items)
                {
                    _items.Add(it);
                }
            }
            if (_items.Count > 0)
            {
                ItemsList.SelectedIndex = 0;
                ItemsList.ScrollIntoView(_items[0]);
                ResetListScrollLeft();
            }
            UpdateDetail();
        }

        private void ResetListScrollLeft()
        {
            try
            {
                var scrollViewer = FindVisualChild<ScrollViewer>(ItemsList);
                scrollViewer?.ScrollToHorizontalOffset(0);
            }
            catch
            {
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        public CompletionItem GetSelected()
        {
            return ItemsList.SelectedItem as CompletionItem;
        }

        public void Move(int delta)
        {
            try
            {
                if (_items.Count == 0) return;
                int idx = ItemsList.SelectedIndex;
                if (idx < 0) idx = 0;
                idx = (idx + delta) % _items.Count;
                if (idx < 0) idx += _items.Count;
                ItemsList.SelectedIndex = idx;
                ItemsList.ScrollIntoView(_items[idx]);
                ResetListScrollLeft();
                UpdateDetail();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "CompletionListWindow.Move({0}) failed", delta);
            }
        }

        public void MovePage(int delta)
        {
            Move(delta * PageSize);
        }

        public void SelectFirst()
        {
            if (_items.Count == 0) return;
            ItemsList.SelectedIndex = 0;
            ItemsList.ScrollIntoView(_items[0]);
            UpdateDetail();
        }

        public void SelectLast()
        {
            if (_items.Count == 0) return;
            ItemsList.SelectedIndex = _items.Count - 1;
            ItemsList.ScrollIntoView(_items[_items.Count - 1]);
            UpdateDetail();
        }

        public void HidePopup()
        {
            IsOpen = false;
            try
            {
                this.Hide();
            }
            catch
            {
            }
        }

        private void UpdateDetail()
        {
            var sel = GetSelected();
            DetailText.Text = sel?.Description ?? string.Empty;
        }

        private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDetail();
        }

        private void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            CommitClicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        public event EventHandler CommitClicked;
    }
}
