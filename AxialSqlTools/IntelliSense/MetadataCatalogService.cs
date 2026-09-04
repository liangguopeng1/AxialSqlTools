using Microsoft.Data.SqlClient;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Text.RegularExpressions;

namespace AxialSqlTools
{
    namespace IntelliSense
    {
        /// <summary>
        /// 按 (Server, Database) 缓存元数据目录。单例。
        /// 补全热路径只读内存/磁盘，不查库。后台刷新才连独立 SqlConnection 查 sys.*。
        /// </summary>
        public class MetadataCatalogService
        {
            private const int QueryTimeoutSeconds = 5;
            private const int ConnectTimeoutSeconds = 5;

            private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

            private static readonly Lazy<MetadataCatalogService> _instance =
                new Lazy<MetadataCatalogService>(() => new MetadataCatalogService());

            public static MetadataCatalogService Instance => _instance.Value;

            private readonly ConcurrentDictionary<string, MetadataCatalog> _cache =
                new ConcurrentDictionary<string, MetadataCatalog>(StringComparer.OrdinalIgnoreCase);

            private readonly ConcurrentDictionary<string, List<string>> _linkedServerCache =
                new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            private readonly ConcurrentDictionary<string, TableColumnInfo> _tableColumnCache =
                new ConcurrentDictionary<string, TableColumnInfo>(StringComparer.OrdinalIgnoreCase);

            private readonly ConcurrentDictionary<string, List<string>> _databaseListCache =
                new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            private readonly ConcurrentDictionary<string, byte> _memoryLoadedServers =
                new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            private static readonly Regex IPv4Regex = new Regex(
                @"^\d{1,3}(\.\d{1,3}){3}$",
                RegexOptions.CultureInvariant | RegexOptions.Compiled);

            private static string Key(string server, string database)
            {
                return (server ?? string.Empty) + "|" + (database ?? string.Empty);
            }

            private static string LinkedCatalogKey(string linkedServer, string database)
            {
                return "ls:" + (linkedServer ?? string.Empty) + "|" + (database ?? string.Empty);
            }

            /// <summary>仅读缓存，不访问数据库（UI 线程安全、不阻塞）。</summary>
            public MetadataCatalog GetCachedCatalog(ScriptFactoryAccess.ConnectionInfo connInfo, string dbOverride = null)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                    return null;
                string database = string.IsNullOrWhiteSpace(dbOverride) ? (connInfo.Database ?? "master") : dbOverride;
                database = UnbracketSqlIdent(database);
                if (string.IsNullOrWhiteSpace(database))
                    database = "master";
                string key = Key(connInfo.ServerName, database);
                if (_cache.TryGetValue(key, out var cached) && cached != null
                    && (cached.IsIndexed || !cached.IsEmpty)
                    && string.Equals(cached.Database, database, StringComparison.OrdinalIgnoreCase))
                    return cached;
                return null;
            }

            /// <summary>后台预热目录：先磁盘，没有则排队整服务器刷新。补全路径不查库。</summary>
            public void EnsureCatalogBuilding(ScriptFactoryAccess.ConnectionInfo connInfo, string dbOverride = null)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                    return;
                if (GetCachedCatalog(connInfo, dbOverride) != null)
                {
                    MetadataCacheRefreshService.Instance.EnsureServerCache(connInfo);
                    return;
                }
                TryLoadCatalogFromDisk(connInfo, dbOverride);
                MetadataCacheRefreshService.Instance.EnsureServerCache(connInfo);
            }

            /// <summary>
            /// 扫描脚本中的三段名 / db..table，后台预热当前库 + 引用到的跨库目录。
            /// 粘贴整段 SQL 后调用，避免悬停/补全时跨库缓存尚未加载。
            /// </summary>
            public void EnsureCatalogsReferencedInSql(ScriptFactoryAccess.ConnectionInfo connInfo, string sql)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                    return;
                EnsureCatalogBuilding(connInfo);
                if (string.IsNullOrEmpty(sql)) return;
                foreach (var db in ExtractReferencedDatabaseNames(sql))
                {
                    if (string.IsNullOrEmpty(db)) continue;
                    if (string.Equals(db, connInfo.Database, StringComparison.OrdinalIgnoreCase))
                        continue;
                    EnsureCatalogBuilding(connInfo, db);
                }
            }

            /// <summary>
            /// 在后台线程同步构建当前库 + SQL 引用跨库目录（可阻塞，勿在 UI 线程调用）。
            /// </summary>
            public void BuildCatalogsReferencedInSql(ScriptFactoryAccess.ConnectionInfo connInfo, string sql)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                    return;
                GetOrBuildCatalog(connInfo);
                if (string.IsNullOrEmpty(sql)) return;
                foreach (var db in ExtractReferencedDatabaseNames(sql))
                {
                    if (string.IsNullOrEmpty(db)) continue;
                    if (string.Equals(db, connInfo.Database, StringComparison.OrdinalIgnoreCase))
                        continue;
                    GetOrBuildCatalog(connInfo, db);
                }
            }

            /// <summary>补全/悬停跨库兜底：内存未命中则读磁盘；不等待 SQL 构建（后台异步完成，下次再取）。</summary>
            public MetadataCatalog GetCachedCatalogOrDisk(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string dbOverride = null)
            {
                var cached = GetCachedCatalog(connInfo, dbOverride);
                if (cached != null) return cached;
                return TryLoadCatalogFromDisk(connInfo, dbOverride);
            }

            /// <summary>从 SQL 提取可能的库名（db.schema.obj / db..obj / [db].[schema].[obj] / USE db）。</summary>
            internal static List<string> ExtractReferencedDatabaseNames(string sql)
            {
                var result = new List<string>();
                sql = ClipSqlForCatalogScan(sql);
                if (string.IsNullOrEmpty(sql)) return result;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string useDb = CompletionEngine.GetActiveUseDatabase(sql, sql.Length, cacheResult: false);
                if (IsPlausibleDatabaseName(useDb) && seen.Add(useDb))
                    result.Add(useDb);
                // db.schema.obj 或 [db].[schema].[obj]
                foreach (Match m in ThreePartNameRegex.Matches(sql))
                {
                    string db = UnbracketSqlIdent(m.Groups["db"].Value);
                    if (IsPlausibleDatabaseName(db) && seen.Add(db))
                        result.Add(db);
                }
                // db..obj
                foreach (Match m in DoubleDotNameRegex.Matches(sql))
                {
                    string db = UnbracketSqlIdent(m.Groups["db"].Value);
                    if (IsPlausibleDatabaseName(db) && seen.Add(db))
                        result.Add(db);
                }
                return result;
            }

            private const int CatalogScanHeadChars = 32 * 1024;
            private const int CatalogScanTailChars = 64 * 1024;

            /// <summary>大脚本只扫头尾提取库名，避免对 1MB+ 粘贴做三份全文正则并长期抓住原文。</summary>
            internal static string ClipSqlForCatalogScan(string sql)
            {
                if (string.IsNullOrEmpty(sql)) return sql;
                int n = sql.Length;
                if (n <= CatalogScanHeadChars + CatalogScanTailChars) return sql;
                return sql.Substring(0, CatalogScanHeadChars) + "\n" + sql.Substring(n - CatalogScanTailChars);
            }
            /// 三段名 db.schema.obj → db；否则 USE 库或当前连接库。
            /// 区别于 ExtractReferencedDatabaseNames（后者提取所有引用库，含查询引用），
            /// 失效只应针对 DDL 实际修改的目标库，避免误伤仅被查询引用的库（如 SELECT * FROM otherdb..t）。
            /// </summary>
            internal static List<string> ExtractDdlTargetDatabases(string sql, string currentDatabase)
            {
                var result = new List<string>();
                if (string.IsNullOrEmpty(sql)) return result;
                string useDb = CompletionEngine.GetActiveUseDatabase(sql, sql.Length, cacheResult: false);
                string fallback = !string.IsNullOrWhiteSpace(useDb) && IsPlausibleDatabaseName(useDb)
                    ? useDb
                    : (string.IsNullOrWhiteSpace(currentDatabase) ? "master" : UnbracketSqlIdent(currentDatabase));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in DdlTargetRegex.Matches(sql))
                {
                    string seg3 = UnbracketSqlIdent(m.Groups["seg3"].Value);
                    string db = !string.IsNullOrEmpty(seg3)
                        ? UnbracketSqlIdent(m.Groups["seg1"].Value) // db.schema.obj
                        : fallback;                                  // schema.obj / obj
                    if (IsPlausibleDatabaseName(db) && seen.Add(db))
                        result.Add(db);
                }
                if (result.Count == 0 && IsPlausibleDatabaseName(fallback))
                    result.Add(fallback);
                return result;
            }

            private static readonly Regex DdlTargetRegex = new Regex(
                @"\b(?:CREATE(?:\s+OR\s+ALTER)?|ALTER|DROP|TRUNCATE)\s+(?:TABLE|VIEW|PROCEDURE|PROC|FUNCTION)\s+\[?(?<seg1>[A-Za-z_@#][\w@#$]*)\]?(?:\s*\.\s*\[?(?<seg2>[A-Za-z_@#][\w@#$]*)\]?)?(?:\s*\.\s*\[?(?<seg3>[A-Za-z_@#][\w@#$]*)\]?)?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

            private static readonly Regex ThreePartNameRegex = new Regex(
                @"\[?(?<db>[A-Za-z_@#][\w@#$]*)\]?[ \t]*\.[ \t]*\[?(?<schema>[A-Za-z_@#][\w@#$]*)\]?[ \t]*\.[ \t]*\[?(?<obj>[A-Za-z_@#][\w@#$]*)\]?",
                RegexOptions.CultureInvariant | RegexOptions.Compiled);

            private static readonly Regex DoubleDotNameRegex = new Regex(
                @"\[?(?<db>[A-Za-z_@#][\w@#$]*)\]?[ \t]*\.\.[ \t]*\[?(?<obj>[A-Za-z_@#][\w@#$]*)\]?",
                RegexOptions.CultureInvariant | RegexOptions.Compiled);

            private static string UnbracketSqlIdent(string text)
            {
                if (string.IsNullOrEmpty(text)) return text;
                if (text.Length >= 2 && text[0] == '[' && text[text.Length - 1] == ']')
                    return text.Substring(1, text.Length - 2);
                return text;
            }

            private static bool IsPlausibleDatabaseName(string name)
            {
                if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
                switch (name.ToUpperInvariant())
                {
                    case "SELECT":
                    case "FROM":
                    case "JOIN":
                    case "INNER":
                    case "LEFT":
                    case "RIGHT":
                    case "FULL":
                    case "CROSS":
                    case "OUTER":
                    case "WHERE":
                    case "ON":
                    case "AND":
                    case "OR":
                    case "INSERT":
                    case "UPDATE":
                    case "DELETE":
                    case "INTO":
                    case "VALUES":
                    case "SET":
                    case "EXEC":
                    case "EXECUTE":
                    case "WITH":
                    case "AS":
                    case "NULL":
                    case "SYS":
                    case "INFORMATION_SCHEMA":
                        return false;
                    default:
                        return true;
                }
            }

            /// <summary>取目录：内存 → 磁盘。不连库。requireRoutines 仅作签名兼容。</summary>
            public MetadataCatalog GetOrBuildCatalog(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string dbOverride = null,
                bool requireRoutines = false)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                    return null;

                var cached = GetCachedCatalog(connInfo, dbOverride);
                if (cached != null)
                    return cached;

                cached = TryLoadCatalogFromDisk(connInfo, dbOverride);
                if (cached != null)
                    return cached;

                MetadataCacheRefreshService.Instance.EnsureServerCache(connInfo);
                return null;
            }

            /// <summary>缓存库名列表：内存 → meta.json，不连库。</summary>
            public List<string> GetDatabasesCached(ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                    return new List<string>();
                string key = connInfo.ServerName ?? string.Empty;
                if (_databaseListCache.TryGetValue(key, out var cached) && cached != null)
                    return cached;
                if (MetadataCacheStore.TryGetMeta(connInfo.ServerName, out var meta) && meta?.Databases != null)
                {
                    var list = new List<string>(meta.Databases);
                    _databaseListCache[key] = list;
                    return list;
                }
                return new List<string>();
            }

            public void PutCatalog(string server, string database, MetadataCatalog catalog)
            {
                if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database) || catalog == null)
                    return;
                _cache[Key(server, database)] = catalog;
                _cache[LinkedCatalogKey(server, database)] = catalog;
            }

            public void PutDatabaseList(string server, List<string> databases)
            {
                if (string.IsNullOrWhiteSpace(server))
                    return;
                _databaseListCache[server] = databases ?? new List<string>();
            }

            public void MarkServerLoadedInMemory(string server)
            {
                if (string.IsNullOrWhiteSpace(server))
                    return;
                _memoryLoadedServers[server] = 0;
            }

            public void PutLinkedServers(string server, List<string> linkedServers)
            {
                if (string.IsNullOrWhiteSpace(server))
                    return;
                _linkedServerCache[server] = linkedServers ?? new List<string>();
            }

            public bool IsServerLoadedInMemory(string serverName)
            {
                return !string.IsNullOrWhiteSpace(serverName) && _memoryLoadedServers.ContainsKey(serverName);
            }

            public void LoadServerFromDisk(string serverName, IProgress<IndexBuildProgress> progress = null)
            {
                if (string.IsNullOrWhiteSpace(serverName))
                    return;
                if (_memoryLoadedServers.ContainsKey(serverName))
                    return;
                List<string> databases = null;
                if (MetadataCacheStore.TryGetMeta(serverName, out var meta) && meta?.Databases != null)
                {
                    databases = new List<string>(meta.Databases);
                    _databaseListCache[serverName] = databases;
                    if (meta.LinkedServers != null)
                        _linkedServerCache[serverName] = new List<string>(meta.LinkedServers);
                }
                if (databases != null)
                {
                    int total = databases.Count;
                    Logger.Info("IntelliSense cache load from disk {0} databases={1}", serverName, total);
                    int done = 0;
                    foreach (string db in databases)
                    {
                        var catalog = MetadataCacheStore.TryLoadCatalog(serverName, db);
                        if (catalog != null)
                            _cache[Key(serverName, catalog.Database ?? db)] = catalog;
                        done++;
                        progress?.Report(new IndexBuildProgress
                        {
                            ServerName = serverName,
                            Title = "从磁盘加载 IntelliSense 缓存",
                            CurrentItem = db,
                            Completed = done,
                            Total = total,
                            Message = "磁盘  " + done + "/" + total + "  " + db
                        });
                    }
                    _memoryLoadedServers[serverName] = 0;
                    return;
                }
                int fileTotal = 0;
                foreach (string path in MetadataCacheStore.ListCatalogFiles(serverName))
                    fileTotal++;
                Logger.Info("IntelliSense cache load from disk {0} files={1}", serverName, fileTotal);
                int fileDone = 0;
                foreach (string path in MetadataCacheStore.ListCatalogFiles(serverName))
                {
                    string db = System.IO.Path.GetFileNameWithoutExtension(path);
                    var catalog = MetadataCacheStore.TryLoadCatalog(serverName, db);
                    if (catalog != null)
                        _cache[Key(serverName, catalog.Database ?? db)] = catalog;
                    fileDone++;
                    progress?.Report(new IndexBuildProgress
                    {
                        ServerName = serverName,
                        Title = "从磁盘加载 IntelliSense 缓存",
                        CurrentItem = db,
                        Completed = fileDone,
                        Total = fileTotal,
                        Message = "磁盘  " + fileDone + "/" + fileTotal + "  " + db
                    });
                }
                _memoryLoadedServers[serverName] = 0;
            }

            internal MetadataCatalog BuildCatalogForCache(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string database,
                int commandTimeoutSeconds)
            {
                return BuildCatalog(connInfo, database, commandTimeoutSeconds);
            }

            private MetadataCatalog TryLoadCatalogFromDisk(ScriptFactoryAccess.ConnectionInfo connInfo, string dbOverride)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                    return null;
                string database = string.IsNullOrWhiteSpace(dbOverride) ? (connInfo.Database ?? "master") : dbOverride;
                database = UnbracketSqlIdent(database);
                if (string.IsNullOrWhiteSpace(database))
                    database = "master";
                var catalog = MetadataCacheStore.TryLoadCatalog(connInfo.ServerName, database);
                if (catalog == null)
                    return null;
                _cache[Key(connInfo.ServerName, database)] = catalog;
                return catalog;
            }

            public bool ContainsDatabase(ScriptFactoryAccess.ConnectionInfo connInfo, string name)
            {
                if (string.IsNullOrEmpty(name)) return false;
                var dbs = GetDatabasesCached(connInfo);
                for (int i = 0; i < dbs.Count; i++)
                {
                    if (string.Equals(dbs[i], name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            /// <summary>按需加载单个过程/函数定义（悬停展示 SQL；不在整库 LoadRoutines 里拉，避免超时）。</summary>
            public string EnsureRoutineDefinition(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string database,
                RoutineInfo routine)
            {
                if (routine == null) return null;
                if (!string.IsNullOrEmpty(routine.Definition))
                    return routine.Definition;
                if (connInfo == null || string.IsNullOrWhiteSpace(routine.Name))
                    return null;

                string db = string.IsNullOrWhiteSpace(database) ? connInfo.Database : database;
                if (string.IsNullOrWhiteSpace(db)) db = "master";
                string sch = string.IsNullOrWhiteSpace(routine.Schema) ? "dbo" : routine.Schema;
                var target = ScriptFactoryAccess.CloneWithDatabase(connInfo, db);
                if (target == null || string.IsNullOrWhiteSpace(target.FullConnectionString))
                    return null;

                const string sql = @"
SELECT m.definition
FROM sys.objects o
JOIN sys.schemas s ON o.schema_id = s.schema_id
JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE s.name = @schema AND o.name = @name AND o.type IN ('P','FN','TF','IF') AND o.is_ms_shipped = 0;";
                try
                {
                    using (var conn = new SqlConnection(WithConnectTimeout(target.FullConnectionString)))
                    {
                        conn.Open();
                        using (var cmd = Cmd(conn, sql))
                        {
                            cmd.Parameters.AddWithValue("@schema", sch);
                            cmd.Parameters.AddWithValue("@name", routine.Name);
                            object val = cmd.ExecuteScalar();
                            if (val != null && val != DBNull.Value)
                            {
                                routine.Definition = val as string;
                                return routine.Definition;
                            }
                        }
                    }
                }
                catch
                {
                }
                return null;
            }

            /// <summary>按需加载视图定义（悬停 DDL；不在整库扫描里拉 OBJECT_DEFINITION）。</summary>
            public string EnsureViewDefinition(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string database,
                TableColumnInfo view)
            {
                if (view == null || !view.IsView) return null;
                if (!string.IsNullOrEmpty(view.Definition))
                    return view.Definition;
                if (connInfo == null || string.IsNullOrWhiteSpace(view.Name))
                    return null;

                string db = string.IsNullOrWhiteSpace(database) ? connInfo.Database : database;
                if (string.IsNullOrWhiteSpace(db)) db = "master";
                string sch = string.IsNullOrWhiteSpace(view.Schema) ? "dbo" : view.Schema;
                var target = ScriptFactoryAccess.CloneWithDatabase(connInfo, db);
                if (target == null || string.IsNullOrWhiteSpace(target.FullConnectionString))
                    return null;

                const string sql = @"
SELECT m.definition
FROM sys.objects o
JOIN sys.schemas s ON o.schema_id = s.schema_id
JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE s.name = @schema AND o.name = @name AND o.type = 'V' AND o.is_ms_shipped = 0;";
                try
                {
                    using (var conn = new SqlConnection(WithConnectTimeout(target.FullConnectionString)))
                    {
                        conn.Open();
                        using (var cmd = Cmd(conn, sql))
                        {
                            cmd.Parameters.AddWithValue("@schema", sch);
                            cmd.Parameters.AddWithValue("@name", view.Name);
                            object val = cmd.ExecuteScalar();
                            if (val != null && val != DBNull.Value)
                            {
                                view.Definition = val as string;
                                return view.Definition;
                            }
                        }
                    }
                }
                catch
                {
                }
                return null;
            }

            /// <summary>按需加载单表列（catalog 未命中或跨库时的兜底）。</summary>
            public TableColumnInfo GetTableColumns(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string database,
                string schema,
                string tableName)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(tableName))
                    return null;

                string db = string.IsNullOrWhiteSpace(database) ? connInfo.Database : database;
                if (string.IsNullOrWhiteSpace(db)) db = "master";
                string sch = string.IsNullOrWhiteSpace(schema) ? "dbo" : schema;
                string cacheKey = (connInfo.ServerName ?? string.Empty) + "|" + db + "|" + sch + "|" + tableName;
                if (_tableColumnCache.TryGetValue(cacheKey, out var cached) && cached != null)
                    return cached;

                var target = ScriptFactoryAccess.CloneWithDatabase(connInfo, db);
                if (target == null || string.IsNullOrWhiteSpace(target.FullConnectionString))
                    return null;

                const string sql = @"
SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable,
       OBJECT_DEFINITION(c.default_object_id), ep.value, c.is_identity,
       CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END
FROM sys.columns c
JOIN sys.types t ON c.user_type_id = t.user_type_id
JOIN sys.objects o ON c.object_id = o.object_id
JOIN sys.schemas s ON o.schema_id = s.schema_id
LEFT JOIN sys.extended_properties ep
       ON ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
LEFT JOIN (
    SELECT ic.object_id, ic.column_id
    FROM sys.index_columns ic
    JOIN sys.indexes i ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND i.is_primary_key = 1
) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
WHERE s.name = @schema AND o.name = @table AND o.type IN ('U','V')
ORDER BY c.column_id;";

                try
                {
                    using (var conn = new SqlConnection(WithConnectTimeout(target.FullConnectionString)))
                    {
                        conn.Open();
                        using (var cmd = Cmd(conn, sql))
                        {
                            cmd.Parameters.AddWithValue("@schema", sch);
                            cmd.Parameters.AddWithValue("@table", tableName);
                            using (var reader = cmd.ExecuteReader())
                            {
                                TableColumnInfo info = null;
                                while (reader.Read())
                                {
                                    if (info == null)
                                    {
                                        info = new TableColumnInfo { Schema = sch, Name = tableName };
                                    }
                                    info.Columns.Add(new ColumnInfo
                                    {
                                        Name = reader.IsDBNull(0) ? null : reader.GetString(0),
                                        DataType = FormatDataType(
                                            reader.IsDBNull(1) ? null : reader.GetString(1),
                                            reader.IsDBNull(2) ? (short)0 : reader.GetInt16(2),
                                            reader.IsDBNull(3) ? (byte)0 : reader.GetByte(3),
                                            reader.IsDBNull(4) ? (byte)0 : reader.GetByte(4)),
                                        Nullable = !reader.IsDBNull(5) && reader.GetBoolean(5),
                                        DefaultValue = reader.IsDBNull(6) ? null : reader.GetString(6),
                                        Description = reader.IsDBNull(7) ? null : reader.GetString(7),
                                        IsIdentity = !reader.IsDBNull(8) && reader.GetBoolean(8),
                                        IsPrimaryKey = !reader.IsDBNull(9) && reader.GetInt32(9) == 1
                                    });
                                }
                                if (info != null)
                                    _tableColumnCache[cacheKey] = info;
                                return info;
                            }
                        }
                    }
                }
                catch
                {
                    return null;
                }
            }

            /// <summary>链接服务器上的库列表：内存 → 磁盘。缺失则后台四段名拉取。不查库。</summary>
            public List<string> GetLinkedServerDatabases(ScriptFactoryAccess.ConnectionInfo connInfo, string linkedServer)
            {
                var empty = new List<string>();
                linkedServer = UnbracketSqlIdent(linkedServer);
                if (string.IsNullOrWhiteSpace(linkedServer))
                    return empty;
                MetadataCacheRefreshService.Instance.EnsureLinkedServerCache(connInfo, linkedServer);
                if (_databaseListCache.TryGetValue(linkedServer, out var fromMem) && fromMem != null && fromMem.Count > 0)
                    return new List<string>(fromMem);
                if (MetadataCacheStore.TryGetMeta(linkedServer, out var meta) && meta?.Databases != null)
                {
                    var list = new List<string>(meta.Databases);
                    _databaseListCache[linkedServer] = list;
                    LoadServerFromDisk(linkedServer);
                    return new List<string>(list);
                }
                return empty;
            }

            /// <summary>已注册的链接服务器：内存 → 当前服务器 meta.json。不查库。</summary>
            public List<string> GetLinkedServers(ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                    return new List<string>();

                if (_linkedServerCache.TryGetValue(connInfo.ServerName, out var cached) && cached != null)
                    return new List<string>(cached);

                if (MetadataCacheStore.TryGetMeta(connInfo.ServerName, out var meta) && meta?.LinkedServers != null)
                {
                    var list = new List<string>(meta.LinkedServers);
                    _linkedServerCache[connInfo.ServerName] = list;
                    return new List<string>(list);
                }
                _linkedServerCache[connInfo.ServerName] = new List<string>();
                return new List<string>();
            }

            public bool IsLinkedServerName(ScriptFactoryAccess.ConnectionInfo connInfo, string name)
            {
                name = UnbracketSqlIdent(name);
                if (string.IsNullOrWhiteSpace(name))
                    return false;
                if (IsLocalDatabaseName(connInfo, name))
                    return false;
                if (IPv4Regex.IsMatch(name))
                    return true;
                var linked = GetLinkedServers(connInfo);
                for (int i = 0; i < linked.Count; i++)
                {
                    if (string.Equals(linked[i], name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            public bool IsLinkedServerDatabase(ScriptFactoryAccess.ConnectionInfo connInfo, string linkedServer, string database)
            {
                linkedServer = UnbracketSqlIdent(linkedServer);
                database = UnbracketSqlIdent(database);
                if (string.IsNullOrWhiteSpace(linkedServer) || string.IsNullOrWhiteSpace(database))
                    return false;
                foreach (var db in GetLinkedServerDatabases(connInfo, linkedServer))
                {
                    if (string.Equals(db, database, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            /// <summary>链接服务器目标库目录：内存 → 磁盘。缺失则后台四段名拉取。不查库。</summary>
            public MetadataCatalog GetOrBuildLinkedCatalog(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string linkedServer,
                string database)
            {
                linkedServer = UnbracketSqlIdent(linkedServer);
                database = UnbracketSqlIdent(database);
                if (string.IsNullOrWhiteSpace(linkedServer) || string.IsNullOrWhiteSpace(database))
                    return null;
                MetadataCacheRefreshService.Instance.EnsureLinkedServerCache(connInfo, linkedServer);
                string lsKey = LinkedCatalogKey(linkedServer, database);
                if (_cache.TryGetValue(lsKey, out var cached) && cached != null && (cached.IsIndexed || !cached.IsEmpty))
                    return cached;
                if (_cache.TryGetValue(Key(linkedServer, database), out cached) && cached != null && (cached.IsIndexed || !cached.IsEmpty))
                    return cached;
                var catalog = MetadataCacheStore.TryLoadCatalog(linkedServer, database);
                if (catalog == null)
                    return null;
                _cache[lsKey] = catalog;
                _cache[Key(linkedServer, database)] = catalog;
                return catalog;
            }

            /// <summary>后台刷新用：经四段名枚举链接服务器上的库。补全热路径不要调用。</summary>
            internal List<string> QueryLinkedServerDatabasesForCache(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string linkedServer,
                int commandTimeoutSeconds)
            {
                var list = new List<string>();
                linkedServer = UnbracketSqlIdent(linkedServer);
                if (string.IsNullOrWhiteSpace(connInfo?.FullConnectionString) || string.IsNullOrWhiteSpace(linkedServer))
                    return list;
                string prefix = FourPartPrefix(linkedServer, "master");
                string sql = string.Format(@"
SELECT d.name
FROM {0}.sys.databases d
WHERE d.state = 0
  AND d.name <> 'tempdb'
ORDER BY d.name;", prefix);
                try
                {
                    using (var conn = new SqlConnection(WithConnectTimeout(connInfo.FullConnectionString)))
                    {
                        conn.Open();
                        using (var cmd = Cmd(conn, sql, commandTimeoutSeconds))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                if (!reader.IsDBNull(0))
                                    list.Add(reader.GetString(0));
                            }
                        }
                    }
                }
                catch
                {
                }
                return list;
            }

            /// <summary>后台刷新用：经四段名构建链接服务器上某库目录。补全热路径不要调用。</summary>
            internal MetadataCatalog BuildLinkedCatalogForCache(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string linkedServer,
                string database,
                int commandTimeoutSeconds)
            {
                linkedServer = UnbracketSqlIdent(linkedServer);
                database = UnbracketSqlIdent(database);
                if (string.IsNullOrWhiteSpace(connInfo?.FullConnectionString)
                    || string.IsNullOrWhiteSpace(linkedServer)
                    || string.IsNullOrWhiteSpace(database))
                    return null;
                string prefix = FourPartPrefix(linkedServer, database);
                try
                {
                    using (var conn = new SqlConnection(WithConnectTimeout(connInfo.FullConnectionString)))
                    {
                        conn.Open();
                        var catalog = new MetadataCatalog
                        {
                            Server = linkedServer,
                            Database = database,
                            BuiltAt = DateTime.Now,
                            IsIndexed = true
                        };
                        LoadTablesAndViews(conn, catalog, prefix, commandTimeoutSeconds);
                        LoadRoutines(conn, catalog, prefix, commandTimeoutSeconds);
                        LoadSynonyms(conn, catalog, prefix, commandTimeoutSeconds);
                        return catalog;
                    }
                }
                catch
                {
                    return null;
                }
            }

            /// <summary>后台刷新用：查 sys.servers。补全热路径不要调用。</summary>
            internal List<string> QueryLinkedServersForCache(ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                var list = new List<string>();
                if (string.IsNullOrWhiteSpace(connInfo?.FullConnectionString))
                    return list;
                try
                {
                    using (var conn = new SqlConnection(WithConnectTimeout(connInfo.FullConnectionString)))
                    {
                        conn.Open();
                        using (var cmd = Cmd(conn, "SELECT name FROM sys.servers WHERE is_linked = 1 ORDER BY name"))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                if (!reader.IsDBNull(0))
                                    list.Add(reader.GetString(0));
                            }
                        }
                    }
                }
                catch
                {
                }
                return list;
            }

            private static bool IsLocalDatabaseName(ScriptFactoryAccess.ConnectionInfo connInfo, string name)
            {
                return Instance.ContainsDatabase(connInfo, name);
            }

            private static string FourPartPrefix(string linkedServer, string database)
            {
                return BracketSqlIdent(linkedServer) + "." + BracketSqlIdent(database);
            }

            internal static string BracketSqlIdent(string name)
            {
                if (string.IsNullOrEmpty(name))
                    return name;
                if (name.StartsWith("[", StringComparison.Ordinal))
                    return name;
                return "[" + name.Replace("]", "]]") + "]";
            }

            private MetadataCatalog BuildCatalog(ScriptFactoryAccess.ConnectionInfo connInfo, string database, int commandTimeoutSeconds = QueryTimeoutSeconds)
            {
                // 切到目标库的独立连接，不污染查询会话
                var target = ScriptFactoryAccess.CloneWithDatabase(connInfo, database);
                if (target == null || string.IsNullOrWhiteSpace(target.FullConnectionString))
                {
                    return null;
                }

                string connectionString = WithConnectTimeout(target.FullConnectionString);

                try
                {
                    using (var conn = new SqlConnection(connectionString))
                    {
                        conn.Open();
                        var catalog = new MetadataCatalog
                        {
                            Server = target.ServerName,
                            Database = database,
                            BuiltAt = DateTime.Now,
                            IsIndexed = true
                        };

                        LoadTablesAndViews(conn, catalog, null, commandTimeoutSeconds);
                        LoadRoutines(conn, catalog, null, commandTimeoutSeconds);
                        LoadSynonyms(conn, catalog, null, commandTimeoutSeconds);

                        return catalog;
                    }
                }
                catch
                {
                    // 超时 / 权限 / 网络异常 → 空 Catalog，调用方降级
                    return null;
                }
            }

            private static string WithConnectTimeout(string connectionString)
            {
                try
                {
                    var b = new SqlConnectionStringBuilder(connectionString)
                    {
                        ConnectTimeout = ConnectTimeoutSeconds
                    };
                    return b.ConnectionString;
                }
                catch
                {
                    return connectionString;
                }
            }

            private static SqlCommand Cmd(SqlConnection conn, string sql, int timeoutSeconds = QueryTimeoutSeconds)
            {
                return new SqlCommand(sql, conn) { CommandTimeout = timeoutSeconds };
            }

            private static string QualifySys(string fourPartPrefix, string objectName)
            {
                return string.IsNullOrEmpty(fourPartPrefix) ? objectName : fourPartPrefix + "." + objectName;
            }

            private void LoadTablesAndViews(SqlConnection conn, MetadataCatalog catalog, string fourPartPrefix = null, int timeoutSeconds = QueryTimeoutSeconds)
            {
                string objects = QualifySys(fourPartPrefix, "sys.objects");
                string schemas = QualifySys(fourPartPrefix, "sys.schemas");
                string columns = QualifySys(fourPartPrefix, "sys.columns");
                string types = QualifySys(fourPartPrefix, "sys.types");
                string extProps = QualifySys(fourPartPrefix, "sys.extended_properties");
                string indexColumns = QualifySys(fourPartPrefix, "sys.index_columns");
                string indexes = QualifySys(fourPartPrefix, "sys.indexes");
                string sql = $@"
SELECT s.name AS schema_name,
       o.name AS object_name,
       o.type AS object_type,
       c.name AS column_name,
       t.name AS type_name,
       c.max_length,
       c.precision,
       c.scale,
       c.is_nullable,
       OBJECT_DEFINITION(c.default_object_id) AS default_value,
       ep_col.value AS column_description,
       ep_obj.value AS object_description,
       c.is_identity,
       CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END AS is_primary_key
FROM {objects} o
JOIN {schemas} s ON o.schema_id = s.schema_id
JOIN {columns} c ON c.object_id = o.object_id
JOIN {types} t ON c.user_type_id = t.user_type_id
LEFT JOIN {extProps} ep_col
       ON ep_col.major_id = c.object_id AND ep_col.minor_id = c.column_id AND ep_col.name = 'MS_Description'
LEFT JOIN {extProps} ep_obj
       ON ep_obj.major_id = o.object_id AND ep_obj.minor_id = 0 AND ep_obj.name = 'MS_Description'
LEFT JOIN (
    SELECT ic.object_id, ic.column_id
    FROM {indexColumns} ic
    JOIN {indexes} i ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND i.is_primary_key = 1
) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0
ORDER BY s.name, o.name, c.column_id;";

                try
                {
                    using (var cmd = Cmd(conn, sql, timeoutSeconds))
                    using (var reader = cmd.ExecuteReader())
                    {
                        TableColumnInfo current = null;
                        while (reader.Read())
                        {
                            string schema = reader.IsDBNull(0) ? null : reader.GetString(0);
                            string name = reader.IsDBNull(1) ? null : reader.GetString(1);
                            string type = reader.IsDBNull(2) ? null : reader.GetString(2);
                            string colName = reader.IsDBNull(3) ? null : reader.GetString(3);
                            string typeName = reader.IsDBNull(4) ? null : reader.GetString(4);
                            short maxLength = reader.IsDBNull(5) ? (short)0 : reader.GetInt16(5);
                            byte precision = reader.IsDBNull(6) ? (byte)0 : reader.GetByte(6);
                            byte scale = reader.IsDBNull(7) ? (byte)0 : reader.GetByte(7);
                            bool nullable = !reader.IsDBNull(8) && reader.GetBoolean(8);
                            string defaultValue = reader.IsDBNull(9) ? null : reader.GetString(9);
                            string colDesc = reader.IsDBNull(10) ? null : reader.GetString(10);
                            string objDesc = reader.IsDBNull(11) ? null : reader.GetString(11);
                            bool isIdentity = !reader.IsDBNull(12) && reader.GetBoolean(12);
                            bool isPrimaryKey = !reader.IsDBNull(13) && reader.GetInt32(13) == 1;

                            if (current == null ||
                                !string.Equals(current.Schema, schema, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase))
                            {
                                bool isView = string.Equals(type?.Trim(), "V", StringComparison.OrdinalIgnoreCase);
                                current = new TableColumnInfo
                                {
                                    Schema = schema,
                                    Name = name,
                                    Description = objDesc,
                                    IsView = isView
                                };
                                if (isView)
                                {
                                    catalog.Views.Add(current);
                                }
                                else
                                {
                                    catalog.Tables.Add(current);
                                }
                            }

                            current.Columns.Add(new ColumnInfo
                            {
                                Name = colName,
                                DataType = FormatDataType(typeName, maxLength, precision, scale),
                                Nullable = nullable,
                                DefaultValue = defaultValue,
                                Description = colDesc,
                                IsIdentity = isIdentity,
                                IsPrimaryKey = isPrimaryKey
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 表/视图失败不阻塞其余加载
                    Logger.Warn(ex, "LoadTablesAndViews failed for {0}", catalog.Database);
                }

                LoadIndexes(conn, catalog, fourPartPrefix, timeoutSeconds);
            }

            private void LoadIndexes(SqlConnection conn, MetadataCatalog catalog, string fourPartPrefix = null, int timeoutSeconds = QueryTimeoutSeconds)
            {
                string objects = QualifySys(fourPartPrefix, "sys.objects");
                string schemas = QualifySys(fourPartPrefix, "sys.schemas");
                string indexes = QualifySys(fourPartPrefix, "sys.indexes");
                string indexColumns = QualifySys(fourPartPrefix, "sys.index_columns");
                string columns = QualifySys(fourPartPrefix, "sys.columns");
                string sql = $@"
SELECT s.name AS schema_name,
       o.name AS object_name,
       i.name AS index_name,
       i.is_unique,
       i.is_primary_key,
       c.name AS column_name,
       ic.key_ordinal
FROM {objects} o
JOIN {schemas} s ON o.schema_id = s.schema_id
JOIN {indexes} i ON i.object_id = o.object_id AND i.type > 0 AND i.is_hypothetical = 0
JOIN {indexColumns} ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
JOIN {columns} c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0 AND i.name IS NOT NULL
ORDER BY s.name, o.name, i.index_id, ic.key_ordinal;";

                try
                {
                    using (var cmd = Cmd(conn, sql, timeoutSeconds))
                    using (var reader = cmd.ExecuteReader())
                    {
                        IndexInfo current = null;
                        string curSchema = null;
                        string curTable = null;
                        string curIndex = null;
                        while (reader.Read())
                        {
                            string schema = reader.IsDBNull(0) ? null : reader.GetString(0);
                            string table = reader.IsDBNull(1) ? null : reader.GetString(1);
                            string indexName = reader.IsDBNull(2) ? null : reader.GetString(2);
                            bool isUnique = !reader.IsDBNull(3) && reader.GetBoolean(3);
                            bool isPk = !reader.IsDBNull(4) && reader.GetBoolean(4);
                            string colName = reader.IsDBNull(5) ? null : reader.GetString(5);
                            if (string.IsNullOrEmpty(indexName) || string.IsNullOrEmpty(colName)) continue;

                            if (current == null ||
                                !string.Equals(curSchema, schema, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(curTable, table, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(curIndex, indexName, StringComparison.OrdinalIgnoreCase))
                            {
                                var tableInfo = catalog.FindTableOrView(schema, table);
                                if (tableInfo == null) continue;
                                current = new IndexInfo
                                {
                                    Name = indexName,
                                    IsUnique = isUnique,
                                    IsPrimaryKey = isPk
                                };
                                tableInfo.Indexes.Add(current);
                                curSchema = schema;
                                curTable = table;
                                curIndex = indexName;
                            }
                            current.Columns.Add(colName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "LoadIndexes failed for {0}", catalog.Database);
                }
            }

            private void LoadRoutines(SqlConnection conn, MetadataCatalog catalog, string fourPartPrefix = null, int timeoutSeconds = QueryTimeoutSeconds)
            {
                // 补全只需名/参数类型；不拉 sql_modules / OBJECT_DEFINITION（大库 5s 必超时 → 有表无过程）
                string objects = QualifySys(fourPartPrefix, "sys.objects");
                string schemas = QualifySys(fourPartPrefix, "sys.schemas");
                string parameters = QualifySys(fourPartPrefix, "sys.parameters");
                string types = QualifySys(fourPartPrefix, "sys.types");
                string extProps = QualifySys(fourPartPrefix, "sys.extended_properties");
                string sql = $@"
SELECT s.name AS schema_name,
       o.name AS object_name,
       o.type AS object_type,
       p.parameter_id,
       p.name AS param_name,
       t.name AS type_name,
       p.max_length,
       p.precision,
       p.scale,
       p.is_output,
       p.has_default_value,
       ep.value AS object_description
FROM {objects} o
JOIN {schemas} s ON o.schema_id = s.schema_id
LEFT JOIN {parameters} p ON p.object_id = o.object_id
LEFT JOIN {types} t ON p.user_type_id = t.user_type_id
LEFT JOIN {extProps} ep
       ON ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
WHERE o.type IN ('P','FN','TF','IF') AND o.is_ms_shipped = 0
ORDER BY s.name, o.name, p.parameter_id;";

                try
                {
                    using (var cmd = Cmd(conn, sql, timeoutSeconds))
                    using (var reader = cmd.ExecuteReader())
                    {
                        RoutineInfo current = null;
                        while (reader.Read())
                        {
                            string schema = reader.IsDBNull(0) ? null : reader.GetString(0);
                            string name = reader.IsDBNull(1) ? null : reader.GetString(1);
                            string type = reader.IsDBNull(2) ? null : reader.GetString(2);

                            if (current == null ||
                                !string.Equals(current.Schema, schema, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase))
                            {
                                string objDesc = reader.IsDBNull(11) ? null : reader.GetString(11);
                                current = new RoutineInfo
                                {
                                    Schema = schema,
                                    Name = name,
                                    Kind = RoutineKindFromType(type),
                                    Description = objDesc
                                };
                                AddRoutine(catalog, current);
                            }

                            // parameter_id = 0 表示无参数例程占位行（SQL Server 行为），跳过
                            if (!reader.IsDBNull(3) && reader.GetInt32(3) > 0)
                            {
                                string paramName = reader.IsDBNull(4) ? null : reader.GetString(4);
                                string typeName = reader.IsDBNull(5) ? null : reader.GetString(5);
                                short maxLength = reader.IsDBNull(6) ? (short)0 : reader.GetInt16(6);
                                byte precision = reader.IsDBNull(7) ? (byte)0 : reader.GetByte(7);
                                byte scale = reader.IsDBNull(8) ? (byte)0 : reader.GetByte(8);
                                bool isOutput = !reader.IsDBNull(9) && reader.GetBoolean(9);
                                bool hasDefault = !reader.IsDBNull(10) && reader.GetBoolean(10);

                                current.Parameters.Add(new RoutineParam
                                {
                                    Name = paramName,
                                    DataType = FormatDataType(typeName, maxLength, precision, scale),
                                    IsOutput = isOutput,
                                    HasDefault = hasDefault
                                });
                            }
                        }
                    }
                    catalog.RoutinesLoaded = true;
                }
                catch (Exception ex)
                {
                    catalog.RoutinesLoaded = false;
                    Logger.Warn(ex, "LoadRoutines failed for {0}", catalog.Database);
                }
            }

            private void LoadSynonyms(SqlConnection conn, MetadataCatalog catalog, string fourPartPrefix = null, int timeoutSeconds = QueryTimeoutSeconds)
            {
                string synonyms = QualifySys(fourPartPrefix, "sys.synonyms");
                string schemas = QualifySys(fourPartPrefix, "sys.schemas");
                string extProps = QualifySys(fourPartPrefix, "sys.extended_properties");
                string sql = $@"
SELECT s.name AS schema_name,
       o.name AS object_name,
       ep.value AS object_description
FROM {synonyms} o
JOIN {schemas} s ON o.schema_id = s.schema_id
LEFT JOIN {extProps} ep
       ON ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
WHERE o.is_ms_shipped = 0
ORDER BY s.name, o.name;";

                try
                {
                    using (var cmd = Cmd(conn, sql, timeoutSeconds))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            catalog.Synonyms.Add(new DatabaseObjectInfo
                            {
                                Schema = reader.IsDBNull(0) ? null : reader.GetString(0),
                                Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                                Description = reader.IsDBNull(2) ? null : reader.GetString(2)
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "LoadSynonyms failed for {0}", catalog.Database);
                }
            }

            private static void AddRoutine(MetadataCatalog catalog, RoutineInfo info)
            {
                switch (info.Kind)
                {
                    case RoutineKind.Procedure:
                        catalog.Procedures.Add(info);
                        break;
                    case RoutineKind.ScalarFunction:
                        catalog.ScalarFunctions.Add(info);
                        break;
                    case RoutineKind.TableFunction:
                        catalog.TableFunctions.Add(info);
                        break;
                }
            }

            private static RoutineKind RoutineKindFromType(string type)
            {
                if (string.IsNullOrEmpty(type)) return RoutineKind.Procedure;
                switch (type.ToUpperInvariant())
                {
                    case "FN": return RoutineKind.ScalarFunction;
                    case "TF":
                    case "IF": return RoutineKind.TableFunction;
                    default: return RoutineKind.Procedure;
                }
            }

            /// <summary>格式化 SQL 数据类型（含长度/精度）。</summary>
            public static string FormatDataType(string typeName, short maxLength, byte precision, byte scale)
            {
                if (string.IsNullOrEmpty(typeName)) return string.Empty;
                string t = typeName.ToLowerInvariant();

                switch (t)
                {
                    case "varchar":
                    case "char":
                    case "varbinary":
                    case "binary":
                        return maxLength == -1 ? t + "(max)" : t + "(" + maxLength + ")";
                    case "nvarchar":
                    case "nchar":
                        // nchar/nvarchar max_length 是字节数，字符数 = max_length/2
                        return maxLength == -1 ? t + "(max)" : t + "(" + (maxLength / 2) + ")";
                    case "decimal":
                    case "numeric":
                        return t + "(" + precision + "," + scale + ")";
                    case "float":
                        return precision == 53 ? t : t + "(" + precision + ")";
                    case "datetime2":
                    case "datetimeoffset":
                    case "time":
                        return t + "(" + scale + ")";
                    default:
                        return t;
                }
            }

            /// <summary>手动刷新单个库缓存（Ctrl+Shift+R）。</summary>
            public void Invalidate(string server, string database)
            {
                if (string.IsNullOrWhiteSpace(server)) return;
                _cache.TryRemove(Key(server, database), out _);
            }

            /// <summary>清空全部缓存。</summary>
            public void InvalidateAll()
            {
                _cache.Clear();
                _databaseListCache.Clear();
                _linkedServerCache.Clear();
                _tableColumnCache.Clear();
                _memoryLoadedServers.Clear();
            }

            private static readonly Regex DdlRegex = new Regex(
                @"\b(?:CREATE|ALTER|DROP)\s+(?:OR\s+ALTER\s+)?(?:TABLE|VIEW|PROCEDURE|PROC|FUNCTION)\b|\bTRUNCATE\s+TABLE\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

            /// <summary>SQL 文本是否含表结构变更（CREATE/ALTER/DROP TABLE）。</summary>
            internal static bool ContainsDdl(string sql)
            {
                return !string.IsNullOrEmpty(sql) && DdlRegex.IsMatch(sql);
            }

            /// <summary>
            /// 文档含 DDL 时使目标库缓存失效（内存 + 该库磁盘文件）。
            /// 不删除服务器 meta.json，否则 7 天刷新会被重置、下次启动会整机重拉。
            /// </summary>
            public void InvalidateDdlTargets(ScriptFactoryAccess.ConnectionInfo connInfo, string sql)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                    return;
                if (!ContainsDdl(sql))
                    return;
                // 只失效 DDL 实际修改的目标库（三段名目标 + 当前/USE 库），
                // 不失效仅被查询引用的库，避免 CREATE PROC 体内的 SELECT ... FROM otherdb..t 误伤 otherdb。
                string current = string.IsNullOrWhiteSpace(connInfo.Database) ? "master" : UnbracketSqlIdent(connInfo.Database);
                foreach (var db in ExtractDdlTargetDatabases(sql, current))
                {
                    Invalidate(connInfo.ServerName, db);
                    MetadataCacheStore.DeleteCatalog(connInfo.ServerName, db);
                }
            }

            /// <summary>常用系统对象（includeSystemObjects 开启时追加到候选）。</summary>
            public static readonly string[] CommonSystemSchemas = { "sys", "INFORMATION_SCHEMA" };

            public static readonly string[] CommonSystemObjects =
            {
                "sys.tables", "sys.views", "sys.columns", "sys.objects", "sys.schemas",
                "sys.databases", "sys.types", "sys.indexes", "sys.foreign_keys",
                "sys.sql_modules", "sys.parameters", "sys.procedures", "sys.synonyms",
                "sys.dm_exec_requests", "sys.dm_exec_sessions", "sys.dm_exec_sql_text",
                "sys.dm_os_performance_counters", "sys.dm_os_wait_stats",
                "sys.extended_properties", "sys.identity_columns", "sys.default_constraints",
                "sys.computed_columns", "sys.triggers", "sys.server_principals",
                "sys.database_principals", "sys.sysprocesses",
                "INFORMATION_SCHEMA.TABLES", "INFORMATION_SCHEMA.COLUMNS",
                "INFORMATION_SCHEMA.VIEWS", "INFORMATION_SCHEMA.ROUTINES",
                "INFORMATION_SCHEMA.PARAMETERS", "INFORMATION_SCHEMA.KEY_COLUMN_USAGE",
                "INFORMATION_SCHEMA.TABLE_CONSTRAINTS",
                "sp_help", "sp_who", "sp_who2", "sp_lock", "sp_databases",
                "sp_tables", "sp_columns", "sp_helptext", "sp_rename",
                "sp_executesql", "sp_getapplock", "sp_releaseapplock"
            };
        }
    }
}
