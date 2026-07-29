using Microsoft.Win32;
using System;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 禁用 SSMS 22 自带 IntelliSense（写 SSMS 自身注册表，非插件配置）。
    /// 注意：这是操作 SSMS 选项注册表，路径需在 SSMS 22 实测确认精确键名。
    /// 失败返回 false，调用方降级（避免双弹框策略见 spec §6.1）。
    /// </summary>
    public static class IntelliSenseDisableHelper
    {
        // SSMS 22 IntelliSense 选项注册表路径（待实测确认）
        private const string SsmsIntelliSenseKey = @"Software\Microsoft\SQL Server Management Studio\22.0\Text Editor\Transact-SQL\IntelliSense";
        private const string EnableValueName = "EnableIntelliSense";

        /// <summary>尝试禁用 SSMS 内建 IntelliSense。成功 true。</summary>
        public static bool TryDisableSsmsIntelliSense()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(SsmsIntelliSenseKey))
                {
                    if (key != null)
                    {
                        key.SetValue(EnableValueName, 0, RegistryValueKind.DWord);
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>查询 SSMS 内建是否已禁用。</summary>
        public static bool IsSsmsIntelliSenseDisabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(SsmsIntelliSenseKey))
                {
                    if (key != null)
                    {
                        var v = key.GetValue(EnableValueName);
                        if (v is int i) return i == 0;
                    }
                }
            }
            catch
            {
            }
            return false;
        }
    }
}
