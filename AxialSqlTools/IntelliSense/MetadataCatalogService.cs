using Microsoft.Data.SqlClient;
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
        /// 使用独立 SqlConnection（CloneWithDatabase），不复用查询编辑器会话，避免 USE 污染。
        /// 任何失败返回空 Catalog，不拖垮补全。
        /// </summary>
        public class MetadataCatalogService
        {
            private const int QueryTimeoutSeconds = 5;
            private const int ConnectTimeoutSeconds = 5;

            private static readonly Lazy<MetadataCatalogService> _instance =
                new Lazy<MetadataCatalogService>(() => new MetadataCatalogService());

            public static MetadataCatalogService Instance => _instance.Value;

            private readonly ConcurrentDictionary<string, MetadataCatalog> _cache =
                new ConcurrentDictionary<string, MetadataCatalog>(StringComparer.OrdinalIgnoreCase);

            private readonly ConcurrentDictionary<string, List<string>> _linkedServerCache =
                new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            private readonly ConcurrentDictionary<string, List<string>> _linkedDatabaseCache =
                new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            private readonly ConcurrentDictionary<string, TableColumnInfo> _tableColumnCache =
                new ConcurrentDictionary<string, TableColumnInfo>(StringComparer.OrdinalIgnoreCase);

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

            private static string LinkedDatabaseListKey(string localServer, string linkedServer)
            {
                return (localServer ?? string.Empty) + "|ls|" + (linkedServer ?? string.Empty);
            }

            /// <summary>取或构建目录。connInfo 为当前活动连接；dbOverride 用于三段名跨库。</summary>
            public MetadataCatalog GetOrBuildCatalog(ScriptFactoryAccess.ConnectionInfo connInfo, string dbOverride = null)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                {
                    return null;
                }

                string database = string.IsNullOrWhiteSpace(dbOverride) ? (connInfo.Database ?? "master") : dbOverride;
                if (string.IsNullOrWhiteSpace(database))
                {
                    database = "master";
                }

                string key = Key(connInfo.ServerName, database);
                if (_cache.TryGetValue(key, out var cached) && cached != null && !cached.IsEmpty
                    && string.Equals(cached.Database, database, StringComparison.OrdinalIgnoreCase))
                {
                    return cached;
                }

                _cache.TryRemove(key, out _);

                var catalog = BuildCatalog(connInfo, database);
                if (catalog != null)
                {
                    _cache[key] = catalog;
                }
                return catalog;
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
       OBJECT_DEFINITION(c.default_object_id), ep.value
FROM sys.columns c
JOIN sys.types t ON c.user_type_id = t.user_type_id
JOIN sys.objects o ON c.object_id = o.object_id
JOIN sys.schemas s ON o.schema_id = s.schema_id
LEFT JOIN sys.extended_properties ep
       ON ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
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
                                        Description = reader.IsDBNull(7) ? null : reader.GetString(7)
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

            /// <summary>链接服务器上的库列表（四段名 [server].master.sys.databases）。</summary>
            public List<string> GetLinkedServerDatabases(ScriptFactoryAccess.ConnectionInfo connInfo, string linkedServer)
            {
                var list = new List<string>();
                if (connInfo == null || string.IsNullOrWhiteSpace(linkedServer))
                    return list;

                string cacheKey = LinkedDatabaseListKey(connInfo.ServerName, linkedServer);
                if (_linkedDatabaseCache.TryGetValue(cacheKey, out var cached) && cached != null)
                    return new List<string>(cached);

                if (string.IsNullOrWhiteSpace(connInfo.FullConnectionString))
                    return list;

                string prefix = FourPartPrefix(linkedServer, "master");
                const string sql = @"
SELECT d.name
FROM {0}.sys.databases d
WHERE d.state = 0
  AND d.name <> 'tempdb'
ORDER BY d.name;";

                try
                {
                    using (var conn = new SqlConnection(WithConnectTimeout(connInfo.FullConnectionString)))
                    {
                        conn.Open();
                        using (var cmd = Cmd(conn, string.Format(sql, prefix)))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                if (!reader.IsDBNull(0))
                                    list.Add(reader.GetString(0));
                            }
                        }
                    }
                    _linkedDatabaseCache[cacheKey] = list;
                }
                catch
                {
                }
                return list;
            }

            /// <summary>已注册的链接服务器 + 常见 IP 形态（用于 [server]. 补全）。</summary>
            public List<string> GetLinkedServers(ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                    return new List<string>();

                if (_linkedServerCache.TryGetValue(connInfo.ServerName, out var cached) && cached != null)
                    return new List<string>(cached);

                var list = LoadLinkedServers(connInfo);
                _linkedServerCache[connInfo.ServerName] = list;
                return new List<string>(list);
            }

            public bool IsLinkedServerName(ScriptFactoryAccess.ConnectionInfo connInfo, string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return false;
                if (IsLocalDatabaseName(connInfo, name))
                    return false;
                if (IPv4Regex.IsMatch(name))
                    return true;
                foreach (var server in GetLinkedServers(connInfo))
                {
                    if (string.Equals(server, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            public bool IsLinkedServerDatabase(ScriptFactoryAccess.ConnectionInfo connInfo, string linkedServer, string database)
            {
                if (string.IsNullOrWhiteSpace(linkedServer) || string.IsNullOrWhiteSpace(database))
                    return false;
                foreach (var db in GetLinkedServerDatabases(connInfo, linkedServer))
                {
                    if (string.Equals(db, database, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            /// <summary>链接服务器目标库的表/视图目录。</summary>
            public MetadataCatalog GetOrBuildLinkedCatalog(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string linkedServer,
                string database)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(linkedServer) || string.IsNullOrWhiteSpace(database))
                    return null;

                string key = LinkedCatalogKey(linkedServer, database);
                if (_cache.TryGetValue(key, out var cached) && cached != null && !cached.IsEmpty)
                    return cached;

                var catalog = BuildLinkedCatalog(connInfo, linkedServer, database);
                if (catalog != null)
                    _cache[key] = catalog;
                return catalog;
            }

            private List<string> LoadLinkedServers(ScriptFactoryAccess.ConnectionInfo connInfo)
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

            private MetadataCatalog BuildLinkedCatalog(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string linkedServer,
                string database)
            {
                if (string.IsNullOrWhiteSpace(connInfo?.FullConnectionString))
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
                            BuiltAt = DateTime.Now
                        };
                        LoadTablesAndViews(conn, catalog, prefix);
                        LoadRoutines(conn, catalog, prefix);
                        LoadSynonyms(conn, catalog, prefix);
                        return catalog;
                    }
                }
                catch
                {
                    return null;
                }
            }

            private static bool IsLocalDatabaseName(ScriptFactoryAccess.ConnectionInfo connInfo, string name)
            {
                if (connInfo == null || string.IsNullOrWhiteSpace(name))
                    return false;
                try
                {
                    foreach (var db in ScriptFactoryAccess.GetDatabases(connInfo))
                    {
                        if (string.Equals(db, name, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch
                {
                }
                return false;
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

            private MetadataCatalog BuildCatalog(ScriptFactoryAccess.ConnectionInfo connInfo, string database)
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
                            BuiltAt = DateTime.Now
                        };

                        LoadTablesAndViews(conn, catalog);
                        LoadRoutines(conn, catalog);
                        LoadSynonyms(conn, catalog);

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

            private static SqlCommand Cmd(SqlConnection conn, string sql)
            {
                return new SqlCommand(sql, conn) { CommandTimeout = QueryTimeoutSeconds };
            }

            private static string QualifySys(string fourPartPrefix, string objectName)
            {
                return string.IsNullOrEmpty(fourPartPrefix) ? objectName : fourPartPrefix + "." + objectName;
            }

            private void LoadTablesAndViews(SqlConnection conn, MetadataCatalog catalog, string fourPartPrefix = null)
            {
                string objects = QualifySys(fourPartPrefix, "sys.objects");
                string schemas = QualifySys(fourPartPrefix, "sys.schemas");
                string columns = QualifySys(fourPartPrefix, "sys.columns");
                string types = QualifySys(fourPartPrefix, "sys.types");
                string extProps = QualifySys(fourPartPrefix, "sys.extended_properties");
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
       ep_obj.value AS object_description
FROM {objects} o
JOIN {schemas} s ON o.schema_id = s.schema_id
JOIN {columns} c ON c.object_id = o.object_id
JOIN {types} t ON c.user_type_id = t.user_type_id
LEFT JOIN {extProps} ep_col
       ON ep_col.major_id = c.object_id AND ep_col.minor_id = c.column_id AND ep_col.name = 'MS_Description'
LEFT JOIN {extProps} ep_obj
       ON ep_obj.major_id = o.object_id AND ep_obj.minor_id = 0 AND ep_obj.name = 'MS_Description'
WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0
ORDER BY s.name, o.name, c.column_id;";

                try
                {
                    using (var cmd = Cmd(conn, sql))
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

                            if (current == null ||
                                !string.Equals(current.Schema, schema, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase))
                            {
                                current = new TableColumnInfo
                                {
                                    Schema = schema,
                                    Name = name,
                                    Description = objDesc
                                };
                                if (string.Equals(type, "V", StringComparison.OrdinalIgnoreCase))
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
                                Description = colDesc
                            });
                        }
                    }
                }
                catch
                {
                    // 表/视图失败不阻塞其余加载
                }
            }

            private void LoadRoutines(SqlConnection conn, MetadataCatalog catalog, string fourPartPrefix = null)
            {
                string objects = QualifySys(fourPartPrefix, "sys.objects");
                string schemas = QualifySys(fourPartPrefix, "sys.schemas");
                string parameters = QualifySys(fourPartPrefix, "sys.parameters");
                string types = QualifySys(fourPartPrefix, "sys.types");
                string modules = QualifySys(fourPartPrefix, "sys.sql_modules");
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
       OBJECT_DEFINITION(p.default_object_id) AS default_value,
       m.definition AS object_definition,
       ep.value AS object_description
FROM {objects} o
JOIN {schemas} s ON o.schema_id = s.schema_id
LEFT JOIN {parameters} p ON p.object_id = o.object_id
LEFT JOIN {types} t ON p.user_type_id = t.user_type_id
LEFT JOIN {modules} m ON m.object_id = o.object_id
LEFT JOIN {extProps} ep
       ON ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
WHERE o.type IN ('P','FN','TF','IF') AND o.is_ms_shipped = 0
ORDER BY s.name, o.name, p.parameter_id;";

                try
                {
                    using (var cmd = Cmd(conn, sql))
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
                                string definition = reader.IsDBNull(12) ? null : reader.GetString(12);
                                string objDesc = reader.IsDBNull(13) ? null : reader.GetString(13);
                                current = new RoutineInfo
                                {
                                    Schema = schema,
                                    Name = name,
                                    Kind = RoutineKindFromType(type),
                                    Definition = definition,
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
                                string defValue = reader.IsDBNull(11) ? null : reader.GetString(11);

                                current.Parameters.Add(new RoutineParam
                                {
                                    Name = paramName,
                                    DataType = FormatDataType(typeName, maxLength, precision, scale),
                                    IsOutput = isOutput,
                                    HasDefault = hasDefault,
                                    DefaultValue = defValue
                                });
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            private void LoadSynonyms(SqlConnection conn, MetadataCatalog catalog, string fourPartPrefix = null)
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
                    using (var cmd = Cmd(conn, sql))
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
                catch
                {
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
