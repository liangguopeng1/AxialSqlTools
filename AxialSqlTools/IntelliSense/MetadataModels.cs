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
            public int autoRefreshMinutes = 0; // 0 = 关闭后台刷新
        }

        /// <summary>元数据缓存，按 (Server, Database) 维度存储。</summary>
        public class MetadataCatalog
        {
            public string Server { get; set; }
            public string Database { get; set; }
            public DateTime BuiltAt { get; set; }

            public List<TableColumnInfo> Tables { get; set; } = new List<TableColumnInfo>();
            public List<TableColumnInfo> Views { get; set; } = new List<TableColumnInfo>();
            public List<RoutineInfo> Procedures { get; set; } = new List<RoutineInfo>();
            public List<RoutineInfo> ScalarFunctions { get; set; } = new List<RoutineInfo>();
            public List<RoutineInfo> TableFunctions { get; set; } = new List<RoutineInfo>();
            public List<DatabaseObjectInfo> Synonyms { get; set; } = new List<DatabaseObjectInfo>();

            public bool IsEmpty =>
                Tables.Count == 0 && Views.Count == 0 && Procedures.Count == 0 &&
                ScalarFunctions.Count == 0 && TableFunctions.Count == 0 && Synonyms.Count == 0;

            /// <summary>在所有表/视图里查找匹配 schema.name 的对象（含列）。</summary>
            public TableColumnInfo FindTableOrView(string schema, string name)
            {
                return FindIn(Tables, schema, name) ?? FindIn(Views, schema, name);
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

        /// <summary>表/视图，带列。</summary>
        public class TableColumnInfo : DatabaseObjectInfo
        {
            public List<ColumnInfo> Columns { get; set; } = new List<ColumnInfo>();
        }

        public class ColumnInfo
        {
            public string Name { get; set; }
            public string DataType { get; set; }
            public bool Nullable { get; set; }
            public string DefaultValue { get; set; }
            public string Description { get; set; } // MS_Description

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
            public string Definition { get; set; } // CREATE 脚本
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
