using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using AxialSqlTools.IntelliSense;

namespace AxialSqlTools
{
    public class KeypressCommandFilter : IOleCommandTarget
    {
        private IOleCommandTarget nextCommandTarget;
        private IVsTextView textView;
        private readonly IntelliSenseKeyHandler _intelliSense;

        public KeypressCommandFilter(AxialSqlToolsPackage package, IVsTextView textView)
        {
            this.textView = textView;

            // IntelliSense 独立于 snippets：enabled 即创建处理器
            if (UiSettingsStore.GetIntelliSenseEnabled())
            {
                _intelliSense = new IntelliSenseKeyHandler(package, textView);
            }
        }

        public void AddToChain()
        {
            // Adds this filter into the command chain
            if (textView != null && textView.AddCommandFilter(this, out nextCommandTarget) != VSConstants.S_OK)
            {
                throw new Exception("Failed to add command filter");
            }
        }

        public int Exec(ref Guid cmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            // IntelliSense 优先级链（spec §5.1）：
            // 1. 弹框开 → 路由键（Up/Down/Tab/Enter/Esc 等），吞键
            if (_intelliSense != null && _intelliSense.IsSessionOpen && _intelliSense.HandleSessionKey(cmdGroup, nCmdID))
            {
                return VSConstants.S_OK;
            }

            // 2. Ctrl+Space / Ctrl+J 强制补全；弹框已开时 COMPLETEWORD 提交选中项
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
                // 吞掉 SSMS 内建自动 COMPLETEWORD，由 TYPECHAR 防抖触发本插件补全
                return VSConstants.S_OK;
            }

            // 3. 弹框关 → snippet / asterisk（互斥）
            if (cmdGroup == VSConstants.VSStd2K && IsSupportedKey(nCmdID))
            {
                if (ShouldProcessSnippetKey(nCmdID) && TryReplaceSnippet())
                {
                    // Snippet was replaced — swallow the key so no newline/tab is inserted.
                    return VSConstants.S_OK;
                }

                if (ShouldProcessAsteriskExpansionKey(nCmdID) && AsteriskExpansionService.TryExpand(textView))
                {
                    // Asterisk was expanded — swallow the key so no newline/tab is inserted.
                    return VSConstants.S_OK;
                }
            }

            // 4. 自动触发补全（防抖；弹框开时立即刷新候选）
            if (_intelliSense != null)
            {
                _intelliSense.MaybeScheduleAutoTrigger(nCmdID);
            }

            // Pass along the command so that other command handlers can process it.
            return nextCommandTarget?.Exec(ref cmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? VSConstants.S_OK;
        }

        private bool IsSupportedKey(uint nCmdID)
        {
            return nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN
                || nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB
                || nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD
                || nCmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST;
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
