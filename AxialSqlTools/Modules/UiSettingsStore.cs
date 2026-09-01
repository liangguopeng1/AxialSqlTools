using AxialSqlTools.IntelliSense;
using System;
using System.IO;
using Newtonsoft.Json;

namespace AxialSqlTools
{
    public sealed class UiSettings
    {
        /// <summary>zh-CN (default) or en</summary>
        [JsonProperty("uiLanguage")]
        public string UiLanguage { get; set; } = UiSettingsStore.LanguageZhCn;

        /// <summary>NLog 最低级别：Debug / Info / Warn / Error。默认 Info。</summary>
        [JsonProperty("logLevel")]
        public string LogLevel { get; set; } = UiSettingsStore.LogLevelInfo;

        /// <summary>IntelliSense 设置节点。null 兼容旧 settings.json。</summary>
        [JsonProperty("intelliSense")]
        public IntelliSenseSettings IntelliSense { get; set; }

        /// <summary>Tab History 设置节点。null 兼容旧 settings.json。</summary>
        [JsonProperty("tabHistory")]
        public TabHistorySettings TabHistory { get; set; }
    }

    public sealed class TabHistorySettings
    {
        /// <summary>是否记录标签页历史。默认关闭。</summary>
        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = false;

        /// <summary>历史文件保留天数；0 = 不清理。默认 90。</summary>
        [JsonProperty("retentionDays")]
        public int RetentionDays { get; set; } = 90;
    }

    public static class UiSettingsStore
    {
        public const string LanguageZhCn = "zh-CN";
        public const string LanguageEn = "en";
        public const string LogLevelDebug = "Debug";
        public const string LogLevelInfo = "Info";
        public const string LogLevelWarn = "Warn";
        public const string LogLevelError = "Error";

        private static readonly object SyncRoot = new object();
        private static UiSettings _cache;

        public static UiSettings Load()
        {
            lock (SyncRoot)
            {
                if (_cache != null)
                {
                    return Clone(_cache);
                }
                UserConfigPaths.EnsureRootExists();
                var path = UserConfigPaths.SettingsFile;
                if (!File.Exists(path))
                {
                    _cache = new UiSettings();
                    return Clone(_cache);
                }
                try
                {
                    var json = File.ReadAllText(path);
                    _cache = JsonConvert.DeserializeObject<UiSettings>(json) ?? new UiSettings();
                    Normalize(_cache);
                }
                catch
                {
                    _cache = new UiSettings();
                }
                return Clone(_cache);
            }
        }

        public static void Save(UiSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            Normalize(settings);
            lock (SyncRoot)
            {
                UserConfigPaths.EnsureRootExists();
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(UserConfigPaths.SettingsFile, json);
                _cache = Clone(settings);
            }
        }

        public static string GetUiLanguage()
        {
            return Load().UiLanguage;
        }

        public static void SaveUiLanguage(string language)
        {
            var settings = Load();
            settings.UiLanguage = language;
            Save(settings);
        }

        public static string GetLogLevel()
        {
            return NormalizeLogLevel(Load().LogLevel);
        }

        public static void SaveLogLevel(string level)
        {
            var settings = Load();
            settings.LogLevel = NormalizeLogLevel(level);
            Save(settings);
        }

        public static IntelliSenseSettings GetIntelliSenseSettings()
        {
            var settings = Load();
            return settings.IntelliSense ?? new IntelliSenseSettings();
        }

        public static bool GetIntelliSenseEnabled()
        {
            return GetIntelliSenseSettings().enabled;
        }

        public static void SaveIntelliSenseSettings(IntelliSenseSettings intelliSense)
        {
            var settings = Load();
            settings.IntelliSense = intelliSense ?? new IntelliSenseSettings();
            Save(settings);
        }

        public static TabHistorySettings GetTabHistorySettings()
        {
            var settings = Load();
            return settings.TabHistory ?? new TabHistorySettings();
        }

        public static bool GetTabHistoryEnabled()
        {
            return GetTabHistorySettings().Enabled;
        }

        public static void SaveTabHistoryEnabled(bool enabled)
        {
            var settings = Load();
            if (settings.TabHistory == null) settings.TabHistory = new TabHistorySettings();
            settings.TabHistory.Enabled = enabled;
            Save(settings);
        }

        public static int GetTabHistoryRetentionDays()
        {
            var s = GetTabHistorySettings();
            if (s.RetentionDays < 0) return 0;
            if (s.RetentionDays > 3650) return 3650;
            return s.RetentionDays;
        }

        public static void SaveTabHistoryRetentionDays(int retentionDays)
        {
            if (retentionDays < 0) retentionDays = 0;
            if (retentionDays > 3650) retentionDays = 3650;
            var settings = Load();
            if (settings.TabHistory == null) settings.TabHistory = new TabHistorySettings();
            settings.TabHistory.RetentionDays = retentionDays;
            Save(settings);
        }

        public static void InvalidateCache()
        {
            lock (SyncRoot)
            {
                _cache = null;
            }
        }

        private static void Normalize(UiSettings settings)
        {
            if (settings == null)
            {
                return;
            }
            if (string.Equals(settings.UiLanguage, LanguageEn, StringComparison.OrdinalIgnoreCase)
                || string.Equals(settings.UiLanguage, "en-US", StringComparison.OrdinalIgnoreCase)
                || string.Equals(settings.UiLanguage, "en-GB", StringComparison.OrdinalIgnoreCase))
            {
                settings.UiLanguage = LanguageEn;
            }
            else
            {
                settings.UiLanguage = LanguageZhCn;
            }

            settings.LogLevel = NormalizeLogLevel(settings.LogLevel);

            // IntelliSense 节点缺失时给默认值，保证功能可用。
            if (settings.IntelliSense == null)
            {
                settings.IntelliSense = new IntelliSenseSettings();
            }

            // TabHistory 节点缺失时给默认值，保证读写安全。
            if (settings.TabHistory == null)
            {
                settings.TabHistory = new TabHistorySettings();
            }
        }

        private static UiSettings Clone(UiSettings source)
        {
            return new UiSettings
            {
                UiLanguage = source.UiLanguage,
                LogLevel = NormalizeLogLevel(source.LogLevel),
                IntelliSense = source.IntelliSense ?? new IntelliSenseSettings(),
                TabHistory = source.TabHistory ?? new TabHistorySettings()
            };
        }

        internal static string NormalizeLogLevel(string level)
        {
            if (string.IsNullOrWhiteSpace(level))
                return LogLevelInfo;
            switch (level.Trim().ToLowerInvariant())
            {
                case "debug":
                case "trace":
                    return LogLevelDebug;
                case "warn":
                case "warning":
                    return LogLevelWarn;
                case "error":
                case "fatal":
                    return LogLevelError;
                default:
                    return LogLevelInfo;
            }
        }
    }
}
