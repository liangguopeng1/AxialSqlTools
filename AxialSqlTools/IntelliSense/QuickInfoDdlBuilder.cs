using System.Text;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>生成悬停 ToolTip 用的表/列 DDL 片段（含注释与索引）。</summary>
    internal static class QuickInfoDdlBuilder
    {
        public static string BuildCreateTable(TableColumnInfo table)
        {
            if (table == null) return string.Empty;
            if (table.IsView)
                return BuildCreateView(table);
            if (table.Columns == null || table.Columns.Count == 0)
                return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine("-- auto-generated definition");
            sb.AppendLine("create table " + Qualify(table.Schema, table.Name) + " (");
            for (int i = 0; i < table.Columns.Count; i++)
            {
                var col = table.Columns[i];
                sb.Append("    ").Append(col.Name).Append(" ").Append(col.DataType ?? string.Empty);
                if (col.IsIdentity) sb.Append(" identity");
                if (col.IsPrimaryKey) sb.Append(" primary key");
                if (!col.Nullable) sb.Append(" not null");
                if (i < table.Columns.Count - 1) sb.AppendLine(",");
                else sb.AppendLine();
            }
            sb.AppendLine(")");
            sb.AppendLine("go");
            bool wroteDesc = false;
            if (!string.IsNullOrEmpty(table.Description))
            {
                sb.AppendLine();
                sb.AppendLine("exec sp_addextendedproperty 'MS_Description', N'" + EscapeSqlString(table.Description)
                    + "', 'SCHEMA', '" + EscapeSqlIdent(table.Schema ?? "dbo")
                    + "', 'TABLE', '" + EscapeSqlIdent(table.Name) + "'");
                sb.AppendLine("go");
                wroteDesc = true;
            }
            foreach (var col in table.Columns)
            {
                if (string.IsNullOrEmpty(col.Description)) continue;
                if (!wroteDesc) sb.AppendLine();
                wroteDesc = true;
                sb.AppendLine("exec sp_addextendedproperty 'MS_Description', N'" + EscapeSqlString(col.Description)
                    + "', 'SCHEMA', '" + EscapeSqlIdent(table.Schema ?? "dbo")
                    + "', 'TABLE', '" + EscapeSqlIdent(table.Name)
                    + "', 'COLUMN', '" + EscapeSqlIdent(col.Name) + "'");
                sb.AppendLine("go");
            }
            if (table.Indexes != null && table.Indexes.Count > 0)
            {
                sb.AppendLine();
                foreach (var idx in table.Indexes)
                {
                    if (idx == null || string.IsNullOrEmpty(idx.Name) || idx.Columns == null || idx.Columns.Count == 0)
                        continue;
                    if (idx.IsPrimaryKey) continue;
                    sb.Append("create ");
                    if (idx.IsUnique) sb.Append("unique ");
                    sb.Append("index ").Append(idx.Name)
                        .Append(" on ").Append(Qualify(table.Schema, table.Name))
                        .Append(" (").Append(string.Join(", ", idx.Columns)).AppendLine(")");
                    sb.AppendLine("go");
                }
            }
            return sb.ToString().TrimEnd();
        }

        public static string BuildCreateView(TableColumnInfo view)
        {
            if (view == null) return string.Empty;
            if (!string.IsNullOrEmpty(view.Definition))
                return view.Definition.Trim();
            // 无定义时至少给出列结构提示
            if (view.Columns == null || view.Columns.Count == 0)
                return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine("-- view definition unavailable");
            sb.AppendLine("create view " + Qualify(view.Schema, view.Name) + " as");
            sb.Append("select ");
            for (int i = 0; i < view.Columns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(view.Columns[i].Name);
            }
            return sb.ToString().TrimEnd();
        }

        public static string BuildColumnDdl(TableColumnInfo table, ColumnInfo col)
        {
            if (table == null || col == null) return string.Empty;
            string objKind = table.IsView ? "view" : "table";
            string propKind = table.IsView ? "VIEW" : "TABLE";
            var sb = new StringBuilder();
            sb.AppendLine("alter " + objKind + " " + Qualify(table.Schema, table.Name));
            sb.Append("  add ").Append(col.Name).Append(" ").Append(col.DataType ?? string.Empty);
            if (col.IsIdentity) sb.Append(" identity");
            if (col.IsPrimaryKey) sb.Append(" primary key");
            if (!col.Nullable) sb.Append(" not null");
            sb.AppendLine();
            sb.AppendLine("go");
            if (!string.IsNullOrEmpty(col.Description))
            {
                sb.AppendLine();
                sb.AppendLine("exec sp_addextendedproperty 'MS_Description', N'" + EscapeSqlString(col.Description)
                    + "', 'SCHEMA', '" + EscapeSqlIdent(table.Schema ?? "dbo")
                    + "', '" + propKind + "', '" + EscapeSqlIdent(table.Name)
                    + "', 'COLUMN', '" + EscapeSqlIdent(col.Name) + "'");
                sb.Append("go");
            }
            return sb.ToString().TrimEnd();
        }

        private static string Qualify(string schema, string name)
        {
            string sch = string.IsNullOrEmpty(schema) ? "dbo" : schema;
            return sch + "." + name;
        }

        private static string EscapeSqlIdent(string value)
        {
            return string.IsNullOrEmpty(value) ? value : value.Replace("'", "''");
        }

        private static string EscapeSqlString(string value)
        {
            return string.IsNullOrEmpty(value) ? value : value.Replace("'", "''");
        }
    }
}
