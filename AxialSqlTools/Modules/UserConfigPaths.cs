using System;
using System.IO;
using System.Text;

namespace AxialSqlTools
{
    /// <summary>
    /// Unified user config root: %APPDATA%\AxialSqlTools\
    /// </summary>
    public static class UserConfigPaths
    {
        public static string Root
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AxialSqlTools");
            }
        }

        public static string SettingsFile => Path.Combine(Root, "settings.json");
        public static string SnippetsFile => Path.Combine(Root, "snippets.json");
        public static string DataTransferConnectionsFile => Path.Combine(Root, "data-transfer-connections.json");
        public static string GitHubSyncProfilesFile => Path.Combine(Root, "github-sync-profiles.json");
        public static string QueryHistoryDirectory => Path.Combine(Root, "query-history");
        public static string TabHistoryDirectory => Path.Combine(Root, "tab-history");
        public static string TemplatesDirectory => Path.Combine(Root, "templates");
        public static string LogsDirectory => Path.Combine(Root, "logs");
        public static string QuickSearchIndexDirectory => Path.Combine(Root, "quick-search-index");
        public static string IntelliSenseCacheDirectory => Path.Combine(Root, "intellisense-cache");

        public static void EnsureRootExists()
        {
            Directory.CreateDirectory(Root);
        }

        /// <summary>把服务器/库名变成可作目录或文件名的片段。</summary>
        public static string SanitizePathSegment(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "_unknown";
            }

            var builder = new StringBuilder(name.Length);
            foreach (char c in name.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append('_');
                }
            }

            string result = builder.ToString();
            // 防御路径穿越："." / ".." / "..." 等纯点号段会被 Path.Combine 解析为目录跳转，归一为占位
            if (string.IsNullOrEmpty(result) || result.Trim('.').Length == 0)
                return "_unknown";
            return result;
        }
    }
}
