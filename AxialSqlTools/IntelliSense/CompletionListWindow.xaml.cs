using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
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
        private bool _mouseCommitScheduled;

        public bool IsOpen { get; private set; }

        public CompletionListWindow()
        {
            InitializeComponent();
            ItemsList.ItemsSource = _items;
            ItemsList.PreviewMouseLeftButtonDown += ItemsList_PreviewMouseLeftButtonDown;
            ItemsList.PreviewMouseLeftButtonUp += ItemsList_PreviewMouseLeftButtonUp;
            Deactivated += (s, e) => _logger.Debug("CompletionListWindow.Deactivated (IsOpen={0})", IsOpen);
            Closed += (s, e) => _logger.Debug("CompletionListWindow.Closed");
            SourceInitialized += OnSourceInitialized;
            ApplyThemeColors();
            PreviewKeyDown += CompletionListWindow_PreviewKeyDown;
            var copyMenu = new ContextMenu();
            var copyItem = new MenuItem { Header = "复制" };
            copyItem.Click += (s, e) => TryCopyDetail();
            copyMenu.Items.Add(copyItem);
            DetailPrimary.ContextMenu = copyMenu;
            DetailDatabase.ContextMenu = copyMenu;
            DetailTable.ContextMenu = copyMenu;
            DetailSecondary.ContextMenu = copyMenu;
        }

        private void CompletionListWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (TryCopyDetail())
                    e.Handled = true;
            }
            else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (TrySelectAllDetail())
                    e.Handled = true;
            }
        }

        /// <summary>复制右侧详情选中文本；无选中则复制整段详情。</summary>
        public bool TryCopyDetail()
        {
            try
            {
                if (CopyIfSelected(DetailPrimary)
                    || CopyIfSelected(DetailDatabase)
                    || CopyIfSelected(DetailTable)
                    || CopyIfSelected(DetailSecondary))
                    return true;

                var sb = new System.Text.StringBuilder();
                AppendDetailLine(sb, DetailPrimary.Text);
                if (DetailSourcePanel.Visibility == Visibility.Visible)
                {
                    if (!string.IsNullOrWhiteSpace(DetailDatabase.Text))
                        sb.AppendLine("数据库: " + DetailDatabase.Text);
                    if (!string.IsNullOrWhiteSpace(DetailTable.Text))
                        sb.AppendLine("表: " + DetailTable.Text);
                }
                AppendDetailLine(sb, DetailSecondary.Text);
                string text = sb.ToString().TrimEnd();
                if (string.IsNullOrEmpty(text)) return false;
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TrySelectAllDetail()
        {
            try
            {
                TextBox target = DetailPrimary;
                if (DetailDatabase.IsKeyboardFocusWithin || DetailDatabase.IsMouseOver)
                    target = DetailDatabase;
                else if (DetailTable.IsKeyboardFocusWithin || DetailTable.IsMouseOver)
                    target = DetailTable;
                else if (DetailSecondary.IsKeyboardFocusWithin || DetailSecondary.IsMouseOver)
                    target = DetailSecondary;
                else if (DetailPrimary.IsKeyboardFocusWithin || DetailPrimary.IsMouseOver)
                    target = DetailPrimary;
                target.Focus();
                target.SelectAll();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool CopyIfSelected(TextBox box)
        {
            if (box == null || box.SelectionLength <= 0) return false;
            box.Copy();
            return true;
        }

        private static void AppendDetailLine(System.Text.StringBuilder sb, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            sb.AppendLine(text);
        }

        private void ApplyThemeColors()
        {
            try
            {
                Brush bg = VsThemeBrushResolver.ResolveBrush(this, EnvironmentColors.ToolWindowBackgroundBrushKey)
                    ?? SystemColors.WindowBrush;
                Brush fg = VsThemeBrushResolver.ResolveBrush(this, EnvironmentColors.ToolWindowTextBrushKey)
                    ?? SystemColors.WindowTextBrush;
                Brush border = VsThemeBrushResolver.ResolveBrush(this, EnvironmentColors.ToolWindowBorderBrushKey)
                    ?? SystemColors.ActiveBorderBrush;
                Brush accent = VsThemeBrushResolver.ResolveEnvironmentBrushByName(this, "MainWindowActiveDefaultBorderBrushKey")
                    ?? VsThemeBrushResolver.ResolveEnvironmentBrushByName(this, "SystemAccentBrushKey")
                    ?? border;

                Color bgColor = VsThemeBrushResolver.GetBrushColor(bg, Colors.White);
                Color fgColor = VsThemeBrushResolver.GetBrushColor(fg, Colors.Black);
                Color borderColor = VsThemeBrushResolver.GetBrushColor(border, Color.FromRgb(0xC8, 0xC8, 0xC8));
                Color accentColor = VsThemeBrushResolver.GetBrushColor(accent, Color.FromRgb(0x00, 0x7A, 0xCC));
                bool light = VsThemeBrushResolver.GetRelativeLuminance(bgColor) > 0.6;

                // 与悬停框一致：主体用工具窗口背景，右侧略深一层
                Color panelColor = light
                    ? VsThemeBrushResolver.BlendColors(bgColor, Colors.Black, 0.06)
                    : VsThemeBrushResolver.BlendColors(bgColor, Colors.White, 0.08);
                Color selectedColor = light
                    ? VsThemeBrushResolver.BlendColors(bgColor, Colors.Black, 0.12)
                    : VsThemeBrushResolver.BlendColors(bgColor, Colors.White, 0.14);
                Color hoverColor = light
                    ? VsThemeBrushResolver.BlendColors(bgColor, Colors.Black, 0.07)
                    : VsThemeBrushResolver.BlendColors(bgColor, Colors.White, 0.10);
                Color mutedColor = light
                    ? VsThemeBrushResolver.BlendColors(fgColor, bgColor, 0.45)
                    : VsThemeBrushResolver.BlendColors(fgColor, bgColor, 0.35);

                SetBrush("PopupBgBrush", bgColor);
                SetBrush("PopupFgBrush", fgColor);
                SetBrush("PopupBorderBrush", borderColor);
                SetBrush("PopupPanelBgBrush", panelColor);
                SetBrush("PopupMutedFgBrush", mutedColor);
                SetBrush("PopupSelectedBgBrush", selectedColor);
                SetBrush("PopupHoverBgBrush", hoverColor);
                SetBrush("PopupAccentBrush", accentColor);
                // 命中字必须和正文拉开：不用强调色（常接近正文），浅色用深橙、深色用金黄
                Color matchColor = light
                    ? Color.FromRgb(0xC2, 0x41, 0x0C)
                    : Color.FromRgb(0xFB, 0xBF, 0x24);
                SetBrush("PopupMatchFgBrush", matchColor);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "CompletionListWindow ApplyThemeColors failed");
            }
        }

        private void SetBrush(string key, Color color)
        {
            if (Resources[key] is SolidColorBrush existing && !existing.IsFrozen)
            {
                existing.Color = color;
                return;
            }
            Resources[key] = new SolidColorBrush(color);
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
            ApplyThemeColors();
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
                // 不要把编辑器 HWND 设为 Owner：新标签编辑器窗口链未稳时会导致 SSMS 退出
                IntPtr ownerHwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (ownerHwnd != IntPtr.Zero && helper.Owner != ownerHwnd)
                    helper.Owner = ownerHwnd;
            }
            catch
            {
            }
        }

        private void ApplyScreenPosition(double deviceScreenX, double deviceScreenY, IntPtr editorHwnd)
        {
            double scaleX = 1.0;
            double scaleY = 1.0;
            try
            {
                // 只用本窗口的 PresentationSource 做 DPI，禁止 HwndSource.FromHwnd(编辑器 HWND)
                var src = PresentationSource.FromVisual(this) as HwndSource;
                if (src?.CompositionTarget != null)
                {
                    var m = src.CompositionTarget.TransformFromDevice;
                    scaleX = m.M11;
                    scaleY = m.M22;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "ApplyScreenPosition DPI conversion failed");
            }
            double dipX = deviceScreenX * scaleX;
            double dipY = deviceScreenY * scaleY;
            UpdateLayout();
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;
            Rect work = PopupScreenPlacement.GetWorkAreaDip(deviceScreenX, deviceScreenY, scaleX, scaleY);
            Point pos = PopupScreenPlacement.ClampToWorkArea(dipX, dipY, width, height, work);
            Left = pos.X;
            Top = pos.Y;
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
            return ItemsList.SelectedItem as CompletionItem
                ?? (_items.Count > 0 ? _items[0] : null);
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
                // WS_EX_NOACTIVATE 窗口不会走激活/失活路径，隐藏时可能仍持有 OS 鼠标捕获，
                // 宿主编辑器将收不到后续鼠标消息（表现为编辑器卡死）。隐藏前显式释放本窗口的捕获，
                // Capture(null) 幂等；捕获在别的窗口时不碰。
                var captured = System.Windows.Input.Mouse.Captured as DependencyObject;
                if (captured != null && this.IsAncestorOf(captured))
                    System.Windows.Input.Mouse.Capture(null);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "CompletionListWindow.HidePopup: release mouse capture failed");
            }
            try
            {
                this.Hide();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "CompletionListWindow.HidePopup: Hide failed");
            }
        }

        private void UpdateDetail()
        {
            var sel = GetSelected();
            if (sel == null)
            {
                KindBadge.Visibility = Visibility.Collapsed;
                DetailPrimary.Text = string.Empty;
                DetailDatabase.Text = string.Empty;
                DetailTable.Text = string.Empty;
                DetailSourcePanel.Visibility = Visibility.Collapsed;
                DetailSecondary.Text = string.Empty;
                return;
            }
            KindBadge.Visibility = Visibility.Visible;
            KindBadgeText.Text = sel.KindLabel;
            KindBadge.Background = new SolidColorBrush(KindAccentColor(sel.Kind));
            DetailPrimary.Text = string.IsNullOrWhiteSpace(sel.Description) ? sel.DisplayText : sel.Description;

            bool hasDb = !string.IsNullOrWhiteSpace(sel.SourceDatabase);
            bool hasTable = !string.IsNullOrWhiteSpace(sel.SourceTable);
            if (hasDb || hasTable)
            {
                DetailSourcePanel.Visibility = Visibility.Visible;
                DetailDatabase.Text = hasDb ? sel.SourceDatabase : "—";
                DetailTable.Text = hasTable ? sel.SourceTable : "—";
                DetailSecondary.Text = string.IsNullOrWhiteSpace(sel.Description) ? string.Empty : sel.DisplayText;
            }
            else
            {
                DetailSourcePanel.Visibility = Visibility.Collapsed;
                DetailDatabase.Text = string.Empty;
                DetailTable.Text = string.Empty;
                DetailSecondary.Text = string.IsNullOrWhiteSpace(sel.Description) ? string.Empty : sel.DisplayText;
            }
        }

        private static Color KindAccentColor(CompletionKind kind)
        {
            switch (kind)
            {
                case CompletionKind.Column: return Color.FromRgb(0x2B, 0x7C, 0xD3);
                case CompletionKind.AllColumns: return Color.FromRgb(0x15, 0x65, 0xC0);
                case CompletionKind.Table: return Color.FromRgb(0x2E, 0x7D, 0x32);
                case CompletionKind.View: return Color.FromRgb(0x00, 0x89, 0x7B);
                case CompletionKind.Database: return Color.FromRgb(0xEF, 0x6C, 0x00);
                case CompletionKind.Schema: return Color.FromRgb(0x45, 0x5A, 0x64);
                case CompletionKind.Procedure: return Color.FromRgb(0x39, 0x49, 0xAB);
                case CompletionKind.ScalarFunction:
                case CompletionKind.TableFunction: return Color.FromRgb(0x00, 0x83, 0x8F);
                case CompletionKind.Keyword: return Color.FromRgb(0x60, 0x7D, 0x8B);
                case CompletionKind.Snippet: return Color.FromRgb(0xC6, 0x28, 0x28);
                case CompletionKind.Variable: return Color.FromRgb(0x54, 0x6E, 0x7A);
                case CompletionKind.Synonym: return Color.FromRgb(0x55, 0x8B, 0x2F);
                case CompletionKind.Parameter: return Color.FromRgb(0xF9, 0xA8, 0x25);
                default: return Color.FromRgb(0x5F, 0x6B, 0x7A);
            }
        }

        private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDetail();
        }

        private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            SelectItemUnderMouse(e.OriginalSource as DependencyObject);
        }

        private void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            SelectItemUnderMouse(e.OriginalSource as DependencyObject);
            if (ItemsList.SelectedItem is CompletionItem)
            {
                // 先吞掉本次点击，提交推迟到路由事件返回之后（见 ScheduleMouseCommit）。
                // 不能在 WPF 鼠标路由/捕获尚未结束时同步提交：提交会 Hide() 本窗口并调用
                // IVsTextLines.ReplaceLines 改写 SSMS 缓冲，两套输入状态互相重入 → 偶发卡死。
                e.Handled = true;
                ScheduleMouseCommit();
            }
        }

        private void ScheduleMouseCommit()
        {
            // 把鼠标选中的补全推迟到当前 WPF 输入路由事件返回后再提交。
            // 直接在被点击控件的路由事件里 Hide() 弹框 + 改写编辑器缓冲，会因鼠标捕获/HWND 焦点
            // 切换与宿主编辑器的同步重入导致间歇性卡死；异步化后宿主可先完成输入处理再插入。
            // 同一时刻只保留一次待提交，避免连点重复插入。
            if (_mouseCommitScheduled) return;
            _mouseCommitScheduled = true;
            var dispatcher = Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                _mouseCommitScheduled = false;
                return;
            }
            dispatcher.BeginInvoke(new Action(() =>
            {
                _mouseCommitScheduled = false;
                // 延迟期间可能已失焦关闭、或被新的候选刷新替换，校验后再提交
                if (!IsOpen) return;
                if (!(ItemsList.SelectedItem is CompletionItem selected)) return;
                _logger.Info("CompletionListWindow: mouse commit (deferred) item={0}", selected.DisplayText);
                CommitClicked?.Invoke(this, EventArgs.Empty);
            }), DispatcherPriority.Input);
        }

        private void SelectItemUnderMouse(DependencyObject source)
        {
            try
            {
                // 必须走 WpfTreeWalk：项名称文本由 Run 内联组成，鼠标事件的 OriginalSource 就是 Run，
                // 而 Run 不是 Visual —— 直接用 VisualTreeHelper.GetParent 会抛
                // "…不是 Visual 或 Visual3D"，异常发生在 WPF 输入分派里会中断鼠标路由，
                // 宿主编辑器随即失去响应（编辑器卡死）；命中文字才触发 → 症状间歇。
                var listItem = WpfTreeWalk.FindAncestor<ListBoxItem>(source);
                if (listItem == null) return;
                var item = listItem.DataContext as CompletionItem;
                if (item == null) return;
                ItemsList.SelectedItem = item;
                UpdateDetail();
            }
            catch (Exception ex)
            {
                // 输入事件路径：绝不能让异常逃出 WPF 路由，否则会破坏宿主输入分派（表现为编辑器卡死）
                _logger.Error(ex, "CompletionListWindow.SelectItemUnderMouse failed; source={0}",
                    source == null ? "<null>" : source.GetType().FullName);
            }
        }

        public event EventHandler CommitClicked;
    }

    /// <summary>把 MatchIndices 画到补全项 DisplayText 上（命中字符加粗并换强调色）。</summary>
    public static class MatchHighlight
    {
        public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached(
            "Item",
            typeof(CompletionItem),
            typeof(MatchHighlight),
            new PropertyMetadata(null, OnItemChanged));

        public static void SetItem(TextBlock element, CompletionItem value)
        {
            element.SetValue(ItemProperty, value);
        }

        public static CompletionItem GetItem(TextBlock element)
        {
            return (CompletionItem)element.GetValue(ItemProperty);
        }

        private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var tb = d as TextBlock;
            if (tb == null) return;
            tb.Inlines.Clear();
            var item = e.NewValue as CompletionItem;
            string text = item != null ? (item.DisplayText ?? string.Empty) : string.Empty;
            if (text.Length == 0) return;
            ApplyRuns(tb, text, item.MatchIndices);
        }

        private static void ApplyRuns(TextBlock tb, string text, int[] hits)
        {
            if (hits == null || hits.Length == 0)
            {
                tb.Inlines.Add(new Run(text));
                return;
            }
            // Run 不在逻辑树上，DynamicResource 会落到正文色；从 TextBlock 取资源再赋给 Run
            Brush matchFg = tb.TryFindResource("PopupMatchFgBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0xC2, 0x41, 0x0C));
            var mark = new bool[text.Length];
            foreach (int i in hits)
            {
                if (i >= 0 && i < text.Length) mark[i] = true;
            }
            int start = 0;
            while (start < text.Length)
            {
                bool hl = mark[start];
                int end = start + 1;
                while (end < text.Length && mark[end] == hl) end++;
                var run = new Run(text.Substring(start, end - start));
                if (hl)
                {
                    run.FontWeight = FontWeights.Bold;
                    run.Foreground = matchFg;
                }
                tb.Inlines.Add(run);
                start = end;
            }
        }
    }

    /// <summary>
    /// 弹框定位：按光标所在显示器的工作区夹紧，避免 SystemParameters.WorkArea（仅主屏）把窗口拉回第一屏。
    /// </summary>
    public static class PopupScreenPlacement
    {
        private const int DwmwaExtendedFrameBounds = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out NativeRect pvAttribute, int cbAttribute);

        public static Rect GetWorkAreaDip(double deviceScreenX, double deviceScreenY, double scaleX, double scaleY)
        {
            try
            {
                var pixel = new System.Drawing.Point(
                    (int)Math.Round(deviceScreenX),
                    (int)Math.Round(deviceScreenY));
                var wa = System.Windows.Forms.Screen.FromPoint(pixel).WorkingArea;
                if (scaleX <= 0) scaleX = 1;
                if (scaleY <= 0) scaleY = 1;
                return new Rect(wa.Left * scaleX, wa.Top * scaleY, wa.Width * scaleX, wa.Height * scaleY);
            }
            catch
            {
                return SystemParameters.WorkArea;
            }
        }

        public static Rect GetWindowRectDip(IntPtr hwnd, double scaleX, double scaleY)
        {
            try
            {
                if (hwnd == IntPtr.Zero)
                    return SystemParameters.WorkArea;
                NativeRect rc;
                if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rc, Marshal.SizeOf(typeof(NativeRect))) != 0
                    || rc.Right <= rc.Left)
                {
                    if (!GetWindowRect(hwnd, out rc) || rc.Right <= rc.Left)
                        return SystemParameters.WorkArea;
                }
                if (scaleX <= 0) scaleX = 1;
                if (scaleY <= 0) scaleY = 1;
                return new Rect(rc.Left * scaleX, rc.Top * scaleY, (rc.Right - rc.Left) * scaleX, (rc.Bottom - rc.Top) * scaleY);
            }
            catch
            {
                return SystemParameters.WorkArea;
            }
        }

        public static Point PlaceBottomRight(Rect anchor, double width, double height)
        {
            return new Point(anchor.Right - width - 16, anchor.Bottom - height - 16);
        }

        public static Point ClampToWorkArea(double left, double top, double width, double height, Rect work)
        {
            if (top + height > work.Bottom)
                top = Math.Max(work.Top, work.Bottom - height - 4);
            if (left + width > work.Right)
                left = Math.Max(work.Left, work.Right - width - 4);
            if (left < work.Left)
                left = work.Left;
            if (top < work.Top)
                top = work.Top;
            return new Point(left, top);
        }
    }
}
