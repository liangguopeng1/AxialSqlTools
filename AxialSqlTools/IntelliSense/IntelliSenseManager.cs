using System;
using System.Collections.Generic;
using NLog;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 总调度入口。Filter 挂载由 AxialSqlToolsPackage 负责，
    /// KeyHandler 内部 Attach 悬停 ToolTip。
    /// 收口 SSMS 内建禁用 + 防双弹框降级（spec §6.1）。
    /// </summary>
    public static class IntelliSenseManager
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        public static bool IsEnabled => UiSettingsStore.GetIntelliSenseEnabled();

        /// <summary>
        /// 当前 DTE 活动窗口是否是 SQL 查询编辑器。
        /// 工具窗口 / 设置页 / 非 SQL 文档为 false，全局悬停定时器直接停手，不必给每个功能窗口单独黑名单。
        /// </summary>
        public static bool SqlEditorIsForeground { get; private set; } = true;

        /// <summary>
        /// 本次会话是否抑制自动触发。
        /// SSMS 内建禁用需重启 SSMS 才生效，重启前若内建仍开着，
        /// 自动触发会造成双弹框干扰，故抑制自动弹，仅 Ctrl+Space 手动可用。
        /// 重启后 Ensure 检测到内建已禁，自动清除此标志，恢复自动弹。
        /// </summary>
        public static bool AutoTriggerSuppressed { get; private set; }

        /// <summary>
        /// 尝试禁用 SSMS 自带 IntelliSense（写 SSMS 用户 settings.json）。
        /// 幂等。返回 false 表示写入失败。
        /// </summary>
        public static bool EnsureSsmsIntelliSenseDisabled()
        {
            var s = UiSettingsStore.GetIntelliSenseSettings();
            if (!s.enabled)
            {
                return true;
            }

            // 内建已禁用 → 无需动作，恢复自动弹
            if (IntelliSenseDisableHelper.IsSsmsIntelliSenseDisabled())
            {
                AutoTriggerSuppressed = false;
                return true;
            }

            // 需要写 settings.json。SSMS 必须重启才生效，本次会话内建仍可能开着 → 抑制自动触发
            bool ok = IntelliSenseDisableHelper.TryDisableSsmsIntelliSense();
            if (ok)
                IntelliSenseDisableHelper.ScheduleDisableRetries();
            AutoTriggerSuppressed = true;
            return ok;
        }

        public static bool AnyPopupOpen() =>
            QuickInfoTooltip.IsOpen || IntelliSenseKeyHandler.AnySessionOpen();

        private static readonly List<(Guid Group, uint Id)> ExecuteCommandIds = new List<(Guid, uint)>();

        public static void RegisterExecuteCommand(Guid group, uint id)
        {
            if (group == Guid.Empty) return;
            lock (ExecuteCommandIds)
            {
                foreach (var existing in ExecuteCommandIds)
                {
                    if (existing.Group == group && existing.Id == id) return;
                }
                ExecuteCommandIds.Add((group, id));
            }
        }

        public static bool IsExecuteCommand(Guid group, uint id)
        {
            lock (ExecuteCommandIds)
            {
                foreach (var existing in ExecuteCommandIds)
                {
                    if (existing.Group == group && existing.Id == id) return true;
                }
            }
            return false;
        }

        /// <summary>切换窗口/失焦/菜单命令时关闭补全弹框与悬停提示。</summary>
        public static void CloseAllPopups()
        {
            IntelliSenseKeyHandler.CloseAllSessions();
            IntelliSenseKeyHandler.SuppressAutoTriggerBriefly();
            QuickInfoTooltip.Close();
        }

        /// <summary>DTE WindowActivated：用活动窗口类型做总闸，而不是给每个工具窗口单独挂钩。</summary>
        public static void NotifyWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            try
            {
                if (gotFocus != null && lostFocus != null && gotFocus == lostFocus)
                    return;
                bool sql = IsSqlQueryDocument(gotFocus);
                if (sql)
                {
                    // lostFocus 为空多半是补全/QuickInfo 焦点抖动，不能关弹框、也不能重计悬停
                    SetSqlEditorForeground(true, resetHover: lostFocus != null);
                    if (lostFocus != null)
                        CloseAllPopups();
                    return;
                }
                if (gotFocus == null)
                    return;
                string kind = null;
                try { kind = gotFocus.Kind; } catch { }
                if (string.IsNullOrEmpty(kind))
                    return;
                SetSqlEditorForeground(false);
                CloseAllPopups();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "IntelliSense NotifyWindowActivated failed");
            }
        }

        public static void SetSqlEditorForeground(bool value, bool resetHover = false)
        {
            bool changed = SqlEditorIsForeground != value;
            SqlEditorIsForeground = value;
            if (changed)
                _logger.Info("IntelliSense sql-editor-foreground={0}", value);
            if (!value)
            {
                CloseAllPopups();
                return;
            }
            if (resetHover)
                IntelliSenseTextViewExtension.OnSqlEditorActivated();
        }

        /// <summary>SQL 查询文档：Kind=Document 且 Object.DocData 存在。工具窗口没有 DocData。</summary>
        public static bool IsSqlQueryDocument(EnvDTE.Window window)
        {
            if (window == null) return false;
            try
            {
                string kind = null;
                try { kind = window.Kind; } catch { }
                if (!string.Equals(kind, "Document", StringComparison.OrdinalIgnoreCase))
                    return false;
                object winObj = null;
                try { winObj = window.Object; } catch { return false; }
                if (winObj == null) return false;
                return GridAccess.GetProperty(winObj, "DocData") != null;
            }
            catch
            {
                return false;
            }
        }
    }
}