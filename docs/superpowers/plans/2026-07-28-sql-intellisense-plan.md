# SQL IntelliSense Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add complete SQL IntelliSense (auto-completion + hover tooltip + parameter hints) to AxialSqlTools SSMS 22 extension, replacing SSMS built-in IntelliSense.

**Architecture:** WPF-based completion popup driven by ScriptDOM AST context analysis; metadata cached in-memory per (Server, Database) from sys.* queries; keyboard interception via existing KeypressCommandFilter; mouse hover tooltip via IVsTextViewEvents.

**Tech Stack:** C# / WPF / .NET Framework 4.7.2 / VSSDK (SSMS 22) / ScriptDOM (TSql170Parser) / Microsoft.Data.SqlClient

## Global Constraints

- Target SSMS 22, amd64, .NET Framework 4.7.2
- New files in `AxialSqlTools/IntelliSense/`, MUST register in `AxialSqlTools.csproj` (Compile for .cs, Page for .xaml)
- Reuse `ScriptFactoryAccess` for connections, `SettingsManager` for config, `KeypressCommandFilter` for keyboard hooks
- Any IntelliSense failure MUST NOT crash editor, lose text, or crash Package — all paths wrapped in try/catch
- Use NLog: `private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();`
- Settings stored in `%APPDATA%\AxialSqlTools\settings.json` under `"intelliSense"` key
- Follow existing code conventions: no unnecessary blank lines, preserve existing comments, minimal scope changes

---

### Task 1: Data Models (MetadataModels.cs + CompletionItem.cs)

**Files:**
- Create: `AxialSqlTools/IntelliSense/MetadataModels.cs`
- Create: `AxialSqlTools/IntelliSense/CompletionItem.cs`
- Modify: `AxialSqlTools/AxialSqlTools.csproj` (register both)

**Interfaces:**
- Consumes: nothing
- Produces: `MetadataCatalog`, `MetadataTable`, `MetadataColumn`, `MetadataRoutine`, `MetadataParameter`, `MetadataDatabase`, `CompletionItem`, `CompletionContextType`

- [ ] **Step 1: Create MetadataModels.cs**

```csharp
using System.Collections.Generic;

namespace AxialSqlTools
{
    internal sealed class MetadataColumn
    {
        public string ColumnName { get; set; }
        public string DataType { get; set; }
        public int MaxLength { get; set; }
        public int Precision { get; set; }
        public int Scale { get; set; }
        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
        public string DefaultValue { get; set; }
        public int ColumnId { get; set; }
        public string Description { get; set; }
    }

    internal sealed class MetadataTable
    {
        public string SchemaName { get; set; }
        public string TableName { get; set; }
        public string ObjectType { get; set; } // "TABLE", "VIEW"
        public string Description { get; set; }
        public string CreateScript { get; set; }
        public List<MetadataColumn> Columns { get; set; } = new List<MetadataColumn>();
    }

    internal sealed class MetadataParameter
    {
        public string ParameterName { get; set; }
        public string DataType { get; set; }
        public int MaxLength { get; set; }
        public int Precision { get; set; }
        public int Scale { get; set; }
        public bool IsOutput { get; set; }
        public bool HasDefaultValue { get; set; }
        public string DefaultValue { get; set; }
        public int ParameterId { get; set; }
    }

    internal sealed class MetadataRoutine
    {
        public string SchemaName { get; set; }
        public string RoutineName { get; set; }
        public string ObjectType { get; set; } // "PROCEDURE", "FUNCTION"
        public string Description { get; set; }
        public string CreateScript { get; set; }
        public string ReturnType { get; set; } // for functions
        public List<MetadataParameter> Parameters { get; set; } = new List<MetadataParameter>();
    }

    internal sealed class MetadataDatabase
    {
        public string DatabaseName { get; set; }
    }

    internal sealed class MetadataCatalog
    {
        public string Server { get; set; }
        public string Database { get; set; }
        public System.DateTime BuiltAtUtc { get; set; }
        public List<MetadataTable> TablesAndViews { get; set; } = new List<MetadataTable>();
        public List<MetadataRoutine> Routines { get; set; } = new List<MetadataRoutine>();
        public List<MetadataDatabase> Databases { get; set; } = new List<MetadataDatabase>();
        public bool IncludeSystemObjects { get; set; }
    }
}
```

- [ ] **Step 2: Create CompletionItem.cs**

```csharp
using System;

namespace AxialSqlTools
{
    internal enum CompletionContextType
    {
        Unknown,
        StatementStart,       // new batch / GO / blank line → keywords
        CreateAfter,          // CREATE <here>
        AlterAfter,           // ALTER <here>
        FromClause,           // FROM / JOIN / , in FROM
        SelectElements,       // SELECT <here> (before FROM)
        ExecAfter,            // EXEC / EXECUTE <here>
        InsertIntoAfter,      // INSERT INTO <here>
        UpdateAfter,          // UPDATE <here>
        DeleteFromAfter,      // DELETE FROM <here>
        WhereClause,          // WHERE / ON / HAVING / AND / OR
        UpdateSetClause,      // SET (inside UPDATE)
        OrderByClause,        // ORDER BY <here>
        GroupByClause,        // GROUP BY <here>
        DotAfterTable,        // tableAlias.<here>
        UseAfter,             // USE <here>
        VariableAfter,        // @<here>
        ParameterHint         // func( — parameter list
    }

    internal enum CompletionItemType
    {
        Keyword,
        Table,
        View,
        Procedure,
        Function,
        Column,
        Database,
        Schema,
        LocalVariable,
        CteName,
        TempTable,
        TableVariable
    }

    internal sealed class CompletionItem
    {
        public string DisplayText { get; set; }     // what shows in the popup list
        public string InsertText { get; set; }       // what gets inserted into editor
        public CompletionItemType ItemType { get; set; }
        public string SchemaName { get; set; }
        public string ObjectName { get; set; }
        public string Description { get; set; }      // short inline description
        public string DetailText { get; set; }       // full detail for right panel (CREATE script)
        public string DatabaseName { get; set; }      // for cross-database objects
        public int PrefixMatchLength { get; set; }    // for sorting: how many prefix chars matched
        public bool IsSystemObject { get; set; }
    }
}
```

- [ ] **Step 3: Register in csproj**

```xml
<!-- Add after the last Compile entry in the relevant <ItemGroup> -->
<Compile Include="IntelliSense\MetadataModels.cs" />
<Compile Include="IntelliSense\CompletionItem.cs" />
```

- [ ] **Step 4: Build to verify**

```powershell
msbuild AxialSqlTools\AxialSqlTools.csproj /p:Configuration=Release /t:Rebuild
```

Expected: Build succeeds, no compilation errors.

- [ ] **Step 5: Commit**

```bash
git add AxialSqlTools/IntelliSense/MetadataModels.cs AxialSqlTools/IntelliSense/CompletionItem.cs AxialSqlTools/AxialSqlTools.csproj
git commit -m "feat(intellisense): add data models for metadata catalog and completion items"
```

---

### Task 2: Metadata Catalog Service

**Files:**
- Create: `AxialSqlTools/IntelliSense/MetadataCatalogService.cs`
- Modify: `AxialSqlTools/AxialSqlTools.csproj` (register)

**Interfaces:**
- Consumes: `MetadataModels.cs`, `CompletionItem.cs`, `ScriptFactoryAccess.ConnectionInfo`
- Produces: `MetadataCatalogService` (static singleton), `GetOrBuildCatalogAsync(ConnectionInfo, database, includeSystemObjects, CancellationToken)` → `Task<MetadataCatalog>`, `Invalidate(string server, string database)`, `Clear()`

- [ ] **Step 1: Write MetadataCatalogService.cs**

```csharp
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AxialSqlTools
{
    internal static class MetadataCatalogService
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();
        private static readonly ConcurrentDictionary<string, MetadataCatalog> _catalogs
            = new ConcurrentDictionary<string, MetadataCatalog>(StringComparer.OrdinalIgnoreCase);

        private static string BuildCacheKey(string server, string database)
        {
            return $"{(server ?? "").Trim().ToUpperInvariant()}|{(database ?? "").Trim().ToUpperInvariant()}";
        }

        public static bool TryGetCatalog(string server, string database, out MetadataCatalog catalog)
        {
            return _catalogs.TryGetValue(BuildCacheKey(server, database), out catalog);
        }

        public static void Invalidate(string server, string database)
        {
            _catalogs.TryRemove(BuildCacheKey(server, database), out _);
        }

        public static void Clear()
        {
            _catalogs.Clear();
        }

        public static async Task<MetadataCatalog> GetOrBuildCatalogAsync(
            ScriptFactoryAccess.ConnectionInfo connectionInfo,
            string database,
            bool includeSystemObjects,
            CancellationToken cancellationToken)
        {
            if (connectionInfo == null || string.IsNullOrWhiteSpace(connectionInfo.FullConnectionString))
                return null;

            string databaseName = database ?? connectionInfo.Database;
            if (string.IsNullOrWhiteSpace(databaseName))
                return null;

            string serverName = connectionInfo.ServerName ?? "(unknown)";
            string key = BuildCacheKey(serverName, databaseName);

            if (_catalogs.TryGetValue(key, out MetadataCatalog cached))
                return cached;

            var catalog = new MetadataCatalog
            {
                Server = serverName,
                Database = databaseName,
                BuiltAtUtc = DateTime.UtcNow,
                IncludeSystemObjects = includeSystemObjects
            };

            try
            {
                var builder = new SqlConnectionStringBuilder(connectionInfo.FullConnectionString)
                {
                    InitialCatalog = databaseName,
                    TrustServerCertificate = true
                };

                using (var conn = new SqlConnection(builder.ConnectionString))
                {
                    await conn.OpenAsync(cancellationToken);

                    await LoadDatabasesAsync(conn, catalog, cancellationToken);
                    await LoadTablesAndViewsAsync(conn, catalog, includeSystemObjects, cancellationToken);
                    await LoadColumnsAsync(conn, catalog, includeSystemObjects, cancellationToken);
                    await LoadRoutinesAsync(conn, catalog, includeSystemObjects, cancellationToken);
                    await LoadParametersAsync(conn, catalog, cancellationToken);
                }

                _catalogs[key] = catalog;
                return catalog;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Failed to build metadata catalog for {serverName}.{databaseName}");
                return null;
            }
        }

        private static async Task LoadDatabasesAsync(
            SqlConnection conn, MetadataCatalog catalog, CancellationToken ct)
        {
            const string sql = @"SELECT [name] FROM sys.databases WHERE state = 0 ORDER BY [name];";
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 5 })
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    catalog.Databases.Add(new MetadataDatabase
                    {
                        DatabaseName = reader.GetString(0)
                    });
                }
            }
        }

        private static async Task LoadTablesAndViewsAsync(
            SqlConnection conn, MetadataCatalog catalog, bool includeSystemObjects, CancellationToken ct)
        {
            var sql = new StringBuilder(@"
SELECT 
    s.[name] AS SchemaName,
    o.[name] AS TableName,
    CASE WHEN o.[type] = 'V' THEN 'VIEW' ELSE 'TABLE' END AS ObjectType,
    ISNULL(ep.[value], '') AS Description,
    OBJECT_DEFINITION(o.[object_id]) AS CreateScript
FROM sys.objects o
INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN sys.extended_properties ep ON ep.major_id = o.object_id 
    AND ep.minor_id = 0 AND ep.[name] = 'MS_Description'
WHERE o.[type] IN ('U', 'V')");

            if (!includeSystemObjects)
                sql.Append(" AND o.is_ms_shipped = 0 AND s.[name] NOT IN ('sys', 'INFORMATION_SCHEMA')");

            sql.Append(" ORDER BY s.[name], o.[name];");

            using (var cmd = new SqlCommand(sql.ToString(), conn) { CommandTimeout = 15 })
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    catalog.TablesAndViews.Add(new MetadataTable
                    {
                        SchemaName = reader.GetString(0),
                        TableName = reader.GetString(1),
                        ObjectType = reader.GetString(2),
                        Description = ReadString(reader, 3),
                        CreateScript = ReadString(reader, 4)
                    });
                }
            }
        }

        private static async Task LoadColumnsAsync(
            SqlConnection conn, MetadataCatalog catalog, bool includeSystemObjects, CancellationToken ct)
        {
            var sql = new StringBuilder(@"
SELECT 
    s.[name] AS SchemaName,
    o.[name] AS TableName,
    c.[name] AS ColumnName,
    tp.[name] AS DataType,
    c.max_length,
    c.[precision],
    c.scale,
    c.is_nullable,
    c.is_identity,
    ISNULL(dc.[definition], '') AS DefaultValue,
    c.column_id,
    ISNULL(ep.[value], '') AS Description
FROM sys.columns c
INNER JOIN sys.objects o ON o.object_id = c.object_id
INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
INNER JOIN sys.types tp ON tp.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id 
    AND dc.parent_column_id = c.column_id
LEFT JOIN sys.extended_properties ep ON ep.major_id = c.object_id 
    AND ep.minor_id = c.column_id AND ep.[name] = 'MS_Description'
WHERE o.[type] IN ('U', 'V')");

            if (!includeSystemObjects)
                sql.Append(" AND o.is_ms_shipped = 0 AND s.[name] NOT IN ('sys', 'INFORMATION_SCHEMA')");

            sql.Append(" ORDER BY s.[name], o.[name], c.column_id;");

            using (var cmd = new SqlCommand(sql.ToString(), conn) { CommandTimeout = 15 })
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                var index = new Dictionary<string, MetadataTable>(StringComparer.OrdinalIgnoreCase);
                foreach (var table in catalog.TablesAndViews)
                    index[$"{table.SchemaName}.{table.TableName}"] = table;

                while (await reader.ReadAsync(ct))
                {
                    string key = $"{reader.GetString(0)}.{reader.GetString(1)}";
                    if (index.TryGetValue(key, out MetadataTable table))
                    {
                        table.Columns.Add(new MetadataColumn
                        {
                            ColumnName = reader.GetString(2),
                            DataType = reader.GetString(3),
                            MaxLength = ReadInt16(reader, 4),
                            Precision = ReadByte(reader, 5),
                            Scale = ReadByte(reader, 6),
                            IsNullable = reader.GetBoolean(7),
                            IsIdentity = reader.GetBoolean(8),
                            DefaultValue = ReadString(reader, 9),
                            ColumnId = reader.GetInt32(10),
                            Description = ReadString(reader, 11)
                        });
                    }
                }
            }
        }

        private static async Task LoadRoutinesAsync(
            SqlConnection conn, MetadataCatalog catalog, bool includeSystemObjects, CancellationToken ct)
        {
            var sql = new StringBuilder(@"
SELECT 
    s.[name] AS SchemaName,
    o.[name] AS RoutineName,
    CASE WHEN o.[type] = 'P' THEN 'PROCEDURE' ELSE 'FUNCTION' END AS ObjectType,
    ISNULL(ep.[value], '') AS Description,
    OBJECT_DEFINITION(o.[object_id]) AS CreateScript,
    ISNULL(tp.[name], '') AS ReturnType
FROM sys.objects o
INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN sys.extended_properties ep ON ep.major_id = o.object_id 
    AND ep.minor_id = 0 AND ep.[name] = 'MS_Description'
LEFT JOIN sys.parameters p ON p.object_id = o.object_id AND p.parameter_id = 0
LEFT JOIN sys.types tp ON tp.user_type_id = p.user_type_id
WHERE o.[type] IN ('P', 'FN', 'IF', 'TF')");

            if (!includeSystemObjects)
                sql.Append(" AND o.is_ms_shipped = 0 AND s.[name] NOT IN ('sys', 'INFORMATION_SCHEMA')");

            sql.Append(" ORDER BY s.[name], o.[name];");

            using (var cmd = new SqlCommand(sql.ToString(), conn) { CommandTimeout = 15 })
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    catalog.Routines.Add(new MetadataRoutine
                    {
                        SchemaName = reader.GetString(0),
                        RoutineName = reader.GetString(1),
                        ObjectType = reader.GetString(2),
                        Description = ReadString(reader, 3),
                        CreateScript = ReadString(reader, 4),
                        ReturnType = ReadString(reader, 5)
                    });
                }
            }
        }

        private static async Task LoadParametersAsync(
            SqlConnection conn, MetadataCatalog catalog, CancellationToken ct)
        {
            const string sql = @"
SELECT 
    s.[name] AS SchemaName,
    o.[name] AS RoutineName,
    p.[name] AS ParameterName,
    tp.[name] AS DataType,
    p.max_length,
    p.[precision],
    p.scale,
    p.is_output,
    p.has_default_value,
    ISNULL(p.default_value, '') AS DefaultValue,
    p.parameter_id
FROM sys.parameters p
INNER JOIN sys.objects o ON o.object_id = p.object_id
INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
INNER JOIN sys.types tp ON tp.user_type_id = p.user_type_id
WHERE o.[type] IN ('P', 'FN', 'IF', 'TF')
  AND p.parameter_id > 0
ORDER BY s.[name], o.[name], p.parameter_id;";

            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 })
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                var index = new Dictionary<string, MetadataRoutine>(StringComparer.OrdinalIgnoreCase);
                foreach (var routine in catalog.Routines)
                    index[$"{routine.SchemaName}.{routine.RoutineName}"] = routine;

                while (await reader.ReadAsync(ct))
                {
                    string key = $"{reader.GetString(0)}.{reader.GetString(1)}";
                    if (index.TryGetValue(key, out MetadataRoutine routine))
                    {
                        routine.Parameters.Add(new MetadataParameter
                        {
                            ParameterName = reader.GetString(2),
                            DataType = reader.GetString(3),
                            MaxLength = ReadInt16(reader, 4),
                            Precision = ReadByte(reader, 5),
                            Scale = ReadByte(reader, 6),
                            IsOutput = reader.GetBoolean(7),
                            HasDefaultValue = reader.GetBoolean(8),
                            DefaultValue = ReadString(reader, 9),
                            ParameterId = reader.GetInt32(10)
                        });
                    }
                }
            }
        }

        private static string ReadString(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static short ReadInt16(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? (short)0 : reader.GetInt16(ordinal);
        }

        private static byte ReadByte(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? (byte)0 : reader.GetByte(ordinal);
        }
    }
}
```

- [ ] **Step 2: Register in csproj**

```xml
<Compile Include="IntelliSense\MetadataCatalogService.cs" />
```

- [ ] **Step 3: Build to verify**

```powershell
msbuild AxialSqlTools\AxialSqlTools.csproj /p:Configuration=Release /t:Rebuild
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add AxialSqlTools/IntelliSense/MetadataCatalogService.cs AxialSqlTools/AxialSqlTools.csproj
git commit -m "feat(intellisense): add metadata catalog service with sys.* queries and memory cache"
```

---

### Task 3: Completion Engine (Context Detection + Filtering)

**Files:**
- Create: `AxialSqlTools/IntelliSense/CompletionEngine.cs`
- Modify: `AxialSqlTools/AxialSqlTools.csproj` (register)

**Interfaces:**
- Consumes: `MetadataModels.cs`, `CompletionItem.cs`, `MetadataCatalogService`
- Produces: `CompletionEngine.GetCompletions(string fullText, int line, int column, MetadataCatalog catalog, bool includeKeywords, bool includeLocalObjects, bool includeVariables)` → `Task<List<CompletionItem>>`, `GetContext(string fullText, int line, int column)` → `CompletionContextType`

- [ ] **Step 1: Write CompletionEngine.cs**

Create the file with NLog field, TSql170Parser reused from AsteriskExpansionService pattern, and context method. Due to length, refer to spec section 4 for the full context mapping table. Core structure:

```csharp
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AxialSqlTools
{
    internal static class CompletionEngine
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        // --- Keyword definitions ---
        private static readonly string[] _statementStartKeywords = {
            "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER",
            "DROP", "EXEC", "EXECUTE", "USE", "DECLARE", "SET", "IF", "BEGIN",
            "END", "TRUNCATE", "MERGE", "GRANT", "DENY", "REVOKE", "GO"
        };

        private static readonly string[] _createAfterKeywords = {
            "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "INDEX", "SCHEMA", "TYPE",
            "OR"
        };

        private static readonly string[] _alterAfterKeywords = {
            "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "INDEX", "SCHEMA"
        };

        private static readonly string[] _aggregateFunctions = {
            "COUNT", "SUM", "AVG", "MIN", "MAX", "COUNT_BIG", "STDEV", "STDEVP",
            "VAR", "VARP", "CHECKSUM_AGG", "GROUPING", "STRING_AGG"
        };

        private static readonly string[] _builtinFunctions = {
            "ISNULL", "COALESCE", "NULLIF", "CAST", "CONVERT", "TRY_CAST",
            "TRY_CONVERT", "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME",
            "DATEADD", "DATEDIFF", "DATENAME", "DATEPART", "YEAR", "MONTH", "DAY",
            "LEN", "LEFT", "RIGHT", "SUBSTRING", "REPLACE", "CHARINDEX", "LTRIM",
            "RTRIM", "UPPER", "LOWER", "CONCAT", "NEWID", "ROW_NUMBER", "RANK",
            "DENSE_RANK", "NTILE", "LEAD", "LAG", "FIRST_VALUE", "LAST_VALUE",
            "ABS", "ROUND", "CEILING", "FLOOR", "POWER", "SQRT", "IIF", "CHOOSE",
            "ISNUMERIC", "ISDATE", "OBJECT_ID", "OBJECT_NAME", "DB_NAME",
            "SCHEMA_NAME", "SUSER_SNAME", "SYSTEM_USER", "SESSION_USER", "ERROR_MESSAGE",
            "ERROR_NUMBER", "ERROR_LINE", "ERROR_PROCEDURE", "ERROR_SEVERITY",
            "ERROR_STATE", "FORMAT", "PARSENAME", "QUOTENAME", "SCOPE_IDENTITY",
            "@@IDENTITY", "@@ROWCOUNT", "@@ERROR", "@@TRANCOUNT", "@@VERSION",
            "@@SPID", "@@SERVERNAME", "@@SERVICENAME"
        };

        private static readonly string[] _operators = {
            "=", "<>", "!=", ">", "<", ">=", "<=", "LIKE", "NOT LIKE", "IN",
            "NOT IN", "BETWEEN", "NOT BETWEEN", "IS NULL", "IS NOT NULL",
            "AND", "OR", "NOT", "EXISTS", "NOT EXISTS"
        };

        // --- Public API ---

        public static CompletionContextType GetContext(string fullText, int line, int column)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fullText))
                    return CompletionContextType.StatementStart;

                var parser = new TSql170Parser(false);
                TSqlFragment fragment = parser.Parse(new StringReader(fullText), out IList<ParseError> _);
                if (fragment == null)
                    return CompletionContextType.Unknown;

                // Scan for SQL keyword before cursor (CREATE, ALTER)
                string lineText = GetLineText(fullText, line);
                string prefix = GetWordBeforeCursor(lineText, column);

                int absoluteOffset = GetAbsoluteOffset(fullText, line, column);
                TSqlParserToken prevToken = FindPreviousToken(fragment, absoluteOffset);

                if (prevToken != null)
                {
                    string prevText = prevToken.Text.ToUpperInvariant();

                    // "." after table/alias → DotAfterTable
                    if (lineText.Length >= column && column > 0 && lineText[column - 1] == '.')
                    {
                        string wordBeforeDot = GetWordBeforeCursor(lineText, column - 1);
                        if (!string.IsNullOrWhiteSpace(wordBeforeDot))
                            return CompletionContextType.DotAfterTable;
                    }

                    if (prevText == "CREATE" || prevText == "CREATEOR")
                        return CompletionContextType.CreateAfter;
                    if (prevText == "ALTER")
                        return CompletionContextType.AlterAfter;
                    if (prevText == "EXEC" || prevText == "EXECUTE")
                        return CompletionContextType.ExecAfter;
                    if (prevText == "USE")
                        return CompletionContextType.UseAfter;
                    if (prevText == "FROM" || prevText == "JOIN" || prevText == "INNER"
                        || prevText == "LEFT" || prevText == "RIGHT" || prevText == "CROSS"
                        || prevText == "OUTER" || prevText == "FULL" || prevText == "APPLY"
                        || prevText == ",")
                        return CompletionContextType.FromClause;
                    if (prevText == "INSERT")
                        return CompletionContextType.InsertIntoAfter;
                    if (prevText == "UPDATE")
                        return CompletionContextType.UpdateAfter;
                    if (prevText == "DELETE")
                        return CompletionContextType.DeleteFromAfter;
                    if (prevText == "WHERE" || prevText == "ON" || prevText == "HAVING"
                        || prevText == "AND" || prevText == "OR")
                        return CompletionContextType.WhereClause;
                    if (prevText == "SET")
                    {
                        // Determine if SET is in UPDATE context by scanning upward for UPDATE token
                        // Simplified: check if UPDATE appears in script before this token
                        string textBeforeCursor = fullText.Substring(0, Math.Min(absoluteOffset, fullText.Length));
                        if (textBeforeCursor.ToUpperInvariant().Contains("UPDATE "))
                            return CompletionContextType.UpdateSetClause;
                    }
                    if (prevText == "ORDER")
                        return CompletionContextType.OrderByClause;
                    if (prevText == "GROUP")
                        return CompletionContextType.GroupByClause;
                    if (prevText == "SELECT" || prevText == "INTO" || prevText == "RETURNS")
                        return CompletionContextType.SelectElements;
                }

                // Check if we're inside FROM clause via ScriptDOM traversal
                if (fragment is TSqlScript script && script.Batches.Count > 0)
                {
                    foreach (TSqlBatch batch in script.Batches)
                    {
                        foreach (TSqlStatement stmt in batch.Statements)
                        {
                            CompletionContextType? astCtx = DetermineAstContext(stmt, absoluteOffset);
                            if (astCtx.HasValue)
                                return astCtx.Value;
                        }
                    }
                }

                // "@" check
                if (lineText.Length >= column && column > 0 && lineText[column - 1] == '@')
                    return CompletionContextType.VariableAfter;

                // Detect SELECT ... (before FROM) — keyword between SELECT and FROM
                string upperText = fullText.ToUpperInvariant();
                int fromIndex = -1;
                for (int i = absoluteOffset; i < upperText.Length - 5; i++)
                {
                    if (upperText.Substring(i, 5) == "FROM " || upperText.Substring(i, 4) == "FROM\r" || upperText.Substring(i, 4) == "FROM\n")
                    {
                        fromIndex = i;
                        break;
                    }
                }
                if (fromIndex < 0)
                {
                    int selectIndex = upperText.LastIndexOf("SELECT ", Math.Min(absoluteOffset, upperText.Length));
                    if (selectIndex >= 0)
                        return CompletionContextType.SelectElements;
                }

                return CompletionContextType.StatementStart;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "GetContext failed");
                return CompletionContextType.Unknown;
            }
        }

        public static List<CompletionItem> GetCompletions(
            string fullText, int line, int column,
            MetadataCatalog catalog,
            bool includeKeywords,
            bool includeLocalObjects,
            bool includeVariables)
        {
            CompletionContextType context = GetContext(fullText, line, column);
            string lineText = GetLineText(fullText, line);
            string prefix = GetWordBeforeCursor(lineText, column);

            var results = new List<CompletionItem>();

            try
            {
                switch (context)
                {
                    case CompletionContextType.StatementStart:
                        if (includeKeywords)
                            AddKeywords(results, _statementStartKeywords, CompletionItemType.Keyword, prefix);
                        break;

                    case CompletionContextType.CreateAfter:
                        if (includeKeywords)
                            AddKeywords(results, _createAfterKeywords, CompletionItemType.Keyword, prefix);
                        break;

                    case CompletionContextType.AlterAfter:
                        if (includeKeywords)
                            AddKeywords(results, _alterAfterKeywords, CompletionItemType.Keyword, prefix);
                        break;

                    case CompletionContextType.FromClause:
                        AddTables(results, catalog, prefix);
                        AddViews(results, catalog, prefix);
                        if (includeKeywords)
                            AddKeywords(results, new[] { "(", "SELECT" }, CompletionItemType.Keyword, prefix);
                        break;

                    case CompletionContextType.SelectElements:
                        AddColumns(results, catalog, prefix);
                        AddLocalColumns(results, includeLocalObjects, fullText, prefix);
                        if (includeKeywords)
                        {
                            AddKeywords(results, _builtinFunctions, CompletionItemType.Function, prefix);
                            AddKeywords(results, _aggregateFunctions, CompletionItemType.Function, prefix);
                            AddKeywords(results, new[] { "*", "CASE", "DISTINCT", "TOP" }, CompletionItemType.Keyword, prefix);
                        }
                        break;

                    case CompletionContextType.ExecAfter:
                        AddProcedures(results, catalog, prefix);
                        AddFunctions(results, catalog, prefix);
                        break;

                    case CompletionContextType.DotAfterTable:
                        string tableName = GetWordBeforeCursor(lineText, column - 1);
                        AddColumnsForTable(results, catalog, tableName, includeLocalObjects, fullText, prefix);
                        break;

                    case CompletionContextType.WhereClause:
                    case CompletionContextType.UpdateSetClause:
                    case CompletionContextType.OrderByClause:
                    case CompletionContextType.GroupByClause:
                        AddColumns(results, catalog, prefix);
                        AddLocalColumns(results, includeLocalObjects, fullText, prefix);
                        if (includeKeywords && context == CompletionContextType.WhereClause)
                        {
                            AddKeywords(results, _builtinFunctions, CompletionItemType.Function, prefix);
                            AddKeywords(results, _operators, CompletionItemType.Keyword, prefix);
                        }
                        break;

                    case CompletionContextType.InsertIntoAfter:
                    case CompletionContextType.UpdateAfter:
                    case CompletionContextType.DeleteFromAfter:
                        AddTables(results, catalog, prefix);
                        AddViews(results, catalog, prefix);
                        break;

                    case CompletionContextType.UseAfter:
                        AddDatabases(results, catalog, prefix);
                        break;

                    case CompletionContextType.VariableAfter:
                        if (includeVariables)
                            AddVariables(results, fullText, prefix);
                        break;
                }

                SortResults(results, prefix);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"GetCompletions failed for context {context}");
            }

            return results;
        }

        // --- AST-based context detection ---
        private static CompletionContextType? DetermineAstContext(TSqlStatement stmt, int cursorOffset)
        {
            if (!ContainsOffset(stmt, cursorOffset))
                return null;

            // Simplified AST traversal — detailed version expands tree-walking for all statement types.
            // For complete implementation: walk the AST nodes to find the exact clause containing cursorOffset.
            // Each clause type maps to a CompletionContextType as per spec section 4.

            // Detect FROM clause
            if (stmt is SelectStatement selectStmt)
            {
                var query = selectStmt.QueryExpression as QuerySpecification;
                if (query != null)
                {
                    if (query.FromClause != null && ContainsOffset(query.FromClause, cursorOffset))
                        return CompletionContextType.FromClause;
                    if (query.WhereClause != null && ContainsOffset(query.WhereClause, cursorOffset))
                        return CompletionContextType.WhereClause;
                    if (query.GroupByClause != null && ContainsOffset(query.GroupByClause, cursorOffset))
                        return CompletionContextType.GroupByClause;
                    if (query.OrderByClause != null && ContainsOffset(query.OrderByClause, cursorOffset))
                        return CompletionContextType.OrderByClause;
                    // If before FROM, treat as SelectElements
                    if (query.FromClause != null && cursorOffset < query.FromClause.StartOffset)
                        return CompletionContextType.SelectElements;
                }
            }
            if (stmt is InsertStatement insStmt)
            {
                if (ContainsOffset(insStmt, cursorOffset))
                    return CompletionContextType.InsertIntoAfter;
            }
            if (stmt is UpdateStatement updStmt)
            {
                if (updStmt.UpdateSpecification?.SetClauses != null
                    && updStmt.UpdateSpecification.SetClauses.Count > 0
                    && ContainsOffset(updStmt.UpdateSpecification.SetClauses[0], cursorOffset))
                    return CompletionContextType.UpdateSetClause;
                return CompletionContextType.UpdateAfter;
            }
            if (stmt is DeleteStatement delStmt)
                return CompletionContextType.DeleteFromAfter;
            if (stmt is ExecuteStatement execStmt)
                return CompletionContextType.ExecAfter;

            return null;
        }

        private static bool ContainsOffset(TSqlFragment fragment, int offset)
        {
            return fragment != null && fragment.StartOffset <= offset
                && offset < fragment.StartOffset + fragment.FragmentLength;
        }

        // --- Helper: text extraction ---
        private static string GetLineText(string fullText, int line)
        {
            int currentLine = 0;
            int start = 0;
            for (int i = 0; i < fullText.Length; i++)
            {
                if (currentLine == line)
                {
                    start = i;
                    while (i < fullText.Length && fullText[i] != '\r' && fullText[i] != '\n')
                        i++;
                    return fullText.Substring(start, i - start);
                }
                if (fullText[i] == '\r')
                {
                    if (i + 1 < fullText.Length && fullText[i + 1] == '\n') i++;
                    currentLine++;
                }
                else if (fullText[i] == '\n') currentLine++;
            }
            return "";
        }

        private static string GetWordBeforeCursor(string lineText, int column)
        {
            if (string.IsNullOrEmpty(lineText) || column <= 0 || column > lineText.Length)
                return "";
            int start = column - 1;
            while (start >= 0 && (char.IsLetterOrDigit(lineText[start])
                || lineText[start] == '_' || lineText[start] == '#' || lineText[start] == '@'))
                start--;
            return lineText.Substring(start + 1, column - start - 1);
        }

        private static int GetAbsoluteOffset(string text, int line, int column)
        {
            int currentLine = 0;
            int currentColumn = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (currentLine == line && currentColumn == column)
                    return i;
                if (text[i] == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    currentLine++;
                    currentColumn = 0;
                }
                else if (text[i] == '\n')
                {
                    currentLine++;
                    currentColumn = 0;
                }
                else currentColumn++;
            }
            return text.Length;
        }

        private static TSqlParserToken FindPreviousToken(TSqlFragment fragment, int cursorOffset)
        {
            if (fragment?.ScriptTokenStream == null) return null;
            for (int i = fragment.ScriptTokenStream.Count - 1; i >= 0; i--)
            {
                var token = fragment.ScriptTokenStream[i];
                if (token.TokenType != TSqlTokenType.WhiteSpace && token.Column < cursorOffset)
                    return token;
            }
            return null;
        }

        // --- Completion builders ---
        private static void AddKeywords(List<CompletionItem> results, string[] keywords,
            CompletionItemType itemType, string prefix)
        {
            string upperPrefix = (prefix ?? "").ToUpperInvariant();
            foreach (string kw in keywords)
            {
                if (string.IsNullOrEmpty(upperPrefix) || kw.StartsWith(upperPrefix))
                {
                    results.Add(new CompletionItem
                    {
                        DisplayText = kw,
                        InsertText = kw,
                        ItemType = itemType,
                        ObjectName = kw,
                        PrefixMatchLength = string.IsNullOrEmpty(upperPrefix) ? 0
                            : kw.StartsWith(upperPrefix) ? upperPrefix.Length : 0
                    });
                }
            }
        }

        private static void AddTables(List<CompletionItem> results, MetadataCatalog catalog, string prefix)
        {
            if (catalog == null) return;
            foreach (var t in catalog.TablesAndViews)
            {
                if (t.ObjectType == "TABLE" && MatchesPrefix($"[{t.SchemaName}].[{t.TableName}]", prefix))
                {
                    AddTableItem(results, t, "TABLE", prefix);
                }
            }
        }

        private static void AddViews(List<CompletionItem> results, MetadataCatalog catalog, string prefix)
        {
            if (catalog == null) return;
            foreach (var t in catalog.TablesAndViews)
            {
                if (t.ObjectType == "VIEW" && MatchesPrefix($"[{t.SchemaName}].[{t.TableName}]", prefix))
                {
                    AddTableItem(results, t, "VIEW", prefix);
                }
            }
        }

        private static void AddTableItem(List<CompletionItem> results, MetadataTable table,
            string objectType, string prefix)
        {
            string display = $"[{table.SchemaName}].[{table.TableName}]";
            string insert;
            // If user already typed schema prefix, omit it
            if (prefix != null && prefix.StartsWith($"[{table.SchemaName}]", StringComparison.OrdinalIgnoreCase))
                insert = $"[{table.TableName}]";
            else if (prefix != null
                && prefix.StartsWith(table.SchemaName, StringComparison.OrdinalIgnoreCase)
                && prefix.Length > table.SchemaName.Length)
                insert = $"[{table.TableName}]";
            else
                insert = display;

            results.Add(new CompletionItem
            {
                DisplayText = display,
                InsertText = insert,
                ItemType = objectType == "VIEW" ? CompletionItemType.View : CompletionItemType.Table,
                SchemaName = table.SchemaName,
                ObjectName = table.TableName,
                Description = table.Description,
                DetailText = table.CreateScript,
                IsSystemObject = table.SchemaName == "sys" || table.SchemaName == "INFORMATION_SCHEMA",
                PrefixMatchLength = CalculatePrefixMatch(table.TableName, prefix ?? "")
            });
        }

        private static void AddProcedures(List<CompletionItem> results, MetadataCatalog catalog, string prefix)
        {
            if (catalog == null) return;
            foreach (var r in catalog.Routines)
            {
                if (r.ObjectType == "PROCEDURE"
                    && MatchesPrefix($"[{r.SchemaName}].[{r.RoutineName}]", prefix))
                {
                    results.Add(new CompletionItem
                    {
                        DisplayText = $"[{r.SchemaName}].[{r.RoutineName}]",
                        InsertText = $"[{r.SchemaName}].[{r.RoutineName}]",
                        ItemType = CompletionItemType.Procedure,
                        SchemaName = r.SchemaName,
                        ObjectName = r.RoutineName,
                        Description = r.Description,
                        DetailText = r.CreateScript,
                        IsSystemObject = r.SchemaName == "sys",
                        PrefixMatchLength = CalculatePrefixMatch(r.RoutineName, prefix ?? "")
                    });
                }
            }
        }

        private static void AddFunctions(List<CompletionItem> results, MetadataCatalog catalog, string prefix)
        {
            if (catalog == null) return;
            foreach (var r in catalog.Routines)
            {
                if (r.ObjectType == "FUNCTION"
                    && MatchesPrefix($"[{r.SchemaName}].[{r.RoutineName}]", prefix))
                {
                    results.Add(new CompletionItem
                    {
                        DisplayText = $"[{r.SchemaName}].[{r.RoutineName}]",
                        InsertText = $"[{r.SchemaName}].[{r.RoutineName}]",
                        ItemType = CompletionItemType.Function,
                        SchemaName = r.SchemaName,
                        ObjectName = r.RoutineName,
                        Description = r.Description,
                        DetailText = r.CreateScript,
                        IsSystemObject = r.SchemaName == "sys",
                        PrefixMatchLength = CalculatePrefixMatch(r.RoutineName, prefix ?? "")
                    });
                }
            }
        }

        private static void AddColumns(List<CompletionItem> results, MetadataCatalog catalog, string prefix)
        {
            if (catalog == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in catalog.TablesAndViews)
            {
                foreach (var col in table.Columns)
                {
                    string display = $"[{col.ColumnName}]";
                    if (seen.Add(col.ColumnName) && MatchesPrefix(display, prefix))
                    {
                        results.Add(new CompletionItem
                        {
                            DisplayText = display,
                            InsertText = display,
                            ItemType = CompletionItemType.Column,
                            ObjectName = col.ColumnName,
                            Description = $"{col.DataType}{(col.IsNullable ? " NULL" : " NOT NULL")} — {col.Description}",
                            DetailText = col.Description,
                            PrefixMatchLength = CalculatePrefixMatch(col.ColumnName, prefix ?? "")
                        });
                    }
                }
            }
        }

        private static void AddColumnsForTable(List<CompletionItem> results, MetadataCatalog catalog,
            string tableName, bool includeLocal, string fullText, string prefix)
        {
            if (catalog == null) return;
            string cleanName = tableName.Trim('[', ']');
            foreach (var table in catalog.TablesAndViews)
            {
                if (string.Equals(table.TableName, cleanName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals($"[{table.SchemaName}].[{table.TableName}]",
                        tableName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var col in table.Columns)
                    {
                        string display = $"[{col.ColumnName}]";
                        if (MatchesPrefix(display, prefix ?? ""))
                        {
                            results.Add(new CompletionItem
                            {
                                DisplayText = display,
                                InsertText = display,
                                ItemType = CompletionItemType.Column,
                                ObjectName = col.ColumnName,
                                Description = $"{col.DataType} — {col.Description}",
                                DetailText = col.Description,
                                PrefixMatchLength = CalculatePrefixMatch(col.ColumnName, prefix ?? "")
                            });
                        }
                    }
                    return;
                }
            }
        }

        private static void AddDatabases(List<CompletionItem> results, MetadataCatalog catalog, string prefix)
        {
            if (catalog == null) return;
            foreach (var db in catalog.Databases)
            {
                if (MatchesPrefix($"[{db.DatabaseName}]", prefix))
                {
                    results.Add(new CompletionItem
                    {
                        DisplayText = $"[{db.DatabaseName}]",
                        InsertText = $"[{db.DatabaseName}]",
                        ItemType = CompletionItemType.Database,
                        ObjectName = db.DatabaseName,
                        PrefixMatchLength = CalculatePrefixMatch(db.DatabaseName, prefix ?? "")
                    });
                }
            }
        }

        private static void AddLocalColumns(List<CompletionItem> results,
            bool includeLocal, string fullText, string prefix)
        {
            if (!includeLocal) return;
            // Parse local CTEs, #temp tables, @table variables from ScriptDOM
            // (Expanded in subsequent task for local object support)
        }

        private static void AddVariables(List<CompletionItem> results, string fullText, string prefix)
        {
            // Parse DECLARE @var statements from fullText
            // (Expanded in subsequent task)
        }

        // --- Sorting ---
        private static void SortResults(List<CompletionItem> results, string prefix)
        {
            results.Sort((a, b) =>
            {
                // Primary: prefix match length (descending)
                int matchCmp = b.PrefixMatchLength.CompareTo(a.PrefixMatchLength);
                if (matchCmp != 0) return matchCmp;

                // Secondary: type grouping order
                int typeOrderA = GetTypeOrder(a.ItemType);
                int typeOrderB = GetTypeOrder(b.ItemType);
                if (typeOrderA != typeOrderB) return typeOrderA.CompareTo(typeOrderB);

                // Tertiary: alphabetic
                return string.Compare(a.DisplayText, b.DisplayText, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static int GetTypeOrder(CompletionItemType type)
        {
            switch (type)
            {
                case CompletionItemType.Keyword: return 0;
                case CompletionItemType.Table: return 1;
                case CompletionItemType.View: return 2;
                case CompletionItemType.Function: return 3;
                case CompletionItemType.Procedure: return 4;
                case CompletionItemType.Column: return 5;
                case CompletionItemType.Database: return 6;
                case CompletionItemType.Schema: return 7;
                default: return 10;
            }
        }

        // --- Utilities ---
        private static bool MatchesPrefix(string text, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return true;
            return text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int CalculatePrefixMatch(string text, string prefix)
        {
            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(text)) return 0;
            int i = 0;
            while (i < prefix.Length && i < text.Length
                && char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(prefix[i]))
                i++;
            return i;
        }
    }
}
```

- [ ] **Step 2: Register in csproj**

```xml
<Compile Include="IntelliSense\CompletionEngine.cs" />
```

- [ ] **Step 3: Build to verify**

```powershell
msbuild AxialSqlTools\AxialSqlTools.csproj /p:Configuration=Release /t:Rebuild
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add AxialSqlTools/IntelliSense/CompletionEngine.cs AxialSqlTools/AxialSqlTools.csproj
git commit -m "feat(intellisense): add completion engine with SQL context detection and filtering"
```

---

### Task 4: IntelliSense Key Handler

**Files:**
- Create: `AxialSqlTools/IntelliSense/IntelliSenseKeyHandler.cs`
- Modify: `AxialSqlTools/AxialSqlTools.csproj` (register)

**Interfaces:**
- Consumes: `CompletionEngine`, `MetadataCatalogService`, `ScriptFactoryAccess`, `CompletionItem`, `IVsTextView`, `IVsTextLines`
- Produces: `IntelliSenseKeyHandler` class — instantiated per text view, `ExecIfEnabled(IVsTextView, ref bool handled)` called from KeypressCommandFilter

- [ ] **Step 1: Write IntelliSenseKeyHandler.cs**

```csharp
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AxialSqlTools
{
    internal sealed class IntelliSenseKeyHandler
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly IVsTextView _textView;
        private readonly AxialSqlToolsPackage _package;
        private CancellationTokenSource _debounceCts;
        private bool _popupVisible;
        private List<CompletionItem> _currentItems;
        private CompletionListWindow _completionWindow;

        public IntelliSenseKeyHandler(IVsTextView textView, AxialSqlToolsPackage package)
        {
            _textView = textView;
            _package = package;
        }

        public bool ExecIfEnabled(ref bool handled)
        {
            var settings = SettingsManager.GetIntelliSenseSettings();
            if (!settings.enabled) return false;

            try
            {
                _ = TriggerCompletion(force: false);
                handled = true;
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "ExecIfEnabled failed");
                return false;
            }
        }

        public bool ExecForce()
        {
            var settings = SettingsManager.GetIntelliSenseSettings();
            if (!settings.enabled) return false;

            try
            {
                _ = TriggerCompletion(force: true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "ExecForce failed");
                return false;
            }
        }

        private async Task TriggerCompletion(bool force)
        {
            // Debounce
            CancelDebounce();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            if (!force)
            {
                var settings = SettingsManager.GetIntelliSenseSettings();
                try
                {
                    await Task.Delay(settings.autoTriggerDelayMs, token);
                }
                catch (TaskCanceledException) { return; }
            }

            if (token.IsCancellationRequested) return;

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(token);

            // Get current text
            _textView.GetCaretPos(out int line, out int column);
            if (_textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK) return;

            textLines.GetLastLineIndex(out int lastLine, out int lastIndex);
            textLines.GetLineText(0, 0, lastLine, lastIndex, out string fullText);

            // Get connection
            var connInfo = ScriptFactoryAccess.GetCurrentConnectionInfo();
            string database = connInfo?.Database;

            // Build catalog
            MetadataCatalog catalog = null;
            if (connInfo != null && !string.IsNullOrWhiteSpace(connInfo.FullConnectionString)
                && !string.IsNullOrWhiteSpace(database))
            {
                catalog = await System.Threading.Tasks.Task.Run(() =>
                    MetadataCatalogService.GetOrBuildCatalogAsync(
                        connInfo, database, settings.includeSystemObjects, CancellationToken.None));
            }

            // Get completions
            var completions = CompletionEngine.GetCompletions(
                fullText, line, column, catalog,
                settings.includeKeywords,
                settings.includeLocalTempTables,
                settings.includeLocalVariables);

            if ((completions == null || completions.Count == 0) && !force) return;

            _currentItems = completions ?? new List<CompletionItem>();

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Show popup
            ShowCompletionWindow(line, column);
        }

        private void ShowCompletionWindow(int line, int column)
        {
            CloseCompletionWindow();

            _completionWindow = new CompletionListWindow(_currentItems, OnCompletionAccepted, OnCompletionClosed, OnItemDot, OnItemOpenParen);
            _completionWindow.ShowAtCursor(_textView, line, column);
            _popupVisible = true;
        }

        public void CloseCompletionWindow()
        {
            if (_completionWindow != null)
            {
                _completionWindow.Close();
                _completionWindow = null;
            }
            _popupVisible = false;
        }

        public bool IsPopupVisible => _popupVisible;

        private void OnCompletionAccepted(CompletionItem item)
        {
            try
            {
                _textView.GetCaretPos(out int line, out int column);
                if (_textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK) return;

                textLines.GetLengthOfLine(line, out int lineLength);
                textLines.GetLineText(line, 0, line, lineLength, out string lineText);

                // Find the prefix word to replace
                string prefix = GetWordBeforeCursor(lineText, column);
                int replaceStart = column - (prefix?.Length ?? 0);

                string insertText = item.InsertText;

                // For procedures/functions, append "("
                if (item.ItemType == CompletionItemType.Procedure
                    || item.ItemType == CompletionItemType.Function)
                {
                    insertText += "(";
                }

                // Replace prefix with insert text
                IntPtr pNewText = Marshal.StringToHGlobalUni(insertText);
                try
                {
                    TextSpan[] changedSpan = new TextSpan[1];
                    textLines.ReplaceLines(line, replaceStart, line, column, pNewText, insertText.Length, changedSpan);
                }
                finally
                {
                    Marshal.FreeHGlobal(pNewText);
                }

                int newColumn = replaceStart + insertText.Length;
                _textView.SetCaretPos(line, newColumn);

                _popupVisible = false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "OnCompletionAccepted failed");
            }
        }

        private void OnCompletionClosed() { _popupVisible = false; }

        private void OnItemDot(CompletionItem item)
        {
            // Allow completion popup to re-trigger for dot-after-table
        }

        private void OnItemOpenParen(CompletionItem item)
        {
            // Parameter hint mode
        }

        private void CancelDebounce()
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        private static string GetWordBeforeCursor(string lineText, int column)
        {
            if (string.IsNullOrEmpty(lineText) || column <= 0 || column > lineText.Length)
                return "";
            int start = column - 1;
            while (start >= 0 && (char.IsLetterOrDigit(lineText[start])
                || lineText[start] == '_' || lineText[start] == '#' || lineText[start] == '@'
                || lineText[start] == '[' || lineText[start] == ']'))
                start--;
            return lineText.Substring(start + 1, column - start - 1);
        }
    }
}
```

- [ ] **Step 2: Register in csproj**

```xml
<Compile Include="IntelliSense\IntelliSenseKeyHandler.cs" />
```

- [ ] **Step 3: Build to verify**

```powershell
msbuild AxialSqlTools\AxialSqlTools.csproj /p:Configuration=Release /t:Rebuild
```

Expected: Build fails with missing `CompletionListWindow` — expected, created in Task 5.

- [ ] **Step 4: Commit**

```bash
git add AxialSqlTools/IntelliSense/IntelliSenseKeyHandler.cs AxialSqlTools/AxialSqlTools.csproj
git commit -m "feat(intellisense): add key handler with debounce, catalog lookup, and text replacement"
```

---

### Task 5: Completion List Window (WPF Popup UI)

**Files:**
- Create: `AxialSqlTools/IntelliSense/CompletionListWindow.xaml`
- Create: `AxialSqlTools/IntelliSense/CompletionListWindow.xaml.cs`
- Modify: `AxialSqlTools/AxialSqlTools.csproj` (register)

**Interfaces:**
- Consumes: `CompletionItem`, `IVsTextView`
- Produces: `CompletionListWindow` — WPF Popup with ListBox + detail panel, `ShowAtCursor(IVsTextView, int line, int column)`, `Close()`, callback delegates for accept/close/dot/openParen

- [ ] **Step 1: Write CompletionListWindow.xaml**

```xml
<Window x:Class="AxialSqlTools.CompletionListWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None"
        AllowsTransparency="True"
        Background="Transparent"
        ShowInTaskbar="False"
        Topmost="True"
        SizeToContent="WidthAndHeight"
        ResizeMode="NoResize"
        MaxHeight="360"
        MaxWidth="540">
    <Border Background="{DynamicResource AxialThemeBackgroundBrush}"
            BorderBrush="{DynamicResource AxialThemeBorderBrush}"
            BorderThickness="1"
            CornerRadius="2">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" MinWidth="180"/>
                <ColumnDefinition Width="200"/>
            </Grid.ColumnDefinitions>

            <!-- Left: Completion list -->
            <ListBox Grid.Column="0"
                     x:Name="CompletionListBox"
                     Background="Transparent"
                     Foreground="{DynamicResource AxialThemeForegroundBrush}"
                     BorderThickness="0"
                     SelectionChanged="CompletionListBox_SelectionChanged"
                     KeyDown="CompletionListBox_KeyDown"
                     MouseDoubleClick="CompletionListBox_MouseDoubleClick">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <StackPanel Orientation="Horizontal" Margin="2">
                            <TextBlock Text="{Binding DisplayText}"
                                       Foreground="{DynamicResource AxialThemeForegroundBrush}"
                                       FontFamily="Consolas"
                                       FontSize="12"/>
                        </StackPanel>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>

            <!-- Right: Detail panel -->
            <Border Grid.Column="1" BorderBrush="{DynamicResource AxialThemeBorderBrush}"
                    BorderThickness="1,0,0,0" Padding="8" MaxWidth="200">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel>
                        <TextBlock x:Name="DetailTypeText" FontWeight="Bold" Margin="0,0,0,4"
                                   Foreground="{DynamicResource AxialThemeForegroundBrush}"
                                   TextWrapping="Wrap"/>
                        <TextBlock x:Name="DetailDescriptionText"
                                   Foreground="{DynamicResource AxialThemeForegroundBrush}"
                                   TextWrapping="Wrap" FontSize="11" Margin="0,0,0,8"/>
                        <TextBlock x:Name="DetailScriptText"
                                   Foreground="{DynamicResource AxialThemeForegroundBrush}"
                                   FontFamily="Consolas" FontSize="10"
                                   TextWrapping="NoWrap" TextTrimming="CharacterEllipsis"
                                   MaxHeight="200"/>
                    </StackPanel>
                </ScrollViewer>
            </Border>
        </Grid>
    </Border>
</Window>
```

- [ ] **Step 2: Write CompletionListWindow.xaml.cs**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.TextManager.Interop;

namespace AxialSqlTools
{
    public partial class CompletionListWindow : Window
    {
        private readonly List<CompletionItem> _items;
        private readonly Action<CompletionItem> _onAccept;
        private readonly Action _onClose;
        private readonly Action<CompletionItem> _onDot;
        private readonly Action<CompletionItem> _onOpenParen;

        public CompletionListWindow(
            List<CompletionItem> items,
            Action<CompletionItem> onAccept,
            Action onClose,
            Action<CompletionItem> onDot,
            Action<CompletionItem> onOpenParen)
        {
            InitializeComponent();
            _items = items;
            _onAccept = onAccept;
            _onClose = onClose;
            _onDot = onDot;
            _onOpenParen = onOpenParen;

            CompletionListBox.ItemsSource = _items;
            if (_items.Count > 0)
                CompletionListBox.SelectedIndex = 0;

            this.Deactivated += (s, e) => Close();
        }

        public void ShowAtCursor(IVsTextView textView, int line, int column)
        {
            // Get screen position from text view
            // Use IVsTextView.GetPointOfScreenPosition() pattern
            // Place popup below the caret line
            this.WindowStartupLocation = WindowStartupLocation.Manual;
            this.Left = System.Windows.Forms.Cursor.Position.X;
            this.Top = System.Windows.Forms.Cursor.Position.Y + 20;
            this.Show();
        }

        private void CompletionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CompletionListBox.SelectedItem is CompletionItem item)
            {
                DetailTypeText.Text = GetTypeLabel(item.ItemType);
                DetailDescriptionText.Text = item.Description ?? "";
                DetailScriptText.Text = item.DetailText ?? "";
            }
        }

        private static string GetTypeLabel(CompletionItemType type)
        {
            switch (type)
            {
                case CompletionItemType.Table: return "Table";
                case CompletionItemType.View: return "View";
                case CompletionItemType.Procedure: return "Procedure";
                case CompletionItemType.Function: return "Function";
                case CompletionItemType.Column: return "Column";
                case CompletionItemType.Keyword: return "Keyword";
                case CompletionItemType.Database: return "Database";
                case CompletionItemType.LocalVariable: return "Variable";
                case CompletionItemType.CteName: return "CTE";
                case CompletionItemType.TempTable: return "Temp Table";
                default: return "";
            }
        }

        private void CompletionListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                if (CompletionListBox.SelectedItem is CompletionItem item)
                {
                    _onAccept?.Invoke(item);
                    Close();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.OemPeriod || e.Key == Key.Decimal)
            {
                if (CompletionListBox.SelectedItem is CompletionItem item)
                {
                    _onDot?.Invoke(item);
                }
            }
        }

        private void CompletionListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CompletionListBox.SelectedItem is CompletionItem item)
            {
                _onAccept?.Invoke(item);
                Close();
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.OemComma || e.Key == Key.OemOpenBrackets)
            {
                if (CompletionListBox.SelectedItem is CompletionItem item)
                {
                    _onOpenParen?.Invoke(item);
                }
            }
            base.OnPreviewKeyDown(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _onClose?.Invoke();
            base.OnClosed(e);
        }
    }
}
```

- [ ] **Step 3: Register in csproj**

```xml
<Compile Include="IntelliSense\CompletionListWindow.xaml.cs">
  <DependentUpon>CompletionListWindow.xaml</DependentUpon>
</Compile>
<Page Include="IntelliSense\CompletionListWindow.xaml">
  <Generator>MSBuild:Compile</Generator>
  <SubType>Designer</SubType>
</Page>
```

- [ ] **Step 4: Build to verify**

```powershell
msbuild AxialSqlTools\AxialSqlTools.csproj /p:Configuration=Release /t:Rebuild
```

Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add AxialSqlTools/IntelliSense/CompletionListWindow.xaml AxialSqlTools/IntelliSense/CompletionListWindow.xaml.cs AxialSqlTools/AxialSqlTools.csproj
git commit -m "feat(intellisense): add WPF completion popup window with detail panel"
```

---

### Task 6: Tooltip Provider + TextView Extension

**Files:**
- Create: `AxialSqlTools/IntelliSense/QuickInfoProvider.cs`
- Create: `AxialSqlTools/IntelliSense/QuickInfoTooltip.cs`
- Create: `AxialSqlTools/IntelliSense/IntelliSenseTextViewExtension.cs`
- Modify: `AxialSqlTools/AxialSqlTools.csproj` (register)

**Interfaces:**
- Consumes: `MetadataCatalogService`, `MetadataModels`, `ScriptFactoryAccess`, `CompletionEngine`, `IVsTextView`
- Produces: `QuickInfoProvider.GetTooltipContent(string fullText, int line, int column, MetadataCatalog catalog)` → `QuickInfoData`, `IntelliSenseTextViewExtension` — hooks IVsTextView mouse hover events

- [ ] **Step 1: Write QuickInfoProvider.cs**

```csharp
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AxialSqlTools
{
    internal sealed class QuickInfoData
    {
        public string Title { get; set; }
        public string DetailText { get; set; }
    }

    internal static class QuickInfoProvider
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        public static QuickInfoData GetTooltipContent(
            string fullText, int line, int column, MetadataCatalog catalog)
        {
            if (catalog == null || string.IsNullOrWhiteSpace(fullText))
                return null;

            try
            {
                var parser = new TSql170Parser(false);
                TSqlFragment fragment = parser.Parse(new StringReader(fullText), out IList<ParseError> _);
                if (fragment?.ScriptTokenStream == null) return null;

                int cursorOffset = GetAbsoluteOffset(fullText, line, column);
                TSqlParserToken token = FindTokenAtOffset(fragment, cursorOffset);
                if (token == null) return null;

                string tokenText = token.Text.Trim('[', ']');

                // Try match table/view
                foreach (var table in catalog.TablesAndViews)
                {
                    if (string.Equals(table.TableName, tokenText, StringComparison.OrdinalIgnoreCase))
                    {
                        return BuildTableTooltip(table);
                    }
                }

                // Try match column (check all tables)
                foreach (var table in catalog.TablesAndViews)
                {
                    foreach (var col in table.Columns)
                    {
                        if (string.Equals(col.ColumnName, tokenText, StringComparison.OrdinalIgnoreCase))
                        {
                            return BuildColumnTooltip(col, table);
                        }
                    }
                }

                // Try match routine
                foreach (var routine in catalog.Routines)
                {
                    if (string.Equals(routine.RoutineName, tokenText, StringComparison.OrdinalIgnoreCase))
                    {
                        return BuildRoutineTooltip(routine);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "GetTooltipContent failed");
            }

            return null;
        }

        private static QuickInfoData BuildTableTooltip(MetadataTable table)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE TABLE [{table.SchemaName}].[{table.TableName}](");
            foreach (var col in table.Columns)
            {
                string nullStr = col.IsNullable ? "NULL" : "NOT NULL";
                string identityStr = col.IsIdentity ? " IDENTITY" : "";
                string colDef = $"    [{col.ColumnName}] {col.DataType}{identityStr} {nullStr}";
                if (!string.IsNullOrEmpty(col.DefaultValue))
                    colDef += $" DEFAULT {col.DefaultValue}";
                sb.AppendLine(colDef + ",");
            }
            if (table.Columns.Count > 0)
                sb.Length -= 3 + Environment.NewLine.Length; // Remove last ","
            sb.AppendLine();
            sb.Append(")");

            if (!string.IsNullOrEmpty(table.Description))
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.Append($"Description: {table.Description}");
            }

            return new QuickInfoData
            {
                Title = $"[{table.SchemaName}].[{table.TableName}] ({table.ObjectType})",
                DetailText = sb.ToString()
            };
        }

        private static QuickInfoData BuildColumnTooltip(MetadataColumn col, MetadataTable table)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Table: [{table.SchemaName}].[{table.TableName}]");
            sb.AppendLine($"Type: {col.DataType}");

            if (col.MaxLength > 0 && col.DataType.Contains("char", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine($"Length: {(col.MaxLength == -1 ? "MAX" : col.MaxLength.ToString())}");
            if (col.Precision > 0)
                sb.AppendLine($"Precision: {col.Precision}, Scale: {col.Scale}");

            sb.AppendLine($"Nullable: {(col.IsNullable ? "Yes" : "No")}");
            if (col.IsIdentity)
                sb.AppendLine("Identity: Yes");
            if (!string.IsNullOrEmpty(col.DefaultValue))
                sb.AppendLine($"Default: {col.DefaultValue}");
            if (!string.IsNullOrEmpty(col.Description))
                sb.AppendLine($"Description: {col.Description}");

            return new QuickInfoData
            {
                Title = $"[{col.ColumnName}]",
                DetailText = sb.ToString()
            };
        }

        private static QuickInfoData BuildRoutineTooltip(MetadataRoutine routine)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Parameters:");
            if (routine.Parameters.Count == 0)
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var p in routine.Parameters)
                {
                    string def = p.HasDefaultValue && !string.IsNullOrEmpty(p.DefaultValue)
                        ? $" = {p.DefaultValue}" : "";
                    string output = p.IsOutput ? " OUTPUT" : "";
                    sb.AppendLine($"  @{p.ParameterName} {p.DataType}{def}{output}");
                }
            }

            if (!string.IsNullOrEmpty(routine.ReturnType))
            {
                sb.AppendLine();
                sb.AppendLine($"Returns: {routine.ReturnType}");
            }

            if (!string.IsNullOrEmpty(routine.CreateScript))
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.Append(routine.CreateScript);
            }

            if (!string.IsNullOrEmpty(routine.Description))
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.Append($"Description: {routine.Description}");
            }

            return new QuickInfoData
            {
                Title = $"[{routine.SchemaName}].[{routine.RoutineName}] ({routine.ObjectType})",
                DetailText = sb.ToString()
            };
        }

        private static int GetAbsoluteOffset(string text, int line, int column)
        {
            int currentLine = 0;
            int currentColumn = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (currentLine == line && currentColumn == column) return i;
                if (text[i] == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    currentLine++;
                    currentColumn = 0;
                }
                else if (text[i] == '\n') { currentLine++; currentColumn = 0; }
                else currentColumn++;
            }
            return text.Length;
        }

        private static TSqlParserToken FindTokenAtOffset(TSqlFragment fragment, int offset)
        {
            if (fragment?.ScriptTokenStream == null) return null;
            foreach (var token in fragment.ScriptTokenStream)
            {
                if (token.Offset <= offset && offset <= token.Offset + token.Text.Length)
                    return token;
            }
            return null;
        }
    }
}
```

- [ ] **Step 2: Write QuickInfoTooltip.cs**

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AxialSqlTools
{
    internal static class QuickInfoTooltip
    {
        public static ToolTip Create(QuickInfoData data)
        {
            if (data == null) return null;

            var panel = new StackPanel();

            var titleBlock = new TextBlock
            {
                Text = data.Title,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4)
            };
            panel.Children.Add(titleBlock);

            if (!string.IsNullOrEmpty(data.DetailText))
            {
                var detailBlock = new TextBlock
                {
                    Text = data.DetailText,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 500
                };
                panel.Children.Add(detailBlock);
            }

            return new ToolTip
            {
                Content = panel,
                MaxWidth = 520,
                StaysOpen = false
            };
        }
    }
}
```

- [ ] **Step 3: Write IntelliSenseTextViewExtension.cs**

```csharp
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Timers;
using System.Windows.Controls;

namespace AxialSqlTools
{
    internal sealed class IntelliSenseTextViewExtension : IVsTextViewEvents
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly IVsTextView _textView;
        private readonly AxialSqlToolsPackage _package;
        private ToolTip _currentTooltip;
        private Timer _hoverTimer;

        public IntelliSenseTextViewExtension(IVsTextView textView, AxialSqlToolsPackage package)
        {
            _textView = textView;
            _package = package;
            _hoverTimer = new Timer { Interval = 500, AutoReset = false };
            _hoverTimer.Elapsed += HoverTimer_Elapsed;
        }

        private void HoverTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowTooltip();
            });
        }

        private void ShowTooltip()
        {
            try
            {
                var settings = SettingsManager.GetIntelliSenseSettings();
                if (!settings.enabled || !settings.hoverTooltipEnabled) return;

                _textView.GetCaretPos(out int line, out int column);
                if (_textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK) return;

                textLines.GetLastLineIndex(out int lastLine, out int lastIndex);
                textLines.GetLineText(0, 0, lastLine, lastIndex, out string fullText);

                var connInfo = ScriptFactoryAccess.GetCurrentConnectionInfo();
                string database = connInfo?.Database;

                MetadataCatalog catalog = null;
                if (connInfo != null && !string.IsNullOrWhiteSpace(connInfo.FullConnectionString)
                    && !string.IsNullOrWhiteSpace(database))
                {
                    catalog = MetadataCatalogService.GetOrBuildCatalogAsync(
                        connInfo, database, settings.includeSystemObjects,
                        System.Threading.CancellationToken.None).Result;
                }

                var data = QuickInfoProvider.GetTooltipContent(fullText, line, column, catalog);
                if (data == null) return;

                HideTooltip();
                _currentTooltip = QuickInfoTooltip.Create(data);
                _currentTooltip.IsOpen = true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "ShowTooltip failed");
            }
        }

        public void HideTooltip()
        {
            if (_currentTooltip != null)
            {
                _currentTooltip.IsOpen = false;
                _currentTooltip = null;
            }
        }

        // IVsTextViewEvents
        public void OnChangeCaretLine(IVsTextView view, int iNewLine, int iOldLine)
        {
            HideTooltip();
            _hoverTimer.Stop();
            _hoverTimer.Start();
        }

        public void OnChangeScrollInfo(IVsTextView view, int iBar, int iMinUnit, int iMaxUnits,
            int iVisibleUnits, int iFirstVisibleUnit) { }

        // Not in IVsTextViewEvents interface — called manually from Package
        public void OnMouseHover()
        {
            _hoverTimer.Stop();
            _hoverTimer.Start();
        }

        public void Dispose()
        {
            HideTooltip();
            _hoverTimer?.Dispose();
            _hoverTimer = null;
        }
    }
}
```

- [ ] **Step 4: Register in csproj**

```xml
<Compile Include="IntelliSense\QuickInfoProvider.cs" />
<Compile Include="IntelliSense\QuickInfoTooltip.cs" />
<Compile Include="IntelliSense\IntelliSenseTextViewExtension.cs" />
```

- [ ] **Step 5: Build to verify**

```powershell
msbuild AxialSqlTools\AxialSqlTools.csproj /p:Configuration=Release /t:Rebuild
```

Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add AxialSqlTools/IntelliSense/QuickInfoProvider.cs AxialSqlTools/IntelliSense/QuickInfoTooltip.cs AxialSqlTools/IntelliSense/IntelliSenseTextViewExtension.cs AxialSqlTools/AxialSqlTools.csproj
git commit -m "feat(intellisense): add tooltip provider, WPF tooltip renderer, and text view extension for hover"
```

---

### Task 7: SSMS IntelliSense Disable Helper

**Files:**
- Create: `AxialSqlTools/IntelliSense/IntelliSenseDisableHelper.cs`
- Modify: `AxialSqlTools/AxialSqlTools.csproj` (register)

**Interfaces:**
- Consumes: nothing (Win32 registry + SettingsManager)
- Produces: `IntelliSenseDisableHelper.DisableSsmsIntelliSense()`, `RestoreSsmsIntelliSense()`, `IsSsmsIntelliSenseDisabled()` → `bool`

- [ ] **Step 1: Write IntelliSenseDisableHelper.cs**

```csharp
using Microsoft.Win32;
using System;

namespace AxialSqlTools
{
    internal static class IntelliSenseDisableHelper
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();
        private static readonly string _keyPath = @"Software\Microsoft\SQL Server Management Studio\22.0_IsoShell\Text Editor\SqlEditor";
        private static readonly string _valueName = "IntelliSenseEnabled";
        private static int? _previousValue;

        public static bool IsSsmsIntelliSenseDisabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false))
                {
                    if (key == null) return false;
                    object value = key.GetValue(_valueName);
                    return value is int intVal && intVal == 0;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to read SSMS IntelliSense registry setting");
                return false;
            }
        }

        public static void DisableSsmsIntelliSense()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
                {
                    if (key == null)
                    {
                        using (var created = Registry.CurrentUser.CreateSubKey(_keyPath))
                        {
                            _previousValue = 1; // default SSMS value is enabled
                            created.SetValue(_valueName, 0, RegistryValueKind.DWord);
                        }
                        return;
                    }

                    object existing = key.GetValue(_valueName);
                    _previousValue = (existing is int intVal) ? intVal : (int?)1;
                    key.SetValue(_valueName, 0, RegistryValueKind.DWord);
                }

                _logger.Info("SSMS built-in IntelliSense disabled via registry");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to disable SSMS IntelliSense via registry — will rely on keyboard interception");
            }
        }

        public static void RestoreSsmsIntelliSense()
        {
            try
            {
                int restoreValue = _previousValue ?? 1;
                using (var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
                {
                    if (key != null)
                    {
                        key.SetValue(_valueName, restoreValue, RegistryValueKind.DWord);
                        _previousValue = null;
                    }
                }

                _logger.Info("SSMS built-in IntelliSense restored via registry");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to restore SSMS IntelliSense via registry");
            }
        }
    }
}
```

- [ ] **Step 2: Register in csproj**

```xml
<Compile Include="IntelliSense\IntelliSenseDisableHelper.cs" />
```

- [ ] **Step 3: Build to verify**

```powershell
msbuild AxialSqlTools\AxialSqlTools.csproj /p:Configuration=Release /t:Rebuild
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add AxialSqlTools/IntelliSense/IntelliSenseDisableHelper.cs AxialSqlTools/AxialSqlTools.csproj
git commit -m "feat(intellisense): add SSMS IntelliSense disable helper via registry"
```

---

### Task 8: IntelliSense Manager (Glue + Lifecycle)

**Files:**
- Create: `AxialSqlTools/IntelliSense/IntelliSenseManager.cs`
- Modify: `AxialSqlTools/AxialSqlTools.csproj` (register)

**Interfaces:**
- Consumes: IntelliSenseKeyHandler, IntelliSenseTextViewExtension, IntelliSenseDisableHelper
- Produces: `IntelliSenseManager` — static manager, `AttachToView(Window)`, `DetachAll()`

- [ ] **Step 1: Write IntelliSenseManager.cs**

```csharp
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;

namespace AxialSqlTools
{
    internal static class IntelliSenseManager
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();
        private static readonly Dictionary<IVsTextView, IntelliSenseHandlerBundle> _bundles
            = new Dictionary<IVsTextView, IntelliSenseHandlerBundle>();
        private static AxialSqlToolsPackage _package;
        private static bool _initialized;

        private sealed class IntelliSenseHandlerBundle
        {
            public IntelliSenseKeyHandler KeyHandler;
            public IntelliSenseTextViewExtension ViewExtension;
        }

        public static void Initialize(AxialSqlToolsPackage package)
        {
            if (_initialized) return;
            _package = package;
            _initialized = true;

            _ = package.JoinableTaskFactory.RunAsync(async () =>
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();

                var settings = SettingsManager.GetIntelliSenseSettings();
                if (settings.enabled && settings.disableSsmsIntelliSense)
                {
                    IntelliSenseDisableHelper.DisableSsmsIntelliSense();
                }
            });
        }

        public static void AttachToView(Window window)
        {
            if (!_initialized || _package == null) return;
            if (window?.Object == null) return;

            var settings = SettingsManager.GetIntelliSenseSettings();
            if (!settings.enabled) return;

            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

                try
                {
                    var docData = GridAccess.GetProperty(window.Object, "DocData");
                    if (docData == null) return;

                    var txtMgr = (IVsTextManager)GridAccess.GetProperty(docData, "TextManager");
                    if (txtMgr == null) return;

                    if (txtMgr.GetActiveView(0, null, out IVsTextView textView) != VSConstants.S_OK
                        || textView == null) return;

                    if (_bundles.ContainsKey(textView)) return; // Already attached

                    var keyHandler = new IntelliSenseKeyHandler(textView, _package);
                    var viewExtension = new IntelliSenseTextViewExtension(textView, _package);

                    _bundles[textView] = new IntelliSenseHandlerBundle
                    {
                        KeyHandler = keyHandler,
                        ViewExtension = viewExtension
                    };

                    _logger.Debug("IntelliSense attached to text view");
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to attach IntelliSense to view");
                }
            });
        }

        public static IntelliSenseKeyHandler GetKeyHandler(IVsTextView textView)
        {
            if (textView != null && _bundles.TryGetValue(textView, out var bundle))
                return bundle.KeyHandler;
            return null;
        }

        public static bool HasKeyHandler(IVsTextView textView)
        {
            return textView != null && _bundles.ContainsKey(textView);
        }

        public static void DetachAll()
        {
            foreach (var bundle in _bundles.Values)
            {
                bundle.ViewExtension?.HideTooltip();
                bundle.KeyHandler?.CloseCompletionWindow();
            }
            _bundles.Clear();
        }

        public static void OnSettingChanged()
        {
            var settings = SettingsManager.GetIntelliSenseSettings();
            if (settings.enabled && settings.disableSsmsIntelliSense)
            {
                IntelliSenseDisableHelper.DisableSsmsIntelliSense();
            }
            else if (!settings.disableSsmsIntelliSense)
            {
                IntelliSenseDisableHelper.RestoreSsmsIntelliSense();
            }
        }
    }
}
```

- [ ] **Step 2: Register in csproj**

```xml
<Compile Include="IntelliSense\IntelliSenseManager.cs" />
```

- [ ] **Step 3: Build to verify**

```powershell
msbuild AxialSqlTools\AxialSqlTools.csproj /p:Configuration=Release /t:Rebuild
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add AxialSqlTools/IntelliSense/IntelliSenseManager.cs AxialSqlTools/AxialSqlTools.csproj
git commit -m "feat(intellisense): add IntelliSenseManager for lifecycle and view attachment"
```

---

### Task 9: Wire into KeypressCommandFilter

**Files:**
- Modify: `AxialSqlTools/Modules/KeypressCommandFilter.cs` (add IntelliSense branch)

**Interfaces:**
- Consumes: `IntelliSenseManager`
- Produces: modified `Exec` method

- [ ] **Step 1: Modify KeypressCommandFilter.cs**

Add member field:

```csharp
// In KeypressCommandFilter class, add:
private readonly AxialSqlToolsPackage _package;
```

Update constructor to accept package:

```csharp
public KeypressCommandFilter(AxialSqlToolsPackage package, IVsTextView textView)
{
    _package = package;  // ADD THIS LINE
    this.textView = textView;
}
```

Modify `Exec` method — add IntelliSense handling before snippet/asterisk:

```csharp
public int Exec(ref Guid cmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
{
    // --- IntelliSense handling (NEW) ---
    if (cmdGroup == VSConstants.VSStd2K)
    {
        var handler = IntelliSenseManager.GetKeyHandler(textView);
        if (handler != null)
        {
            if (nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD
                || nCmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST)
            {
                // Ctrl+Space or member list: force show
                if (handler.ExecForce())
                    return VSConstants.S_OK;
            }
            else if (IsTypingKey(nCmdID) && !handler.IsPopupVisible)
            {
                // Auto-trigger on typing
                // ExecIfEnabled handles debounce internally
            }
        }
    }
    // --- END IntelliSense ---

    if (cmdGroup == VSConstants.VSStd2K && IsSupportedKey(nCmdID))
    {
        // ... existing snippet/asterisk code ...
```

Add IsTypingKey utility method:

```csharp
private static bool IsTypingKey(uint nCmdID)
{
    return nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR;
}
```

**For the auto-trigger on typing**: We need to also intercept TYPECHAR. Modify `QueryStatus` to also enable TYPECHAR, and in `Exec` call `handler.ExecIfEnabled(ref bool handled)` for TYPECHAR.

Full modified `Exec`:

```csharp
public int Exec(ref Guid cmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
{
    if (cmdGroup == VSConstants.VSStd2K)
    {
        var handler = IntelliSenseManager.GetKeyHandler(textView);
        if (handler != null)
        {
            if (nCmdID == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD
                || nCmdID == (uint)VSConstants.VSStd2KCmdID.SHOWMEMBERLIST)
            {
                // Take over these commands — don't let SSMS handle them
                bool handled = false;
                if (handler.ExecForce())
                    return VSConstants.S_OK;
            }
            else if (nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR)
            {
                // Auto-trigger on character input
                bool handled = false;
                if (handler.ExecIfEnabled(ref handled) && handled)
                    return VSConstants.S_OK;
            }
        }
    }

    if (cmdGroup == VSConstants.VSStd2K && IsSupportedKey(nCmdID))
    {
        // ... existing snippet/asterisk code unchanged ...
```

- [ ] **Step 2: Build to verify**

```powershell
msbuild AxialSqlTools\AxialSqlTools.csproj /p:Configuration=Release /t:Rebuild
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add AxialSqlTools/Modules/KeypressCommandFilter.cs
git commit -m "feat(intellisense): wire IntelliSense key handler into KeypressCommandFilter"
```

---

### Task 10: Wire into AxialSqlToolsPackage.cs

**Files:**
- Modify: `AxialSqlTools/AxialSqlToolsPackage.cs` (add IntelliSense initialization + window activation hook)

**Interfaces:**
- Consumes: `IntelliSenseManager`
- Produces: modified Package initialization and WindowActivated handler

- [ ] **Step 1: Modify AxialSqlToolsPackage.cs**

In `InitializeAsync`, after all existing command initializations, add:

```csharp
// Initialize IntelliSense (after all commands are registered)
try
{
    IntelliSenseManager.Initialize(this);
    _logger.Info("IntelliSense manager initialized");
}
catch (Exception ex)
{
    _logger.Error(ex, "Failed to initialize IntelliSense manager");
}
```

In `WindowActivated_Event`, after snippet filter registration block, add:

```csharp
// Attach IntelliSense to the active query window
try
{
    IntelliSenseManager.AttachToView(GotFocus);
}
catch (Exception ex)
{
    _logger.Error(ex, "Failed to attach IntelliSense to window");
}
```

- [ ] **Step 2: Build to verify**

```powershell
msbuild AxialSqlTools\AxialSqlTools.csproj /p:Configuration=Release /t:Rebuild
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add AxialSqlTools/AxialSqlToolsPackage.cs
git commit -m "feat(intellisense): wire IntelliSense manager into package lifecycle"
```

---

### Task 11: Settings Manager + Settings UI

**Files:**
- Modify: `AxialSqlTools/Modules/SettingsManager.cs` (add IntelliSenseSettings class + getter/setter)
- Modify: `AxialSqlTools/WindowSettings/SettingsWindowControl.xaml` (add IntelliSense GroupBox in General tab)
- Modify: `AxialSqlTools/WindowSettings/SettingsWindowControl.xaml.cs` (add load/save handlers)

**Interfaces:**
- Consumes: existing SettingsManager JSON read/write pattern
- Produces: `SettingsManager.IntelliSenseSettings` class, `GetIntelliSenseSettings()`, `SaveIntelliSenseSettings()`, UI controls

- [ ] **Step 1: Add IntelliSenseSettings to SettingsManager.cs**

Add after existing `AsteriskExpansionSettings` class:

```csharp
public class IntelliSenseSettings
{
    public bool enabled = true;
    public bool disableSsmsIntelliSense = true;
    public bool autoTrigger = true;
    public int autoTriggerDelayMs = 200;
    public bool hoverTooltipEnabled = true;
    public int hoverTooltipDelayMs = 500;
    public bool includeKeywords = true;
    public bool includeSystemObjects = true;
    public bool includeLocalTempTables = true;
    public bool includeLocalVariables = true;
    public int maxCompletionItems = 50;
}

public static IntelliSenseSettings GetIntelliSenseSettings()
{
    try
    {
        var json = ReadSettingsJson();
        if (json?["intelliSense"] == null)
            return new IntelliSenseSettings();

        return json["intelliSense"].ToObject<IntelliSenseSettings>()
            ?? new IntelliSenseSettings();
    }
    catch
    {
        return new IntelliSenseSettings();
    }
}

public static void SaveIntelliSenseSettings(IntelliSenseSettings settings)
{
    try
    {
        var json = ReadSettingsJson() ?? new JObject();
        var node = JToken.FromObject(settings ?? new IntelliSenseSettings());
        json["intelliSense"] = node;
        WriteSettingsJson(json);
    }
    catch { }
}
```

- [ ] **Step 2: Add IntelliSense UI to SettingsWindowControl.xaml**

In the General Tab's Grid, add a new row after the language section (rows 3+):

See spec section 8.2 for the exact XAML.

- [ ] **Step 3: Add code-behind handlers in SettingsWindowControl.xaml.cs**

In `LoadSavedSettings()` method, add:

```csharp
var intelliSenseSettings = SettingsManager.GetIntelliSenseSettings();
IntelliSenseEnabled.IsChecked = intelliSenseSettings.enabled;
IntelliSenseDisableSsms.IsChecked = intelliSenseSettings.disableSsmsIntelliSense;
IntelliSenseAutoTrigger.IsChecked = intelliSenseSettings.autoTrigger;
IntelliSenseHoverTooltip.IsChecked = intelliSenseSettings.hoverTooltipEnabled;
IntelliSenseIncludeKeywords.IsChecked = intelliSenseSettings.includeKeywords;
IntelliSenseIncludeSystemObjects.IsChecked = intelliSenseSettings.includeSystemObjects;
```

Add save handler:

```csharp
private void Button_SaveIntelliSenseSettings_Click(object sender, RoutedEventArgs e)
{
    SettingsManager.SaveIntelliSenseSettings(new SettingsManager.IntelliSenseSettings
    {
        enabled = IntelliSenseEnabled.IsChecked.GetValueOrDefault(),
        disableSsmsIntelliSense = IntelliSenseDisableSsms.IsChecked.GetValueOrDefault(),
        autoTrigger = IntelliSenseAutoTrigger.IsChecked.GetValueOrDefault(),
        hoverTooltipEnabled = IntelliSenseHoverTooltip.IsChecked.GetValueOrDefault(),
        includeKeywords = IntelliSenseIncludeKeywords.IsChecked.GetValueOrDefault(),
        includeSystemObjects = IntelliSenseIncludeSystemObjects.IsChecked.GetValueOrDefault()
    });

    IntelliSenseManager.OnSettingChanged();
    SavedMessage();
}
```

- [ ] **Step 4: Build to verify**

```powershell
msbuild AxialSqlTools\AxialSqlTools.csproj /p:Configuration=Release /t:Rebuild
```

Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add AxialSqlTools/Modules/SettingsManager.cs AxialSqlTools/WindowSettings/SettingsWindowControl.xaml AxialSqlTools/WindowSettings/SettingsWindowControl.xaml.cs
git commit -m "feat(intellisense): add IntelliSense settings to SettingsManager and Settings UI"
```

---

### Task 12: Build, Install, and Manual Verification

**Files:**
- No new files — use existing build/pack script

- [ ] **Step 1: Close SSMS**

```powershell
taskkill /F /IM ssms.exe
```

- [ ] **Step 2: Clean build**

```powershell
powershell -File skills/axial-build-release/scripts/pack-release.ps1
```

Expected: Build succeeds, VSIX placed in `bin/Release/`.

- [ ] **Step 3: Install VSIX**

```powershell
"D:\Program Files\Microsoft SQL Server Management Studio 22\Common7\IDE\VSIXInstaller.exe" /quiet /admin AxialSqlTools\bin\Release\AxialSqlTools.vsix
```

- [ ] **Step 4: Verify SSMS loads with extension**

Launch SSMS 22, open a query window, connect to a database. Check:
- AxialSQL Tools toolbar is visible
- Settings window → General tab → IntelliSense section shows with all checkboxes

- [ ] **Step 5: Manual verification checklist**

| Scenario | Expected Result |
|---|---|
| Type `SELECT * FROM` and wait 200ms | Popup shows table/view list from current DB |
| Type `FROM db.` | Popup lists tables in that database |
| Type `EXEC ` (space after) | Popup lists stored procedures |
| Type `SELECT` then type column prefix | Popup lists columns from FROM tables |
| Type `alias.` after FROM ... AS alias | Popup lists columns for that table |
| Ctrl+Space | Force show popup regardless of debounce |
| Hover mouse over table name (500ms) | Tooltip shows CREATE TABLE + description |
| Hover mouse over column name | Tooltip shows data type + description |
| Hover mouse over proc name | Tooltip shows parameters + CREATE script |
| Type `GO` + newline + start typing | Keywords popup resets |
| Press Escape while popup visible | Popup closes, text unchanged |
| Settings → IntelliSense → Disable → Save → restart SSMS | SSMS built-in IntelliSense restored |

- [ ] **Step 6: Commit**

```bash
git status
# If any remaining changes exist from build artifacts or final tweaks:
git add -A
git commit -m "chore(intellisense): final build verification and tweaks"
```
