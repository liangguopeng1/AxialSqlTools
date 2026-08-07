using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 悬停 ToolTip。可鼠标选中复制、拖拽调宽高；跟随主题色，失败退回白底。
    /// </summary>
    public static class QuickInfoTooltip
    {
        private const double MinWidth = 280;
        private const double MinHeight = 120;
        private const double DefaultMaxWidth = 720;
        private const double DefaultMaxHeight = 520;

        private static Window _window;
        private static Border _outerBorder;
        private static ScrollViewer _scrollViewer;
        private static StackPanel _contentStack;
        private static TextBox _headerBox;
        private static TextBox _ddlBox;
        private static Button _goToSourceButton;
        private static Border _actionBar;
        private static Grid _rootGrid;
        private static bool _isOpen;
        private static bool _isPinned;
        private static bool _isResizing;
        private static bool _contextMenuOpen;
        private static DateTime _suppressDeactivateCloseUntil = DateTime.MinValue;
        private static bool _wasMouseLeftDown;
        private static string _lastContentKey;
        private static bool _userResized;
        private static double _savedWidth = 480;
        private static double _savedHeight = 320;
        private static object _owner;
        private static string _goToSourceSql;
        private static string _goToSourceDatabase;

        public static bool IsOpen => _isOpen;

        /// <summary>用户已点击弹框：移开鼠标不关闭，点其他地方才关。</summary>
        public static bool IsPinned => _isPinned && _isOpen;

        /// <summary>应保持打开：已钉住 / 正在缩放 / 右键菜单 / 鼠标在弹框上。
        /// 切走文档后由编辑器 timer 在「不在编辑器且不在弹框」时强制关闭。</summary>
        public static bool ShouldKeepOpen =>
            _isOpen && (_isPinned || _isResizing || _contextMenuOpen
                || DateTime.UtcNow < _suppressDeactivateCloseUntil
                || IsMouseOverPopup());

        /// <summary>指针是否在悬停弹框上（供编辑器 timer 判断是否离开）。</summary>
        public static bool IsPointerOverPopup() => IsMouseOverPopup();

        /// <summary>当前弹框是否由指定悬停源打开（多标签共用一个弹框时防互关闪烁）。</summary>
        public static bool IsOwnedBy(object owner) =>
            _isOpen && owner != null && ReferenceEquals(_owner, owner);

        /// <summary>鼠标在弹框上，或用户正在选中/调整大小。</summary>
        public static bool IsInteracting => ShouldKeepOpen;

        private static bool IsMouseOverPopup()
        {
            if (!_isOpen || _window == null || !_window.IsVisible) return false;
            if (_window.IsMouseOver) return true;
            try
            {
                IntPtr tip = GetWindowHandle();
                if (tip == IntPtr.Zero) return false;
                POINT pt;
                if (!GetCursorPos(out pt)) return false;
                IntPtr hwnd = WindowFromPoint(pt);
                return hwnd != IntPtr.Zero && (hwnd == tip || IsChild(tip, hwnd));
            }
            catch
            {
                return false;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static readonly uint SsmsProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        private static bool _appHooksRegistered;

        /// <summary>SSMS 是否仍是前台进程；切到其他应用时应关掉弹框。</summary>
        public static bool IsSsmsForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                GetWindowThreadProcessId(fg, out uint pid);
                return pid == SsmsProcessId;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>前台丢失时强制关闭（忽略钉住）。</summary>
        public static void CloseIfAppDeactivated()
        {
            if (!_isOpen) return;
            if (IsSsmsForeground()) return;
            Close();
        }

        private static void EnsureAppLifecycleHooks()
        {
            if (_appHooksRegistered) return;
            _appHooksRegistered = true;
            try
            {
                var app = Application.Current;
                if (app == null) return;
                app.Deactivated += (s, e) =>
                {
                    if (_contextMenuOpen || DateTime.UtcNow < _suppressDeactivateCloseUntil)
                        return;
                    try { Close(); } catch { }
                };
                app.Exit += (s, e) =>
                {
                    try { Close(); } catch { }
                };
            }
            catch
            {
            }
        }

        public static void Pin()
        {
            if (_isOpen) _isPinned = true;
        }

        /// <summary>点击编辑器等外部区域时关闭（已钉住也关）。</summary>
        public static void CloseFromOutsideClick()
        {
            Close();
        }

        /// <summary>
        /// 轮询外部点击：左键新按下且不在弹框上 → 关闭。
        /// 由悬停 timer 调用（编辑器子 HWND 常收不到 WM_LBUTTONDOWN）。
        /// </summary>
        public static void PollOutsideClick()
        {
            if (!_isOpen) return;
            if (_contextMenuOpen || DateTime.UtcNow < _suppressDeactivateCloseUntil)
                return;
            if (!IsSsmsForeground())
            {
                Close();
                return;
            }
            bool down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            bool pressed = down && !_wasMouseLeftDown;
            _wasMouseLeftDown = down;
            if (!pressed) return;
            if (_isResizing) return;
            if (IsMouseOverPopup()) return;
            CloseFromOutsideClick();
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int VK_LBUTTON = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        static QuickInfoTooltip()
        {
            _headerBox = CreateSelectableBox(false);
            _ddlBox = CreateSelectableBox(true);
            _contentStack = new StackPanel();
            _contentStack.Children.Add(_headerBox);
            _contentStack.Children.Add(_ddlBox);
            _scrollViewer = new ScrollViewer
            {
                Content = _contentStack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0),
                Focusable = true
            };
            _goToSourceButton = new Button
            {
                Content = "跳转源码",
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0),
                FontSize = 12,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Focusable = true
            };
            _goToSourceButton.Click += OnGoToSourceClick;
            _actionBar = new Border
            {
                Padding = new Thickness(0, 6, 16, 0),
                Child = _goToSourceButton,
                Visibility = Visibility.Collapsed
            };
            var body = new DockPanel();
            DockPanel.SetDock(_actionBar, Dock.Bottom);
            body.Children.Add(_actionBar);
            body.Children.Add(_scrollViewer);
            _outerBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 6, 8, 6),
                Child = body
            };
            var resizeGrip = new Thumb
            {
                Width = 14,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = Cursors.SizeNWSE,
                Opacity = 0.55,
                Margin = new Thickness(0, 0, 2, 2)
            };
            resizeGrip.DragStarted += (s, e) =>
            {
                _isResizing = true;
                Pin();
            };
            resizeGrip.DragCompleted += (s, e) => _isResizing = false;
            resizeGrip.DragDelta += OnResizeGripDragDelta;
            resizeGrip.ToolTip = "拖动调整大小";
            _rootGrid = new Grid();
            _rootGrid.Children.Add(_outerBorder);
            _rootGrid.Children.Add(resizeGrip);
            _window = new Window
            {
                Content = _rootGrid,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false,
                Focusable = true,
                ResizeMode = ResizeMode.CanResize,
                SizeToContent = SizeToContent.Manual,
                Background = Brushes.Transparent,
                MinWidth = MinWidth,
                MinHeight = MinHeight,
                MaxWidth = DefaultMaxWidth,
                MaxHeight = DefaultMaxHeight,
                Width = _savedWidth,
                Height = _savedHeight
            };
            var chrome = new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(8),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(_window, chrome);
            WindowChrome.SetIsHitTestVisibleInChrome(resizeGrip, true);
            WindowChrome.SetIsHitTestVisibleInChrome(_scrollViewer, true);
            WindowChrome.SetIsHitTestVisibleInChrome(_headerBox, true);
            WindowChrome.SetIsHitTestVisibleInChrome(_ddlBox, true);
            WindowChrome.SetIsHitTestVisibleInChrome(_goToSourceButton, true);
            WindowChrome.SetIsHitTestVisibleInChrome(_actionBar, true);
            _window.Closed += (s, e) =>
            {
                _isOpen = false;
                _isPinned = false;
                _isResizing = false;
            };
            _window.PreviewMouseDown += (s, e) =>
            {
                Pin();
                try { _window.Activate(); } catch { }
            };
            _window.Deactivated += OnWindowDeactivated;
            _window.PreviewKeyDown += OnPreviewKeyDown;
            _window.GotFocus += (s, e) => Pin();
            _window.SourceInitialized += OnWindowSourceInitialized;
            HookTextBoxContextMenu(_headerBox);
            HookTextBoxContextMenu(_ddlBox);
            _window.SizeChanged += (s, e) =>
            {
                // 仅用户拖拽缩放时锁定尺寸；自动 Measure / Pin 选中不要当成「用户已调大小」
                if (!_isOpen || !_isResizing) return;
                _userResized = true;
                _savedWidth = _window.ActualWidth > 0 ? _window.ActualWidth : _window.Width;
                _savedHeight = _window.ActualHeight > 0 ? _window.ActualHeight : _window.Height;
            };
            ApplyThemeColors();
            EnsureAppLifecycleHooks();
        }

        /// <summary>右键菜单打开期间/刚复制完勿因失活关掉弹框。</summary>
        private static void HookTextBoxContextMenu(TextBox box)
        {
            if (box == null) return;
            box.ContextMenuOpening += (s, e) =>
            {
                _contextMenuOpen = true;
                _suppressDeactivateCloseUntil = DateTime.UtcNow.AddSeconds(30);
                Pin();
            };
            box.ContextMenuClosing += (s, e) =>
            {
                _contextMenuOpen = false;
                _suppressDeactivateCloseUntil = DateTime.UtcNow.AddMilliseconds(600);
                Pin();
                try { _window?.Activate(); } catch { }
            };
        }

        private static void OnWindowDeactivated(object sender, EventArgs e)
        {
            if (!_isOpen || _isResizing) return;
            if (_contextMenuOpen || DateTime.UtcNow < _suppressDeactivateCloseUntil)
                return;
            // 延迟判断：点弹框内控件时可能短暂失活；切到其他应用则立即关
            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isOpen || _isResizing) return;
                if (_contextMenuOpen || DateTime.UtcNow < _suppressDeactivateCloseUntil)
                    return;
                if (!IsSsmsForeground())
                {
                    Close();
                    return;
                }
                if (IsMouseOverPopup()) return;
                if (_window.IsActive) return;
                // ShowActivated=false：悬停弹出本就不激活窗口，Deactivated 不代表该关；
                // 未钉住时由 PollOutsideClick / 编辑器点击关闭，避免刚 Show 就闪关。
                if (!_isPinned) return;
                CloseFromOutsideClick();
            }), DispatcherPriority.Input);
        }

        private static void OnWindowSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                var helper = new WindowInteropHelper(_window);
                var source = HwndSource.FromHwnd(helper.Handle);
                source?.AddHook(WndProcHook);
            }
            catch
            {
            }
        }

        private static IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_ENTERSIZEMOVE = 0x0231;
            const int WM_EXITSIZEMOVE = 0x0232;
            const int WM_NCLBUTTONDOWN = 0x00A1;
            if (msg == WM_ENTERSIZEMOVE || msg == WM_NCLBUTTONDOWN)
            {
                _isResizing = true;
                Pin();
            }
            else if (msg == WM_EXITSIZEMOVE)
            {
                _isResizing = false;
                _userResized = true;
                _savedWidth = _window.ActualWidth > 0 ? _window.ActualWidth : _window.Width;
                _savedHeight = _window.ActualHeight > 0 ? _window.ActualHeight : _window.Height;
            }
            return IntPtr.Zero;
        }

        private static TextBox CreateSelectableBox(bool mono)
        {
            return new TextBox
            {
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FontFamily = new FontFamily(mono ? "Consolas" : "Segoe UI"),
                FontSize = 12,
                Focusable = true,
                IsUndoEnabled = false,
                Visibility = Visibility.Collapsed
            };
        }

        private static void OnGoToSourceClick(object sender, RoutedEventArgs e)
        {
            Pin();
            _suppressDeactivateCloseUntil = DateTime.UtcNow.AddSeconds(2);
            string sql = _goToSourceSql;
            string database = _goToSourceDatabase;
            if (string.IsNullOrEmpty(sql)) return;
            try
            {
                Close();
                ThreadHelper.ThrowIfNotOnUIThread();
                OpenProcedureSourceInNewQuery(sql, database);
            }
            catch (Exception ex)
            {
                try
                {
                    System.Windows.MessageBox.Show(
                        "打开源码失败: " + ex.Message,
                        "Axial SQL Tools",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch
                {
                }
            }
        }

        private static void OpenProcedureSourceInNewQuery(string sql, string database)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var connInfo = ScriptFactoryAccess.GetCurrentConnectionInfo();
            if (connInfo != null && !string.IsNullOrWhiteSpace(database))
                connInfo = ScriptFactoryAccess.CloneWithDatabase(connInfo, database) ?? connInfo;

            if (connInfo?.ActiveConnectionInfo != null)
                ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, connInfo.ActiveConnectionInfo, null);
            else
            {
                var ui = TryBuildUiConnection(connInfo);
                if (ui != null)
                    ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, ui, null);
                else
                    ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql);
            }

            var doc = (EnvDTE.TextDocument)ServiceCache.ExtensibilityModel.Application.ActiveDocument.Object(null);
            doc.EndPoint.CreateEditPoint().Insert(sql);
        }

        private static UIConnectionInfo TryBuildUiConnection(ScriptFactoryAccess.ConnectionInfo connInfo)
        {
            if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.FullConnectionString))
                return null;
            try
            {
                var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connInfo.FullConnectionString);
                return ScriptFactoryAccess.TryCreateUiConnectionInfo(
                    builder,
                    builder.UserID,
                    builder.Password,
                    string.Empty);
            }
            catch
            {
                return null;
            }
        }

        private static void OnResizeGripDragDelta(object sender, DragDeltaEventArgs e)
        {
            Pin();
            _isResizing = true;
            double w = Math.Max(MinWidth, _window.Width + e.HorizontalChange);
            double h = Math.Max(MinHeight, _window.Height + e.VerticalChange);
            w = Math.Min(w, DefaultMaxWidth);
            h = Math.Min(h, DefaultMaxHeight);
            _window.Width = w;
            _window.Height = h;
            _userResized = true;
            _savedWidth = w;
            _savedHeight = h;
        }

        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                try
                {
                    if (_ddlBox.IsKeyboardFocusWithin && _ddlBox.SelectionLength > 0)
                        _ddlBox.Copy();
                    else if (_headerBox.IsKeyboardFocusWithin && _headerBox.SelectionLength > 0)
                        _headerBox.Copy();
                    else if (_ddlBox.SelectionLength > 0)
                        _ddlBox.Copy();
                    else if (_headerBox.SelectionLength > 0)
                        _headerBox.Copy();
                    else
                    {
                        var sb = new StringBuilder();
                        if (_headerBox.Visibility == Visibility.Visible && !string.IsNullOrEmpty(_headerBox.Text))
                            sb.AppendLine(_headerBox.Text);
                        if (_ddlBox.Visibility == Visibility.Visible && !string.IsNullOrEmpty(_ddlBox.Text))
                            sb.Append(_ddlBox.Text);
                        if (sb.Length > 0)
                            Clipboard.SetText(sb.ToString());
                    }
                    e.Handled = true;
                }
                catch
                {
                }
            }
            else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                try
                {
                    if (_ddlBox.IsKeyboardFocusWithin || _ddlBox.IsMouseOver)
                        _ddlBox.SelectAll();
                    else
                        _headerBox.SelectAll();
                    e.Handled = true;
                }
                catch
                {
                }
            }
            else if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private static void ApplyThemeColors()
        {
            Brush bg = null;
            Brush fg = null;
            Brush border = null;
            try
            {
                bg = Application.Current?.TryFindResource(EnvironmentColors.ToolWindowBackgroundBrushKey) as Brush;
                fg = Application.Current?.TryFindResource(EnvironmentColors.ToolWindowTextBrushKey) as Brush;
                border = Application.Current?.TryFindResource(EnvironmentColors.ToolWindowBorderBrushKey) as Brush;
            }
            catch
            {
            }
            if (bg == null) bg = Brushes.White;
            if (fg == null) fg = Brushes.Black;
            if (border == null) border = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));

            _outerBorder.Background = bg;
            _outerBorder.BorderBrush = border;
            _headerBox.Foreground = fg;
            _headerBox.Background = Brushes.Transparent;
            _ddlBox.Foreground = fg;
            Color bgColor = bg is SolidColorBrush sb ? sb.Color : Colors.White;
            bool light = (0.2126 * bgColor.R + 0.7152 * bgColor.G + 0.0722 * bgColor.B) / 255.0 > 0.6;
            _ddlBox.Background = light
                ? new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0))
                : new SolidColorBrush(Color.FromArgb(50, 0, 0, 0));
            _scrollViewer.Background = Brushes.Transparent;
            _rootGrid.Background = Brushes.Transparent;
            try
            {
                _goToSourceButton.Foreground = fg;
                _goToSourceButton.Background = light
                    ? new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8))
                    : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
                _goToSourceButton.BorderBrush = border;
            }
            catch
            {
            }
        }

        public static bool Show(QuickInfoData data, double deviceScreenX, double deviceScreenY, IntPtr ownerHwnd, object owner = null)
        {
            if (data == null || data.IsEmpty) return false;
            EnsureAppLifecycleHooks();
            if (!IsSsmsForeground()) return false;
            // 钉住时仍允许换内容（列→表），仅相同内容时跳过
            string key = BuildContentKey(data);
            bool sameContent = _isOpen && string.Equals(_lastContentKey, key, StringComparison.Ordinal);
            if (sameContent)
                return true;

            // 换内容：必须重新按内容测算，忽略上次用户/自动尺寸
            _userResized = false;
            _owner = owner ?? (object)ownerHwnd;
            // 刚弹出时抑制 Deactivated 闪关
            _suppressDeactivateCloseUntil = DateTime.UtcNow.AddMilliseconds(800);
            ApplyThemeColors();
            var header = new StringBuilder();
            foreach (var line in data.HeaderLines)
                header.AppendLine(line);
            if (!string.IsNullOrEmpty(data.Description))
            {
                if (header.Length > 0) header.AppendLine();
                header.Append("-- ").Append(data.Description);
            }
            string headerText = header.ToString().TrimEnd();
            if (!string.IsNullOrEmpty(headerText))
            {
                _headerBox.Text = headerText;
                _headerBox.Visibility = Visibility.Visible;
                _headerBox.Margin = string.IsNullOrEmpty(data.DdlText)
                    ? new Thickness(0)
                    : new Thickness(0, 0, 0, 8);
            }
            else
            {
                _headerBox.Text = string.Empty;
                _headerBox.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrEmpty(data.DdlText))
            {
                _ddlBox.Text = data.DdlText;
                _ddlBox.Visibility = Visibility.Visible;
                _ddlBox.Padding = new Thickness(8, 6, 8, 6);
            }
            else
            {
                _ddlBox.Text = string.Empty;
                _ddlBox.Visibility = Visibility.Collapsed;
            }

            if (data.CanGoToSource && !string.IsNullOrEmpty(data.DdlText))
            {
                _goToSourceSql = data.DdlText;
                _goToSourceDatabase = data.SourceDatabase;
                string tipName = string.IsNullOrEmpty(data.SourceObjectName)
                    ? "源码"
                    : data.SourceObjectName;
                _goToSourceButton.ToolTip = "在新查询窗口打开 " + tipName;
                _actionBar.Visibility = Visibility.Visible;
            }
            else
            {
                _goToSourceSql = null;
                _goToSourceDatabase = null;
                _actionBar.Visibility = Visibility.Collapsed;
            }

            _lastContentKey = key;
            PositionAndShow(deviceScreenX, deviceScreenY, ownerHwnd, reposition: true);
            return _isOpen && _window != null && _window.IsVisible;
        }

        public static bool Show(string text, double deviceScreenX, double deviceScreenY, IntPtr ownerHwnd, object owner = null)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return Show(new QuickInfoData { DdlText = text }, deviceScreenX, deviceScreenY, ownerHwnd, owner);
        }

        private static string BuildContentKey(QuickInfoData data)
        {
            var sb = new StringBuilder();
            if (data.HeaderLines != null)
            {
                foreach (var line in data.HeaderLines)
                    sb.Append(line).Append('|');
            }
            sb.Append(data.Description).Append('|').Append(data.DdlText);
            return sb.ToString();
        }

        private static void PositionAndShow(double deviceScreenX, double deviceScreenY, IntPtr ownerHwnd, bool reposition)
        {
            double dipX = deviceScreenX;
            double dipY = deviceScreenY;
            try
            {
                IntPtr hwnd = ownerHwnd != IntPtr.Zero ? ownerHwnd : new WindowInteropHelper(_window).Owner;
                if (hwnd == IntPtr.Zero)
                    hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    // 用主窗口作 Owner，避免绑编辑器 HWND 引起闪烁/激活抖动
                    var helper = new WindowInteropHelper(_window);
                    IntPtr main = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                    if (main != IntPtr.Zero && helper.Owner != main)
                        helper.Owner = main;
                    var src = HwndSource.FromHwnd(helper.Handle);
                    if (src == null)
                        src = PresentationSource.FromVisual(_window) as HwndSource;
                    if (src?.CompositionTarget != null)
                    {
                        var m = src.CompositionTarget.TransformFromDevice;
                        dipX = deviceScreenX * m.M11;
                        dipY = deviceScreenY * m.M22;
                    }
                }
            }
            catch
            {
            }

            if (!_userResized)
            {
                ApplyAutoSizeFromContent();
            }
            else
            {
                _window.SizeToContent = SizeToContent.Manual;
                _window.Width = _savedWidth;
                _window.Height = _savedHeight;
            }

            if (reposition || !_isOpen)
            {
                const double offset = 16;
                double left = dipX + 8;
                double top = dipY + offset;
                double workRight = SystemParameters.WorkArea.Right;
                double workBottom = SystemParameters.WorkArea.Bottom;
                if (left + _window.Width > workRight)
                    left = Math.Max(0, dipX - _window.Width - 8);
                if (top + _window.Height > workBottom)
                    top = Math.Max(0, dipY - _window.Height - offset);
                _window.Left = left;
                _window.Top = top;
            }

            _window.Visibility = Visibility.Visible;
            if (!_isOpen)
            {
                try
                {
                    _window.Show();
                    _isOpen = true;
                }
                catch
                {
                    _isOpen = false;
                }
            }
            else if (!_window.IsVisible)
            {
                try { _window.Show(); } catch { }
            }

            // Show 后再强制一次尺寸（首次 Show 前 Actual 可能不准）
            if (!_userResized)
            {
                ApplyAutoSizeFromContent();
                _window.UpdateLayout();
            }
        }

        /// <summary>
        /// 直接测量 StackPanel 内容尺寸。ScrollViewer 的 DesiredSize 是视口而非内容，
        /// 测 Window 会沿用旧小尺寸，导致列→表后弹框缩成最小。
        /// </summary>
        private static void ApplyAutoSizeFromContent()
        {
            const double chrome = 28; // border + padding + grip
            double maxContentW = DefaultMaxWidth - chrome;
            double maxContentH = DefaultMaxHeight - chrome;

            var prevV = _scrollViewer.VerticalScrollBarVisibility;
            var prevH = _scrollViewer.HorizontalScrollBarVisibility;
            _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            try
            {
                // 限定最大宽度让 TextWrapping 生效，高度不限以便算出全文高度
                _headerBox.MaxWidth = maxContentW;
                _ddlBox.MaxWidth = maxContentW;
                _contentStack.InvalidateMeasure();
                _contentStack.Measure(new Size(maxContentW, double.PositiveInfinity));
                double contentW = Math.Ceiling(_contentStack.DesiredSize.Width);
                double contentH = Math.Ceiling(_contentStack.DesiredSize.Height);
                // 兜底：按行数估高，避免 TextBox 偶发 DesiredSize 过小
                int lines = EstimateLineCount(_headerBox.Text) + EstimateLineCount(_ddlBox.Text);
                double minByLines = Math.Min(maxContentH, 24 + lines * 16);
                contentH = Math.Max(contentH, minByLines);
                contentW = Math.Max(contentW, MinWidth - chrome);

                double desiredW = Math.Max(MinWidth, Math.Min(DefaultMaxWidth, contentW + chrome));
                double desiredH = Math.Max(MinHeight, Math.Min(DefaultMaxHeight, contentH + chrome));

                _window.SizeToContent = SizeToContent.Manual;
                _window.Width = desiredW;
                _window.Height = desiredH;
                _savedWidth = desiredW;
                _savedHeight = desiredH;
            }
            finally
            {
                _headerBox.ClearValue(FrameworkElement.MaxWidthProperty);
                _ddlBox.ClearValue(FrameworkElement.MaxWidthProperty);
                _scrollViewer.VerticalScrollBarVisibility = prevV;
                _scrollViewer.HorizontalScrollBarVisibility = prevH;
            }
        }

        private static int EstimateLineCount(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int n = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') n++;
            }
            return n;
        }

        public static IntPtr GetWindowHandle()
        {
            try
            {
                return new WindowInteropHelper(_window).Handle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        public static void Close()
        {
            if (!_isOpen) return;
            try
            {
                _window.Hide();
            }
            catch
            {
            }
            _isOpen = false;
            _isPinned = false;
            _isResizing = false;
            _contextMenuOpen = false;
            _suppressDeactivateCloseUntil = DateTime.MinValue;
            _userResized = false;
            _wasMouseLeftDown = false;
            _lastContentKey = null;
            _owner = null;
            _goToSourceSql = null;
            _goToSourceDatabase = null;
            if (_actionBar != null)
                _actionBar.Visibility = Visibility.Collapsed;
        }

        /// <summary>仅当弹框属于该 owner 时关闭，避免其他标签的悬停 timer 误关导致闪烁。</summary>
        public static void CloseIfOwnedBy(object owner)
        {
            if (IsOwnedBy(owner))
                Close();
        }
    }
}
