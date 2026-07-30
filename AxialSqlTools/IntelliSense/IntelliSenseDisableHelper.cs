using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 同步 SSMS 22 内建 IntelliSense 开关（写 SSMS 用户 settings.json，非插件配置）。
    /// 从 Ssms.exe 同目录 SSMS.isolation.ini 读取 InstallationID，
    /// 定位 %LOCALAPPDATA%\Microsoft\SSMS\22.0_{InstallationID}\settings.json。
    /// </summary>
    public static class IntelliSenseDisableHelper
    {
        private const string SettingsHeader = "/* Visual Studio Settings File */";
        private const string EnableIntelliSenseKey = "languages.sql.intelliSense.enableIntellisense";
        private const string IsolationFileName = "SSMS.isolation.ini";
        private const string SsmsVersionPrefix = "22.0_";
        private const int WriteRetryCount = 5;

        private static readonly Regex EnableIntelliSenseRegex = new Regex(
            @"(""languages\.sql\.intelliSense\.enableIntellisense""\s*:\s*)(true|false)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ExcessiveBlankLinesRegex = new Regex(
            @"(\r?\n){4,}",
            RegexOptions.CultureInvariant);

        /// <summary>禁用 SSMS 内建 IntelliSense。</summary>
        public static bool TryDisableSsmsIntelliSense() =>
            TrySetSsmsIntelliSenseEnabled(false, out _);

        /// <summary>恢复 SSMS 内建 IntelliSense。</summary>
        public static bool TryEnableSsmsIntelliSense() =>
            TrySetSsmsIntelliSenseEnabled(true, out _);

        public static bool TrySetSsmsIntelliSenseEnabled(bool enabled, out string error)
        {
            error = null;
            if (!TryResolveSsmsUserSettingsPath(out string settingsPath))
            {
                error = "无法定位 SSMS settings.json（请确认 SSMS.isolation.ini 或 %LOCALAPPDATA%\\Microsoft\\SSMS\\22.0_* 目录存在）。";
                return false;
            }
            if (!TryWriteEnableIntelliSense(settingsPath, enabled, out error))
                return false;
            return true;
        }

        /// <summary>查询 SSMS 内建是否已禁用。</summary>
        public static bool IsSsmsIntelliSenseDisabled()
        {
            if (!TryResolveSsmsUserSettingsPath(out string settingsPath))
                return false;
            return TryReadEnableIntelliSense(settingsPath, out bool enabled) && !enabled;
        }

        public static bool TryGetSsmsUserSettingsPath(out string settingsPath) =>
            TryResolveSsmsUserSettingsPath(out settingsPath);

        /// <summary>保存后 SSMS 可能覆盖 settings.json，延迟重试同步。</summary>
        public static void ScheduleSyncRetries(bool ssmsIntelliSenseEnabled)
        {
            ScheduleRetryWrite(ssmsIntelliSenseEnabled, TimeSpan.FromSeconds(1));
            ScheduleRetryWrite(ssmsIntelliSenseEnabled, TimeSpan.FromSeconds(3));
        }

        public static void ScheduleDisableRetries() => ScheduleSyncRetries(false);

        private static void ScheduleRetryWrite(bool enabled, TimeSpan delay)
        {
            try
            {
                var timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = delay
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    TrySetSsmsIntelliSenseEnabled(enabled, out _);
                };
                timer.Start();
            }
            catch
            {
            }
        }

        private static bool TryResolveSsmsUserSettingsPath(out string settingsPath)
        {
            if (TryGetSsmsUserSettingsPathFromIsolation(out settingsPath))
                return true;
            return TryGetSsmsUserSettingsPathFromLocalAppData(out settingsPath);
        }

        private static bool TryGetSsmsUserSettingsPathFromIsolation(out string settingsPath)
        {
            settingsPath = null;
            if (!TryGetInstallationId(out string installationId))
                return false;
            settingsPath = BuildSettingsPath(installationId);
            return true;
        }

        private static string BuildSettingsPath(string installationId)
        {
            string ssmsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "SSMS", SsmsVersionPrefix + installationId);
            return Path.Combine(ssmsDir, "settings.json");
        }

        private static bool TryGetSsmsUserSettingsPathFromLocalAppData(out string settingsPath)
        {
            settingsPath = null;
            try
            {
                string ssmsRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "SSMS");
                if (!Directory.Exists(ssmsRoot))
                    return false;
                string[] dirs = Directory.GetDirectories(ssmsRoot, SsmsVersionPrefix + "*");
                if (dirs.Length == 0)
                    return false;
                Array.Sort(dirs, (a, b) =>
                {
                    DateTime ta = File.Exists(Path.Combine(a, "settings.json"))
                        ? File.GetLastWriteTimeUtc(Path.Combine(a, "settings.json"))
                        : DateTime.MinValue;
                    DateTime tb = File.Exists(Path.Combine(b, "settings.json"))
                        ? File.GetLastWriteTimeUtc(Path.Combine(b, "settings.json"))
                        : DateTime.MinValue;
                    return tb.CompareTo(ta);
                });
                settingsPath = Path.Combine(dirs[0], "settings.json");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetInstallationId(out string installationId)
        {
            installationId = null;
            try
            {
                string exeDir = GetHostIdeDirectory();
                if (string.IsNullOrWhiteSpace(exeDir))
                    return false;
                string iniPath = Path.Combine(exeDir, IsolationFileName);
                if (!File.Exists(iniPath))
                    return false;
                return TryParseInstallationId(iniPath, out installationId);
            }
            catch
            {
                return false;
            }
        }

        private static string GetHostIdeDirectory()
        {
            string exePath = GetCurrentProcessPath();
            if (string.IsNullOrWhiteSpace(exePath))
                return null;
            return Path.GetDirectoryName(exePath);
        }

        private static string GetCurrentProcessPath()
        {
            try
            {
                return GetModuleFileName(IntPtr.Zero);
            }
            catch
            {
            }
            try
            {
                return Process.GetCurrentProcess()?.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileName(IntPtr hModule, StringBuilder lpFilename, int nSize);

        private static string GetModuleFileName(IntPtr hModule)
        {
            var sb = new StringBuilder(512);
            while (true)
            {
                uint len = GetModuleFileName(hModule, sb, sb.Capacity);
                if (len == 0)
                    return null;
                if (len < sb.Capacity - 1)
                    return sb.ToString();
                sb.EnsureCapacity(sb.Capacity * 2);
            }
        }

        private static bool TryParseInstallationId(string iniPath, out string installationId)
        {
            installationId = null;
            bool inInfo = false;
            foreach (string rawLine in File.ReadAllLines(iniPath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    inInfo = string.Equals(line, "[Info]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inInfo) continue;
                if (!line.StartsWith("InstallationID=", StringComparison.OrdinalIgnoreCase)) continue;
                installationId = line.Substring("InstallationID=".Length).Trim();
                return !string.IsNullOrEmpty(installationId);
            }
            return false;
        }

        private static bool TryReadEnableIntelliSense(string settingsPath, out bool enabled)
        {
            enabled = true;
            if (!File.Exists(settingsPath))
                return false;
            try
            {
                string content = ReadAllTextShared(settingsPath);
                if (string.IsNullOrWhiteSpace(content))
                    return false;
                var match = EnableIntelliSenseRegex.Match(content);
                if (match.Success)
                {
                    enabled = string.Equals(match.Groups[2].Value, "true", StringComparison.OrdinalIgnoreCase);
                    return true;
                }
                string json = ReadJsonBody(content);
                var obj = JObject.Parse(json);
                JToken token = obj[EnableIntelliSenseKey];
                if (token == null || token.Type == JTokenType.Null)
                    return true;
                enabled = token.Value<bool>();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWriteEnableIntelliSense(string settingsPath, bool enabled, out string error)
        {
            error = null;
            string valueText = enabled ? "true" : "false";
            for (int attempt = 0; attempt < WriteRetryCount; attempt++)
            {
                try
                {
                    string dir = Path.GetDirectoryName(settingsPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    string content = File.Exists(settingsPath)
                        ? ReadAllTextShared(settingsPath)
                        : SettingsHeader + "\r\n{}\r\n";
                    string output = PatchEnableIntelliSense(content, valueText);
                    WriteAllTextShared(settingsPath, output);
                    return true;
                }
                catch (IOException ex) when (attempt < WriteRetryCount - 1)
                {
                    Thread.Sleep(120 * (attempt + 1));
                    error = ex.Message;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
            if (string.IsNullOrEmpty(error))
                error = "settings.json 被占用，写入失败：" + settingsPath;
            return false;
        }

        private static string PatchEnableIntelliSense(string content, string valueText)
        {
            if (string.IsNullOrEmpty(content))
                content = SettingsHeader + "\r\n{}\r\n";
            if (ExcessiveBlankLinesRegex.IsMatch(content))
                content = CompactSettingsJson(content);
            if (EnableIntelliSenseRegex.IsMatch(content))
                return EnableIntelliSenseRegex.Replace(content, "$1" + valueText, 1);
            return BuildFullSettingsJson(content, valueText == "true");
        }

        /// <summary>修复历史错误写入产生的多余空行，并规范为 SSMS 常用格式。</summary>
        private static string CompactSettingsJson(string content)
        {
            bool hasHeader = content.TrimStart().StartsWith(SettingsHeader, StringComparison.Ordinal);
            JObject obj = JObject.Parse(ReadJsonBody(content));
            string body = obj.ToString(Formatting.Indented);
            return hasHeader ? SettingsHeader + "\r\n" + body + "\r\n" : body + "\r\n";
        }

        private static string BuildFullSettingsJson(string content, bool enabled)
        {
            bool hasHeader = content.TrimStart().StartsWith(SettingsHeader, StringComparison.Ordinal);
            JObject obj = JObject.Parse(ReadJsonBody(content));
            obj[EnableIntelliSenseKey] = enabled;
            string body = obj.ToString(Formatting.Indented);
            return hasHeader ? SettingsHeader + "\r\n" + body + "\r\n" : body + "\r\n";
        }

        private static string ReadAllTextShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void WriteAllTextShared(string path, string content)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
            }
        }

        private static string ReadJsonBody(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return "{}";
            string trimmed = content.TrimStart();
            if (!trimmed.StartsWith("/*", StringComparison.Ordinal))
                return trimmed;
            int end = trimmed.IndexOf("*/", StringComparison.Ordinal);
            if (end < 0)
                return trimmed;
            return trimmed.Substring(end + 2).TrimStart();
        }
    }
}
