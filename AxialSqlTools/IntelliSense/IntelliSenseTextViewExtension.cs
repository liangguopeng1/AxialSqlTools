using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Threading;
using static Microsoft.VisualStudio.VSConstants;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 悬停 ToolTip 监听。通过编辑器 HWND 的 NativeWindow subclass + DispatcherTimer 检测停留。
    /// 鼠标 client 坐标 → 行列（GetPointOfLineColumn 反查）→ token；屏幕坐标锚定弹框。
    /// </summary>
    public class IntelliSenseTextViewExtension : NativeWindow, IDisposable
    {
        private const int MoveCloseThreshold = 20;
        private const int HoverMoveThreshold = 3;

        private readonly IVsTextView _textView;
        private readonly QuickInfoProvider _provider = new QuickInfoProvider();
        private DispatcherTimer _hoverTimer;
        private DateTime _lastMoveTime;
        private Point _lastMovePos;
        private Point _tooltipAnchorPos;
        private bool _focusHookActive;
        private bool _tracking;
        private bool _tooltipShowing;

        /// <summary>编辑器 HWND 失去焦点（wParam = 获得焦点的 HWND）。</summary>
        public event Action<IntPtr> EditorFocusLost;

        /// <summary>用户在编辑器内按下鼠标键（点击其他位置时应关闭补全）。</summary>
        public event Action EditorPointerDown;

        /// <summary>若返回 true，焦点转移到该 HWND 时不触发 EditorFocusLost（如补全弹框）。</summary>
        public Func<IntPtr, bool> ShouldIgnoreFocusTarget { get; set; }

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        public IntelliSenseTextViewExtension(IVsTextView textView)
        {
            _textView = textView;
        }

        public void Attach()
        {
            var settings = UiSettingsStore.GetIntelliSenseSettings();
            if (!settings.enabled) return;

            try
            {
                IntPtr hwnd = _textView.GetWindowHandle();
                if (hwnd == IntPtr.Zero) return;

                if (Handle == IntPtr.Zero)
                    AssignHandle(hwnd);
                _focusHookActive = true;

                if (!settings.hoverTooltipEnabled) return;

                _tracking = true;
                _lastMoveTime = DateTime.Now;

                int delay = settings.hoverTooltipDelayMs > 0 ? settings.hoverTooltipDelayMs : 500;
                _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(Math.Max(100, delay / 2))
                };
                _hoverTimer.Tick += OnHoverTick;
                _hoverTimer.Start();
            }
            catch
            {
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            const int WM_KILLFOCUS = 0x0008;
            const int WM_ACTIVATE = 0x0006;
            const int WA_INACTIVE = 0;

            if (m.Msg == WM_ACTIVATE && _focusHookActive)
            {
                int state = m.WParam.ToInt32() & 0xFFFF;
                if (state == WA_INACTIVE)
                    EditorFocusLost?.Invoke(IntPtr.Zero);
            }

            if (m.Msg == WM_KILLFOCUS && _focusHookActive)
            {
                IntPtr newFocus = m.WParam;
                if (ShouldIgnoreFocusTarget == null || !ShouldIgnoreFocusTarget(newFocus))
                    EditorFocusLost?.Invoke(newFocus);
            }

            if (_focusHookActive)
            {
                const int WM_LBUTTONDOWN = 0x0201;
                const int WM_RBUTTONDOWN = 0x0204;
                const int WM_MBUTTONDOWN = 0x0207;
                if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_RBUTTONDOWN || m.Msg == WM_MBUTTONDOWN)
                    EditorPointerDown?.Invoke();
            }

            if (!_tracking) return;

            const int WM_MOUSEMOVE = 0x0200;
            if (m.Msg == WM_MOUSEMOVE)
            {
                int lParam = m.LParam.ToInt32();
                int x = (short)(lParam & 0xFFFF);
                int y = (short)((lParam >> 16) & 0xFFFF);
                var newPos = new Point(x, y);
                int dx = Math.Abs(newPos.X - _lastMovePos.X);
                int dy = Math.Abs(newPos.Y - _lastMovePos.Y);
                if (dx <= HoverMoveThreshold && dy <= HoverMoveThreshold) return;

                _lastMovePos = newPos;
                _lastMoveTime = DateTime.Now;

                if (_tooltipShowing)
                {
                    int anchorDx = Math.Abs(newPos.X - _tooltipAnchorPos.X);
                    int anchorDy = Math.Abs(newPos.Y - _tooltipAnchorPos.Y);
                    if (anchorDx > MoveCloseThreshold || anchorDy > MoveCloseThreshold)
                    {
                        _tooltipShowing = false;
                        QuickInfoTooltip.Close();
                    }
                }
            }
        }

        private void OnHoverTick(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var settings = UiSettingsStore.GetIntelliSenseSettings();
            if (!settings.enabled || !settings.hoverTooltipEnabled) return;

            int threshold = settings.hoverTooltipDelayMs > 0 ? settings.hoverTooltipDelayMs : 500;
            if ((DateTime.Now - _lastMoveTime).TotalMilliseconds < threshold) return;

            if (_tooltipShowing && QuickInfoTooltip.IsOpen)
            {
                int dx = Math.Abs(_lastMovePos.X - _tooltipAnchorPos.X);
                int dy = Math.Abs(_lastMovePos.Y - _tooltipAnchorPos.Y);
                if (dx <= HoverMoveThreshold && dy <= HoverMoveThreshold) return;
            }

            TryShowQuickInfo();
        }

        private void TryShowQuickInfo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_textView == null) return;

            try
            {
                if (!TryGetLineColumnFromClientPoint(_lastMovePos.X, _lastMovePos.Y, out int line, out int col))
                {
                    return;
                }

                string text = GetFullText();
                int offset = GetOffsetFromLineColumn(line, col);
                if (offset < 0) return;

                var connInfo = SafeGetCurrentConnection();
                var catalog = connInfo != null
                    ? MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo)
                    : null;

                var info = _provider.GetQuickInfo(text, offset, catalog);
                if (string.IsNullOrEmpty(info))
                {
                    if (_tooltipShowing)
                    {
                        _tooltipShowing = false;
                        QuickInfoTooltip.Close();
                    }
                    return;
                }

                IntPtr hwnd = _textView.GetWindowHandle();
                var screenPt = new POINT { x = _lastMovePos.X, y = _lastMovePos.Y };
                if (!ClientToScreen(hwnd, ref screenPt)) return;

                QuickInfoTooltip.Show(info, screenPt.x, screenPt.y, hwnd);
                _tooltipShowing = true;
                _tooltipAnchorPos = _lastMovePos;
            }
            catch
            {
            }
        }

        private bool TryGetLineColumnFromClientPoint(int clientX, int clientY, out int line, out int col)
        {
            line = 0;
            col = 0;
            if (_textView.GetBuffer(out IVsTextLines textLines) != S_OK) return false;
            textLines.GetLastLineIndex(out int lastLine, out _);

            int lo = 0, hi = lastLine, bestLine = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                POINT[] pts = new POINT[1];
                if (_textView.GetPointOfLineColumn(mid, 0, pts) != S_OK) return false;
                if (pts[0].y <= clientY)
                {
                    bestLine = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            line = bestLine;

            textLines.GetLengthOfLine(line, out int lineLen);
            int colLo = 0, colHi = lineLen, bestCol = 0;
            while (colLo <= colHi)
            {
                int midCol = (colLo + colHi) / 2;
                POINT[] pts = new POINT[1];
                if (_textView.GetPointOfLineColumn(line, midCol, pts) != S_OK) break;
                if (pts[0].x <= clientX)
                {
                    bestCol = midCol;
                    colLo = midCol + 1;
                }
                else
                {
                    colHi = midCol - 1;
                }
            }
            col = bestCol;
            return true;
        }

        private string GetFullText()
        {
            if (_textView.GetBuffer(out IVsTextLines textLines) != S_OK) return string.Empty;
            textLines.GetLastLineIndex(out int lastLine, out int lastCol);
            var sb = new StringBuilder();
            for (int i = 0; i <= lastLine; i++)
            {
                textLines.GetLengthOfLine(i, out int lineLen);
                textLines.GetLineText(i, 0, i, lineLen, out string lineText);
                sb.Append(lineText);
                if (i < lastLine) sb.Append("\r\n");
            }
            return sb.ToString();
        }

        private int GetOffsetFromLineColumn(int line, int col)
        {
            if (_textView.GetBuffer(out IVsTextLines textLines) != S_OK) return -1;
            int offset = 0;
            for (int i = 0; i < line; i++)
            {
                textLines.GetLengthOfLine(i, out int lineLen);
                offset += lineLen + 2;
            }
            return offset + col;
        }

        private ScriptFactoryAccess.ConnectionInfo SafeGetCurrentConnection()
        {
            try { return ScriptFactoryAccess.GetCurrentConnectionInfo(); }
            catch { return null; }
        }

        public void Detach()
        {
            _focusHookActive = false;
            _tracking = false;
            _tooltipShowing = false;
            if (_hoverTimer != null)
            {
                _hoverTimer.Stop();
                _hoverTimer = null;
            }
            if (this.Handle != IntPtr.Zero)
            {
                ReleaseHandle();
            }
            QuickInfoTooltip.Close();
        }

        public void Dispose()
        {
            Detach();
        }
    }
}
