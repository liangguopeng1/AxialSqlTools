using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 悬停信息：定位光标 token，匹配元数据对象，返回描述文本。
    /// 复用 MetadataCatalogService 缓存。任何失败返回 null（不弹 ToolTip）。
    /// </summary>
    public class QuickInfoProvider
    {
        private readonly TSql170Parser _parser = new TSql170Parser(true);

        public string GetQuickInfo(string fullText, int caretOffset, MetadataCatalog catalog)
        {
            if (string.IsNullOrEmpty(fullText) || catalog == null || caretOffset < 0)
            {
                return null;
            }

            try
            {
                TSqlScript script;
                using (var reader = new StringReader(fullText))
                {
                    script = _parser.Parse(reader, out var errors) as TSqlScript;
                }
                if (script == null) return null;

                var tokens = script.ScriptTokenStream;
                if (tokens == null) return null;

                // 找光标所在/紧邻的标识符 token
                TSqlParserToken identToken = null;
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null) continue;
                    if (caretOffset >= t.Offset && caretOffset <= t.Offset + t.Text.Length &&
                        (t.TokenType == TSqlTokenType.Identifier || t.TokenType == TSqlTokenType.QuotedIdentifier))
                    {
                        identToken = t;
                        break;
                    }
                }
                if (identToken == null) return null;

                // 查前一个 token 是否 "."，组成 schema.name
                string name = identToken.Text;
                string schema = null;
                for (int i = tokens.IndexOf(identToken) - 1; i >= 0; i--)
                {
                    var t = tokens[i];
                    if (t == null) continue;
                    if (t.TokenType == TSqlTokenType.WhiteSpace) continue;
                    if (t.TokenType == TSqlTokenType.Dot)
                    {
                        // 再往前找 schema 标识符
                        for (int j = i - 1; j >= 0; j--)
                        {
                            var s = tokens[j];
                            if (s == null) continue;
                            if (s.TokenType == TSqlTokenType.WhiteSpace) continue;
                            if (s.TokenType == TSqlTokenType.Identifier || s.TokenType == TSqlTokenType.QuotedIdentifier)
                            {
                                schema = s.Text;
                            }
                            break;
                        }
                    }
                    break;
                }

                return LookupObject(catalog, schema, name);
            }
            catch
            {
                return null;
            }
        }

        private string LookupObject(MetadataCatalog catalog, string schema, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string cleanName = name.Trim('[', ']');

            // 表 / 视图
            var tcol = catalog.FindTableOrView(schema, cleanName);
            if (tcol != null)
            {
                return BuildTableDescription(tcol);
            }

            // 例程
            var routines = catalog.Procedures.Concat(catalog.ScalarFunctions).Concat(catalog.TableFunctions);
            foreach (var r in routines)
            {
                if (string.Equals(r.Name, cleanName, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(schema) || string.Equals(r.Schema, schema, StringComparison.OrdinalIgnoreCase)))
                {
                    return BuildRoutineDescription(r);
                }
            }

            // 列：在所有表/视图里找
            foreach (var t in catalog.Tables.Concat(catalog.Views))
            {
                foreach (var c in t.Columns)
                {
                    if (string.Equals(c.Name, cleanName, StringComparison.OrdinalIgnoreCase))
                    {
                        return BuildColumnDescription(t, c);
                    }
                }
            }

            return null;
        }

        private static string BuildTableDescription(TableColumnInfo t)
        {
            var sb = new StringBuilder();
            sb.AppendLine(t.QualifiedName);
            if (!string.IsNullOrEmpty(t.Description))
            {
                sb.AppendLine();
                sb.AppendLine(t.Description);
            }
            if (t.Columns != null && t.Columns.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("列:");
                foreach (var c in t.Columns)
                {
                    sb.Append("  ").Append(c.Name).Append(" ").AppendLine(c.DataType);
                }
            }
            return sb.ToString();
        }

        private static string BuildColumnDescription(TableColumnInfo t, ColumnInfo c)
        {
            var sb = new StringBuilder();
            sb.Append(c.Name).Append(" ").Append(c.DataType);
            sb.Append(c.Nullable ? " NULL" : " NOT NULL");
            if (!string.IsNullOrEmpty(c.DefaultValue))
            {
                sb.Append(" DEFAULT ").Append(c.DefaultValue);
            }
            sb.AppendLine();
            sb.Append("所属: ").AppendLine(t.QualifiedName);
            if (!string.IsNullOrEmpty(c.Description))
            {
                sb.AppendLine();
                sb.Append(c.Description);
            }
            return sb.ToString();
        }

        private static string BuildRoutineDescription(RoutineInfo r)
        {
            var sb = new StringBuilder();
            sb.Append(r.Kind == RoutineKind.Procedure ? "存储过程: " : "函数: ");
            sb.AppendLine(r.QualifiedName);
            if (r.Parameters != null && r.Parameters.Count > 0)
            {
                sb.AppendLine("参数:");
                foreach (var p in r.Parameters)
                {
                    sb.Append("  ").Append(p.Name).Append(" ").Append(p.DataType);
                    if (p.IsOutput) sb.Append(" OUTPUT");
                    sb.AppendLine();
                }
            }
            if (!string.IsNullOrEmpty(r.Description))
            {
                sb.AppendLine();
                sb.Append(r.Description);
            }
            return sb.ToString();
        }
    }
}
