using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using AxialSqlTools.IntelliSense;
using NLog;

namespace AxialSqlTools
{
    public class KeypressCommandFilter : IOleCommandTarget
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();
        private IOleCommandTarget nextCommandTarget;
        private readonly IVsTextView textView;
        private readonly AxialSqlToolsPackage _package;
        private IntelliSenseKeyHandler _intelliSense;
        private bool _intelliSenseInitTried;
        private bool _intelliSenseInitPending;
        private bool _pendingPasteWarmup;
        private bool _pendingAutoTriggerCatchup;
        private DispatcherTimer _bufferWatch;
        private int _lastSeenTextLen;
        private int _lastWarmupTextLen;
        private int _warmupRetryLeft;
        private uint _textEventsCookie;
        private IConnectionPoint _textEventsCp;
        private BufferTextEvents _bufferEvents;
        private bool _bufferScanQueued;

        public KeypressCommandFilter(AxialSqlToolsPackage package, IVsTextView textView)
        {
            _package = package;
            this.textView = textView;
            // 不在构造时创建 KeyHandler：Ctrl+N 新建标签瞬间创建 WPF/Timer 易导致 SSMS 退出
        }

        public void AddToChain()
        {
            if (textView != null && textView.AddCommandFilter(this, out nextCommandTarget) != VSConstants.S_OK)
            {
                throw new Exception("Failed to add command filter");
            }
            // 粘贴常发生在 Filter 注册之前：挂载时文本已在，必须主动扫描，不能只靠「增长量」
            TryAdviseBufferEvents();
            EnsureBufferWatch();
            ScanExistingDocumentForWarmup("attach");
            ScheduleDeferredDocumentScans();
            ScheduleEnsureCacheOnOpen();
        }

        private int _cacheOpenRetry;

        /// <summary>打开标签后立刻把磁盘缓存装进内存（不等输入）。连接未就绪则短重试。</summary>
        private void ScheduleEnsureCacheOnOpen()
        {
            _cacheOpenRetry = 8;
            TryEnsureCacheOnOpen();
        }

        private void TryEnsureCacheOnOpen()
        {
            try
            {
                if (!UiSettingsStore.GetIntelliSenseEnabled())
                    return;
                ScriptFactoryAccess.ConnectionInfo conn = null;
                try { conn = ScriptFactoryAccess.GetCurrentConnectionInfo(); }
                catch { }
                if (conn == null || string.IsNullOrWhiteSpace(conn.ServerName))
                {
                    if (_cacheOpenRetry-- <= 0)
                        return;
                    var dispatcher = System.Windows.Application.Current?.Dispatcher
                        ?? Dispatcher.CurrentDispatcher;
                    var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(400)
                    };
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        TryEnsureCacheOnOpen();
                    };
                    timer.Start();
                    return;
                }
                MetadataCacheRefreshService.Instance.EnsureServerCache(conn);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "TryEnsureCacheOnOpen failed");
            }
        }

        /// <summary>
        /// 禁止在 IOleCommandTarget.Exec 同步路径上 new KeyHandler（第二个标签首次按键会卡死退出）。
        /// 一律排到 ApplicationIdle 再建。
        /// </summary>
        private void ScheduleEnsureIntelliSense(bool warmupAfterCreate = false, bool catchupAutoTrigger = false)
        {
            if (_intelliSense != null)
            {
                if (warmupAfterCreate)
                    SchedulePasteCatalogWarmup();
                return;
            }
            if (_intelliSenseInitTried || _intelliSenseInitPending)
            {
                if (warmupAfterCreate)
                    _pendingPasteWarmup = true;
                if (catchupAutoTrigger)
                    _pendingAutoTriggerCatchup = true;
                return;
            }
            if (!UiSettingsStore.GetIntelliSenseEnabled())
            {
                _intelliSenseInitTried = true;
                return;
            }
            _intelliSenseInitPending = true;
            if (warmupAfterCreate)
                _pendingPasteWarmup = true;
            if (catchupAutoTrigger)
                _pendingAutoTriggerCatchup = true;
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher
                    ?? Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_intelliSense != null)
                        {
                            if (_pendingPasteWarmup)
                            {
                                _pendingPasteWarmup = false;
                                SchedulePasteCatalogWarmup();
                            }
                            return;
                        }
                        if (_intelliSenseInitTried) return;
                        _intelliSenseInitTried = true;
                        _logger.Info("IntelliSense KeyHandler creating (deferred, off Exec)");
                        _intelliSense = new IntelliSenseKeyHandler(_package, textView);
                        _logger.Info("IntelliSense KeyHandler created ok");
                        // 仅真实按键补一次自动触发；粘贴只做目录预热，勿伪装 TYPECHAR 弹框
                        if (_pendingAutoTriggerCatchup)
                        {
                            _pendingAutoTriggerCatchup = false;
                            _intelliSense.MaybeScheduleAutoTrigger((uint)VSConstants.VSStd2KCmdID.TYPECHAR);
                        }
                        // 缓冲区已有文本时一律预热（粘贴命令常识别不到）
                        _pendingPasteWarmup = false;
                        SchedulePasteCatalogWarmup();
                        AxialSqlToolsPackage.FlushLogsAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "IntelliSense KeyHandler create failed");
                        AxialSqlToolsPackage.FlushLogsAsync();
                        _intelliSense = null;
                        _intelliSenseInitTried = false;
                    }
                    finally
                    {
                        _intelliSenseInitPending = false;
                    }
                }), DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "ScheduleEnsureIntelliSense failed");
                _intelliSenseInitPending = false;
                _intelliSenseInitTried = true;
            }
        }

        /// <summary>粘贴/大段文本后预热跨库缓存；可无 KeyHandler，并短重试等连接/缓冲区就绪。</summary>
        private void SchedulePasteCatalogWarmup()
        {
            _pendingPasteWarmup = true;
            _warmupRetryLeft = Math.Max(_warmupRetryLeft, 6);
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher
                    ?? Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(new Action(AttemptCatalogWarmup), DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "SchedulePasteCatalogWarmup failed");
            }
        }

        private void AttemptCatalogWarmup()
        {
            try
            {
                if (TryWarmupCatalogsNow())
                {
                    _pendingPasteWarmup = false;
                    _warmupRetryLeft = 0;
                    return;
                }
                if (_warmupRetryLeft <= 0) return;
                _warmupRetryLeft--;
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(350)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    AttemptCatalogWarmup();
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "AttemptCatalogWarmup failed");
            }
        }

        private bool TryWarmupCatalogsNow()
        {
            if (_intelliSense != null)
            {
                bool ok = _intelliSense.TryScheduleCatalogWarmupFromDocument();
                if (ok)
                {
                    try { _lastWarmupTextLen = Math.Max(_lastWarmupTextLen, GetTextLengthSafe()); }
                    catch { }
                }
                return ok;
            }

            if (!UiSettingsStore.GetIntelliSenseEnabled()) return true;
            string text = GetFullTextSafe();
            ScriptFactoryAccess.ConnectionInfo connInfo = null;
            try { connInfo = ScriptFactoryAccess.GetCurrentConnectionInfo(); }
            catch { }
            if (connInfo == null || string.IsNullOrWhiteSpace(text))
            {
                _logger.Info("Catalog warmup pending (no handler): textLen={0} hasConn={1}",
                    text?.Length ?? 0, connInfo != null);
                return false;
            }
            _logger.Info("Catalog warmup from filter len={0} db={1}", text.Length, connInfo.Database);
            var snap = connInfo;
            var sql = MetadataCatalogService.ClipSqlForCatalogScan(text);
            _lastWarmupTextLen = text.Length;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    MetadataCatalogService.Instance.BuildCatalogsReferencedInSql(snap, sql);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Catalog warmup from filter failed");
                }
            });
            return true;
        }

        private void EnsureBufferWatch()
        {
            if (_bufferWatch != null) return;
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher
                    ?? Dispatcher.CurrentDispatcher;
                _lastSeenTextLen = GetTextLengthSafe();
                _bufferWatch = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _bufferWatch.Tick += (s, e) =>
                {
                    try
                    {
                        ScanExistingDocumentForWarmup("poll");
                    }
                    catch
                    {
                    }
                };
                _bufferWatch.Start();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "EnsureBufferWatch failed");
            }
        }

        private const int CatalogWarmupMinGrowth = 256;

        /// <summary>
        /// 文档已有足够 SQL 且尚未预热过该长度 → 创建 KeyHandler + 预热。
        /// 覆盖「先粘贴后挂 Filter」：此时没有增长量可观测。
        /// 逐字输入不重扫目录，避免每次按键拷贝并正则 1MB+ 原文。
        /// </summary>
        private void ScanExistingDocumentForWarmup(string reason)
        {
            if (!UiSettingsStore.GetIntelliSenseEnabled()) return;
            int len = GetTextLengthSafe();
            _lastSeenTextLen = len;
            if (len + CatalogWarmupMinGrowth < _lastWarmupTextLen)
                _lastWarmupTextLen = 0;
            if (len < 60 || len <= _lastWarmupTextLen) return;
            int growth = len - _lastWarmupTextLen;
            if (_lastWarmupTextLen > 0 && growth < CatalogWarmupMinGrowth)
            {
                _lastWarmupTextLen = len;
                return;
            }
            // 已在创建/重试预热中则勿重复打点
            if (_pendingPasteWarmup || _warmupRetryLeft > 0 || _intelliSenseInitPending)
                return;
            _logger.Info("Document scan warmup reason={0} len={1} lastWarmup={2}",
                reason, len, _lastWarmupTextLen);
            ScheduleEnsureIntelliSense(warmupAfterCreate: true);
            SchedulePasteCatalogWarmup();
        }

        private void ScheduleDeferredDocumentScans()
        {
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher
                    ?? Dispatcher.CurrentDispatcher;
                // 粘贴可能稍晚于 WindowActivated 注册；多探几次
                int[] delaysMs = { 200, 500, 1000, 2000 };
                foreach (int delay in delaysMs)
                {
                    int d = delay;
                    var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(d)
                    };
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        ScanExistingDocumentForWarmup("deferred_" + d);
                    };
                    timer.Start();
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "ScheduleDeferredDocumentScans failed");
            }
        }

        private void TryAdviseBufferEvents()
        {
            if (_textEventsCookie != 0 || textView == null) return;
            try
            {
                if (textView.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK || lines == null)
                    return;
                var cpc = lines as IConnectionPointContainer;
                if (cpc == null) return;
                Guid iid = typeof(IVsTextLinesEvents).GUID;
                cpc.FindConnectionPoint(ref iid, out _textEventsCp);
                if (_textEventsCp == null) return;
                _bufferEvents = new BufferTextEvents(this);
                _textEventsCp.Advise(_bufferEvents, out _textEventsCookie);
                _logger.Info("Buffer text events advised cookie={0}", _textEventsCookie);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "TryAdviseBufferEvents failed");
            }
        }

        internal void OnBufferTextChanged()
        {
            if (_bufferScanQueued) return;
            _bufferScanQueued = true;
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher
                    ?? Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    _bufferScanQueued = false;
                    ScanExistingDocumentForWarmup("text_event");
                }), DispatcherPriority.ApplicationIdle);
            }
            catch
            {
                _bufferScanQueued = false;
            }
        }

        [ComVisible(true)]
        private sealed class BufferTextEvents : IVsTextLinesEvents
        {
            private readonly KeypressCommandFilter _owner;
            public BufferTextEvents(KeypressCommandFilter owner) { _owner = owner; }
            public void OnChangeLineText(TextLineChange[] pTextLineChange, int fLast)
            {
                _owner?.OnBufferTextChanged();
            }
            public void OnChangeLineAttributes(int iFirstLine, int iLastLine) { }
        }

        private int GetTextLengthSafe()
        {
            try
            {
                if (textView == null || textView.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK || lines == null)
                    return 0;
                if (lines is IVsTextBuffer buf && buf.GetSize(out int size) == VSConstants.S_OK)
                    return Math.Max(0, size);
                lines.GetLastLineIndex(out int lastLine, out int lastCol);
                return Math.Max(0, lastCol);
            }
            catch
            {
                return 0;
            }
        }

        private string GetFullTextSafe()
        {
            try
            {
                return EditorSelectionHelper.GetFullText(textView) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public int Exec(ref Guid cmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            // 首次有实际输入/补全相关命令时再初始化，避开新建标签的创建窗口窗口期
            bool isPaste = IsPasteCommand(cmdGroup, nCmdID);
            bool mayNeedIntelliSense =
                isPaste ||
                (cmdGroup == VSConstants.VSStd2K &&
                (nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.BACKSPACE
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.DELETE
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.CANCEL));
            if (mayNeedIntelliSense)
            {
                bool isTyping = cmdGroup == VSConstants.VSStd2K
                    && (nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR
                        || nCmdID == (uint)VSConstants.VSStd2KCmdID.BACKSPACE
                        || nCmdID == (uint)VSConstants.VSStd2KCmdID.DELETE);
                ScheduleEnsureIntelliSense(warmupAfterCreate: isPaste, catchupAutoTrigger: isTyping);
            }

            if (IntelliSenseManager.IsExecuteCommand(cmdGroup, nCmdID))
            {
                IntelliSenseManager.CloseAllPopups();
            }
            else if (_intelliSense != null && EditorSelectionHelper.HasTextSelection(textView))
            {
                if (_intelliSense.IsSessionOpen)
                    _intelliSense.CloseSession();
            }

            if (_intelliSense != null && _intelliSense.IsSessionOpen && IsCopyCommand(cmdGroup, nCmdID))
            {
                if (_intelliSense.TryCopyCompletionDetail())
                    return VSConstants.S_OK;
            }

            if (IsCopyCommand(cmdGroup, nCmdID)
                && QuickInfoTooltip.TryHandleCopyCommand(EditorSelectionHelper.HasTextSelection(textView)))
                return VSConstants.S_OK;

            // 复制的是编辑器选区：关掉未钉住的悬停框，避免挡着选区
            if (IsCopyCommand(cmdGroup, nCmdID)
                && EditorSelectionHelper.HasTextSelection(textView)
                && !QuickInfoTooltip.IsPinned
                && !QuickInfoTooltip.IsPointerOverPopup())
            {
                IntelliSenseTextViewExtension.DismissQuickInfoByUser();
            }

            if (_intelliSense != null && _intelliSense.HandleSessionKey(cmdGroup, nCmdID))
            {
                return VSConstants.S_OK;
            }

            if (cmdGroup == VSConstants.VSStd2K && nCmdID == (uint)VSConstants.VSStd2KCmdID.CANCEL
                && QuickInfoTooltip.IsOpen && Keyboard.IsKeyDown(Key.Escape))
            {
                IntelliSenseTextViewExtension.DismissQuickInfoByUser();
                return VSConstants.S_OK;
            }

            if (_intelliSense != null && cmdGroup == VSConstants.VSStd2K &&
                (nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD || nCmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST))
            {
                if (_intelliSense.IsSessionOpen)
                {
                    return VSConstants.S_OK;
                }
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    _intelliSense.TriggerCompletion(true);
                    return VSConstants.S_OK;
                }
                if (EditorSelectionHelper.HasTextSelection(textView))
                {
                    return nextCommandTarget?.Exec(ref cmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? VSConstants.S_OK;
                }
                return VSConstants.S_OK;
            }

            if (cmdGroup == VSConstants.VSStd2K && IsSupportedKey(nCmdID))
            {
                if (ShouldProcessSnippetKey(nCmdID) && TryReplaceSnippet())
                {
                    return VSConstants.S_OK;
                }

                if (ShouldProcessAsteriskExpansionKey(nCmdID) && AsteriskExpansionService.TryExpand(textView))
                {
                    return VSConstants.S_OK;
                }
            }

            if (_intelliSense != null)
            {
                char typed = '\0';
                if (cmdGroup == VSConstants.VSStd2K
                    && nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR
                    && pvaIn != IntPtr.Zero)
                {
                    try
                    {
                        object raw = Marshal.GetObjectForNativeVariant(pvaIn);
                        if (raw != null)
                            typed = Convert.ToChar(raw);
                    }
                    catch
                    {
                    }
                }
                _intelliSense.MaybeScheduleAutoTrigger(nCmdID, typed);
            }

            int lenBefore = GetTextLengthSafe();
            int hr = nextCommandTarget?.Exec(ref cmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? VSConstants.S_OK;
            int lenAfter = GetTextLengthSafe();
            _lastSeenTextLen = lenAfter;
            // 粘贴完成后预热：须在 Exec 之后，缓冲区才有新文本
            if (isPaste || (lenAfter - lenBefore >= 60))
            {
                if (!isPaste)
                    _logger.Info("Treat as paste-like growth cmd={0}/{1} delta={2}",
                        cmdGroup, nCmdID, lenAfter - lenBefore);
                ScheduleEnsureIntelliSense(warmupAfterCreate: true);
                SchedulePasteCatalogWarmup();
            }
            return hr;
        }

        private bool IsSupportedKey(uint nCmdID)
        {
            return nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN
                || nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB
                || nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD
                || nCmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST;
        }

        private static bool IsCopyCommand(Guid cmdGroup, uint nCmdID)
        {
            if (nCmdID != (uint)VSConstants.VSStd97CmdID.Copy)
                return false;
            return cmdGroup == typeof(VSConstants.VSStd97CmdID).GUID
                || cmdGroup == VSConstants.GUID_VSStandardCommandSet97;
        }

        private static bool IsPasteCommand(Guid cmdGroup, uint nCmdID)
        {
            if (cmdGroup == typeof(VSConstants.VSStd97CmdID).GUID
                && nCmdID == (uint)VSConstants.VSStd97CmdID.Paste)
                return true;
            if (cmdGroup == VSConstants.GUID_VSStandardCommandSet97
                && nCmdID == (uint)VSConstants.VSStd97CmdID.Paste)
                return true;
            // 部分宿主走 OLE / VSStd2K
            if (cmdGroup == VSConstants.VSStd2K && nCmdID == (uint)VSConstants.VSStd2KCmdID.PASTE)
                return true;
            return false;
        }

        private bool ShouldProcessSnippetKey(uint nCmdID)
        {
            var snippetSettings = SettingsManager.GetSnippetSettings();

            if (!snippetSettings.useSnippets)
            {
                return false;
            }

            return KeyMatches(snippetSettings.replaceKey, nCmdID);
        }

        private bool ShouldProcessAsteriskExpansionKey(uint nCmdID)
        {
            if (!SettingsManager.GetUseSnippets())
                return false;

            var settings = SettingsManager.GetAsteriskExpansionSettings();
            return settings.useAsteriskExpansion && KeyMatches(settings.triggerKey, nCmdID);
        }

        private bool KeyMatches(SettingsManager.SnippetReplaceKey key, uint nCmdID)
        {
            switch (key)
            {
                case SettingsManager.SnippetReplaceKey.Enter:
                    return nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN &&
                           (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift;

                case SettingsManager.SnippetReplaceKey.ShiftEnter:
                    return nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN &&
                           (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                case SettingsManager.SnippetReplaceKey.Tab:
                    return nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB;

                case SettingsManager.SnippetReplaceKey.CtrlSpace:
                    return (nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD ||
                            nCmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST) &&
                           (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

                default:
                    return false;
            }
        }

        private bool TryReplaceSnippet()
        {
            return SnippetExpansionHelper.TryExpandWordAtCaret(textView);
        }

        public int QueryStatus(ref Guid cmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (cmdGroup == VSConstants.VSStd2K)
            {
                for (int i = 0; i < prgCmds.Length; i++)
                {
                    if (prgCmds[i].cmdID == (uint)VSConstants.VSStd2KCmdID.RETURN ||
                        prgCmds[i].cmdID == (uint)VSConstants.VSStd2KCmdID.TAB ||
                        prgCmds[i].cmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD ||
                        prgCmds[i].cmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST)
                    {
                        prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
                        return VSConstants.S_OK;
                    }
                }
            }

            return nextCommandTarget?.QueryStatus(ref cmdGroup, cCmds, prgCmds, pCmdText) ?? VSConstants.S_OK;
        }

        public void Dispose()
        {
            try
            {
                if (_bufferWatch != null)
                {
                    _bufferWatch.Stop();
                    _bufferWatch = null;
                }
            }
            catch { }
            UnadviseBufferEvents();
            try { _intelliSense?.Dispose(); } catch { }
            _intelliSense = null;
            try
            {
                if (textView != null)
                    textView.RemoveCommandFilter(this);
            }
            catch { }
        }

        private void UnadviseBufferEvents()
        {
            if (_textEventsCookie == 0 || _textEventsCp == null) return;
            try { _textEventsCp.Unadvise(_textEventsCookie); } catch { }
            _textEventsCookie = 0;
            _textEventsCp = null;
            _bufferEvents = null;
        }
    }
}
