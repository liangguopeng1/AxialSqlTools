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

        /// <summary>IntelliSense 设置节点。null 兼容旧 settings.json。</summary>
        [JsonProperty("intelliSense")]
        public IntelliSenseSettings IntelliSense { get; set; }
    }

    public static class UiSettingsStore
    {
        public const string LanguageZhCn = "zh-CN";
        public const string LanguageEn = "en";

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

            // IntelliSense 节点缺失时给默认值，保证功能可用。
            if (settings.IntelliSense == null)
            {
                settings.IntelliSense = new IntelliSenseSettings();
            }
        }

        private static UiSettings Clone(UiSettings source)
        {
            return new UiSettings
            {
                UiLanguage = source.UiLanguage,
                IntelliSense = source.IntelliSense ?? new IntelliSenseSettings()
            };
        }
    }
}
