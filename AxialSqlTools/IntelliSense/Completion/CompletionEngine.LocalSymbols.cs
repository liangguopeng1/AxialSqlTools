using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AxialSqlTools
{
    namespace IntelliSense
    {
        public partial class CompletionEngine
        {
            #region 本地符号

            private LocalSymbols ExtractLocalSymbols(TSqlScript script)
            {
                var local = new LocalSymbols();
                if (script == null) return local;

                foreach (var batch in script.Batches)
                {
                    foreach (var stmt in batch.Statements)
                    {
                        CollectFromStatement(stmt, local);
                    }
                }
                return local;
            }

            /// <summary>当前切片 AST + 本 GO 批次光标前的 #临时表 / 表变量 / 变量（大脚本切片后 AST 看不到前文）。</summary>
            private LocalSymbols BuildLocalSymbols(TSqlScript script, string fullText, int caret)
            {
                var local = ExtractLocalSymbols(script);
                EnrichLocalSymbolsFromBatch(local, fullText, caret);
                return local;
            }

            private void EnrichLocalSymbolsFromBatch(LocalSymbols local, string fullText, int caret)
            {
                if (local == null || string.IsNullOrEmpty(fullText)) return;
                if (!TryGetBatchAt(fullText, caret, out string batch, out int batchStart))
                    return;
                CollectLocalSymbolsFromText(batch, caret - batchStart, local);
            }

            private static void CollectLocalSymbolsFromText(string text, int limit, LocalSymbols local)
            {
                if (string.IsNullOrEmpty(text) || local == null || limit <= 0) return;
                int n = Math.Min(limit, text.Length);
                bool inLineComment = false;
                bool inBlockComment = false;
                bool inString = false;
                int i = 0;
                while (i < n)
                {
                    char c = text[i];
                    char next = i + 1 < n ? text[i + 1] : '\0';
                    if (inLineComment)
                    {
                        if (c == '\n' || c == '\r') inLineComment = false;
                        i++;
                        continue;
                    }
                    if (inBlockComment)
                    {
                        if (c == '*' && next == '/') { inBlockComment = false; i += 2; continue; }
                        i++;
                        continue;
                    }
                    if (inString)
                    {
                        if (c == '\'')
                        {
                            if (next == '\'') i += 2;
                            else { inString = false; i++; }
                            continue;
                        }
                        i++;
                        continue;
                    }
                    if (c == '-' && next == '-') { inLineComment = true; i += 2; continue; }
                    if (c == '/' && next == '*') { inBlockComment = true; i += 2; continue; }
                    if (c == '\'') { inString = true; i++; continue; }
                    if ((c == 'N' || c == 'n') && next == '\'')
                    {
                        bool identBefore = i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
                        if (!identBefore) { inString = true; i += 2; continue; }
                    }
                    if (KeywordAt(text, i, n, "CREATE"))
                    {
                        int j = SkipWs(text, i + 6, n);
                        if (KeywordAt(text, j, n, "TABLE"))
                        {
                            j = SkipWs(text, j + 5, n);
                            string name = ReadSqlName(text, ref j, n);
                            if (!string.IsNullOrEmpty(name) && name[0] == '#')
                            {
                                j = SkipWs(text, j, n);
                                var cols = new List<string>();
                                if (j < n && text[j] == '(')
                                    ParseTableColumnNames(text, ref j, n, cols);
                                MergeLocalTable(local.TempTables, name, cols);
                            }
                            i = Math.Max(i + 1, j);
                            continue;
                        }
                    }
                    if (KeywordAt(text, i, n, "INTO"))
                    {
                        int j = SkipWs(text, i + 4, n);
                        string name = ReadSqlName(text, ref j, n);
                        if (!string.IsNullOrEmpty(name) && name[0] == '#')
                            MergeLocalTable(local.TempTables, name, null);
                        i = Math.Max(i + 1, j);
                        continue;
                    }
                    if (KeywordAt(text, i, n, "DECLARE"))
                    {
                        i = CollectDeclareList(text, i + 7, n, local);
                        continue;
                    }
                    i++;
                }
            }

            private static int CollectDeclareList(string text, int i, int n, LocalSymbols local)
            {
                while (i < n)
                {
                    i = SkipWs(text, i, n);
                    if (i >= n) break;
                    if (text[i] == ';') break;
                    string name = ReadSqlName(text, ref i, n);
                    if (string.IsNullOrEmpty(name) || name[0] != '@') break;
                    i = SkipWs(text, i, n);
                    if (KeywordAt(text, i, n, "TABLE"))
                    {
                        i = SkipWs(text, i + 5, n);
                        var cols = new List<string>();
                        if (i < n && text[i] == '(')
                            ParseTableColumnNames(text, ref i, n, cols);
                        MergeLocalTable(local.TableVariables, name, cols);
                    }
                    else
                    {
                        MergeScalar(local, name);
                        int depth = 0;
                        bool inStr = false;
                        while (i < n)
                        {
                            char c = text[i];
                            char next = i + 1 < n ? text[i + 1] : '\0';
                            if (inStr)
                            {
                                if (c == '\'')
                                {
                                    if (next == '\'') i += 2;
                                    else { inStr = false; i++; }
                                    continue;
                                }
                                i++;
                                continue;
                            }
                            if (c == '\'') { inStr = true; i++; continue; }
                            if (c == '(') { depth++; i++; continue; }
                            if (c == ')') { if (depth > 0) depth--; i++; continue; }
                            if (depth == 0 && c == ',') { i++; break; }
                            if (depth == 0 && (c == ';' || KeywordAt(text, i, n, "SELECT") || KeywordAt(text, i, n, "INSERT")
                                || KeywordAt(text, i, n, "UPDATE") || KeywordAt(text, i, n, "DELETE")
                                || KeywordAt(text, i, n, "CREATE") || KeywordAt(text, i, n, "EXEC")
                                || KeywordAt(text, i, n, "WITH") || KeywordAt(text, i, n, "GO")))
                                return i;
                            i++;
                        }
                        continue;
                    }
                    i = SkipWs(text, i, n);
                    if (i < n && text[i] == ',') { i++; continue; }
                    break;
                }
                return i;
            }

            private static int SkipWs(string text, int i, int n)
            {
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                return i;
            }

            private static string ReadSqlName(string text, ref int i, int n)
            {
                if (i >= n) return null;
                if (text[i] == '[')
                {
                    int start = i + 1;
                    i++;
                    while (i < n && text[i] != ']')
                    {
                        if (text[i] == ']' && i + 1 < n && text[i + 1] == ']') { i += 2; continue; }
                        i++;
                    }
                    string inner = i > start ? text.Substring(start, Math.Min(i, n) - start) : string.Empty;
                    if (i < n && text[i] == ']') i++;
                    return inner;
                }
                if (!(IsIdentChar(text[i]) || text[i] == '#')) return null;
                int s = i;
                while (i < n && (IsIdentChar(text[i]) || text[i] == '#')) i++;
                return text.Substring(s, i - s);
            }

            private static readonly HashSet<string> TableConstraintKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CONSTRAINT", "PRIMARY", "UNIQUE", "CHECK", "FOREIGN", "INDEX", "KEY", "WITH", "PERIOD"
            };

            private static void ParseTableColumnNames(string text, ref int i, int n, List<string> cols)
            {
                if (i >= n || text[i] != '(') return;
                i++;
                int depth = 1;
                bool expectName = true;
                bool inStr = false;
                while (i < n && depth > 0)
                {
                    char c = text[i];
                    char next = i + 1 < n ? text[i + 1] : '\0';
                    if (inStr)
                    {
                        if (c == '\'')
                        {
                            if (next == '\'') i += 2;
                            else { inStr = false; i++; }
                            continue;
                        }
                        i++;
                        continue;
                    }
                    if (c == '\'') { inStr = true; i++; continue; }
                    if (c == '-' && next == '-') { while (i < n && text[i] != '\n' && text[i] != '\r') i++; continue; }
                    if (c == '/' && next == '*')
                    {
                        i += 2;
                        while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/')) i++;
                        i += 2;
                        continue;
                    }
                    if (c == '(') { depth++; i++; continue; }
                    if (c == ')') { depth--; i++; expectName = depth == 1; continue; }
                    if (c == ',' && depth == 1) { expectName = true; i++; continue; }
                    if (expectName && depth == 1 && (IsIdentChar(c) || c == '[' || c == '#'))
                    {
                        string name = ReadSqlName(text, ref i, n);
                        if (!string.IsNullOrEmpty(name) && !TableConstraintKeywords.Contains(name))
                            cols.Add(name);
                        expectName = false;
                        continue;
                    }
                    i++;
                }
            }

            private static void MergeLocalTable(List<LocalTableInfo> dest, string name, List<string> cols)
            {
                if (dest == null || string.IsNullOrEmpty(name)) return;
                var existing = dest.Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    var t = new LocalTableInfo { Name = name };
                    if (cols != null)
                    {
                        foreach (var col in cols)
                        {
                            if (!string.IsNullOrEmpty(col)) t.ColumnNames.Add(col);
                        }
                    }
                    dest.Add(t);
                    return;
                }
                if (existing.ColumnNames.Count == 0 && cols != null)
                {
                    foreach (var col in cols)
                    {
                        if (!string.IsNullOrEmpty(col)) existing.ColumnNames.Add(col);
                    }
                }
            }

            private static void MergeScalar(LocalSymbols local, string name)
            {
                if (local == null || string.IsNullOrEmpty(name)) return;
                foreach (var v in local.ScalarVariables)
                {
                    if (string.Equals(v, name, StringComparison.OrdinalIgnoreCase)) return;
                }
                local.ScalarVariables.Add(name);
            }

            private void CollectFromStatement(TSqlStatement stmt, LocalSymbols local)
            {
                if (stmt is DeclareTableVariableStatement dtv)
                {
                    var body = dtv.Body;
                    var t = new LocalTableInfo { Name = body?.VariableName?.Value };
                    foreach (var col in body?.Definition?.ColumnDefinitions ?? Enumerable.Empty<ColumnDefinition>())
                    {
                        t.ColumnNames.Add(col.ColumnIdentifier?.Value);
                    }
                    if (t.Name != null) local.TableVariables.Add(t);
                }
                else if (stmt is DeclareVariableStatement dvs)
                {
                    foreach (var decl in dvs.Declarations)
                    {
                        if (decl.VariableName?.Value != null)
                        {
                            local.ScalarVariables.Add(decl.VariableName.Value);
                        }
                    }
                }
                else if (stmt is CreateTableStatement cts)
                {
                    var name = cts.SchemaObjectName;
                    string tableName = GetLastIdentifier(name);
                    if (!string.IsNullOrEmpty(tableName) && tableName.StartsWith("#"))
                    {
                        var t = new LocalTableInfo { Name = tableName };
                        foreach (var def in cts.Definition.ColumnDefinitions)
                        {
                            t.ColumnNames.Add(def.ColumnIdentifier?.Value);
                        }
                        local.TempTables.Add(t);
                    }
                }
                else if (stmt is SelectStatement ss)
                {
                    // CTE 定义
                    if (ss.WithCtesAndXmlNamespaces != null)
                    {
                        foreach (var cte in ss.WithCtesAndXmlNamespaces.CommonTableExpressions)
                        {
                            var info = new CteInfo { Name = cte.ExpressionName?.Value };
                            if (cte.Columns != null)
                            {
                                foreach (var col in cte.Columns)
                                {
                                    info.ColumnNames.Add(col.Value);
                                }
                            }
                            if (info.Name != null) local.Ctes.Add(info);
                        }
                    }
                    // SELECT INTO #t
                    if (ss.Into != null)
                    {
                        string intoName = GetLastIdentifier(ss.Into);
                        if (!string.IsNullOrEmpty(intoName) && intoName.StartsWith("#"))
                        {
                            local.TempTables.Add(new LocalTableInfo { Name = intoName });
                        }
                    }
                }
            }

            private static string GetLastIdentifier(SchemaObjectName sobj)
            {
                if (sobj == null) return null;
                var ids = sobj.Identifiers;
                if (ids == null || ids.Count == 0) return null;
                return ids[ids.Count - 1].Value;
            }

            /// <summary>收集光标所在语句的 FROM 别名映射。</summary>
            private void CollectAliases(TSqlScript script, int localOffset, LocalSymbols local, List<TSqlParserToken> tokens)
            {
                local.Aliases.Clear();
                TSqlStatement target = null;
                TSqlStatement nearest = null;
                int nearestStart = -1;
                if (script != null)
                {
                    foreach (var batch in script.Batches)
                    {
                        foreach (var stmt in batch.Statements)
                        {
                            if (GetFromClause(stmt) == null && !IsDmlStatement(stmt)) continue;
                            int start = stmt.StartOffset;
                            int end = start + Math.Max(0, stmt.FragmentLength);
                            if (localOffset >= start && localOffset <= end)
                                target = stmt;
                            // 残缺 WHERE（如 and d.）时 FragmentLength 可能短于光标：取光标前最近带 FROM 的语句
                            if (localOffset >= start && start >= nearestStart)
                            {
                                nearestStart = start;
                                nearest = stmt;
                            }
                        }
                    }
                }
                if (target == null) target = nearest;

                // ScriptDom 常只解析出分号前完整句，后继残缺 SELECT 不进 AST；
                // 若 token 定位到的 FROM 已越过该语句末尾，说明选中了上一句，必须丢弃。
                if (target != null && tokens != null)
                {
                    int fromIdx = FindFromClauseTokenIndex(tokens, localOffset);
                    if (fromIdx >= 0 && fromIdx < tokens.Count && tokens[fromIdx] != null)
                    {
                        int fromOff = tokens[fromIdx].Offset;
                        int targetEnd = target.StartOffset + Math.Max(0, target.FragmentLength);
                        if (fromOff >= targetEnd)
                            target = null;
                    }
                    else
                    {
                        // SELECT * fro|：当前句无 FROM，光标已越过上一句末尾 → 清空别名
                        int targetEnd = target.StartOffset + Math.Max(0, target.FragmentLength);
                        if (localOffset > targetEnd)
                            target = null;
                    }
                }

                if (target != null)
                {
                    CollectDmlTarget(target, local);
                    var from = GetFromClause(target);
                    if (from != null)
                    {
                        foreach (var tr in from.TableReferences ?? Enumerable.Empty<TableReference>())
                            CollectFromClause(tr, local);
                    }
                }
                // 始终用 token 补全 JOIN 别名（ScriptDom 对 kucun(nolock) 等可能丢别名）
                if (tokens != null)
                    CollectAliasesFromTokens(tokens, localOffset, local);
                // INSERT INTO t (|)：把目标表登记进别名，便于列/全字段补全
                if (tokens != null)
                    CollectInsertTargetFromTokens(tokens, localOffset, local);
                // UPDATE t SET：无 FROM 时也要把目标表登记进别名（SET/WHERE 列补全）
                if (tokens != null)
                    CollectUpdateTargetFromTokens(tokens, localOffset, local);
            }

            /// <summary>INSERT INTO [db.][schema.]table ( 列清单内时，登记目标表。</summary>
            private static void CollectInsertTargetFromTokens(
                List<TSqlParserToken> tokens, int localOffset, LocalSymbols local)
            {
                if (local == null || !TryParseInsertColumnList(tokens, localOffset, out TableRef tref))
                    return;
                if (tref == null || string.IsNullOrEmpty(tref.Name)) return;
                if (!local.Aliases.ContainsKey(tref.Name))
                    local.Aliases[tref.Name] = tref;
            }

            /// <summary>
            /// 光标是否在 INSERT INTO table ( ... ) 列清单内（非 VALUES）。
            /// </summary>
            private static bool TryParseInsertColumnList(
                List<TSqlParserToken> tokens, int localOffset, out TableRef tableRef)
            {
                tableRef = null;
                if (tokens == null || tokens.Count == 0) return false;
                int caretIdx = FindTokenIndexBefore(tokens, localOffset);
                if (caretIdx < 0) return false;
                int depth = 0;
                int openParenIdx = -1;
                for (int i = caretIdx; i >= 0; i--)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.TokenType == TSqlTokenType.RightParenthesis)
                    {
                        depth++;
                        continue;
                    }
                    if (t.TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        if (depth == 0)
                        {
                            openParenIdx = i;
                            break;
                        }
                        depth--;
                        continue;
                    }
                    if (depth > 0) continue;
                    string text = t.Text;
                    if (string.IsNullOrEmpty(text)) continue;
                    if (text == ";" || text.Equals("GO", StringComparison.OrdinalIgnoreCase))
                        return false;
                    // 尚未找到开括号就遇到 VALUES/SELECT → 不在列清单
                    string up = text.ToUpperInvariant();
                    if (up == "VALUES" || up == "SELECT" || up == "WHERE" || up == "SET")
                        return false;
                }
                if (openParenIdx < 0) return false;
                // 开括号前解析 [db.][schema.]table，再确认 INSERT [INTO]
                var segs = new List<string>();
                bool lastWasDot = false;
                bool usesDoubleDot = false;
                int j = openParenIdx - 1;
                while (j >= 0)
                {
                    var t = tokens[j];
                    if (t == null || IsInsignificantToken(t)) { j--; continue; }
                    if (t.TokenType == TSqlTokenType.Dot || t.Text == ".")
                    {
                        if (lastWasDot) usesDoubleDot = true;
                        lastWasDot = true;
                        j--;
                        continue;
                    }
                    if (IsWordLikeToken(t) || t.TokenType == TSqlTokenType.QuotedIdentifier
                        || (t.Text != null && t.Text.Length > 0 && t.Text[0] == '['))
                    {
                        string id = UnbracketIdentifier(t.Text);
                        if (string.IsNullOrEmpty(id)) { j--; continue; }
                        string up = id.ToUpperInvariant();
                        if (up == "INTO" || up == "INSERT" || up == "VALUES" || up == "SELECT"
                            || up == "WITH" || up == "TOP" || up == "AS")
                            break;
                        segs.Insert(0, id);
                        lastWasDot = false;
                        j--;
                        // 最多三段：db.schema.table
                        if (segs.Count >= 3) break;
                        continue;
                    }
                    break;
                }
                if (segs.Count == 0) return false;
                // 跳过表名后应能看到 INTO / INSERT
                bool sawInsert = false;
                while (j >= 0)
                {
                    var t = tokens[j];
                    if (t == null || IsInsignificantToken(t)) { j--; continue; }
                    string up = t.Text?.ToUpperInvariant();
                    if (string.IsNullOrEmpty(up)) { j--; continue; }
                    if (up == "INTO") { j--; continue; }
                    if (up == "INSERT") { sawInsert = true; break; }
                    if (up == ";" || up == "GO") break;
                    // TOP (n) 等噪声
                    if (up == "TOP" || t.TokenType == TSqlTokenType.Integer) { j--; continue; }
                    break;
                }
                if (!sawInsert) return false;
                // 开括号与光标之间不应出现 VALUES（列清单之后才是 VALUES）
                for (int k = openParenIdx + 1; k <= caretIdx && k < tokens.Count; k++)
                {
                    var t = tokens[k];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= localOffset) break;
                    string up = t.Text?.ToUpperInvariant();
                    if (up == "VALUES") return false;
                }
                tableRef = new TableRef();
                if (usesDoubleDot && segs.Count == 2)
                {
                    tableRef.Database = segs[0];
                    tableRef.Schema = "dbo";
                    tableRef.Name = segs[1];
                }
                else if (usesDoubleDot && segs.Count == 3)
                {
                    tableRef.LinkedServer = segs[0];
                    tableRef.Database = segs[1];
                    tableRef.Schema = "dbo";
                    tableRef.Name = segs[2];
                }
                else if (segs.Count == 1)
                {
                    tableRef.Name = segs[0];
                }
                else if (segs.Count == 2)
                {
                    tableRef.Schema = segs[0];
                    tableRef.Name = segs[1];
                }
                else
                {
                    tableRef.Database = segs[0];
                    tableRef.Schema = segs[1];
                    tableRef.Name = segs[2];
                }
                return true;
            }

            /// <summary>UPDATE [db.][schema.]table SET/WHERE 时登记目标表，供 SET 列补全。</summary>
            private static void CollectUpdateTargetFromTokens(
                List<TSqlParserToken> tokens, int localOffset, LocalSymbols local)
            {
                if (local == null || !TryParseUpdateTarget(tokens, localOffset, out TableRef tref, out string alias))
                    return;
                if (tref == null || string.IsNullOrEmpty(tref.Name)) return;
                if (!string.IsNullOrEmpty(alias) && !IsSqlTableKeyword(alias))
                    MergeAlias(local, alias, tref);
                MergeAlias(local, tref.Name, tref);
                if (!string.IsNullOrEmpty(tref.Schema))
                    MergeAlias(local, tref.Schema + "." + tref.Name, tref);
                if (!string.IsNullOrEmpty(tref.Database))
                    MergeAlias(local, tref.Database + "." + (tref.Schema ?? "dbo") + "." + tref.Name, tref);
            }

            private static bool TryParseUpdateTarget(
                List<TSqlParserToken> tokens, int localOffset, out TableRef tableRef, out string alias)
            {
                tableRef = null;
                alias = null;
                if (tokens == null || tokens.Count == 0) return false;
                int updIdx = -1;
                int depth = 0;
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= localOffset) break;
                    if (t.TokenType == TSqlTokenType.LeftParenthesis) { depth++; continue; }
                    if (t.TokenType == TSqlTokenType.RightParenthesis)
                    {
                        if (depth > 0) depth--;
                        continue;
                    }
                    if (depth > 0) continue;
                    string text = t.Text?.ToUpperInvariant();
                    if (text == ";" || text == "GO") { updIdx = -1; continue; }
                    if (text == "UPDATE") updIdx = i;
                    else if (IsNewStatementKeyword(text) && text != "UPDATE")
                        updIdx = -1;
                }
                if (updIdx < 0) return false;
                var segs = new List<string>();
                    bool lastWasDot = false;
                    bool usesDoubleDot = false;
                    bool sawTerminator = false;
                    int j = updIdx + 1;
                while (j < tokens.Count)
                {
                    var t = tokens[j];
                    if (t == null || IsInsignificantToken(t)) { j++; continue; }
                    if (t.Offset >= localOffset && segs.Count > 0) break;
                    string up = t.Text?.ToUpperInvariant();
                    if (string.IsNullOrEmpty(up)) { j++; continue; }
                    if (up == "TOP")
                    {
                        j++;
                        while (j < tokens.Count)
                        {
                            var tt = tokens[j];
                            if (tt == null || IsInsignificantToken(tt)) { j++; continue; }
                            if (tt.TokenType == TSqlTokenType.LeftParenthesis)
                            {
                                j = SkipParenthesesForward(tokens, j);
                                continue;
                            }
                            if (string.Equals(tt.Text, "PERCENT", StringComparison.OrdinalIgnoreCase))
                            { j++; continue; }
                            break;
                        }
                        continue;
                    }
                    if (IsUpdateTargetTerminator(up)) { sawTerminator = true; break; }
                    if (up == "WITH")
                    {
                        j++;
                        while (j < tokens.Count)
                        {
                            var wt = tokens[j];
                            if (wt == null || IsInsignificantToken(wt)) { j++; continue; }
                            if (wt.TokenType == TSqlTokenType.LeftParenthesis)
                            {
                                j = SkipParenthesesForward(tokens, j);
                                continue;
                            }
                            break;
                        }
                        continue;
                    }
                    if (up == "AS") { j++; continue; }
                    if (t.TokenType == TSqlTokenType.Dot || t.Text == ".")
                    {
                        if (lastWasDot) usesDoubleDot = true;
                        lastWasDot = true;
                        j++;
                        continue;
                    }
                    if (IsWordLikeToken(t) || t.TokenType == TSqlTokenType.QuotedIdentifier
                        || (t.Text != null && t.Text.Length > 0 && t.Text[0] == '['))
                    {
                        string id = UnbracketIdentifier(t.Text);
                        if (string.IsNullOrEmpty(id) || IsSqlTableKeyword(id) || IsUpdateTargetTerminator(id.ToUpperInvariant()))
                        { j++; continue; }
                        if (segs.Count > 0 && !lastWasDot)
                        {
                            alias = id;
                            j++;
                            break;
                        }
                        segs.Add(id);
                        lastWasDot = false;
                        j++;
                        if (segs.Count >= 4) break;
                        continue;
                    }
                    j++;
                }
                if (segs.Count == 0) return false;
                tableRef = new TableRef();
                if (usesDoubleDot && segs.Count == 2)
                {
                    tableRef.Database = segs[0];
                    tableRef.Schema = "dbo";
                    tableRef.Name = segs[1];
                }
                else if (usesDoubleDot && segs.Count == 3)
                {
                    tableRef.LinkedServer = segs[0];
                    tableRef.Database = segs[1];
                    tableRef.Schema = "dbo";
                    tableRef.Name = segs[2];
                }
                else if (segs.Count == 1)
                {
                    tableRef.Name = segs[0];
                }
                else if (segs.Count == 2)
                {
                    tableRef.Schema = segs[0];
                    tableRef.Name = segs[1];
                }
                else if (segs.Count == 3)
                {
                    tableRef.Database = segs[0];
                    tableRef.Schema = segs[1];
                    tableRef.Name = segs[2];
                }
                else
                {
                    tableRef.LinkedServer = segs[0];
                    tableRef.Database = segs[1];
                    tableRef.Schema = segs[2];
                    tableRef.Name = segs[3];
                }
                NormalizeTableRef(tableRef);
                return sawTerminator && !string.IsNullOrEmpty(tableRef.Name);
            }

            private static int SkipParenthesesForward(List<TSqlParserToken> tokens, int openIdx)
            {
                int depth = 0;
                for (int i = openIdx; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null) continue;
                    if (t.TokenType == TSqlTokenType.LeftParenthesis) depth++;
                    else if (t.TokenType == TSqlTokenType.RightParenthesis)
                    {
                        depth--;
                        if (depth <= 0) return i + 1;
                    }
                }
                return tokens.Count;
            }

            private static bool IsUpdateTargetTerminator(string kw)
            {
                if (string.IsNullOrEmpty(kw)) return false;
                switch (kw.ToUpperInvariant())
                {
                    case "SET":
                    case "FROM":
                    case "WHERE":
                    case "OUTPUT":
                    case "OPTION":
                        return true;
                    default:
                        return false;
                }
            }

            private static bool IsNewStatementKeyword(string text)
            {
                if (string.IsNullOrEmpty(text)) return false;
                switch (text.ToUpperInvariant())
                {
                    case "SELECT":
                    case "INSERT":
                    case "UPDATE":
                    case "DELETE":
                    case "MERGE":
                    case "CREATE":
                    case "ALTER":
                    case "DROP":
                    case "EXEC":
                    case "EXECUTE":
                    case "USE":
                    case "DECLARE":
                    case "TRUNCATE":
                        return true;
                    default:
                        return false;
                }
            }

            /// <summary>定位当前 SELECT 对应的 FROM（允许 FROM 在光标之后；支持误写分号后继续 AND 条件）。</summary>
            private static int FindFromClauseTokenIndex(List<TSqlParserToken> tokens, int localOffset)
            {
                int selectIdx = -1;
                int selectBeforeSemi = -1;
                int lastSemiIdx = -1;
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset > localOffset) break;
                    string text = t.Text?.ToUpperInvariant();
                    if (text == ";" || text == "GO")
                    {
                        selectBeforeSemi = selectIdx;
                        lastSemiIdx = i;
                        selectIdx = -1;
                        continue;
                    }
                    if (text == "SELECT")
                        selectIdx = i;
                    else if (IsNewStatementKeyword(text) && text != "SELECT")
                        selectIdx = -1;
                }
                // where ... ; and alias.col → 分号后无新 SELECT，沿用分号前的查询别名
                if (selectIdx < 0 && selectBeforeSemi >= 0 && lastSemiIdx >= 0
                    && IsClauseContinuationAfter(tokens, lastSemiIdx, localOffset))
                {
                    selectIdx = selectBeforeSemi;
                }
                if (selectIdx >= 0)
                {
                    for (int i = selectIdx + 1; i < tokens.Count; i++)
                    {
                        var t = tokens[i];
                        if (t == null || IsInsignificantToken(t)) continue;
                        // 允许越过误写的分号去找 FROM（FROM 一定在分号前）— 仅限续写；新 SELECT 不跨句
                        string kw = t.Text?.ToUpperInvariant();
                        if (kw == ";" || kw == "GO")
                            break;
                        if (kw == "SELECT")
                            break;
                        if (string.Equals(t.Text, "FROM", StringComparison.OrdinalIgnoreCase))
                            return i;
                        if (kw == "WHERE" || kw == "GROUP" || kw == "ORDER" || kw == "HAVING"
                            || kw == "UNION" || kw == "EXCEPT" || kw == "INTERSECT")
                            break;
                    }
                    // 当前 SELECT 尚无 FROM（SELECT * fro|）：勿回退到上一句 FROM，否则串列/误成 WhereClause
                    return -1;
                }
                int lastFrom = -1;
                int fromBeforeSemi = -1;
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset > localOffset) break;
                    string text = t.Text?.ToUpperInvariant();
                    if (text == ";" || text == "GO")
                    {
                        fromBeforeSemi = lastFrom;
                        lastSemiIdx = i;
                        lastFrom = -1;
                        continue;
                    }
                    if (IsNewStatementKeyword(text))
                        lastFrom = -1;
                    if (text == "FROM")
                        lastFrom = i;
                }
                if (lastFrom >= 0) return lastFrom;
                if (fromBeforeSemi >= 0 && lastSemiIdx >= 0
                    && IsClauseContinuationAfter(tokens, lastSemiIdx, localOffset))
                    return fromBeforeSemi;
                return -1;
            }

            /// <summary>分号后是否为 AND/OR/别名.列 等子句续写（而非新的 SELECT 语句）。</summary>
            private static bool IsClauseContinuationAfter(List<TSqlParserToken> tokens, int semiIdx, int localOffset)
            {
                for (int i = semiIdx + 1; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= localOffset) break;
                    string kw = t.Text?.ToUpperInvariant();
                    if (kw == "AND" || kw == "OR" || kw == "WHERE" || kw == "ORDER" || kw == "GROUP"
                        || kw == "HAVING" || kw == "ON")
                        return true;
                    if (kw == "SELECT" || kw == "INSERT" || kw == "UPDATE" || kw == "DELETE"
                        || kw == "MERGE" || kw == "CREATE" || kw == "ALTER" || kw == "DROP"
                        || kw == "EXEC" || kw == "EXECUTE" || kw == "USE" || kw == "DECLARE" || kw == "SET")
                        return false;
                    if (IsWordLikeToken(t) || IsPartialObjectNameToken(t) || IsIdentifierLike(t))
                        return true;
                    if (t.TokenType == TSqlTokenType.Dot)
                        return true;
                    return false;
                }
                return false;
            }

            private static int FindFromRegionEnd(List<TSqlParserToken> tokens, int fromIdx)
            {
                for (int i = fromIdx + 1; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    string kw = t.Text?.ToUpperInvariant();
                    if (kw == "WHERE" || kw == "GROUP" || kw == "ORDER" || kw == "HAVING"
                        || kw == "UNION" || kw == "EXCEPT" || kw == "INTERSECT"
                        || kw == ";" || kw == "GO" || kw == "SELECT")
                    {
                        return t.Offset;
                    }
                }
                for (int i = tokens.Count - 1; i >= 0; i--)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    return t.Offset + t.Text.Length;
                }
                return int.MaxValue;
            }

            /// <summary>从当前语句整个 FROM…WHERE 区间收集所有表/JOIN 别名（含 db..table、table(nolock) alias）。</summary>
            private void CollectAliasesFromTokens(List<TSqlParserToken> tokens, int localOffset, LocalSymbols local)
            {
                int fromIdx = FindFromClauseTokenIndex(tokens, localOffset);
                if (fromIdx < 0) return;
                int regionEnd = FindFromRegionEnd(tokens, fromIdx);
                int i = fromIdx + 1;
                while (i < tokens.Count)
                {
                    var t0 = tokens[i];
                    if (t0 == null || IsInsignificantToken(t0)) { i++; continue; }
                    if (t0.Offset >= regionEnd) break;
                    string kw0 = t0.Text?.ToUpperInvariant();
                    if (kw0 == "INNER" || kw0 == "LEFT" || kw0 == "RIGHT" || kw0 == "FULL"
                        || kw0 == "CROSS" || kw0 == "OUTER" || kw0 == "JOIN" || kw0 == ",")
                    {
                        i++;
                        continue;
                    }
                    if (kw0 == "ON")
                    {
                        i++;
                        while (i < tokens.Count)
                        {
                            var ot = tokens[i];
                            if (ot == null || IsInsignificantToken(ot)) { i++; continue; }
                            if (ot.Offset >= regionEnd) return;
                            string okw = ot.Text?.ToUpperInvariant();
                            if (okw == "INNER" || okw == "LEFT" || okw == "RIGHT" || okw == "FULL"
                                || okw == "CROSS" || okw == "OUTER" || okw == "JOIN" || okw == ",")
                                break;
                            i++;
                        }
                        continue;
                    }
                    if (kw0 == "AS") { i++; continue; }

                    string linkedServer = null;
                    string database = null;
                    string schema = null;
                    string table = null;
                    string alias = null;
                    int dotRun = 0;
                    int partCount = 0;
                    int segmentStartI = i;
                    while (i < tokens.Count)
                    {
                        var t = tokens[i];
                        if (t == null || IsInsignificantToken(t)) { i++; continue; }
                        if (t.Offset >= regionEnd) break;
                        string kw = t.Text?.ToUpperInvariant();
                        if (kw == "WHERE" || kw == "GROUP" || kw == "ORDER" || kw == "HAVING"
                            || kw == "UNION" || kw == "EXCEPT" || kw == "INTERSECT" || kw == ";"
                            || kw == "INNER" || kw == "LEFT" || kw == "RIGHT" || kw == "FULL"
                            || kw == "CROSS" || kw == "OUTER" || kw == "JOIN" || kw == "," || kw == "ON")
                        {
                            break;
                        }
                        if (kw == "AS")
                        {
                            int j = i + 1;
                            while (j < tokens.Count)
                            {
                                var at = tokens[j];
                                if (at == null || IsInsignificantToken(at)) { j++; continue; }
                                if (at.Offset >= regionEnd) break;
                                if (IsWordLikeToken(at) || IsPartialObjectNameToken(at) || IsIdentifierLike(at))
                                {
                                    string an = UnbracketIdentifier(at.Text);
                                    if (!string.IsNullOrEmpty(an) && !IsSqlTableKeyword(an))
                                        alias = an;
                                    j++;
                                }
                                break;
                            }
                            i = j;
                            break;
                        }
                        if (kw == "WITH") { i++; continue; }
                        if (t.TokenType == TSqlTokenType.LeftParenthesis)
                        {
                            // 跳过 (nolock) 等表提示
                            int depth = 0;
                            int parenStart = i;
                            for (; i < tokens.Count; i++)
                            {
                                var pt = tokens[i];
                                if (pt == null) continue;
                                if (pt.TokenType == TSqlTokenType.LeftParenthesis) depth++;
                                else if (pt.TokenType == TSqlTokenType.RightParenthesis)
                                {
                                    depth--;
                                    if (depth <= 0) { i++; break; }
                                }
                            }
                            if (i == parenStart) i++;
                            continue;
                        }
                        if (t.TokenType == TSqlTokenType.Dot)
                        {
                            dotRun++;
                            i++;
                            continue;
                        }
                        if (!(IsFromObjectToken(t)))
                        {
                            i++;
                            continue;
                        }
                        string name = UnbracketIdentifier(t.Text);
                        if (IsSqlTableKeyword(name)) { i++; continue; }
                        if (table != null && dotRun == 0)
                        {
                            // 后接 a.b → 下行裸限定名，结束本表且不把首段当别名
                            if (NextSignificantTokenIsDot(tokens, i + 1, regionEnd))
                                break;
                            alias = name;
                            i++;
                            break;
                        }
                        if (dotRun >= 2 && partCount == 1)
                        {
                            database = table;
                            schema = "dbo";
                            table = name;
                            partCount = 2;
                            dotRun = 0;
                            i++;
                            continue;
                        }
                        if (dotRun >= 2 && partCount == 2 && database == null && linkedServer == null)
                        {
                            // server.db..table：省略架构的四段名
                            linkedServer = schema;
                            database = table;
                            schema = "dbo";
                            table = name;
                            partCount = 4;
                            dotRun = 0;
                            i++;
                            continue;
                        }
                        if (dotRun >= 1 && partCount >= 1)
                        {
                            if (partCount == 1)
                            {
                                schema = table;
                                table = name;
                                partCount = 2;
                            }
                            else if (partCount == 2 && database == null && linkedServer == null)
                            {
                                database = schema;
                                schema = table;
                                table = name;
                                partCount = 3;
                            }
                            else if (partCount == 3 && linkedServer == null)
                            {
                                // server.db.schema.table
                                linkedServer = database;
                                database = schema;
                                schema = table;
                                table = name;
                                partCount = 4;
                            }
                            else
                            {
                                table = name;
                            }
                            dotRun = 0;
                            i++;
                            continue;
                        }
                        table = name;
                        partCount = 1;
                        dotRun = 0;
                        i++;
                    }
                    // i 未前进时强制步进，避免空表名 continue 导致外层死循环
                    if (string.IsNullOrEmpty(table) || IsSqlTableKeyword(table))
                    {
                        if (i <= segmentStartI) i = segmentStartI + 1;
                        continue;
                    }
                    var tref = new TableRef
                    {
                        LinkedServer = linkedServer,
                        Database = database,
                        Schema = schema,
                        Name = table
                    };
                    NormalizeTableRef(tref);
                    if (!string.IsNullOrEmpty(alias) && !IsSqlTableKeyword(alias))
                        MergeAlias(local, alias, tref);
                    MergeAlias(local, tref.Name, tref);
                    if (!string.IsNullOrEmpty(tref.Schema))
                        MergeAlias(local, tref.Schema + "." + tref.Name, tref);
                    if (!string.IsNullOrEmpty(tref.Database))
                        MergeAlias(local, tref.Database + "." + (tref.Schema ?? "dbo") + "." + tref.Name, tref);
                    if (!string.IsNullOrEmpty(tref.LinkedServer))
                        MergeAlias(local, tref.LinkedServer + "." + (tref.Database ?? "") + "." + (tref.Schema ?? "dbo") + "." + tref.Name, tref);

                    // 仅逗号 / JOIN 可接下一表；残留 AS alias 则跳过继续扫
                    int look = i;
                    while (look < tokens.Count)
                    {
                        var lt = tokens[look];
                        if (lt == null || IsInsignificantToken(lt)) { look++; continue; }
                        if (lt.Offset >= regionEnd) return;
                        string lkw = lt.Text?.ToUpperInvariant();
                        if (lkw == "," || lkw == "INNER" || lkw == "LEFT" || lkw == "RIGHT" || lkw == "FULL"
                            || lkw == "CROSS" || lkw == "OUTER" || lkw == "JOIN" || lkw == "ON")
                            break;
                        if (lkw == "AS")
                        {
                            look++;
                            while (look < tokens.Count)
                            {
                                var at = tokens[look];
                                if (at == null || IsInsignificantToken(at)) { look++; continue; }
                                if (IsWordLikeToken(at) || IsPartialObjectNameToken(at) || IsIdentifierLike(at))
                                    look++;
                                break;
                            }
                            continue;
                        }
                        return;
                    }
                }
            }

            private static bool NextSignificantTokenIsDot(List<TSqlParserToken> tokens, int fromIdx, int regionEnd)
            {
                for (int j = fromIdx; j < tokens.Count; j++)
                {
                    var t = tokens[j];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= regionEnd) return false;
                    return t.TokenType == TSqlTokenType.Dot;
                }
                return false;
            }

            private static void MergeAlias(LocalSymbols local, string key, TableRef tref)
            {
                if (string.IsNullOrEmpty(key) || tref == null) return;
                TableRef existing;
                if (!local.Aliases.TryGetValue(key, out existing))
                {
                    local.Aliases[key] = tref;
                    return;
                }
                if (string.IsNullOrEmpty(existing.LinkedServer) && !string.IsNullOrEmpty(tref.LinkedServer))
                    local.Aliases[key] = tref;
                else if (string.IsNullOrEmpty(existing.Database) && !string.IsNullOrEmpty(tref.Database))
                    local.Aliases[key] = tref;
            }

            private static bool IsSqlTableKeyword(string name)
            {
                if (string.IsNullOrEmpty(name)) return true;
                switch (name.ToUpperInvariant())
                {
                    case "AS":
                    case "ON":
                    case "WITH":
                    case "NOLOCK":
                    case "READUNCOMMITTED":
                        return true;
                    default:
                        return false;
                }
            }

            private static void NormalizeTableRef(TableRef tref)
            {
                if (tref == null) return;
                if (!string.IsNullOrEmpty(tref.Database) && string.IsNullOrEmpty(tref.Schema))
                    tref.Schema = "dbo";
            }

            private static bool IsDmlStatement(TSqlStatement stmt)
            {
                return stmt is UpdateStatement || stmt is DeleteStatement || stmt is InsertStatement;
            }

            private void CollectDmlTarget(TSqlStatement stmt, LocalSymbols local)
            {
                if (stmt is UpdateStatement us)
                    CollectFromClause(us.UpdateSpecification?.Target, local);
                else if (stmt is DeleteStatement ds)
                    CollectFromClause(ds.DeleteSpecification?.Target, local);
            }

            private FromClause GetFromClause(TSqlStatement stmt)
            {
                if (stmt is SelectStatement ss)
                {
                    return (ss.QueryExpression as QuerySpecification)?.FromClause;
                }
                if (stmt is UpdateStatement us) return us.UpdateSpecification?.FromClause;
                if (stmt is DeleteStatement ds) return ds.DeleteSpecification?.FromClause;
                return null;
            }

            private static TableRef BuildTableRef(SchemaObjectName sobj)
            {
                if (sobj == null) return null;
                var tref = new TableRef
                {
                    LinkedServer = sobj.ServerIdentifier?.Value,
                    Database = sobj.DatabaseIdentifier?.Value,
                    Schema = sobj.SchemaIdentifier?.Value,
                    Name = sobj.BaseIdentifier?.Value ?? GetLastIdentifier(sobj)
                };
                if (string.IsNullOrEmpty(tref.Name))
                {
                    int count = sobj.Identifiers?.Count ?? 0;
                    if (count == 0) return null;
                    if (count == 1)
                    {
                        tref.Name = sobj.Identifiers[0].Value;
                    }
                    else if (count == 2)
                    {
                        if (!string.IsNullOrEmpty(sobj.DatabaseIdentifier?.Value))
                        {
                            tref.Database = sobj.DatabaseIdentifier.Value;
                            tref.Name = sobj.Identifiers[1].Value;
                        }
                        else
                        {
                            tref.Schema = sobj.Identifiers[0].Value;
                            tref.Name = sobj.Identifiers[1].Value;
                        }
                    }
                    else if (count == 4)
                    {
                        // server.db.schema.table
                        tref.LinkedServer = sobj.Identifiers[0].Value;
                        tref.Database = sobj.Identifiers[1].Value;
                        tref.Schema = sobj.Identifiers[2].Value;
                        tref.Name = sobj.Identifiers[3].Value;
                    }
                    else
                    {
                        tref.Database = sobj.Identifiers[0].Value;
                        tref.Schema = sobj.Identifiers[1].Value;
                        tref.Name = sobj.Identifiers[count - 1].Value;
                    }
                }
                // ServerIdentifier 有值但 Identifiers 路径未填时，纠正四段
                if (!string.IsNullOrEmpty(tref.LinkedServer)
                    && !string.IsNullOrEmpty(tref.Database)
                    && string.IsNullOrEmpty(tref.Schema)
                    && !string.IsNullOrEmpty(tref.Name))
                {
                    // server.db..table 形式由 ScriptDom 可能落成 LinkedServer+Database+Name
                }
                NormalizeTableRef(tref);
                return string.IsNullOrEmpty(tref.Name) ? null : tref;
            }

            private void CollectFromClause(TableReference tableRef, LocalSymbols local)
            {
                if (tableRef == null) return;

                if (tableRef is NamedTableReference ntr)
                {
                    var alias = ntr.Alias?.Value;
                    var tref = BuildTableRef(ntr.SchemaObject);
                    if (tref == null || string.IsNullOrEmpty(tref.Name)) return;
                    if (!string.IsNullOrEmpty(alias))
                    {
                        local.Aliases[alias] = tref;
                    }
                    local.Aliases[tref.Name] = tref;
                    if (!string.IsNullOrEmpty(tref.Schema))
                    {
                        local.Aliases[tref.Schema + "." + tref.Name] = tref;
                    }
                    if (!string.IsNullOrEmpty(tref.Database))
                    {
                        local.Aliases[tref.Database + "." + tref.Schema + "." + tref.Name] = tref;
                    }
                }
                else if (tableRef is QualifiedJoin qj)
                {
                    CollectFromClause(qj.FirstTableReference, local);
                    CollectFromClause(qj.SecondTableReference, local);
                }
                else if (tableRef is UnqualifiedJoin uj)
                {
                    CollectFromClause(uj.FirstTableReference, local);
                    CollectFromClause(uj.SecondTableReference, local);
                }
                else if (tableRef is JoinParenthesisTableReference pj)
                {
                    CollectFromClause(pj.Join, local);
                }
            }

            #endregion
        }
    }
}
