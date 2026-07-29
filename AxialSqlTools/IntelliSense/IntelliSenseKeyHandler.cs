using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
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

        private readonly AxialSqlToolsPackage _package;
        private readonly IVsTextView _textView;
        private readonly CompletionEngine _engine = new CompletionEngine();
        private readonly CompletionListWindow _window;
        private readonly DispatcherTimer _debounceTimer;
        private readonly DispatcherTimer _externalFocusCloseTimer;
        private readonly DispatcherTimer _sessionWatchdogTimer;
        private readonly IntelliSenseTextViewExtension _textViewExtension;

        private bool _sessionOpen;
        private DateTime _sessionOpenedAt;
        private int _sessionCaretLine = -1;
        private int _replaceStartOffset = -1;
        private int _replaceEndOffset = -1;
        private const int SessionAcceptDelayMs = 400;

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
            _window = new CompletionListWindow();
            _window.CommitClicked += (s, e) => TryCommitSelected();

            var settings = UiSettingsStore.GetIntelliSenseSettings();
            int delay = settings.autoTriggerDelayMs > 0 ? settings.autoTriggerDelayMs : 200;
            _debounceTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(delay)
            };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                TriggerCompletion(false);
            };

            _externalFocusCloseTimer = new DispatcherTimer(DispatcherPriority.Background)
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
                if (EditorHasFocus() || IsCompletionPopupFocus(GetFocus())) return;
                if (IsWithinAcceptGrace()) return;
                CloseSession();
            };

            _sessionWatchdogTimer = new DispatcherTimer(DispatcherPriority.Background)
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
            _textViewExtension.ShouldIgnoreFocusTarget = IsCompletionPopupFocus;
            _textViewExtension.EditorFocusLost += OnEditorFocusLost;
            _textViewExtension.EditorPointerDown += OnEditorPointerDown;
            _textViewExtension.Attach();
            ActiveHandlers.Add(this);
        }

        private bool IsCompletionPopupFocus(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !_window.IsOpen) return false;
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

        public bool IsSessionOpen => _sessionOpen;

        private void OnEditorPointerDown()
        {
            if (!_sessionOpen || IsWithinAcceptGrace()) return;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                if (_sessionOpen) CloseSession();
            }), DispatcherPriority.Input);
        }

        private void OnEditorFocusLost(IntPtr newFocusHwnd)
        {
            if (IsCompletionPopupFocus(newFocusHwnd)) return;
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
                if (EditorHasFocus() || IsCompletionPopupFocus(GetFocus())) return;
                QuickInfoTooltip.Close();
            }), DispatcherPriority.Input);
        }

        /// <summary>弹框开时处理导航/提交/取消键。返回 true=吞键。</summary>
        public bool HandleSessionKey(Guid cmdGroup, uint nCmdID)
        {
            if (!_sessionOpen || cmdGroup != VSConstants.VSStd2K) return false;

            try
            {
                switch ((VSStd2KCmdID)nCmdID)
                {
                    case VSStd2KCmdID.UP:
                        _window.Move(-1);
                        return true;
                    case VSStd2KCmdID.DOWN:
                        _window.Move(1);
                        return true;
                    case VSStd2KCmdID.PAGEUP:
                        _window.MovePage(-1);
                        return true;
                    case VSStd2KCmdID.PAGEDN:
                        _window.MovePage(1);
                        return true;
                    case VSStd2KCmdID.HOME:
                        _window.SelectFirst();
                        return true;
                    case VSStd2KCmdID.END:
                        _window.SelectLast();
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
                            CloseSession();
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
        public void MaybeScheduleAutoTrigger(uint nCmdID)
        {
            var settings = UiSettingsStore.GetIntelliSenseSettings();
            if (!settings.enabled || !settings.autoTrigger) return;

            // SSMS 内建未禁用（首次安装/注册表写失败）→ 抑制自动弹，仅 Ctrl+Space 手动可用，避免双弹框
            if (IntelliSenseManager.AutoTriggerSuppressed) return;

            bool isTypeChar = nCmdID == (uint)VSStd2KCmdID.TYPECHAR;
            bool isBackspace = nCmdID == (uint)VSStd2KCmdID.BACKSPACE;
            bool isDelete = nCmdID == (uint)VSStd2KCmdID.DELETE;
            if (!isTypeChar && !isBackspace && !isDelete) return;

            if (_sessionOpen)
            {
                if (isBackspace || isDelete)
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                    {
                        if (IsCaretAtEmptyCompletionContext())
                        {
                            CloseSession();
                            return;
                        }
                        TriggerCompletion(false, editingRefresh: true);
                    }), DispatcherPriority.Background);
                    return;
                }
                if (IsWithinAcceptGrace()) return;
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => TriggerCompletion(false)),
                    DispatcherPriority.Background);
            }
            else
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }
        }

        /// <summary>强制/自动触发补全。force=true 立即（Ctrl+Space）。</summary>
        public void TriggerCompletion(bool force, bool editingRefresh = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var settings = UiSettingsStore.GetIntelliSenseSettings();
            if (!settings.enabled && !force)
            {
                CloseSession();
                return;
            }

            try
            {
                string text = GetFullText();
                int caret = GetCaretOffset();
                if (caret < 0)
                {
                    CloseSession();
                    return;
                }

                var connInfo = SafeGetCurrentConnection();
                var catalog = connInfo != null
                    ? MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo)
                    : null;

                var result = _engine.GetCompletion(text, caret, catalog, settings, connInfo);
                _replaceStartOffset = result.ReplaceStartOffset;
                _replaceEndOffset = result.ReplaceEndOffset;

                if (editingRefresh && string.IsNullOrEmpty(result.Prefix) && IsCaretAtEmptyCompletionContext())
                {
                    CloseSession();
                    return;
                }

                if (result.IsEmpty)
                {
                    if (force || editingRefresh)
                        CloseSession();
                    return;
                }

                var pt = GetCaretScreenPoint();
                if (!pt.HasValue)
                {
                    if (force || editingRefresh)
                        CloseSession();
                    return;
                }

                IntPtr hwnd = _textView.GetWindowHandle();
                if (_sessionOpen)
                {
                    _window.UpdateItems(result.Items);
                    _window.RepositionAt(pt.Value.x, pt.Value.y, hwnd);
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
                    _window.ShowAt(pt.Value.x, pt.Value.y, result.Items, hwnd);
                    UpdateSessionCaretAnchor();
                    EnsureSessionWatchdogRunning();
                }
            }
            catch
            {
                CloseSession();
            }
        }

        private void TryCommitSelected()
        {
            if (IsWithinAcceptGrace()) return;
            CommitSelected();
        }

        public void CommitSelected()
        {
            var item = _window.GetSelected();
            CloseSession();
            if (item == null) return;
            try
            {
                RefreshReplaceEndFromCaret();
                if (TryCommitExactSnippet())
                {
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

                if (item.Kind == CompletionKind.Snippet && item.SnippetCursorOffset >= 0)
                {
                    OffsetToLineCol(_replaceStartOffset, out int line, out int col);
                    SnippetExpansionHelper.SetCaretPosition(_textView, line, col, insertText, item.SnippetCursorOffset);
                }
            }
            catch
            {
                // 插入失败不阻塞编辑
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
            _sessionOpen = false;
            _sessionCaretLine = -1;
            _debounceTimer?.Stop();
            _externalFocusCloseTimer?.Stop();
            _sessionWatchdogTimer?.Stop();
            _window.HidePopup();
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
