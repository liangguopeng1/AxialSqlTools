using System;
using System.Collections.Generic;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 总调度入口。Filter 挂载由 AxialSqlToolsPackage 负责，
    /// KeyHandler 内部 Attach 悬停 ToolTip。
    /// 收口 SSMS 内建禁用 + 防双弹框降级（spec §6.1）。
    /// </summary>
    public static class IntelliSenseManager
    {
        public static bool IsEnabled => UiSettingsStore.GetIntelliSenseEnabled();

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
    }
}