using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AxialSqlTools
{
    internal sealed class QuickSearchIndexMeta
    {
        public string ServerName { get; set; }
        public DateTime IndexedAtUtc { get; set; }
        public int ObjectCount { get; set; }
        public string ConnectionFingerprint { get; set; }
    }

    internal sealed class QuickSearchIndexEntry
    {
        public string DatabaseName { get; set; }
        public string ObjectType { get; set; }
        public string SchemaName { get; set; }
        public string ObjectName { get; set; }
        public string MatchLocation { get; set; }
        public string SourceText { get; set; }
        public string ScriptDatabaseName { get; set; }
        public string ScriptSchemaName { get; set; }
        public string ScriptObjectName { get; set; }
    }

    internal static class QuickSearchIndexStore
    {
        public static string GetServerDirectory(string serverName)
        {
            string safe = SanitizeServerKey(serverName);
            return Path.Combine(UserConfigPaths.QuickSearchIndexDirectory, safe);
        }

        public static string SanitizeServerKey(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                return "_unknown";
            }

            var builder = new StringBuilder(serverName.Length);
            foreach (char c in serverName.Trim())
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

            return builder.ToString();
        }

        public static string BuildFingerprint(ScriptFactoryAccess.ConnectionInfo connectionInfo)
        {
            if (connectionInfo == null)
            {
                return string.Empty;
            }

            try
            {
                var builder = new SqlConnectionStringBuilder(connectionInfo.FullConnectionString);
                return string.Join("|",
                    builder.DataSource ?? string.Empty,
                    builder.IntegratedSecurity ? "Integrated" : "Sql",
                    builder.UserID ?? string.Empty,
                    builder.Encrypt.ToString(),
                    builder.TrustServerCertificate.ToString());
            }
            catch
            {
                return connectionInfo.ServerName ?? string.Empty;
            }
        }

        public static bool TryGetMeta(string serverName, out QuickSearchIndexMeta meta)
        {
            meta = null;
            string path = Path.Combine(GetServerDirectory(serverName), "meta.json");
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                meta = JsonConvert.DeserializeObject<QuickSearchIndexMeta>(File.ReadAllText(path));
                return meta != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool HasIndex(string serverName)
        {
            if (!TryGetMeta(serverName, out _))
            {
                return false;
            }

            string objectsPath = Path.Combine(GetServerDirectory(serverName), "objects.jsonl");
            return File.Exists(objectsPath);
        }

        public static List<QuickSearchIndexEntry> LoadEntries(string serverName)
        {
            var entries = new List<QuickSearchIndexEntry>();
            string objectsPath = Path.Combine(GetServerDirectory(serverName), "objects.jsonl");
            if (!File.Exists(objectsPath))
            {
                return entries;
            }

            foreach (string line in File.ReadLines(objectsPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonConvert.DeserializeObject<QuickSearchIndexEntry>(line);
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
                }
                catch
                {
                }
            }

            return entries;
        }

        public static async Task BuildIndexAsync(
            ScriptFactoryAccess.ConnectionInfo connectionInfo,
            IReadOnlyList<string> databases,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            if (connectionInfo == null || string.IsNullOrWhiteSpace(connectionInfo.ServerName))
            {
                throw new ArgumentException("connectionInfo");
            }

            string directory = GetServerDirectory(connectionInfo.ServerName);
            Directory.CreateDirectory(directory);

            string tempObjects = Path.Combine(directory, "objects.jsonl.tmp");
            string finalObjects = Path.Combine(directory, "objects.jsonl");
            string metaPath = Path.Combine(directory, "meta.json");

            if (File.Exists(tempObjects))
            {
                File.Delete(tempObjects);
            }

            int count = 0;
            using (var writer = new StreamWriter(tempObjects, false, new UTF8Encoding(false)))
            {
                foreach (string databaseName in databases ?? Array.Empty<string>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(databaseName);

                    List<QuickSearchIndexEntry> rows = await LoadDatabaseEntriesAsync(connectionInfo, databaseName, cancellationToken);
                    foreach (QuickSearchIndexEntry entry in rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await writer.WriteLineAsync(JsonConvert.SerializeObject(entry));
                        count++;
                    }
                }
            }

            if (File.Exists(finalObjects))
            {
                File.Delete(finalObjects);
            }

            File.Move(tempObjects, finalObjects);

            var meta = new QuickSearchIndexMeta
            {
                ServerName = connectionInfo.ServerName,
                IndexedAtUtc = DateTime.UtcNow,
                ObjectCount = count,
                ConnectionFingerprint = BuildFingerprint(connectionInfo)
            };

            File.WriteAllText(metaPath, JsonConvert.SerializeObject(meta, Formatting.Indented));
        }

        public static List<QuickSearchIndexEntry> Search(
            string serverName,
            string searchText,
            bool useWildcards,
            string selectedDatabase,
            bool includeProcs,
            bool includeViews,
            bool includeFunctions,
            bool includeTables,
            bool includeAgentJobSteps)
        {
            var results = new List<QuickSearchIndexEntry>();
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return results;
            }

            foreach (QuickSearchIndexEntry entry in LoadEntries(serverName))
            {
                if (!string.IsNullOrWhiteSpace(selectedDatabase) &&
                    !string.Equals(entry.DatabaseName, selectedDatabase, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsTypeIncluded(entry, includeProcs, includeViews, includeFunctions, includeTables, includeAgentJobSteps))
                {
                    continue;
                }

                if (Matches(entry.SourceText, searchText, useWildcards) ||
                    Matches(entry.ObjectName, searchText, useWildcards))
                {
                    results.Add(entry);
                }
            }

            return results;
        }

        private static bool IsTypeIncluded(
            QuickSearchIndexEntry entry,
            bool includeProcs,
            bool includeViews,
            bool includeFunctions,
            bool includeTables,
            bool includeAgentJobSteps)
        {
            string type = entry.ObjectType ?? string.Empty;
            if (string.Equals(type, "Stored Procedure", StringComparison.OrdinalIgnoreCase))
                return includeProcs;
            if (string.Equals(type, "View", StringComparison.OrdinalIgnoreCase))
                return includeViews;
            if (string.Equals(type, "Function", StringComparison.OrdinalIgnoreCase))
                return includeFunctions;
            if (string.Equals(type, "Table", StringComparison.OrdinalIgnoreCase))
                return includeTables;
            if (string.Equals(type, "SQL Agent Job Step", StringComparison.OrdinalIgnoreCase))
                return includeAgentJobSteps;
            return false;
        }

        private static bool Matches(string source, string searchText, bool useWildcards)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(searchText))
            {
                return false;
            }

            if (!useWildcards)
            {
                return source.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            string pattern = "^" + System.Text.RegularExpressions.Regex.Escape(searchText)
                .Replace(@"\%", ".*")
                .Replace(@"\_", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(
                source,
                pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static async Task<List<QuickSearchIndexEntry>> LoadDatabaseEntriesAsync(
            ScriptFactoryAccess.ConnectionInfo connectionInfo,
            string databaseName,
            CancellationToken cancellationToken)
        {
            var result = new List<QuickSearchIndexEntry>();
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(connectionInfo.FullConnectionString)
            {
                InitialCatalog = databaseName
            };

            const string sql = @"
SELECT
    DB_NAME() AS DatabaseName,
    CASE
        WHEN o.[type] = 'P' THEN 'Stored Procedure'
        WHEN o.[type] = 'V' THEN 'View'
        ELSE 'Function'
    END AS ObjectType,
    s.[name] AS SchemaName,
    o.[name] AS ObjectName,
    'Definition' AS MatchLocation,
    m.[definition] AS SourceText,
    DB_NAME() AS ScriptDatabaseName,
    s.[name] AS ScriptSchemaName,
    o.[name] AS ScriptObjectName
FROM sys.objects o
INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
INNER JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE o.[type] IN ('P', 'V', 'FN', 'IF', 'TF')
  AND o.is_ms_shipped = 0
UNION ALL
SELECT
    DB_NAME(),
    'Table',
    s.[name],
    t.[name],
    'Table Name',
    t.[name],
    DB_NAME(),
    s.[name],
    t.[name]
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
UNION ALL
SELECT
    DB_NAME(),
    'Table',
    s.[name],
    t.[name],
    'Column',
    c.[name],
    DB_NAME(),
    s.[name],
    t.[name]
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
INNER JOIN sys.columns c ON c.object_id = t.object_id
UNION ALL
SELECT
    DB_NAME(),
    CASE
        WHEN o.[type] = 'P' THEN 'Stored Procedure'
        WHEN o.[type] = 'V' THEN 'View'
        ELSE 'Function'
    END,
    s.[name],
    o.[name],
    'Parameter',
    p.[name],
    DB_NAME(),
    s.[name],
    o.[name]
FROM sys.parameters p
INNER JOIN sys.objects o ON o.object_id = p.object_id
INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE p.parameter_id > 0
  AND o.[type] IN ('P', 'V', 'FN', 'IF', 'TF');";

            using (var conn = new SqlConnection(builder.ConnectionString))
            {
                await conn.OpenAsync(cancellationToken);
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.CommandTimeout = 180;
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            result.Add(ReadEntry(reader));
                        }
                    }
                }

                if (string.Equals(databaseName, "msdb", StringComparison.OrdinalIgnoreCase))
                {
                    const string agentSql = @"
SELECT
    'msdb' AS DatabaseName,
    'SQL Agent Job Step' AS ObjectType,
    'dbo' AS SchemaName,
    j.[name] + N' / Step ' + CONVERT(varchar(12), js.step_id) + N' - ' + js.step_name AS ObjectName,
    'JobStep' AS MatchLocation,
    js.[command] AS SourceText,
    N'msdb' AS ScriptDatabaseName,
    N'dbo' AS ScriptSchemaName,
    j.[name] AS ScriptObjectName
FROM dbo.sysjobs j
INNER JOIN dbo.sysjobsteps js ON js.job_id = j.job_id;";

                    using (var cmd = new SqlCommand(agentSql, conn))
                    {
                        cmd.CommandTimeout = 180;
                        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                        {
                            while (await reader.ReadAsync(cancellationToken))
                            {
                                result.Add(ReadEntry(reader));
                            }
                        }
                    }
                }
            }

            return result;
        }

        private static QuickSearchIndexEntry ReadEntry(SqlDataReader reader)
        {
            return new QuickSearchIndexEntry
            {
                DatabaseName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                ObjectType = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                SchemaName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                ObjectName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                MatchLocation = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                SourceText = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                ScriptDatabaseName = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                ScriptSchemaName = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                ScriptObjectName = reader.IsDBNull(8) ? string.Empty : reader.GetString(8)
            };
        }
    }
}
