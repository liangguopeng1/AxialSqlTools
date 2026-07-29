using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 悬停 ToolTip 渲染。无边框 WPF Window（非系统 ToolTip），锚定鼠标左/右下方，避免闪关。
    /// </summary>
    public static class QuickInfoTooltip
    {
        private static Window _window;
        private static TextBlock _textBlock;
        private static bool _isOpen;

        public static bool IsOpen => _isOpen;

        static QuickInfoTooltip()
        {
            _textBlock = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1F1F1F")),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            };
            var border = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFBEA")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0C36B")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 5, 8, 5),
                Child = _textBlock
            };
            _window = new Window
            {
                Content = border,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false,
                Focusable = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = Brushes.Transparent
            };
            _window.Closed += (s, e) => _isOpen = false;
        }

        /// <param name="deviceScreenX">鼠标屏幕坐标（device pixels）</param>
        /// <param name="deviceScreenY">鼠标屏幕坐标（device pixels）</param>
        public static void Show(string text, double deviceScreenX, double deviceScreenY, IntPtr ownerHwnd)
        {
            if (string.IsNullOrEmpty(text)) return;
            _textBlock.Text = text;
            double dipX = deviceScreenX;
            double dipY = deviceScreenY;
            try
            {
                IntPtr hwnd = ownerHwnd != IntPtr.Zero ? ownerHwnd : new WindowInteropHelper(_window).Owner;
                if (hwnd == IntPtr.Zero)
                {
                    hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                }
                if (hwnd != IntPtr.Zero)
                {
                    var helper = new WindowInteropHelper(_window) { Owner = hwnd };
                    var src = HwndSource.FromHwnd(hwnd);
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
            _window.UpdateLayout();
            const double offset = 12;
            double left = dipX;
            double top = dipY + offset;
            double workRight = SystemParameters.WorkArea.Right;
            double workBottom = SystemParameters.WorkArea.Bottom;
            if (left + _window.ActualWidth > workRight)
            {
                left = Math.Max(0, dipX - _window.ActualWidth);
            }
            if (top + _window.ActualHeight > workBottom)
            {
                top = Math.Max(0, dipY - _window.ActualHeight - offset);
            }
            _window.Left = left;
            _window.Top = top;
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
        }
    }
}
