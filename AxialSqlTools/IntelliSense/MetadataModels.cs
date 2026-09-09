using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace AxialSqlTools
{
    namespace IntelliSense
    {
        /// <summary>
        /// IntelliSense 用户设置。持久化于 %APPDATA%\AxialSqlTools\settings.json 的 intelliSense 节点。
        /// 不放进 SettingsManager（避免注册表/JSON 后端混用）。
        /// </summary>
        public class IntelliSenseSettings
        {
            public bool enabled = true;
            /// <summary>已合并到 enabled；保存时与 enabled 同步，仅作 JSON 向后兼容。</summary>
            public bool disableSsmsIntelliSense = true;
            /// <summary>已合并到 enabled；启用 Axial 即输入时自动补全。仅作 JSON 向后兼容。</summary>
            public bool autoTrigger = true;
            public int autoTriggerDelayMs = 200;
            public bool hoverTooltipEnabled = true;
            public int hoverTooltipDelayMs = 500;
            public bool includeKeywords = true;
            public bool includeSystemObjects = true;
            public bool includeLocalTempTables = true;
            public bool includeLocalVariables = true;
            /// <summary>选中补全项时插入文本是否带方括号 []。</summary>
            public bool bracketIdentifiers = true;
            public int maxCompletionItems = 50;
            /// <summary>磁盘缓存自动刷新间隔（天）。默认 7；0 = 关闭自动更新。</summary>
            public int cacheRefreshDays = 7;
        }

        /// <summary>元数据缓存，按 (Server, Database) 维度存储。</summary>
        public class MetadataCatalog
        {
            public string Server { get; set; }
            public string Database { get; set; }
            public DateTime BuiltAt { get; set; }
            /// <summary>是否已成功索引（空库也有此标记，避免被当作「未缓存」反复重建）。</summary>
            public bool IsIndexed { get; set; }

            public List<TableColumnInfo> Tables { get; set; } = new List<TableColumnInfo>();
            public List<TableColumnInfo> Views { get; set; } = new List<TableColumnInfo>();
            public List<RoutineInfo> Procedures { get; set; } = new List<RoutineInfo>();
            public List<RoutineInfo> ScalarFunctions { get; set; } = new List<RoutineInfo>();
            public List<RoutineInfo> TableFunctions { get; set; } = new List<RoutineInfo>();
            public List<DatabaseObjectInfo> Synonyms { get; set; } = new List<DatabaseObjectInfo>();
            /// <summary>sys.schemas 全量架构名（含 sys / guest / db_owner 等），与表一样从服务器拉取。</summary>
            public List<string> Schemas { get; set; } = new List<string>();
            /// <summary>是否已从服务器加载 sys / INFORMATION_SCHEMA 对象（旧缓存为 false，需重建）。</summary>
            public bool SystemObjectsLoaded { get; set; }
            /// <summary>是否已从服务器加载 sys 例程（过程/函数/DMF）。旧缓存为 false，需重建。</summary>
            public bool SystemRoutinesLoaded { get; set; }
            /// <summary>LoadRoutines 是否成功跑完（失败时可能有表无过程，EXEC 需重建）。</summary>
            public bool RoutinesLoaded { get; set; }

            public bool IsEmpty =>
                Tables.Count == 0 && Views.Count == 0 && Procedures.Count == 0 &&
                ScalarFunctions.Count == 0 && TableFunctions.Count == 0 && Synonyms.Count == 0;

            /// <summary>缓存里是否已有 sys / INFORMATION_SCHEMA 表或视图（空名单不能当「已加载」）。</summary>
            public bool HasSystemCatalogObjects()
            {
                return ContainsSystemSchema(Tables) || ContainsSystemSchema(Views);
            }

            /// <summary>缓存里是否已有 sys / INFORMATION_SCHEMA 过程或函数。</summary>
            public bool HasSystemRoutines()
            {
                return ContainsSystemSchemaRoutine(Procedures)
                    || ContainsSystemSchemaRoutine(ScalarFunctions)
                    || ContainsSystemSchemaRoutine(TableFunctions);
            }

            private static bool ContainsSystemSchema(List<TableColumnInfo> list)
            {
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (IsSystemSchema(list[i].Schema)) return true;
                }
                return false;
            }

            private static bool ContainsSystemSchemaRoutine(List<RoutineInfo> list)
            {
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (IsSystemSchema(list[i].Schema)) return true;
                }
                return false;
            }

            private static bool IsSystemSchema(string schema)
            {
                return string.Equals(schema, "sys", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(schema, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>在所有表/视图里查找匹配 schema.name 的对象（含列）。</summary>
            public TableColumnInfo FindTableOrView(string schema, string name)
            {
                return FindIn(Tables, schema, name) ?? FindIn(Views, schema, name);
            }

            /// <summary>查找存储过程（schema 空则按名称匹配任一架构）。</summary>
            public RoutineInfo FindProcedure(string schema, string name)
            {
                if (Procedures == null || string.IsNullOrEmpty(name)) return null;
                foreach (var p in Procedures)
                {
                    if (!StringComparer.OrdinalIgnoreCase.Equals(p.Name, name)) continue;
                    if (string.IsNullOrEmpty(schema)
                        || StringComparer.OrdinalIgnoreCase.Equals(p.Schema, schema))
                        return p;
                }
                return null;
            }

            private TableColumnInfo FindIn(List<TableColumnInfo> list, string schema, string name)
            {
                if (list == null) return null;
                foreach (var item in list)
                {
                    if (StringComparer.OrdinalIgnoreCase.Equals(item.Name, name) &&
                        (string.IsNullOrEmpty(schema) ||
                         StringComparer.OrdinalIgnoreCase.Equals(item.Schema, schema)))
                    {
                        return item;
                    }
                }
                return null;
            }
        }

        /// <summary>数据库对象基类（表/视图/同义词）。</summary>
        public class DatabaseObjectInfo
        {
            public string Schema { get; set; }
            public string Name { get; set; }
            public string Description { get; set; } // MS_Description

            public string QualifiedName => string.IsNullOrEmpty(Schema)
                ? Name
                : Schema + "." + Name;

            public string BracketedName => string.IsNullOrEmpty(Schema)
                ? "[" + Name + "]"
                : "[" + Schema + "].[" + Name + "]";
        }

        /// <summary>表/视图，带列与索引。</summary>
        public class TableColumnInfo : DatabaseObjectInfo
        {
            public bool IsView { get; set; }
            /// <summary>视图定义（OBJECT_DEFINITION），仅视图有值；按需拉取，不写入磁盘缓存。</summary>
            [JsonIgnore]
            public string Definition { get; set; }
            public List<ColumnInfo> Columns { get; set; } = new List<ColumnInfo>();
            public List<IndexInfo> Indexes { get; set; } = new List<IndexInfo>();
        }

        public class IndexInfo
        {
            public string Name { get; set; }
            public bool IsUnique { get; set; }
            public bool IsPrimaryKey { get; set; }
            public List<string> Columns { get; set; } = new List<string>();
        }

        public class ColumnInfo
        {
            public string Name { get; set; }
            public string DataType { get; set; }
            public bool Nullable { get; set; }
            public string DefaultValue { get; set; }
            public string Description { get; set; } // MS_Description
            public bool IsIdentity { get; set; }
            public bool IsPrimaryKey { get; set; }

            public string BracketedName => "[" + Name + "]";
        }

        public enum RoutineKind
        {
            Procedure,
            ScalarFunction,
            TableFunction
        }

        /// <summary>存储过程 / 函数，带参数与定义。</summary>
        public class RoutineInfo : DatabaseObjectInfo
        {
            public RoutineKind Kind { get; set; }
            public List<RoutineParam> Parameters { get; set; } = new List<RoutineParam>();
            /// <summary>CREATE 脚本；按需拉取，不写入磁盘缓存。</summary>
            [JsonIgnore]
            public string Definition { get; set; }
        }

        public class RoutineParam
        {
            public string Name { get; set; }
            public string DataType { get; set; }
            public bool HasDefault { get; set; }
            public string DefaultValue { get; set; }
            public bool IsOutput { get; set; }
        }
    }
}
