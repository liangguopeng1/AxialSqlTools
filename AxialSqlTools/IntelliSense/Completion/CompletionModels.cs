using System;
using System.Collections.Generic;

namespace AxialSqlTools
{
    namespace IntelliSense
    {
        /// <summary>补全结果。</summary>
        public class CompletionResult
        {
            public CompletionContext Context;
            public string Prefix;
            public List<CompletionItem> Items;
            public int ReplaceStartOffset = -1;
            public int ReplaceEndOffset = -1;
            public bool IsEmpty => Items == null || Items.Count == 0;
        }

        /// <summary>当前批次解析出的本地符号。</summary>
        public class LocalSymbols
        {
            public List<CteInfo> Ctes = new List<CteInfo>();
            public List<LocalTableInfo> TempTables = new List<LocalTableInfo>();
            public List<LocalTableInfo> TableVariables = new List<LocalTableInfo>();
            public List<string> ScalarVariables = new List<string>();
            /// <summary>当前语句 FROM 子句的别名映射：alias(小写) → 限定表名。</summary>
            public Dictionary<string, TableRef> Aliases = new Dictionary<string, TableRef>(StringComparer.OrdinalIgnoreCase);
        }

        public class CteInfo
        {
            public string Name;
            public List<string> ColumnNames = new List<string>();
        }

        public class LocalTableInfo
        {
            public string Name; // #t / @t
            public List<string> ColumnNames = new List<string>();
        }

        public class TableRef
        {
            public string Database;
            public string Schema;
            public string Name;
        }
    }
}
