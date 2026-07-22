using System;
using System.IO;

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
        public static string TemplatesDirectory => Path.Combine(Root, "templates");
        public static string LogsDirectory => Path.Combine(Root, "logs");

        public static void EnsureRootExists()
        {
            Directory.CreateDirectory(Root);
        }
    }
}
