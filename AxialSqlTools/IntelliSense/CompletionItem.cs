using System;

namespace AxialSqlTools
{
    namespace IntelliSense
    {
        /// <summary>补全候选项。</summary>
        public class CompletionItem
        {
            /// <summary>弹框列表中显示的文本。</summary>
            public string DisplayText { get; set; }

            /// <summary>选中后插入编辑器的文本（含 [dbo].[t] 等括号化形式）。</summary>
            public string InsertText { get; set; }

            /// <summary>右侧详情面板内容（类型/参数/描述）。</summary>
            public string Description { get; set; }

            /// <summary>右侧详情：所属数据库（列/表等有来源时）。</summary>
            public string SourceDatabase { get; set; }

            /// <summary>右侧详情：所属表/视图名（可含 schema）。</summary>
            public string SourceTable { get; set; }

            public CompletionKind Kind { get; set; }

            /// <summary>列表左侧图标符号（基于 Kind）。</summary>
            public string Glyph => KindToGlyph(Kind);

            /// <summary>右侧详情用的类型中文标签。</summary>
            public string KindLabel => KindToLabel(Kind);

            private static string KindToGlyph(CompletionKind k)
            {
                switch (k)
                {
                    case CompletionKind.Table: return "T";
                    case CompletionKind.View: return "V";
                    case CompletionKind.Column: return "C";
                    case CompletionKind.Procedure: return "P";
                    case CompletionKind.ScalarFunction: return "F";
                    case CompletionKind.TableFunction: return "F";
                    case CompletionKind.Synonym: return "S";
                    case CompletionKind.Variable: return "@";
                    case CompletionKind.Database: return "D";
                    case CompletionKind.Schema: return ".";
                    case CompletionKind.Parameter: return "?";
                    case CompletionKind.Snippet: return "{}";
                    default: return "K"; // Keyword
                }
            }

            private static string KindToLabel(CompletionKind k)
            {
                switch (k)
                {
                    case CompletionKind.Table: return "表";
                    case CompletionKind.View: return "视图";
                    case CompletionKind.Column: return "列";
                    case CompletionKind.Procedure: return "存储过程";
                    case CompletionKind.ScalarFunction: return "函数";
                    case CompletionKind.TableFunction: return "表值函数";
                    case CompletionKind.Synonym: return "同义词";
                    case CompletionKind.Variable: return "变量";
                    case CompletionKind.Database: return "数据库";
                    case CompletionKind.Schema: return "架构";
                    case CompletionKind.Parameter: return "参数";
                    case CompletionKind.Snippet: return "片段";
                    case CompletionKind.Keyword: return "关键字";
                    default: return "项";
                }
            }

            /// <summary>插入后光标在 InsertText 内的偏移（片段 / 带参数的存储过程）。</summary>
            public int SnippetCursorOffset { get; set; } = -1;

            /// <summary>关联的片段前缀，用于提交时精确匹配（仅 Kind=Snippet 有效）。</summary>
            public string SnippetPrefix { get; set; }

            public CompletionItem()
            {
                DisplayText = string.Empty;
                InsertText = string.Empty;
                Description = string.Empty;
            }

            public CompletionItem(string displayText, string insertText, CompletionKind kind, string description = null)
            {
                DisplayText = displayText ?? string.Empty;
                InsertText = insertText ?? string.Empty;
                Kind = kind;
                Description = description ?? string.Empty;
            }
        }

        public enum CompletionKind
        {
            Keyword,
            Table,
            View,
            Column,
            Procedure,
            ScalarFunction,
            TableFunction,
            Synonym,
            Variable,
            Database,
            Schema,
            Parameter,
            Snippet
        }

        /// <summary>CompletionEngine 判定的光标上下文类型（对应 spec §4 映射表）。</summary>
        public enum CompletionContext
        {
            Unknown,
            BatchStart,          // 新批次 / GO 后
            AfterCreate,         // CREATE 后
            AfterAlter,          // ALTER 后
            SelectElements,      // SELECT 后、FROM 前
            FromClause,          // FROM / JOIN / ,
            MemberAccess,        // schema.name. 或 alias.
            AfterExec,           // EXEC / EXECUTE 后
            InsertTarget,        // INSERT INTO 后
            UpdateTarget,        // UPDATE 后、SET 前
            DeleteTarget,        // DELETE FROM 后
            WhereClause,         // WHERE / ON / HAVING / AND / OR 后
            UpdateSet,           // SET（UPDATE 内）后
            OrderByGroupBy,      // ORDER BY / GROUP BY 后
            AfterUse,            // USE 后
            LocalVariable,       // @ 后
            LocalMemberAccess    // CTE.#t.@t. 后
        }
    }
}
