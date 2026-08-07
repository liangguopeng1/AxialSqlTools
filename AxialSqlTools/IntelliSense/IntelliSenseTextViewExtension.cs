using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using NLog;
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using static Microsoft.VisualStudio.VSConstants;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 悬停 ToolTip：仅用 DispatcherTimer + Win32 轮询，不子类化编辑器 HWND
    ///（AssignHandle 在 Ctrl+N 新建标签时会导致 SSMS 卡死退出）。
    /// </summary>
    public class IntelliSenseTextViewExtension : IDisposable
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();
        private const int MoveCloseThreshold = 24;
        private const int HoverMoveThreshold = 4;
        private const int VK_LBUTTON = 0x01;

        private readonly IVsTextView _textView;
        private readonly QuickInfoProvider _provider = new QuickInfoProvider();
        private DispatcherTimer _hoverTimer;
        private DateTime _lastMoveTime = DateTime.MinValue;
        private DateTime _lastTypeTime = DateTime.MinValue;
        private Point _lastMovePos = new Point(-1, -1);
        private Point _tooltipAnchorPos;
        private IntPtr _editorHwnd;
        private bool _tracking;
        private bool _tooltipShowing;
        private bool _prevLeftDown;
        private bool _editorHadFocus = true;
        private string _lastLoggedWord;
        private DateTime _lastDiagLog = DateTime.MinValue;
        /// <summary>补全提交后抑制悬停，直到鼠标移开。</summary>
        private bool _suppressHoverUntilMouseMove;
        private Point _suppressHoverAnchor = new Point(-1, -1);
        /// <summary>鼠标离开本编辑器的起始时间（移向弹框的短暂间隙）。</summary>
        private DateTime _awayFromEditorSince = DateTime.MinValue;

        public event Action<IntPtr> EditorFocusLost;
        public event Action EditorPointerDown;
        public Func<IntPtr, bool> ShouldIgnoreFocusTarget { get; set; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint lpPoint);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        public IntelliSenseTextViewExtension(IVsTextView textView)
        {
            _textView = textView;
        }

        public void Attach()
        {
            var settings = UiSettingsStore.GetIntelliSenseSettings();
            if (!settings.enabled)
            {
                _logger.Info("QuickInfo Attach skipped: IntelliSense disabled");
                return;
            }

            try
            {
                _tracking = true;
                _lastMoveTime = DateTime.UtcNow;
                TryResolveEditorHwnd();
                EnsureHoverTimer();
                _logger.Info("QuickInfo Attach ok hwnd=0x{0:X}", _editorHwnd.ToInt64());
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "QuickInfo Attach failed");
            }
        }

        /// <summary>只记录 HWND，不 AssignHandle / 不子类化。</summary>
        public bool TryResolveEditorHwnd()
        {
            try
            {
                if (_editorHwnd != IntPtr.Zero) return true;
                IntPtr hwnd = _textView?.GetWindowHandle() ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero) return false;
                _editorHwnd = hwnd;
                _logger.Info("QuickInfo HWND resolved 0x{0:X}", hwnd.ToInt64());
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "QuickInfo TryResolveEditorHwnd failed");
                return false;
            }
        }

        public void EnsureHoverTimer()
        {
            if (_hoverTimer != null)
            {
                if (_editorHwnd == IntPtr.Zero)
                    TryResolveEditorHwnd();
                return;
            }
            var settings = UiSettingsStore.GetIntelliSenseSettings();
            if (!settings.enabled) return;
            TryResolveEditorHwnd();
            int delay = settings.hoverTooltipDelayMs > 0 ? settings.hoverTooltipDelayMs : 500;
            var dispatcher = GetUiDispatcher();
            _hoverTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(50, Math.Min(150, delay / 4)))
            };
            _hoverTimer.Tick += OnHoverTick;
            _hoverTimer.Start();
            _logger.Info("QuickInfo hover timer started delay={0}ms interval={1}ms", delay, _hoverTimer.Interval.TotalMilliseconds);
        }

        private static Dispatcher GetUiDispatcher()
        {
            try
            {
                if (System.Windows.Application.Current?.Dispatcher != null)
                    return System.Windows.Application.Current.Dispatcher;
            }
            catch
            {
            }
            return Dispatcher.CurrentDispatcher;
        }

        private void OnHoverTick(object sender, EventArgs e)
        {
            try
            {
                if (_editorHwnd == IntPtr.Zero)
                    TryResolveEditorHwnd();

                // 切到其他应用：强制关弹框（忽略钉住）；timer 可能仍跑，或 Application.Deactivated 已关
                if (QuickInfoTooltip.IsOpen && !QuickInfoTooltip.IsSsmsForeground())
                {
                    _tooltipShowing = false;
                    QuickInfoTooltip.Close();
                    return;
                }

                PollEditorPointerAndFocus();

                if (QuickInfoTooltip.IsOpen)
                    QuickInfoTooltip.PollOutsideClick();

                if (!_tracking) return;

                // 用「鼠标是否在本编辑器上」做门闩，不用 GetFocus（SSMS 焦点常落在子控件，会误判导致永不弹框）
                var settings = UiSettingsStore.GetIntelliSenseSettings();
                if (!settings.enabled || !settings.hoverTooltipEnabled)
                {
                    CloseTooltip();
                    return;
                }

                if (!TryUpdateCursorFromScreen())
                {
                    // 鼠标不在本编辑器：钉住时若也不在弹框上，稍候强制关（切设置页）
                    if (QuickInfoTooltip.IsOwnedBy(this) && !QuickInfoTooltip.IsPointerOverPopup())
                    {
                        if (_awayFromEditorSince == DateTime.MinValue)
                            _awayFromEditorSince = DateTime.UtcNow;
                        if ((DateTime.UtcNow - _awayFromEditorSince).TotalMilliseconds >= 400)
                        {
                            _awayFromEditorSince = DateTime.MinValue;
                            _tooltipShowing = false;
                            QuickInfoTooltip.CloseIfOwnedBy(this);
                        }
                        return;
                    }
                    _awayFromEditorSince = DateTime.MinValue;
                    if (!QuickInfoTooltip.ShouldKeepOpen)
                        CloseTooltip();
                    return;
                }
                _awayFromEditorSince = DateTime.MinValue;

                if (QuickInfoTooltip.ShouldKeepOpen)
                    return;

                if (IntelliSenseKeyHandler.AnySessionOpen())
                {
                    CloseTooltip();
                    return;
                }

                // 补全刚提交：鼠标未移开前不弹悬停框
                if (_suppressHoverUntilMouseMove)
                {
                    int sdx = Math.Abs(_lastMovePos.X - _suppressHoverAnchor.X);
                    int sdy = Math.Abs(_lastMovePos.Y - _suppressHoverAnchor.Y);
                    if (sdx <= MoveCloseThreshold && sdy <= MoveCloseThreshold)
                    {
                        CloseTooltip();
                        return;
                    }
                    _suppressHoverUntilMouseMove = false;
                    _lastMoveTime = DateTime.UtcNow;
                    return;
                }

                // 本视图已稳定显示：不要每 tick 重算/重绘
                if (_tooltipShowing && QuickInfoTooltip.IsOwnedBy(this) && QuickInfoTooltip.IsOpen)
                {
                    int dx = Math.Abs(_lastMovePos.X - _tooltipAnchorPos.X);
                    int dy = Math.Abs(_lastMovePos.Y - _tooltipAnchorPos.Y);
                    if (dx <= MoveCloseThreshold && dy <= MoveCloseThreshold)
                        return;
                }

                int threshold = settings.hoverTooltipDelayMs > 0 ? settings.hoverTooltipDelayMs : 500;
                if ((DateTime.UtcNow - _lastMoveTime).TotalMilliseconds < threshold) return;
                // 刚输入过：勿把光标附近的词当成悬停（输入 a 别名会误弹表信息框）
                if ((DateTime.UtcNow - _lastTypeTime).TotalMilliseconds < threshold) return;

                LogHoverBlock("try", "pos={0},{1}", _lastMovePos.X, _lastMovePos.Y);
                TryShowQuickInfo();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "QuickInfo OnHoverTick failed");
            }
        }

        /// <summary>轮询替代 WndProc：检测编辑器内左键按下与焦点离开。</summary>
        private void PollEditorPointerAndFocus()
        {
            bool leftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            if (leftDown && !_prevLeftDown)
            {
                NativePoint pt;
                if (GetCursorPos(out pt))
                {
                    IntPtr hwndAtPoint = WindowFromPoint(pt);
                    IntPtr tipHwnd = QuickInfoTooltip.GetWindowHandle();
                    bool onTip = tipHwnd != IntPtr.Zero && (hwndAtPoint == tipHwnd || IsChild(tipHwnd, hwndAtPoint));
                    if (!onTip && _editorHwnd != IntPtr.Zero && IsRelatedHwnd(_editorHwnd, hwndAtPoint))
                        EditorPointerDown?.Invoke();
                }
            }
            _prevLeftDown = leftDown;

            IntPtr focus = GetFocus();
            bool editorFocused = _editorHwnd != IntPtr.Zero && focus != IntPtr.Zero && IsRelatedHwnd(_editorHwnd, focus);
            if (!editorFocused && _editorHadFocus)
            {
                if (ShouldIgnoreFocusTarget == null || !ShouldIgnoreFocusTarget(focus))
                {
                    IntPtr tipHwnd = QuickInfoTooltip.GetWindowHandle();
                    if (tipHwnd == IntPtr.Zero || (focus != tipHwnd && !IsChild(tipHwnd, focus)))
                        EditorFocusLost?.Invoke(focus);
                }
            }
            _editorHadFocus = editorFocused;
        }

        private void NoteMove(Point clientPos)
        {
            int dx = Math.Abs(clientPos.X - _lastMovePos.X);
            int dy = Math.Abs(clientPos.Y - _lastMovePos.Y);
            if (dx <= HoverMoveThreshold && dy <= HoverMoveThreshold) return;
            _lastMovePos = clientPos;
            _lastMoveTime = DateTime.UtcNow;
            if (_tooltipShowing)
            {
                if (QuickInfoTooltip.ShouldKeepOpen) return;
                int anchorDx = Math.Abs(clientPos.X - _tooltipAnchorPos.X);
                int anchorDy = Math.Abs(clientPos.Y - _tooltipAnchorPos.Y);
                if (anchorDx > MoveCloseThreshold || anchorDy > MoveCloseThreshold)
                    CloseTooltip();
            }
        }

        private bool TryUpdateCursorFromScreen()
        {
            if (_editorHwnd == IntPtr.Zero)
            {
                _editorHwnd = _textView?.GetWindowHandle() ?? IntPtr.Zero;
                if (_editorHwnd == IntPtr.Zero) return false;
            }

            NativePoint screenPt;
            if (!GetCursorPos(out screenPt)) return false;

            IntPtr hwndAtPoint = WindowFromPoint(screenPt);
            if (hwndAtPoint != IntPtr.Zero)
            {
                IntPtr tipHwnd = QuickInfoTooltip.GetWindowHandle();
                if (tipHwnd != IntPtr.Zero && (hwndAtPoint == tipHwnd || IsChild(tipHwnd, hwndAtPoint)))
                    return true;
            }

            NativePoint clientPt = screenPt;
            if (!ScreenToClient(_editorHwnd, ref clientPt)) return false;
            if (!GetClientRect(_editorHwnd, out RECT rc)) return false;
            // 客户区内按坐标判定（SSMS 命中 HWND 常是兄弟窗，不能强依赖 IsRelatedHwnd）
            if (clientPt.x < rc.Left || clientPt.y < rc.Top || clientPt.x >= rc.Right || clientPt.y >= rc.Bottom)
            {
                if (hwndAtPoint == IntPtr.Zero || !IsRelatedHwnd(_editorHwnd, hwndAtPoint))
                    return false;
            }

            NoteMove(new Point(clientPt.x, clientPt.y));
            return true;
        }

        private static bool IsRelatedHwnd(IntPtr editorHwnd, IntPtr hwndAtPoint)
        {
            if (editorHwnd == IntPtr.Zero || hwndAtPoint == IntPtr.Zero) return false;
            if (hwndAtPoint == editorHwnd) return true;
            if (IsChild(editorHwnd, hwndAtPoint)) return true;
            if (IsChild(hwndAtPoint, editorHwnd)) return true;
            IntPtr p = hwndAtPoint;
            for (int i = 0; i < 12 && p != IntPtr.Zero; i++)
            {
                if (p == editorHwnd) return true;
                p = GetParent(p);
            }
            p = editorHwnd;
            for (int i = 0; i < 12 && p != IntPtr.Zero; i++)
            {
                if (p == hwndAtPoint) return true;
                p = GetParent(p);
            }
            return false;
        }


        private void TryShowQuickInfo()
        {
            if (_textView == null) return;

            try
            {
                if (IntelliSenseKeyHandler.AnySessionOpen())
                {
                    CloseTooltip();
                    return;
                }

                if (!TryGetHoverLineColumn(out int line, out int col))
                {
                    LogHoverBlock("linecol", "pos={0},{1}", _lastMovePos.X, _lastMovePos.Y);
                    return;
                }

                if (!TryGetWordSpanAtLineColumn(line, col, out int wordStart, out string word))
                {
                    LogHoverBlock("word", "line={0} col={1}", line, col);
                    CloseTooltip();
                    return;
                }

                // 必须鼠标真正落在该词字形上；行尾空白/行外不得 snip 到行末表名
                int wordEnd = wordStart + word.Length;
                if (!IsPointerOverWordGlyph(line, wordStart, wordEnd, _lastMovePos.X, _lastMovePos.Y))
                {
                    LogHoverBlock("glyph", "word={0} pos={1},{2} span={3}-{4}",
                        word, _lastMovePos.X, _lastMovePos.Y, wordStart, wordEnd);
                    CloseTooltip();
                    return;
                }

                string text = GetFullText();
                int offset = GetOffsetFromLineColumn(line, wordStart + Math.Max(0, word.Length / 2));
                if (offset < 0) return;

                var connInfo = SafeGetCurrentConnection();
                MetadataCatalog catalog = null;
                if (connInfo != null)
                {
                    // 悬停跨库对象前也预热引用库
                    MetadataCatalogService.Instance.EnsureCatalogsReferencedInSql(connInfo, text);
                    catalog = MetadataCatalogService.Instance.GetCachedCatalog(connInfo);
                    if (catalog == null)
                        MetadataCatalogService.Instance.EnsureCatalogBuilding(connInfo);
                }

                var info = _provider.GetQuickInfo(text, offset, catalog, connInfo);
                if (info == null || info.IsEmpty)
                    info = _provider.GetQuickInfoByWord(text, offset, word, catalog, connInfo);

                if (info == null || info.IsEmpty)
                {
                    if (!string.Equals(_lastLoggedWord, word, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastLoggedWord = word;
                        _logger.Info("QuickInfo no match word={0} db={1} catalogTables={2}",
                            word,
                            connInfo?.Database ?? "(null)",
                            catalog?.Tables?.Count ?? -1);
                    }
                    CloseTooltip();
                    return;
                }

                IntPtr hwnd = _textView.GetWindowHandle();
                NativePoint screenPt;
                if (!GetCursorPos(out screenPt))
                {
                    screenPt.x = _lastMovePos.X;
                    screenPt.y = _lastMovePos.Y;
                }

                QuickInfoTooltip.Show(info, screenPt.x, screenPt.y, hwnd, this);
                _tooltipShowing = true;
                _tooltipAnchorPos = _lastMovePos;
                if (!string.Equals(_lastLoggedWord, word, StringComparison.OrdinalIgnoreCase))
                {
                    _lastLoggedWord = word;
                    _logger.Info("QuickInfo shown for word={0}", word);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "QuickInfo TryShowQuickInfo failed");
            }
        }

        private void LogDiag(string format, params object[] args)
        {
            if ((DateTime.UtcNow - _lastDiagLog).TotalSeconds < 2) return;
            _lastDiagLog = DateTime.UtcNow;
            _logger.Debug(format, args);
        }

        private void LogHoverBlock(string reason, string format, params object[] args)
        {
            if ((DateTime.UtcNow - _lastDiagLog).TotalSeconds < 1) return;
            _lastDiagLog = DateTime.UtcNow;
            _logger.Info("QuickInfo blocked ({0}): {1}", reason, string.Format(format, args));
        }

        /// <summary>打字时调用：关掉悬停框；鼠标未真正移动前不再弹出（避免 =1 后仍对着旧词弹元数据）。</summary>
        public void NotifyTyping()
        {
            _lastTypeTime = DateTime.UtcNow;
            SuppressHoverUntilMouseMove();
            CloseTooltip();
        }

        /// <summary>补全选中插入后调用：关掉悬停框，直到鼠标移开再允许弹出。</summary>
        public void SuppressHoverAfterCommit()
        {
            SuppressHoverUntilMouseMove();
            _lastMoveTime = DateTime.UtcNow;
            _lastTypeTime = DateTime.UtcNow;
            CloseTooltip();
            try { QuickInfoTooltip.Close(); } catch { }
        }

        /// <summary>抑制悬停直到鼠标离开锚点；锚点必须与 _lastMovePos 同为客户区坐标。</summary>
        private void SuppressHoverUntilMouseMove()
        {
            _suppressHoverUntilMouseMove = true;
            // 优先用当前客户区位置；勿用屏幕坐标与 _lastMovePos 混比（否则抑制立刻失效）
            if (_lastMovePos.X >= 0 && _lastMovePos.Y >= 0)
            {
                _suppressHoverAnchor = _lastMovePos;
                return;
            }
            NativePoint screenPt;
            if (_editorHwnd != IntPtr.Zero && GetCursorPos(out screenPt))
            {
                NativePoint clientPt = screenPt;
                if (ScreenToClient(_editorHwnd, ref clientPt))
                {
                    _suppressHoverAnchor = new Point(clientPt.x, clientPt.y);
                    _lastMovePos = _suppressHoverAnchor;
                    return;
                }
            }
            _suppressHoverAnchor = _lastMovePos;
        }

        private void CloseTooltip()
        {
            if (!_tooltipShowing && !QuickInfoTooltip.IsOwnedBy(this)) return;
            _tooltipShowing = false;
            QuickInfoTooltip.CloseIfOwnedBy(this);
        }

        private bool TryGetHoverLineColumn(out int line, out int col)
        {
            line = 0;
            col = 0;
            if (TryGetLineColumnFromClientPoint(_lastMovePos.X, _lastMovePos.Y, out line, out col))
                return true;

            NativePoint screenPt;
            if (!GetCursorPos(out screenPt)) return false;
            IntPtr hwndAtPoint = WindowFromPoint(screenPt);
            if (hwndAtPoint == IntPtr.Zero) return false;
            NativePoint clientPt = screenPt;
            if (!ScreenToClient(hwndAtPoint, ref clientPt)) return false;
            if (TryGetLineColumnFromClientPoint(clientPt.x, clientPt.y, out line, out col))
                return true;
            clientPt = screenPt;
            if (_editorHwnd != IntPtr.Zero && ScreenToClient(_editorHwnd, ref clientPt))
                return TryGetLineColumnFromClientPoint(clientPt.x, clientPt.y, out line, out col);
            return false;
        }

        private bool TryGetWordSpanAtLineColumn(int line, int col, out int wordStart, out string word)
        {
            wordStart = 0;
            word = string.Empty;
            if (_textView.GetBuffer(out IVsTextLines textLines) != S_OK) return false;
            textLines.GetLengthOfLine(line, out int lineLen);
            if (lineLen <= 0) return false;
            textLines.GetLineText(line, 0, line, lineLen, out string lineText);
            if (string.IsNullOrEmpty(lineText)) return false;
            int index = Math.Min(Math.Max(0, col), lineLen - 1);
            if (!IsWordChar(lineText[index]) && col > 0 && col <= lineLen && IsWordChar(lineText[col - 1]))
                index = col - 1;
            if (!IsWordChar(lineText[index])) return false;
            int start = index;
            while (start > 0 && IsWordChar(lineText[start - 1])) start--;
            int end = index + 1;
            while (end < lineLen && IsWordChar(lineText[end])) end++;
            word = lineText.Substring(start, end - start).Trim();
            if (string.IsNullOrEmpty(word)) return false;
            wordStart = start;
            return true;
        }

        /// <summary>
        /// 鼠标是否落在 [wordStart, wordEnd) 的屏幕字形矩形内。
        /// 避免行尾空白/行下方空白被映射到行末标识符而误弹 QuickInfo。
        /// </summary>
        private bool IsPointerOverWordGlyph(int line, int wordStart, int wordEnd, int clientX, int clientY)
        {
            if (wordEnd <= wordStart) return false;
            POINT[] startPts = new POINT[1];
            POINT[] endPts = new POINT[1];
            if (_textView.GetPointOfLineColumn(line, wordStart, startPts) != S_OK)
                return false;
            if (_textView.GetPointOfLineColumn(line, wordEnd, endPts) != S_OK)
                return false;

            int top = startPts[0].y;
            int bottom;
            POINT[] nextPts = new POINT[1];
            if (_textView.GetBuffer(out IVsTextLines textLines) == S_OK
                && textLines.GetLastLineIndex(out int lastLine, out _) == S_OK
                && line < lastLine
                && _textView.GetPointOfLineColumn(line + 1, 0, nextPts) == S_OK)
            {
                bottom = nextPts[0].y;
            }
            else
            {
                int h = Math.Abs(endPts[0].y - startPts[0].y);
                bottom = top + Math.Max(16, h + 16);
            }

            const int pad = 3;
            int left = Math.Min(startPts[0].x, endPts[0].x) - pad;
            int right = Math.Max(startPts[0].x, endPts[0].x) + pad;
            if (clientX < left || clientX > right) return false;
            if (clientY < top - pad || clientY >= bottom + pad) return false;
            return true;
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#';
        }

        private bool TryGetLineColumnFromClientPoint(int clientX, int clientY, out int line, out int col)
        {
            line = 0;
            col = 0;
            if (_textView.GetBuffer(out IVsTextLines textLines) != S_OK) return false;
            textLines.GetLastLineIndex(out int lastLine, out _);

            int lo = 0, hi = lastLine, bestLine = 0;
            bool anyPoint = false;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                POINT[] pts = new POINT[1];
                if (_textView.GetPointOfLineColumn(mid, 0, pts) != S_OK) return false;
                anyPoint = true;
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
            if (!anyPoint) return false;

            // 鼠标必须落在该行的垂直带内，不能把行下方空白算进上一行
            POINT[] linePts = new POINT[1];
            if (_textView.GetPointOfLineColumn(bestLine, 0, linePts) != S_OK) return false;
            int lineTop = linePts[0].y;
            int lineBottom;
            POINT[] nextLinePts = new POINT[1];
            if (bestLine < lastLine
                && _textView.GetPointOfLineColumn(bestLine + 1, 0, nextLinePts) == S_OK)
            {
                lineBottom = nextLinePts[0].y;
            }
            else
            {
                lineBottom = lineTop + 20;
            }
            if (clientY < lineTop || clientY >= lineBottom)
                return false;

            line = bestLine;
            textLines.GetLengthOfLine(line, out int lineLen);

            // 行尾右侧空白：不要 snip 到最后一个字符
            if (lineLen > 0)
            {
                POINT[] eolPts = new POINT[1];
                if (_textView.GetPointOfLineColumn(line, lineLen, eolPts) == S_OK
                    && clientX > eolPts[0].x + 3)
                    return false;
            }
            else
            {
                return false;
            }

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
            // 勿按 textView 反射 DocView：新标签首次输入会触发 WinForms.Handle 创建导致原生崩溃
            try { return ScriptFactoryAccess.GetCurrentConnectionInfo(); }
            catch { return null; }
        }

        public void Detach()
        {
            _tracking = false;
            _tooltipShowing = false;
            if (_hoverTimer != null)
            {
                _hoverTimer.Stop();
                _hoverTimer = null;
            }
            QuickInfoTooltip.Close();
        }

        public void Dispose()
        {
            Detach();
        }
    }
}
