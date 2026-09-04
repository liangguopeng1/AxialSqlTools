using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
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
        private static readonly List<IntelliSenseTextViewExtension> ActiveExtensions =
            new List<IntelliSenseTextViewExtension>();
        /// <summary>最近获得焦点/点击/输入的编辑器；只有它允许弹出悬停，避免多标签 ScreenToClient 串扰。</summary>
        private static IntelliSenseTextViewExtension _hoverOwner;
        private static DateTime _lastDiagLogUtc = DateTime.MinValue;
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
        private int _tooltipAnchorLine = -1;
        private int _tooltipAnchorCol = -1;
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
        private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

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
                if (!ActiveExtensions.Contains(this))
                    ActiveExtensions.Add(this);
                // 仅当本编辑器已有焦点时认领，避免后 Attach 的标签抢走当前页悬停权
                try
                {
                    IntPtr focus = GetFocus();
                    if (focus != IntPtr.Zero && _editorHwnd != IntPtr.Zero
                        && IsRelatedHwnd(_editorHwnd, focus))
                        ClaimHoverOwnership("attach_focus");
                }
                catch { }
                _logger.Info("QuickInfo Attach ok hwnd=0x{0:X} owners={1}",
                    _editorHwnd.ToInt64(), ActiveExtensions.Count);
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

                // 先判活动文档：后台标签勿做坐标判定（会刷 cursor_miss，且 ClientRect 与命中 HWND 常不一致）
                if (!IsSelectedTextView())
                {
                    // 后台标签：降噪，勿每 tick 写 diag（曾把日志刷到数十 MB）
                    if (ReferenceEquals(_hoverOwner, this))
                        _hoverOwner = null;
                    CloseTooltip();
                    return;
                }

                if (!TryUpdateCursorFromScreen(out string cursorReason))
                {
                    Diag("cursor_miss", cursorReason);
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
                ClaimHoverOwnership("active_view");

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

                // 本视图已稳定显示：不要每 tick 重算/重绘。滚轮不改屏幕坐标，但文档行列会变。
                if (_tooltipShowing && QuickInfoTooltip.IsOwnedBy(this) && QuickInfoTooltip.IsOpen)
                {
                if (!QuickInfoTooltip.ShouldKeepOpen
                    && TryGetHoverLineColumn(out int hoverLine, out int hoverCol)
                    && _tooltipAnchorLine >= 0
                    && (hoverLine != _tooltipAnchorLine
                        || Math.Abs(hoverCol - _tooltipAnchorCol) > 2))
                    {
                        CloseTooltip();
                        _lastMoveTime = DateTime.UtcNow;
                        return;
                    }
                    int dx = Math.Abs(_lastMovePos.X - _tooltipAnchorPos.X);
                    int dy = Math.Abs(_lastMovePos.Y - _tooltipAnchorPos.Y);
                    if (dx <= MoveCloseThreshold && dy <= MoveCloseThreshold)
                        return;
                }

                int threshold = settings.hoverTooltipDelayMs > 0 ? settings.hoverTooltipDelayMs : 500;
                if ((DateTime.UtcNow - _lastMoveTime).TotalMilliseconds < threshold) return;
                // 刚输入过：勿把光标附近的词当成悬停（输入 a 别名会误弹表信息框）
                if ((DateTime.UtcNow - _lastTypeTime).TotalMilliseconds < threshold) return;

                Diag("try_show", "pos={0},{1}", _lastMovePos.X, _lastMovePos.Y);
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
                    {
                        ClaimHoverOwnership("pointer");
                        EditorPointerDown?.Invoke();
                    }
                }
            }
            _prevLeftDown = leftDown;

            IntPtr focus = GetFocus();
            bool editorFocused = _editorHwnd != IntPtr.Zero && focus != IntPtr.Zero && IsRelatedHwnd(_editorHwnd, focus);
            if (editorFocused && !_editorHadFocus)
                ClaimHoverOwnership("focus");
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

        private bool TryUpdateCursorFromScreen(out string reason)
        {
            reason = null;
            if (_editorHwnd == IntPtr.Zero)
            {
                _editorHwnd = _textView?.GetWindowHandle() ?? IntPtr.Zero;
                if (_editorHwnd == IntPtr.Zero)
                {
                    reason = "no_hwnd";
                    return false;
                }
            }

            NativePoint screenPt;
            if (!GetCursorPos(out screenPt))
            {
                reason = "no_cursor";
                return false;
            }

            IntPtr hwndAtPoint = WindowFromPoint(screenPt);
            if (hwndAtPoint != IntPtr.Zero)
            {
                IntPtr tipHwnd = QuickInfoTooltip.GetWindowHandle();
                if (tipHwnd != IntPtr.Zero && (hwndAtPoint == tipHwnd || IsChild(tipHwnd, hwndAtPoint)))
                {
                    reason = "on_tip";
                    return true;
                }
            }

            IntPtr viewHwnd = GetViewHwnd();
            if (viewHwnd == IntPtr.Zero) viewHwnd = _editorHwnd;

            // 优先：屏幕坐标能否映射到本视图行列。SSMS 实际命中 HWND 常与 GetWindowHandle 不一致，
            // 仅用 ClientRect 会误判 out_client，导致悬停永远不弹（只能 Ctrl+Space）。
            if (TryGetLineColumnFromScreenPoint(screenPt.x, screenPt.y, out _, out _))
            {
                NativePoint clientPt = screenPt;
                if (viewHwnd != IntPtr.Zero && ScreenToClient(viewHwnd, ref clientPt))
                    NoteMove(new Point(clientPt.x, clientPt.y));
                else
                    NoteMove(new Point(screenPt.x, screenPt.y));
                reason = string.Format("ok_linecol hit=0x{0:X}", hwndAtPoint.ToInt64());
                return true;
            }

            NativePoint clientFallback = screenPt;
            if (viewHwnd == IntPtr.Zero || !ScreenToClient(viewHwnd, ref clientFallback))
            {
                reason = "s2c_fail";
                return false;
            }
            if (!GetClientRect(viewHwnd, out RECT rc))
            {
                reason = "no_rect";
                return false;
            }
            if (clientFallback.x < rc.Left || clientFallback.y < rc.Top
                || clientFallback.x >= rc.Right || clientFallback.y >= rc.Bottom)
            {
                if (hwndAtPoint == IntPtr.Zero || !IsRelatedHwnd(viewHwnd, hwndAtPoint))
                {
                    reason = string.Format("out_client pt={0},{1} rc={2},{3}-{4},{5} hit=0x{6:X}",
                        clientFallback.x, clientFallback.y, rc.Left, rc.Top, rc.Right, rc.Bottom, hwndAtPoint.ToInt64());
                    return false;
                }
            }

            NoteMove(new Point(clientFallback.x, clientFallback.y));
            reason = string.Format("ok pt={0},{1} hit=0x{2:X}", clientFallback.x, clientFallback.y, hwndAtPoint.ToInt64());
            return true;
        }

        public void ClaimHoverOwnership(string reason)
        {
            if (_editorHwnd == IntPtr.Zero)
                TryResolveEditorHwnd();
            if (!ReferenceEquals(_hoverOwner, this))
            {
                _logger.Debug("QuickInfo hover owner -> 0x{0:X} ({1})", _editorHwnd.ToInt64(), reason);
                Diag("claim", "hwnd=0x{0:X} reason={1}", _editorHwnd.ToInt64(), reason);
            }
            _hoverOwner = this;
        }

        /// <summary>
        /// 当前 SSMS 选中的文档文本视图。多标签客户区重叠时，用它区分前台/后台，
        /// 避免后台页继续按同一屏幕坐标读自己的 buffer。
        /// </summary>
        private bool IsSelectedTextView()
        {
            try
            {
                var tm = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
                if (tm == null) return true;
                if (tm.GetActiveView(0, null, out IVsTextView active) != S_OK || active == null)
                    return true;
                if (ReferenceEquals(active, _textView))
                    return true;
                IntPtr activeHwnd = IntPtr.Zero;
                try { activeHwnd = active.GetWindowHandle(); } catch { }
                if (activeHwnd == IntPtr.Zero) return true;
                // SSMS 常返回父/子 HWND，不能只比全等，否则前台标签也会被判 not_active
                if (_editorHwnd != IntPtr.Zero
                    && (activeHwnd == _editorHwnd || IsRelatedHwnd(_editorHwnd, activeHwnd)))
                    return true;
                try
                {
                    IntPtr mine = _textView?.GetWindowHandle() ?? IntPtr.Zero;
                    if (mine != IntPtr.Zero
                        && (activeHwnd == mine || IsRelatedHwnd(mine, activeHwnd)))
                        return true;
                }
                catch { }
                return false;
            }
            catch
            {
                return true;
            }
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
                if (!IsPointerOverWordGlyph(line, wordStart, wordEnd))
                {
                    LogHoverBlock("glyph", "word={0} pos={1},{2} span={3}-{4}",
                        word, _lastMovePos.X, _lastMovePos.Y, wordStart, wordEnd);
                    CloseTooltip();
                    return;
                }

                string text = GetFullText();
                int offset = GetOffsetFromLineColumn(text, line, wordStart + Math.Max(0, word.Length / 2));
                if (offset < 0) return;
                var sw = Stopwatch.StartNew();
                var connInfo = SafeGetCurrentConnection();
                string useDb = CompletionEngine.GetActiveUseDatabase(text, offset);
                MetadataCatalog catalog = null;
                if (connInfo != null)
                {
                    if (!string.IsNullOrEmpty(useDb))
                        catalog = MetadataCatalogService.Instance.GetCachedCatalog(connInfo, useDb);
                    if (catalog == null)
                        catalog = MetadataCatalogService.Instance.GetCachedCatalog(connInfo);
                    if (catalog == null)
                    {
                        MetadataCacheRefreshService.Instance.EnsureServerCache(connInfo);
                        MetadataCatalogService.Instance.EnsureCatalogBuilding(connInfo, useDb);
                        catalog = MetadataCatalogService.Instance.GetCachedCatalog(connInfo, useDb);
                    }
                }

                var info = _provider.GetQuickInfo(text, offset, catalog, connInfo);
                if (info == null || info.IsEmpty)
                    info = _provider.GetQuickInfoByWord(text, offset, word, catalog, connInfo);

                if (info == null || info.IsEmpty)
                {
                    if (!string.Equals(_lastLoggedWord, word, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastLoggedWord = word;
                        _logger.Info("QuickInfo no match word={0} connDb={1} useDb={2} catalogDb={3} catalogTables={4}",
                            word,
                            connInfo?.Database ?? "(null)",
                            useDb ?? "",
                            catalog?.Database ?? "(null)",
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

                bool opened = QuickInfoTooltip.Show(info, screenPt.x, screenPt.y, hwnd, this);
                _tooltipShowing = opened;
                _tooltipAnchorPos = _lastMovePos;
                _tooltipAnchorLine = line;
                _tooltipAnchorCol = col;
                Diag(opened ? "shown_ok" : "shown_fail", "word={0} open={1} vis={2}",
                    word, QuickInfoTooltip.IsOpen, opened);
                if (opened && !string.Equals(_lastLoggedWord, word, StringComparison.OrdinalIgnoreCase))
                {
                    _lastLoggedWord = word;
                    _logger.Info("QuickInfo shown for word={0} {1:F1}ms", word, sw.Elapsed.TotalMilliseconds);
                }
                else if (!opened)
                {
                    _logger.Debug("QuickInfo Show returned false word={0}", word);
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
            string detail = string.Format(format, args);
            _logger.Debug("QuickInfo blocked ({0}): {1}", reason, detail);
            Diag("blocked_" + reason, detail);
        }

        private void Diag(string tag, string format, params object[] args)
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastDiagLogUtc).TotalMilliseconds < 400
                    && !tag.StartsWith("claim", StringComparison.Ordinal)
                    && !tag.StartsWith("blocked", StringComparison.Ordinal)
                    && !tag.StartsWith("shown", StringComparison.Ordinal)
                    && !tag.StartsWith("not_active", StringComparison.Ordinal))
                    return;
                _lastDiagLogUtc = now;
                string detail = args != null && args.Length > 0 ? string.Format(format, args) : format;
                _logger.Debug("QuickInfo diag {0}|hwnd=0x{1:X}|{2}", tag, _editorHwnd.ToInt64(), detail);
            }
            catch
            {
            }
        }

        /// <summary>打字时调用：关掉悬停框；鼠标未真正移动前不再弹出（避免 =1 后仍对着旧词弹元数据）。</summary>
        public void NotifyTyping()
        {
            ClaimHoverOwnership("type");
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
            _tooltipAnchorLine = -1;
            _tooltipAnchorCol = -1;
            QuickInfoTooltip.CloseIfOwnedBy(this);
        }

        private bool TryGetHoverLineColumn(out int line, out int col)
        {
            line = 0;
            col = 0;
            NativePoint screenPt;
            if (!GetCursorPos(out screenPt)) return false;
            // 全程用屏幕坐标：GetPointOfLineColumn 是视图客户区坐标，经 ClientToScreen 对齐
            if (TryGetLineColumnFromScreenPoint(screenPt.x, screenPt.y, out line, out col))
                return true;
            LogHoverBlock("linecol_detail", "screen={0},{1} clientLast={2},{3}",
                screenPt.x, screenPt.y, _lastMovePos.X, _lastMovePos.Y);
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
        /// 词后若只有行尾空白（如 <c>dbo.table␠</c>），空白也算命中，否则右缘几乎点不中。
        /// </summary>
        private bool IsPointerOverWordGlyph(int line, int wordStart, int wordEnd)
        {
            if (wordEnd <= wordStart) return false;
            NativePoint screenPt;
            if (!GetCursorPos(out screenPt)) return false;

            int glyphEnd = wordEnd;
            try
            {
                if (_textView.GetBuffer(out IVsTextLines buf) == S_OK)
                {
                    buf.GetLengthOfLine(line, out int lineLen);
                    if (lineLen > wordEnd)
                    {
                        buf.GetLineText(line, 0, line, lineLen, out string lineText);
                        if (!string.IsNullOrEmpty(lineText))
                        {
                            int i = wordEnd;
                            while (i < lineLen && char.IsWhiteSpace(lineText[i])) i++;
                            // 仅扩展「词后全是行尾空白」；词间空格不扩展，避免误吸到下一词
                            if (i >= lineLen)
                                glyphEnd = lineLen;
                        }
                    }
                }
            }
            catch
            {
            }

            if (!TryGetViewPointScreen(line, wordStart, out NativePoint startScreen)) return false;
            if (!TryGetViewPointScreen(line, glyphEnd, out NativePoint endScreen)) return false;

            int top = startScreen.y;
            int bottom;
            if (_textView.GetBuffer(out IVsTextLines textLines) == S_OK
                && textLines.GetLastLineIndex(out int lastLine, out _) == S_OK
                && line < lastLine
                && TryGetViewPointScreen(line + 1, 0, out NativePoint nextScreen))
            {
                bottom = nextScreen.y;
            }
            else
            {
                int h = Math.Abs(endScreen.y - startScreen.y);
                bottom = top + Math.Max(16, h + 16);
            }

            const int pad = 4;
            int left = Math.Min(startScreen.x, endScreen.x) - pad;
            int right = Math.Max(startScreen.x, endScreen.x) + pad;
            // 单字母别名（a/b/c）字形极窄，稍扩命中区
            if (right - left < 14)
            {
                int mid = (left + right) / 2;
                left = mid - 7;
                right = mid + 7;
            }
            if (screenPt.x < left || screenPt.x > right) return false;
            if (screenPt.y < top - pad || screenPt.y >= bottom + pad) return false;
            return true;
        }

        /// <summary>
        /// 视图坐标所用 HWND。优先每次取 GetWindowHandle（与 GetPointOfLineColumn 同源）；
        /// 勿优先用可能过期的 _editorHwnd，否则 ScreenToClient 原点会偏，linecol 大面积失败。
        /// </summary>
        private IntPtr GetViewHwnd()
        {
            try
            {
                IntPtr hwnd = _textView?.GetWindowHandle() ?? IntPtr.Zero;
                if (hwnd != IntPtr.Zero)
                {
                    _editorHwnd = hwnd;
                    return hwnd;
                }
            }
            catch
            {
            }
            return _editorHwnd;
        }

        private bool TryGetViewPointClient(int line, int col, out NativePoint client)
        {
            client = new NativePoint();
            POINT[] pts = new POINT[1];
            if (_textView.GetPointOfLineColumn(line, col, pts) != S_OK)
                return false;
            client.x = pts[0].x;
            client.y = pts[0].y;
            return true;
        }

        private bool TryGetViewPointScreen(int line, int col, out NativePoint screen)
        {
            screen = new NativePoint();
            if (!TryGetViewPointClient(line, col, out NativePoint client))
                return false;
            IntPtr hwnd = GetViewHwnd();
            if (hwnd == IntPtr.Zero) return false;
            screen.x = client.x;
            screen.y = client.y;
            return ClientToScreen(hwnd, ref screen);
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#';
        }

        /// <summary>
        /// 屏幕坐标 → 行列。用「视图 HWND」把鼠标 ScreenToClient，再与 GetPointOfLineColumn
        /// 客户区坐标二分（同源 HWND）。不再因「超过行尾 X」直接失败——行尾空白交给字形命中判断。
        /// </summary>
        private bool TryGetLineColumnFromScreenPoint(int screenX, int screenY, out int line, out int col)
        {
            line = 0;
            col = 0;
            IntPtr hwnd = GetViewHwnd();
            if (hwnd == IntPtr.Zero) return false;
            NativePoint client = new NativePoint { x = screenX, y = screenY };
            if (!ScreenToClient(hwnd, ref client)) return false;
            if (_textView.GetBuffer(out IVsTextLines textLines) != S_OK) return false;
            textLines.GetLastLineIndex(out int lastLine, out _);

            int lo = 0, hi = lastLine, bestLine = 0;
            bool anyPoint = false;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (!TryGetViewPointClient(mid, 0, out NativePoint midPt)) return false;
                anyPoint = true;
                if (midPt.y <= client.y)
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

            if (!TryGetViewPointClient(bestLine, 0, out NativePoint lineTopPt)) return false;
            int lineTop = lineTopPt.y;
            int lineBottom;
            if (bestLine < lastLine && TryGetViewPointClient(bestLine + 1, 0, out NativePoint nextPt))
                lineBottom = nextPt.y;
            else
            {
                int lineHeight = 20;
                try
                {
                    if (_textView.GetLineHeight(out int h) == S_OK && h > 0)
                        lineHeight = h;
                }
                catch { }
                lineBottom = lineTop + lineHeight;
            }
            const int yPad = 2;
            if (client.y < lineTop - yPad || client.y >= lineBottom + yPad)
                return false;

            line = bestLine;
            textLines.GetLengthOfLine(line, out int lineLen);
            if (lineLen <= 0) return false;

            int colLo = 0, colHi = lineLen, bestCol = 0;
            while (colLo <= colHi)
            {
                int midCol = (colLo + colHi) / 2;
                if (!TryGetViewPointClient(line, midCol, out NativePoint colPt)) break;
                if (colPt.x <= client.x)
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
            return EditorSelectionHelper.GetFullText(_textView);
        }

        private int GetOffsetFromLineColumn(string text, int line, int col)
        {
            int pos = EditorSelectionHelper.TryGetPositionOfLineIndex(_textView, line, col);
            if (pos >= 0) return pos;
            return EditorSelectionHelper.LineColToOffset(text, line, col);
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
            ActiveExtensions.Remove(this);
            if (ReferenceEquals(_hoverOwner, this))
                _hoverOwner = null;
            if (_hoverTimer != null)
            {
                _hoverTimer.Stop();
                _hoverTimer = null;
            }
            QuickInfoTooltip.CloseIfOwnedBy(this);
        }

        public void Dispose()
        {
            Detach();
        }
    }
}
