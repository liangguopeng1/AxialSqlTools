using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
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
        }

        /// <summary>
        /// 禁止在 IOleCommandTarget.Exec 同步路径上 new KeyHandler（第二个标签首次按键会卡死退出）。
        /// 一律排到 ApplicationIdle 再建。
        /// </summary>
        private void ScheduleEnsureIntelliSense()
        {
            if (_intelliSense != null || _intelliSenseInitTried || _intelliSenseInitPending) return;
            if (!UiSettingsStore.GetIntelliSenseEnabled())
            {
                _intelliSenseInitTried = true;
                return;
            }
            _intelliSenseInitPending = true;
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher
                    ?? Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_intelliSense != null || _intelliSenseInitTried) return;
                        _intelliSenseInitTried = true;
                        _logger.Info("IntelliSense KeyHandler creating (deferred, off Exec)");
                        _intelliSense = new IntelliSenseKeyHandler(_package, textView);
                        _logger.Info("IntelliSense KeyHandler created ok");
                        // 首次按键时 Handler 尚未就绪，补一次自动触发调度
                        _intelliSense.MaybeScheduleAutoTrigger((uint)VSConstants.VSStd2KCmdID.TYPECHAR);
                        LogManager.Flush();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "IntelliSense KeyHandler create failed");
                        LogManager.Flush();
                        _intelliSense = null;
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

        public int Exec(ref Guid cmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            // 首次有实际输入/补全相关命令时再初始化，避开新建标签的创建窗口窗口期
            bool mayNeedIntelliSense =
                cmdGroup == VSConstants.VSStd2K &&
                (nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.BACKSPACE
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.DELETE
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB
                 || nCmdID == (uint)VSConstants.VSStd2KCmdID.CANCEL);
            if (mayNeedIntelliSense)
                ScheduleEnsureIntelliSense();

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

            if (_intelliSense != null && _intelliSense.IsSessionOpen && _intelliSense.HandleSessionKey(cmdGroup, nCmdID))
            {
                return VSConstants.S_OK;
            }

            if (cmdGroup == VSConstants.VSStd2K && nCmdID == (uint)VSConstants.VSStd2KCmdID.CANCEL
                && QuickInfoTooltip.IsOpen && Keyboard.IsKeyDown(Key.Escape))
            {
                QuickInfoTooltip.Close();
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
                _intelliSense.MaybeScheduleAutoTrigger(nCmdID);
            }

            return nextCommandTarget?.Exec(ref cmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? VSConstants.S_OK;
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
            return cmdGroup == typeof(VSConstants.VSStd97CmdID).GUID
                && nCmdID == (uint)VSConstants.VSStd97CmdID.Copy;
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
    }
}
