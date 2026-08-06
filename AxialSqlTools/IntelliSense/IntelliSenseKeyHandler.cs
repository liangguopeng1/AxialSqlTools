using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using NLog;
using static Microsoft.VisualStudio.VSConstants;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 每编辑器视图一个。防抖触发补全、弹框态键路由、提交插入。
    /// 由 KeypressCommandFilter 持有并驱动。所有操作在 UI 线程。
    /// </summary>
    public class IntelliSenseKeyHandler
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();
        private static readonly List<IntelliSenseKeyHandler> ActiveHandlers = new List<IntelliSenseKeyHandler>();

        public static void CloseAllSessions()
        {
            for (int i = ActiveHandlers.Count - 1; i >= 0; i--)
            {
                try
                {
                    ActiveHandlers[i]?.CloseSession();
                }
                catch
                {
                }
            }
        }

        /// <summary>执行查询/切窗口后短暂抑制自动弹，避免 F5 后又弹出补全。</summary>
        public static void SuppressAutoTriggerBriefly(int ms = 1500)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            for (int i = ActiveHandlers.Count - 1; i >= 0; i--)
            {
                try
                {
                    var h = ActiveHandlers[i];
                    if (h != null && h._suppressAutoTriggerUntil < until)
                        h._suppressAutoTriggerUntil = until;
                }
                catch
                {
                }
            }
        }

        public static void EnsureAllHoverTimers()
        {
            for (int i = ActiveHandlers.Count - 1; i >= 0; i--)
            {
                try
                {
                    ActiveHandlers[i]?.EnsureHoverTimer();
                }
                catch
                {
                }
            }
        }

        public static bool AnySessionOpen()
        {
            for (int i = ActiveHandlers.Count - 1; i >= 0; i--)
            {
                var handler = ActiveHandlers[i];
                if (handler != null && handler._sessionOpen)
                    return true;
            }
            return false;
        }

        public void EnsureHoverTimer()
        {
            // 勿每次调用 Attach：按键路径会反复进来，重绑 HWND 易导致 SSMS 卡死
            _textViewExtension?.EnsureHoverTimer();
        }

        private readonly AxialSqlToolsPackage _package;
        private readonly IVsTextView _textView;
        private CompletionListWindow _window;
        private readonly DispatcherTimer _debounceTimer;
        private readonly DispatcherTimer _externalFocusCloseTimer;
        private readonly DispatcherTimer _sessionWatchdogTimer;
        private readonly IntelliSenseTextViewExtension _textViewExtension;

        private bool _sessionOpen;
        private DateTime _sessionOpenedAt;
        private DateTime _suppressAutoTriggerUntil = DateTime.MinValue;
        private int _sessionCaretLine = -1;
        private int _replaceStartOffset = -1;
        private int _replaceEndOffset = -1;
        private int _completionGen;
        private const int SessionAcceptDelayMs = 400;
        private const int SuppressAutoTriggerAfterCommitMs = 500;
        private const int CompletionEngineWarnMs = 500;

        private bool IsWithinAcceptGrace() =>
            _sessionOpen && (DateTime.UtcNow - _sessionOpenedAt).TotalMilliseconds < SessionAcceptDelayMs;

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static readonly uint SsmsProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        public IntelliSenseKeyHandler(AxialSqlToolsPackage package, IVsTextView textView)
        {
            _package = package;
            _textView = textView;
            // CompletionListWindow 延到首次弹框再建，避免新标签首次按键同步创建多个 WPF Window

            var settings = UiSettingsStore.GetIntelliSenseSettings();
            int delay = settings.autoTriggerDelayMs > 0 ? settings.autoTriggerDelayMs : 200;
            var dispatcher = System.Windows.Application.Current?.Dispatcher
                ?? Dispatcher.CurrentDispatcher;
            _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(delay)
            };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                TriggerCompletion(false);
            };

            _externalFocusCloseTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _externalFocusCloseTimer.Tick += (s, e) =>
            {
                _externalFocusCloseTimer.Stop();
                if (!_sessionOpen) return;
                if (!IsSsmsForeground())
                {
                    CloseSession();
                    return;
                }
                if (EditorHasFocus() || IsPopupFocus(GetFocus())) return;
                if (IsWithinAcceptGrace()) return;
                CloseSession();
            };

            _sessionWatchdogTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _sessionWatchdogTimer.Tick += (s, e) =>
            {
                if (!_sessionOpen)
                {
                    _sessionWatchdogTimer.Stop();
                    return;
                }
                if (!IsSsmsForeground())
                {
                    CloseSession();
                    return;
                }
                if (IsWithinAcceptGrace()) return;
                if (_sessionCaretLine >= 0 &&
                    _textView.GetCaretPos(out int line, out _) == VSConstants.S_OK &&
                    line != _sessionCaretLine)
                {
                    CloseSession();
                }
            };

            // 悬停 ToolTip + 编辑器失焦关闭（独立于补全 session 的 ToolTip 部分）
            _textViewExtension = new IntelliSenseTextViewExtension(textView);
            _textViewExtension.ShouldIgnoreFocusTarget = hwnd => IsPopupFocus(hwnd);
            _textViewExtension.EditorFocusLost += OnEditorFocusLost;
            _textViewExtension.EditorPointerDown += OnEditorPointerDown;
            ActiveHandlers.Add(this);
            // 延后 Attach：始终挂上悬停定时器（不再因 GetFocus 误判而永久跳过）
            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _textViewExtension?.Attach();
                        LogManager.Flush();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Deferred QuickInfo Attach failed");
                        LogManager.Flush();
                    }
                }), DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Schedule QuickInfo Attach failed");
                try { _textViewExtension.Attach(); } catch { }
            }
        }

        private CompletionListWindow EnsureCompletionWindow()
        {
            if (_window != null) return _window;
            _window = new CompletionListWindow();
            _window.CommitClicked += (s, e) => TryCommitSelected();
            return _window;
        }

        private bool IsCompletionPopupFocus(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _window == null || !_window.IsOpen) return false;
            try
            {
                var helper = new WindowInteropHelper(_window);
                IntPtr popupHwnd = helper.Handle;
                return popupHwnd != IntPtr.Zero && (hwnd == popupHwnd || IsChild(popupHwnd, hwnd));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsQuickInfoPopupFocus(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !QuickInfoTooltip.IsOpen) return false;
            try
            {
                IntPtr popupHwnd = QuickInfoTooltip.GetWindowHandle();
                return popupHwnd != IntPtr.Zero && (hwnd == popupHwnd || IsChild(popupHwnd, hwnd));
            }
            catch
            {
                return false;
            }
        }

        private bool IsPopupFocus(IntPtr hwnd)
        {
            return IsCompletionPopupFocus(hwnd) || IsQuickInfoPopupFocus(hwnd);
        }

        private bool EditorHasFocus()
        {
            try
            {
                IntPtr editorHwnd = _textView.GetWindowHandle();
                if (editorHwnd == IntPtr.Zero) return false;
                IntPtr focus = GetFocus();
                IntPtr foreground = GetForegroundWindow();
                return IsSameOrRelatedHwnd(editorHwnd, focus) || IsSameOrRelatedHwnd(editorHwnd, foreground);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSameOrRelatedHwnd(IntPtr editorHwnd, IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            return hwnd == editorHwnd || IsChild(editorHwnd, hwnd) || IsChild(hwnd, editorHwnd);
        }

        private static bool IsSsmsForeground()
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
                return false;
            }
        }

        private bool IsCaretAtEmptyCompletionContext()
        {
            try
            {
                if (_textView.GetCaretPos(out int line, out int col) != VSConstants.S_OK)
                    return true;
                if (_textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK)
                    return true;
                textLines.GetLengthOfLine(line, out int lineLen);
                if (lineLen == 0 || col == 0)
                    return true;
                textLines.GetLineText(line, 0, line, Math.Min(col, lineLen), out string beforeCaret);
                return string.IsNullOrWhiteSpace(beforeCaret);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 自动触发时：空行、或刚输入 ; / GO 后尚无标识符前缀 → 不弹框（Ctrl+Space 仍可手动）。
        /// </summary>
        private bool ShouldSuppressAutoPopup()
        {
            if (IsCaretAtEmptyCompletionContext()) return true;
            try
            {
                if (_textView.GetCaretPos(out int line, out int col) != VSConstants.S_OK)
                    return false;
                if (_textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK)
                    return false;
                textLines.GetLengthOfLine(line, out int lineLen);
                if (col <= 0) return true;
                textLines.GetLineText(line, 0, line, Math.Min(col, lineLen), out string before);
                if (string.IsNullOrEmpty(before)) return true;

                int i = before.Length - 1;
                while (i >= 0 && (before[i] == ' ' || before[i] == '\t')) i--;
                if (i < 0) return true;

                // 正在输入标识符前缀则允许弹
                if (IsAutoTriggerIdentChar(before[i]))
                    return false;

                if (before[i] == ';') return true;

                // GO 批分隔后
                if ((before[i] == 'O' || before[i] == 'o') && i >= 1)
                {
                    char g = before[i - 1];
                    if ((g == 'G' || g == 'g') &&
                        (i == 1 || !IsAutoTriggerIdentChar(before[i - 2])))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsAutoTriggerIdentChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '[' || c == ']';
        }

        private static bool IsAutoPopupTerminatorChar(char c)
        {
            return c == ';' || c == ')' || c == '\n' || c == '\r';
        }

        public bool IsSessionOpen => _sessionOpen;

        /// <summary>补全框右侧详情复制（Ctrl+C / 右键）。</summary>
        public bool TryCopyCompletionDetail()
        {
            return _window != null && _window.IsOpen && _window.TryCopyDetail();
        }

        private void OnEditorPointerDown()
        {
            // 编辑器收到按下 = 点了编辑器（不是弹框）→ 关闭悬停弹框
            if (QuickInfoTooltip.IsOpen)
                QuickInfoTooltip.CloseFromOutsideClick();

            if (!_sessionOpen) return;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                if (_sessionOpen) CloseSession();
            }), DispatcherPriority.Input);
        }

        private void OnEditorFocusLost(IntPtr newFocusHwnd)
        {
            if (IsPopupFocus(newFocusHwnd)) return;
            if (_sessionOpen)
            {
                if (!IsSsmsForeground())
                {
                    CloseSession();
                    return;
                }
                _externalFocusCloseTimer.Stop();
                _externalFocusCloseTimer.Start();
                return;
            }
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                if (_sessionOpen) return;
                if (EditorHasFocus() || IsPopupFocus(GetFocus())) return;
                if (QuickInfoTooltip.IsPinned)
                {
                    // 钉住后：焦点落到弹框外才关闭
                    if (!IsPopupFocus(GetFocus()))
                        QuickInfoTooltip.CloseFromOutsideClick();
                    return;
                }
                if (QuickInfoTooltip.ShouldKeepOpen) return;
                QuickInfoTooltip.Close();
            }), DispatcherPriority.Input);
        }

        /// <summary>弹框开时处理导航/提交/取消键。返回 true=吞键。</summary>
        public bool HandleSessionKey(Guid cmdGroup, uint nCmdID)
        {
            if (!_sessionOpen || cmdGroup != VSConstants.VSStd2K) return false;

            try
            {
                if (EditorSelectionHelper.HasTextSelection(_textView))
                {
                    switch ((VSStd2KCmdID)nCmdID)
                    {
                        case VSStd2KCmdID.TAB:
                        case VSStd2KCmdID.RETURN:
                            CloseSession();
                            return false;
                    }
                }

                switch ((VSStd2KCmdID)nCmdID)
                {
                    case VSStd2KCmdID.UP:
                        _window?.Move(-1);
                        return true;
                    case VSStd2KCmdID.DOWN:
                        _window?.Move(1);
                        return true;
                    case VSStd2KCmdID.PAGEUP:
                        _window?.MovePage(-1);
                        return true;
                    case VSStd2KCmdID.PAGEDN:
                        _window?.MovePage(1);
                        return true;
                    case VSStd2KCmdID.HOME:
                        _window?.SelectFirst();
                        return true;
                    case VSStd2KCmdID.END:
                        _window?.SelectLast();
                        return true;
                    case VSStd2KCmdID.COMPLETEWORD:
                    case VSStd2KCmdID.SHOWMEMBERLIST:
                        // SSMS 内建自动 COMPLETEWORD 不能当作用户确认
                        return true;
                    case VSStd2KCmdID.TAB:
                    case VSStd2KCmdID.RETURN:
                        TryCommitSelected();
                        return true;
                    case VSStd2KCmdID.CANCEL:
                        // SSMS 内建 IntelliSense 超时会自动发 CANCEL，仅用户按 Esc 时关闭
                        if (Keyboard.IsKeyDown(Key.Escape))
                        {
                            CloseSession();
                            if (QuickInfoTooltip.IsOpen)
                                QuickInfoTooltip.Close();
                        }
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "HandleSessionKey({0}) failed; closing session to avoid stuck popup", nCmdID);
                try { CloseSession(); } catch { }
                return true;
            }
        }

        /// <summary>按键后调度补全（弹框关：防抖新弹；弹框开：立即刷新候选）。</summary>
        public void MaybeScheduleAutoTrigger(uint nCmdID, char typedChar = '\0')
        {
            bool isTypeChar = nCmdID == (uint)VSStd2KCmdID.TYPECHAR;
            bool isBackspace = nCmdID == (uint)VSStd2KCmdID.BACKSPACE;
            bool isDelete = nCmdID == (uint)VSStd2KCmdID.DELETE;
            if (isTypeChar || isBackspace || isDelete)
                _textViewExtension?.NotifyTyping();

            var settings = UiSettingsStore.GetIntelliSenseSettings();
            if (!settings.enabled || !settings.autoTrigger) return;
            if (EditorSelectionHelper.HasTextSelection(_textView))
            {
                _debounceTimer.Stop();
                return;
            }

            // SSMS 内建未禁用（首次安装/settings.json 写失败）→ 抑制自动弹，仅 Ctrl+Space 手动可用，避免双弹框
            if (IntelliSenseManager.AutoTriggerSuppressed) return;
            if (DateTime.UtcNow < _suppressAutoTriggerUntil) return;

            if (!isTypeChar && !isBackspace && !isDelete) return;

            // 分号等语句结束符：关弹框且不自动再弹（等用户开始输入下一条语句前缀）
            if (isTypeChar && IsAutoPopupTerminatorChar(typedChar))
            {
                _debounceTimer.Stop();
                if (_sessionOpen) CloseSession();
                return;
            }

            if (_sessionOpen)
            {
                if (isBackspace || isDelete)
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ShouldSuppressAutoPopup())
                        {
                            CloseSession();
                            return;
                        }
                        TriggerCompletion(false, editingRefresh: true);
                    }), DispatcherPriority.Background);
                    return;
                }
                if (IsWithinAcceptGrace()) return;
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => TriggerCompletion(false, editingRefresh: true)),
                    DispatcherPriority.Background);
            }
            else
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }
        }

        /// <summary>强制/自动触发补全。force=true 立即（Ctrl+Space）。引擎计算在后台线程，避免 UI 卡死。</summary>
        public void TriggerCompletion(bool force, bool editingRefresh = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _logger.Info("TriggerCompletion force={0} refresh={1}", force, editingRefresh);

            if (!force && EditorSelectionHelper.HasTextSelection(_textView))
            {
                CloseSession();
                return;
            }

            var settings = UiSettingsStore.GetIntelliSenseSettings();
            if (!settings.enabled && !force)
            {
                CloseSession();
                return;
            }

            // 自动触发时：空行 / 分号后无前缀 → 不弹框（Ctrl+Space 仍可手动触发）
            if (!force && ShouldSuppressAutoPopup())
            {
                CloseSession();
                return;
            }
            if (!force && DateTime.UtcNow < _suppressAutoTriggerUntil)
            {
                CloseSession();
                return;
            }

            try
            {
                _logger.Info("TriggerCompletion: read text");
                string text = GetFullText();
                int caret = GetCaretOffset();
                if (caret < 0)
                {
                    CloseSession();
                    return;
                }

                _logger.Info("TriggerCompletion: connection");
                var connInfo = SafeGetCurrentConnection();
                // 自动触发：只用缓存，后台预热，避免新标签首次输入时 UI 同步连库卡死
                // EXEC 例外：必须同步拿到过程目录（含跨库），否则候选恒空
                MetadataCatalog catalog = null;
                if (connInfo != null)
                {
                    bool likelyExec = LooksLikeExecContext(text, caret);
                    catalog = MetadataCatalogService.Instance.GetCachedCatalog(connInfo);
                    if (catalog == null || (likelyExec && !catalog.RoutinesLoaded))
                    {
                        MetadataCatalogService.Instance.EnsureCatalogBuilding(connInfo);
                        if (force || likelyExec)
                            catalog = MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo, null, requireRoutines: likelyExec);
                    }
                }

                int gen = Interlocked.Increment(ref _completionGen);
                var dispatcher = System.Windows.Application.Current?.Dispatcher
                    ?? Dispatcher.CurrentDispatcher;
                _logger.Info("TriggerCompletion: queue engine caret={0} textLen={1} db={2} gen={3}",
                    caret, text?.Length ?? 0, connInfo?.Database, gen);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        // 独立实例，避免与并发刷新争用 _lastBatchText 缓存
                        var engine = new CompletionEngine();
                        var result = engine.GetCompletion(text, caret, catalog, settings, connInfo);
                        sw.Stop();
                        if (sw.ElapsedMilliseconds >= CompletionEngineWarnMs)
                            _logger.Warn("GetCompletion slow {0}ms gen={1} ctx={2}", sw.ElapsedMilliseconds, gen, result?.Context);
                        else
                            _logger.Info("GetCompletion {0}ms gen={1} ctx={2} items={3}",
                                sw.ElapsedMilliseconds, gen, result?.Context, result?.Items?.Count ?? 0);
                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (gen != _completionGen) return;
                            ApplyCompletionResult(result, force, editingRefresh);
                        }), DispatcherPriority.Background);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "GetCompletion background failed gen={0}", gen);
                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (gen != _completionGen) return;
                            CloseSession();
                        }), DispatcherPriority.Background);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "TriggerCompletion failed");
                FlushLogsAsync();
                CloseSession();
            }
        }

        private void ApplyCompletionResult(CompletionResult result, bool force, bool editingRefresh)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (result == null)
            {
                CloseSession();
                return;
            }

            _replaceStartOffset = result.ReplaceStartOffset;
            _replaceEndOffset = result.ReplaceEndOffset;

            if (result.IsEmpty)
            {
                _logger.Info("TriggerCompletion: empty context={0} prefix={1}", result.Context, result.Prefix);
                // 候选为空时必须关掉旧弹框，否则会残留上一语句的列提示
                if (force || editingRefresh || _sessionOpen)
                    CloseSession();
                return;
            }

            if (result.Context == CompletionContext.AfterExec)
                _logger.Info("TriggerCompletion: AfterExec items={0} prefix={1}", result.Items.Count, result.Prefix);

            // 补全弹框打开时关闭悬停注释框，避免叠在一起
            if (QuickInfoTooltip.IsOpen)
                QuickInfoTooltip.Close();

            _logger.Info("TriggerCompletion: show items={0}", result.Items.Count);
            var pt = GetCaretScreenPoint();
            if (!pt.HasValue)
            {
                if (force || editingRefresh)
                    CloseSession();
                return;
            }

            IntPtr hwnd = _textView.GetWindowHandle();
            var window = EnsureCompletionWindow();
            if (_sessionOpen)
            {
                window.UpdateItems(result.Items);
                window.RepositionAt(pt.Value.x, pt.Value.y, hwnd);
                if (editingRefresh)
                {
                    _sessionOpenedAt = DateTime.UtcNow;
                }
                UpdateSessionCaretAnchor();
                EnsureSessionWatchdogRunning();
            }
            else
            {
                _sessionOpen = true;
                _sessionOpenedAt = DateTime.UtcNow;
                window.ShowAt(pt.Value.x, pt.Value.y, result.Items, hwnd);
                UpdateSessionCaretAnchor();
                EnsureSessionWatchdogRunning();
            }
            _logger.Info("TriggerCompletion: done");
        }

        /// <summary>后台刷盘，不阻塞 UI。</summary>
        private static void FlushLogsAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { LogManager.Flush(); }
                catch { }
            });
        }

        /// <summary>光标前是否处于 EXEC/EXECUTE 目标名（用于自动触发时同步拉过程目录）。</summary>
        private static bool LooksLikeExecContext(string text, int caretOffset)
        {
            if (string.IsNullOrEmpty(text) || caretOffset <= 0) return false;
            int end = Math.Min(caretOffset, text.Length);
            int lineStart = end - 1;
            while (lineStart >= 0 && text[lineStart] != '\n' && text[lineStart] != '\r' && text[lineStart] != ';')
                lineStart--;
            lineStart++;
            string before = text.Substring(lineStart, end - lineStart);
            int idx = before.LastIndexOf("EXECUTE", StringComparison.OrdinalIgnoreCase);
            int execLen = 7;
            if (idx < 0)
            {
                idx = before.LastIndexOf("EXEC", StringComparison.OrdinalIgnoreCase);
                execLen = 4;
            }
            if (idx < 0) return false;
            // 须为独立关键字（前非标识符），且后面至少有空白（已进入目标名区域）或行尾空格
            if (idx > 0 && (char.IsLetterOrDigit(before[idx - 1]) || before[idx - 1] == '_'))
                return false;
            int after = idx + execLen;
            if (after < before.Length && (char.IsLetterOrDigit(before[after]) || before[after] == '_'))
                return false; // 仍在敲 EXEC 关键字本身
            return after < before.Length; // EXEC 后已有内容（空格/目标名）
        }

        private void TryCommitSelected()
        {
            if (IsWithinAcceptGrace()) return;
            CommitSelected();
        }

        public void CommitSelected()
        {
            var item = _window?.GetSelected();
            CloseSession();
            if (item == null) return;
            try
            {
                RefreshReplaceEndFromCaret();
                if (TryCommitExactSnippet())
                {
                    _textViewExtension?.SuppressHoverAfterCommit();
                    return;
                }

                string insertText = item.InsertText;
                if (item.Kind == CompletionKind.Keyword &&
                    !string.IsNullOrEmpty(insertText) &&
                    !insertText.EndsWith(" ", StringComparison.Ordinal))
                {
                    insertText = insertText + " ";
                }

                ReplaceRangeWith(insertText, _replaceStartOffset, _replaceEndOffset);

                if (item.SnippetCursorOffset >= 0)
                {
                    OffsetToLineCol(_replaceStartOffset, out int line, out int col);
                    SnippetExpansionHelper.SetCaretPosition(_textView, line, col, insertText, item.SnippetCursorOffset);
                    // 光标落入 ISNULL(| , ) 等实参位时，短暂抑制自动弹，避免立刻再出 SELECT 列表
                    _suppressAutoTriggerUntil = DateTime.UtcNow.AddMilliseconds(SuppressAutoTriggerAfterCommitMs);
                }
            }
            catch
            {
                // 插入失败不阻塞编辑
            }
            finally
            {
                // 选中补全后不要立刻弹出悬停框
                _textViewExtension?.SuppressHoverAfterCommit();
            }
        }

        private void RefreshReplaceEndFromCaret()
        {
            int caret = GetCaretOffset();
            if (caret >= _replaceStartOffset)
            {
                _replaceEndOffset = caret;
            }
        }

        private bool TryCommitExactSnippet()
        {
            if (!SettingsManager.GetUseSnippets()) return false;

            string typed = GetTextAtReplaceRange();
            if (string.IsNullOrEmpty(typed)) return false;

            var dict = SnippetService.SnippetDictionary;
            if (!dict.TryGetValue(typed, out SnippetItem snippet))
            {
                return false;
            }

            OffsetToLineCol(_replaceStartOffset, out int line, out int wordStart);
            OffsetToLineCol(_replaceEndOffset, out int endLine, out int wordEnd);
            if (endLine != line) return false;

            return SnippetExpansionHelper.TryExpandInView(_textView, line, wordStart, wordEnd, snippet);
        }

        private string GetTextAtReplaceRange()
        {
            if (_replaceStartOffset < 0 || _replaceEndOffset < _replaceStartOffset) return string.Empty;
            if (_textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK) return string.Empty;

            OffsetToLineCol(_replaceStartOffset, out int startLine, out int startCol);
            OffsetToLineCol(_replaceEndOffset, out int endLine, out int endCol);
            if (startLine != endLine) return string.Empty;

            textLines.GetLineText(startLine, startCol, endLine, endCol, out string text);
            return text ?? string.Empty;
        }

        public void CloseSession()
        {
            Interlocked.Increment(ref _completionGen);
            _sessionOpen = false;
            _sessionCaretLine = -1;
            _debounceTimer?.Stop();
            _externalFocusCloseTimer?.Stop();
            _sessionWatchdogTimer?.Stop();
            _window?.HidePopup();
        }

        private void UpdateSessionCaretAnchor()
        {
            if (_textView.GetCaretPos(out int line, out _) == VSConstants.S_OK)
                _sessionCaretLine = line;
        }

        private void EnsureSessionWatchdogRunning()
        {
            if (_sessionWatchdogTimer != null && !_sessionWatchdogTimer.IsEnabled)
                _sessionWatchdogTimer.Start();
        }

        #region 文本与光标

        private string GetFullText()
        {
            if (_textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK)
                return string.Empty;

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

        private int GetCaretOffset()
        {
            if (_textView.GetCaretPos(out int line, out int col) != VSConstants.S_OK)
                return -1;
            if (_textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK)
                return -1;

            int offset = 0;
            for (int i = 0; i < line; i++)
            {
                textLines.GetLengthOfLine(i, out int lineLen);
                offset += lineLen + 2; // \r\n
            }
            return offset + col;
        }

        private POINT? GetCaretScreenPoint()
        {
            try
            {
                if (_textView.GetCaretPos(out int line, out int col) != VSConstants.S_OK)
                    return null;
                POINT[] pts = new POINT[1];
                if (_textView.GetPointOfLineColumn(line, col, pts) != VSConstants.S_OK)
                    return null;
                try
                {
                    _textView.GetLineHeight(out int lineHeight);
                    pts[0].y += lineHeight;
                }
                catch
                {
                    pts[0].y += 18;
                }
                IntPtr hwnd = _textView.GetWindowHandle();
                if (hwnd == IntPtr.Zero || !ClientToScreen(hwnd, ref pts[0]))
                    return null;
                return pts[0];
            }
            catch
            {
            }
            return null;
        }

        private void ReplaceRangeWith(string newText, int startOffset, int endOffset)
        {
            if (string.IsNullOrEmpty(newText)) return;
            if (_textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK) return;

            if (startOffset < 0 || endOffset < startOffset)
            {
                ReplacePrefixWith(newText);
                return;
            }

            OffsetToLineCol(startOffset, out int startLine, out int startCol);
            OffsetToLineCol(endOffset, out int endLine, out int endCol);

            IntPtr pNew = Marshal.StringToHGlobalUni(newText);
            try
            {
                TextSpan[] span = new TextSpan[1];
                textLines.ReplaceLines(startLine, startCol, endLine, endCol, pNew, newText.Length, span);
                OffsetToLineCol(startOffset + newText.Length, out int caretLine, out int caretCol);
                _textView.SetCaretPos(caretLine, caretCol);
            }
            finally
            {
                Marshal.FreeHGlobal(pNew);
            }
        }

        private void OffsetToLineCol(int offset, out int line, out int col)
        {
            line = 0;
            col = 0;
            if (offset < 0) return;
            if (_textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK) return;

            textLines.GetLastLineIndex(out int lastLine, out _);
            int pos = 0;
            for (int i = 0; i <= lastLine; i++)
            {
                textLines.GetLengthOfLine(i, out int lineLen);
                if (offset <= pos + lineLen)
                {
                    line = i;
                    col = offset - pos;
                    return;
                }
                pos += lineLen + 2;
            }
            line = lastLine;
            textLines.GetLengthOfLine(lastLine, out int lastLen);
            col = lastLen;
        }

        private void ReplacePrefixWith(string newText)
        {
            if (string.IsNullOrEmpty(newText)) return;
            if (_textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK) return;

            _textView.GetCaretPos(out int line, out int col);
            textLines.GetLengthOfLine(line, out int lineLen);
            textLines.GetLineText(line, 0, line, lineLen, out string lineText);

            int wordStart = col;
            for (int i = col - 1; i >= 0; i--)
            {
                char c = lineText[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '@' || c == '#' || c == '[' || c == ']')
                {
                    wordStart = i;
                }
                else
                {
                    break;
                }
            }

            if (wordStart >= col) return;

            IntPtr pNew = Marshal.StringToHGlobalUni(newText);
            try
            {
                TextSpan[] span = new TextSpan[1];
                textLines.ReplaceLines(line, wordStart, line, col, pNew, newText.Length, span);
                _textView.SetCaretPos(line, wordStart + newText.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(pNew);
            }
        }

        private ScriptFactoryAccess.ConnectionInfo SafeGetCurrentConnection()
        {
            try
            {
                // 禁止传入 textView：TryGetConnectionInfoForTextView 会反射遍历 DocView/WinForms.Handle，
                // 新标签首次按键时极易导致 SSMS 原生崩溃（无托管异常日志）。
                return ScriptFactoryAccess.GetCurrentConnectionInfo();
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
