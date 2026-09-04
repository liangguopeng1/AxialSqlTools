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

            /// <summary>跨 GO 的 #临时表全文扫描阈值：超过则只回看 LocalSymbolLookbackChars，避免超大脚本热路径线性扫描。</summary>
            private const int FullTextTempScanLimit = 256 * 1024;
            private const int LocalSymbolLookbackChars = 64 * 1024;

            private void EnrichLocalSymbolsFromBatch(LocalSymbols local, string fullText, int caret)
            {
                if (local == null || string.IsNullOrEmpty(fullText)) return;
                int limit = Math.Min(Math.Max(0, caret), fullText.Length);
                if (limit <= 0) return;
                int tempStart = 0;
                if (limit > FullTextTempScanLimit)
                    tempStart = AlignScanStart(fullText, limit - LocalSymbolLookbackChars);
                CollectLocalSymbolsFromText(fullText, tempStart, limit, local, scanTempTables: true, scanVariables: false);
                if (!TryGetBatchRange(fullText, caret, out int batchStart, out int batchEnd))
                    return;
                int varEnd = Math.Min(limit, batchEnd);
                if (varEnd <= batchStart) return;
                int varStart = batchStart;
                if (varEnd - varStart > LocalSymbolLookbackChars)
                {
                    varStart = AlignScanStart(fullText, varEnd - LocalSymbolLookbackChars);
                    if (varStart < batchStart) varStart = batchStart;
                }
                CollectLocalSymbolsFromText(fullText, varStart, varEnd, local, scanTempTables: false, scanVariables: true);
            }

            private static int AlignScanStart(string text, int i)
            {
                if (string.IsNullOrEmpty(text) || i <= 0) return 0;
                if (i >= text.Length) return text.Length;
                while (i < text.Length && text[i] != '\n') i++;
                if (i < text.Length) i++;
                return i;
            }

            private static void CollectLocalSymbolsFromText(string text, int limit, LocalSymbols local, bool scanTempTables, bool scanVariables)
            {
                CollectLocalSymbolsFromText(text, 0, limit, local, scanTempTables, scanVariables);
            }

            private static void CollectLocalSymbolsFromText(string text, int start, int limit, LocalSymbols local, bool scanTempTables, bool scanVariables)
            {
                if (string.IsNullOrEmpty(text) || local == null || limit <= 0) return;
                int n = Math.Min(limit, text.Length);
                int i = start < 0 ? 0 : start;
                if (i >= n) return;
                if (scanTempTables && text.IndexOf('#', i, n - i) < 0)
                    scanTempTables = false;
                if (scanVariables && text.IndexOf('@', i, n - i) < 0)
                    scanVariables = false;
                if (!scanTempTables && !scanVariables) return;
                bool inLineComment = false;
                bool inBlockComment = false;
                bool inString = false;
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
                    if (scanTempTables && KeywordAt(text, i, n, "CREATE"))
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
                    if (scanTempTables && KeywordAt(text, i, n, "INTO"))
                    {
                        int j = SkipWs(text, i + 4, n);
                        string name = ReadSqlName(text, ref j, n);
                        if (!string.IsNullOrEmpty(name) && name[0] == '#')
                            MergeLocalTable(local.TempTables, name, null);
                        i = Math.Max(i + 1, j);
                        continue;
                    }
                    if (scanVariables && KeywordAt(text, i, n, "DECLARE"))
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
                    // CTE 收集统一走词法路径 CollectCtesFromTokens（可处理残缺 SQL + 作用域），
                    // 此处不再从 AST 重复收集，避免跨语句 CTE 污染。
                    // SELECT INTO #t：从 SELECT 列表推导列
                    if (ss.Into != null)
                    {
                        string intoName = GetLastIdentifier(ss.Into);
                        if (!string.IsNullOrEmpty(intoName) && intoName.StartsWith("#"))
                        {
                            var intoCols = new List<string>();
                            if (ss.QueryExpression != null)
                                CollectQuerySelectColumns(ss.QueryExpression, intoCols);
                            MergeLocalTable(local.TempTables, intoName, intoCols);
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
            private void CollectAliases(TSqlScript script, int localOffset, LocalSymbols local, List<TSqlParserToken> tokens, string fullText = null, int caretOffset = -1)
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
                if (!string.IsNullOrEmpty(fullText) && caretOffset >= 0)
                    SupplementFromAliasesFromText(fullText, caretOffset, local);
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

            /// <summary>定位当前查询（括号深度匹配光标）的 FROM；忽略派生表/子查询内的 SELECT FROM。</summary>
            private static int FindFromClauseTokenIndex(List<TSqlParserToken> tokens, int localOffset)
            {
                int selectIdx = FindCurrentQuerySelectIndex(tokens, localOffset, out int lastSemiIdx, out int selectBeforeSemi);
                if (selectIdx < 0 && selectBeforeSemi >= 0 && lastSemiIdx >= 0
                    && IsClauseContinuationAfter(tokens, lastSemiIdx, localOffset))
                    selectIdx = selectBeforeSemi;
                if (selectIdx >= 0)
                {
                    int fromIdx = FindFromAfterSelect(tokens, selectIdx);
                    if (fromIdx >= 0) return fromIdx;
                    return -1;
                }
                return FindLastFromAtCaretDepth(tokens, localOffset);
            }

            /// <summary>光标所在查询的 SELECT（内层派生表 SELECT 在括号闭合后不再当作当前查询）。</summary>
            private static int FindCurrentQuerySelectIndex(
                List<TSqlParserToken> tokens, int localOffset, out int lastSemiIdx, out int selectBeforeSemi)
            {
                var selectIdxs = GetEnclosingSelectIndices(tokens, localOffset, out lastSemiIdx, out selectBeforeSemi);
                return selectIdxs.Count > 0 ? selectIdxs[selectIdxs.Count - 1] : -1;
            }

            /// <summary>由外到内：尚未闭合的 SELECT（相关子查询可见外层别名）。</summary>
            private static List<int> GetEnclosingSelectIndices(
                List<TSqlParserToken> tokens, int localOffset, out int lastSemiIdx, out int selectBeforeSemi)
            {
                lastSemiIdx = -1;
                selectBeforeSemi = -1;
                var selectIdxs = new List<int>();
                if (tokens == null) return selectIdxs;
                int depth = 0;
                var selectDepths = new List<int>();
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= localOffset) break;
                    if (t.TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        depth++;
                        continue;
                    }
                    if (t.TokenType == TSqlTokenType.RightParenthesis)
                    {
                        while (selectIdxs.Count > 0 && selectDepths[selectIdxs.Count - 1] >= depth)
                        {
                            selectIdxs.RemoveAt(selectIdxs.Count - 1);
                            selectDepths.RemoveAt(selectDepths.Count - 1);
                        }
                        if (depth > 0) depth--;
                        continue;
                    }
                    string text = t.Text?.ToUpperInvariant();
                    if (text == ";" || text == "GO")
                    {
                        selectBeforeSemi = selectIdxs.Count > 0 ? selectIdxs[selectIdxs.Count - 1] : -1;
                        lastSemiIdx = i;
                        selectIdxs.Clear();
                        selectDepths.Clear();
                        depth = 0;
                        continue;
                    }
                    if (text == "SELECT")
                    {
                        while (selectIdxs.Count > 0 && selectDepths[selectIdxs.Count - 1] >= depth)
                        {
                            selectIdxs.RemoveAt(selectIdxs.Count - 1);
                            selectDepths.RemoveAt(selectDepths.Count - 1);
                        }
                        selectIdxs.Add(i);
                        selectDepths.Add(depth);
                        continue;
                    }
                    if (IsNewStatementKeyword(text) && text != "SELECT")
                    {
                        selectIdxs.Clear();
                        selectDepths.Clear();
                    }
                }
                return selectIdxs;
            }

            private static int FindFromAfterSelect(List<TSqlParserToken> tokens, int selectIdx)
            {
                int depth = 0;
                for (int i = selectIdx + 1; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        depth++;
                        continue;
                    }
                    if (t.TokenType == TSqlTokenType.RightParenthesis)
                    {
                        if (depth > 0) depth--;
                        continue;
                    }
                    if (depth > 0) continue;
                    string kw = t.Text?.ToUpperInvariant();
                    if (kw == ";" || kw == "GO" || kw == "SELECT")
                        break;
                    if (kw == "FROM")
                        return i;
                    if (kw == "WHERE" || kw == "GROUP" || kw == "ORDER" || kw == "HAVING"
                        || kw == "UNION" || kw == "EXCEPT" || kw == "INTERSECT")
                        break;
                }
                return -1;
            }

            private static int FindLastFromAtCaretDepth(List<TSqlParserToken> tokens, int localOffset)
            {
                int lastFrom = -1;
                int fromBeforeSemi = -1;
                int lastSemiIdx = -1;
                int depth = 0;
                int caretDepth = ComputeParenDepthBefore(tokens, localOffset);
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= localOffset) break;
                    if (t.TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        depth++;
                        continue;
                    }
                    if (t.TokenType == TSqlTokenType.RightParenthesis)
                    {
                        if (depth > 0) depth--;
                        continue;
                    }
                    string text = t.Text?.ToUpperInvariant();
                    if (text == ";" || text == "GO")
                    {
                        fromBeforeSemi = lastFrom;
                        lastSemiIdx = i;
                        lastFrom = -1;
                        depth = 0;
                        continue;
                    }
                    if (IsNewStatementKeyword(text))
                        lastFrom = -1;
                    if (text == "FROM" && depth == caretDepth)
                        lastFrom = i;
                }
                if (lastFrom >= 0) return lastFrom;
                if (fromBeforeSemi >= 0 && lastSemiIdx >= 0
                    && IsClauseContinuationAfter(tokens, lastSemiIdx, localOffset))
                    return fromBeforeSemi;
                return -1;
            }

            private static int ComputeParenDepthBefore(List<TSqlParserToken> tokens, int localOffset)
            {
                int depth = 0;
                if (tokens == null) return 0;
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= localOffset) break;
                    if (t.TokenType == TSqlTokenType.LeftParenthesis) depth++;
                    else if (t.TokenType == TSqlTokenType.RightParenthesis && depth > 0) depth--;
                }
                return depth;
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
                int depth = 0;
                for (int i = fromIdx + 1; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        depth++;
                        continue;
                    }
                    if (t.TokenType == TSqlTokenType.RightParenthesis)
                    {
                        if (depth > 0) depth--;
                        continue;
                    }
                    if (depth > 0) continue;
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

            /// <summary>QuickInfo 复用：当前查询 FROM 别名 / 派生表列（只走词法）。</summary>
            internal static LocalSymbols CollectQueryLocalsFromTokens(List<TSqlParserToken> tokens, int localOffset, string text = null, string fullText = null, int caretOffset = -1)
            {
                var local = new LocalSymbols();
                if (tokens != null)
                    CollectAliasesFromTokens(tokens, localOffset, local, text);
                if (!string.IsNullOrEmpty(fullText) && caretOffset >= 0)
                    SupplementFromAliasesFromText(fullText, caretOffset, local);
                return local;
            }

            /// <summary>
            /// 大切片可能从相关子查询的内层 SELECT 起刀，FROM … AS KC 落在光标之后进不了 token 别名。
            /// 用全文按括号深度找外层 FROM 再收一遍别名（只词法、不进 AST 缓存）。
            /// </summary>
            internal static void SupplementFromAliasesFromText(string fullText, int caret, LocalSymbols local)
            {
                if (string.IsNullOrEmpty(fullText) || local == null || caret < 0) return;
                if (!TryGetBatchRange(fullText, caret, out int batchStart, out int batchEnd))
                    return;
                int searchTo = batchEnd;
                int lookAhead = caret + StmtLookAheadChars;
                if (lookAhead > searchTo) searchTo = lookAhead;
                if (searchTo > fullText.Length) searchTo = fullText.Length;
                int from = FindTopLevelFrom(fullText, caret, batchStart, searchTo);
                if (from >= 0)
                    TokenizeAndCollectFromRegion(fullText, from, local);
                int pos = caret;
                for (int level = 0; level < 6; level++)
                {
                    int open = FindUnmatchedOpenParen(fullText, batchStart, pos);
                    if (open < 0) break;
                    int sel = FindSelectBefore(fullText, open, batchStart);
                    int parentFrom = sel >= 0 ? FindFromAfterSelectInText(fullText, sel, fullText.Length) : -1;
                    if (parentFrom < 0)
                        parentFrom = FindTopLevelFrom(fullText, open, batchStart, searchTo);
                    if (parentFrom >= 0 && parentFrom != from)
                        TokenizeAndCollectFromRegion(fullText, parentFrom, local);
                    pos = open;
                }
            }

            private static void TokenizeAndCollectFromRegion(string text, int fromKw, LocalSymbols local)
            {
                if (string.IsNullOrEmpty(text) || fromKw < 0 || local == null) return;
                int regionEnd = ScanTopLevelFromRegionEnd(text, fromKw, text.Length);
                if (regionEnd <= fromKw) return;
                string fromSlice = text.Substring(fromKw, regionEnd - fromKw);
                TokenizeNoCache(fromSlice, out var tokens);
                if (tokens == null || tokens.Count == 0) return;
                int fromIdx = 0;
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (string.Equals(t.Text, "FROM", StringComparison.OrdinalIgnoreCase))
                    {
                        fromIdx = i;
                        break;
                    }
                }
                CollectAliasesInFromRegion(tokens, fromIdx, local, fromSlice);
            }

            /// <summary>从当前语句整个 FROM…WHERE 区间收集所有表/JOIN 别名（含 db..table、table(nolock) alias）。</summary>
            private static void CollectAliasesFromTokens(List<TSqlParserToken> tokens, int localOffset, LocalSymbols local, string text = null)
            {
                CollectCtesFromTokens(tokens, localOffset, local, text);
                CollectGroupByColumns(tokens, localOffset, local);
                int fromIdx = FindFromClauseTokenIndex(tokens, localOffset);
                if (fromIdx >= 0)
                    CollectAliasesInFromRegion(tokens, fromIdx, local, text);
                CollectEnclosingFromAliases(tokens, localOffset, local, text, fromIdx);
            }

            /// <summary>收集当前语句 GROUP BY 的列名（HAVING 只提示这些列 + 聚合函数）。</summary>
            private static void CollectGroupByColumns(List<TSqlParserToken> tokens, int localOffset, LocalSymbols local)
            {
                if (tokens == null || local == null) return;
                // 只取当前查询（深度 + 分号作用域）的 GROUP BY，避免误取子查询/上一语句的 GROUP BY
                int selectIdx = FindCurrentQuerySelectIndex(tokens, localOffset, out _, out _);
                int scanStart = selectIdx >= 0 ? selectIdx + 1 : 0;
                int groupIdx = -1;
                int depth = 0;
                for (int i = scanStart; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= localOffset) break;
                    if (t.TokenType == TSqlTokenType.LeftParenthesis) { depth++; continue; }
                    if (t.TokenType == TSqlTokenType.RightParenthesis) { if (depth > 0) depth--; continue; }
                    if (depth > 0) continue;
                    if (string.Equals(t.Text, "GROUP", StringComparison.OrdinalIgnoreCase)
                        && NextKeywordIs(tokens, i, "BY"))
                    {
                        groupIdx = i;
                        break;
                    }
                }
                if (groupIdx < 0) return;

                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string lastIdent = null;
                for (int i = groupIdx + 1; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= localOffset) break;
                    string kw = t.Text?.ToUpperInvariant();
                    if (kw == "HAVING" || kw == "ORDER" || kw == "UNION" || kw == "EXCEPT"
                        || kw == "INTERSECT" || kw == ";" || kw == "GO" || kw == "SELECT")
                        break;
                    if (t.TokenType == TSqlTokenType.Comma)
                    {
                        if (!string.IsNullOrEmpty(lastIdent)) cols.Add(lastIdent);
                        lastIdent = null;
                        continue;
                    }
                    if (IsWordLikeToken(t) || IsIdentifierLike(t) || IsPartialObjectNameToken(t))
                    {
                        string name = UnbracketIdentifier(t.Text);
                        if (!string.IsNullOrEmpty(name) && !IsGroupByNoiseKeyword(name))
                            lastIdent = name;
                    }
                    // 点号/括号/运算符不重置 lastIdent（alias.col → col；sum(x) → x）
                }
                if (!string.IsNullOrEmpty(lastIdent)) cols.Add(lastIdent);

                foreach (var c in cols)
                {
                    if (!string.IsNullOrEmpty(c) && !local.GroupByColumns.Contains(c))
                        local.GroupByColumns.Add(c);
                }
            }

            private static bool IsGroupByNoiseKeyword(string name)
            {
                switch (name.ToUpperInvariant())
                {
                    case "BY":
                    case "GROUP":
                    case "GROUPING":
                    case "SETS":
                    case "ROLLUP":
                    case "CUBE":
                        return true;
                    default:
                        return false;
                }
            }

            private static bool NextKeywordIs(List<TSqlParserToken> tokens, int fromIdx, string keyword)
            {
                for (int i = fromIdx + 1; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    return string.Equals(t.Text, keyword, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }

            /// <summary>
            /// 词法级收集 CTE（WITH cte [(cols)] AS (query)），不依赖 ScriptDOM AST。
            /// 残缺 SQL（如尾部 alias. 未闭合）时 AST 常为 0 语句，CTE 只能从 token 流取。
            /// </summary>
            private static void CollectCtesFromTokens(List<TSqlParserToken> tokens, int localOffset, LocalSymbols local, string text)
            {
                if (tokens == null || local == null) return;
                int i = 0;
                int depth = 0;
                while (i < tokens.Count)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) { i++; continue; }
                    if (t.Offset >= localOffset) break;
                    if (t.TokenType == TSqlTokenType.LeftParenthesis) { depth++; i++; continue; }
                    if (t.TokenType == TSqlTokenType.RightParenthesis) { if (depth > 0) depth--; i++; continue; }
                    if (depth > 0) { i++; continue; }
                    if (string.Equals(t.Text, "WITH", StringComparison.OrdinalIgnoreCase))
                    {
                        // WITH (NOLOCK) 等表提示：下一有效 token 是 '('；否则是 CTE
                        int nxt = i + 1;
                        while (nxt < tokens.Count && (tokens[nxt] == null || IsInsignificantToken(tokens[nxt]))) nxt++;
                        if (nxt < tokens.Count && tokens[nxt] != null && tokens[nxt].TokenType == TSqlTokenType.LeftParenthesis)
                        {
                            i++;
                            continue;
                        }
                        // CTE 作用域：只作用于紧随其后的同一条语句；光标已越过该语句则跳过
                        int stmtEnd = FindStatementEnd(tokens, i);
                        if (localOffset <= stmtEnd)
                            i = ParseCteList(tokens, i, local, text, localOffset);
                        else
                            i++;
                        continue;
                    }
                    i++;
                }
            }

            /// <summary>
            /// WITH 语句的结束偏移：其后第一个顶层 ;/GO，或下一条顶层 DML 语句的起始关键字。
            /// 无分号多语句（如两条独立 SELECT）时按第二个顶层 DML 关键字作边界，避免前一语句 CTE 泄漏。
            /// UNION/EXCEPT/INTERSECT 后的 SELECT 是主查询延续；INSERT 的源 SELECT 不算新语句。
            /// 对 INSERT...VALUES 后紧跟 SELECT、MERGE 的 WHEN...THEN action 等罕见歧义，保守地继续扫描
            /// （宁可少量泄漏，也不误伤主查询内的 CTE 引用导致补全缺失）。
            /// </summary>
            private static int FindStatementEnd(List<TSqlParserToken> tokens, int fromIdx)
            {
                int depth = 0;
                bool sawMainQuery = false;
                bool mainIsInsert = false;
                bool sawInsertSelect = false;
                bool pendingUnion = false;
                for (int k = fromIdx + 1; k < tokens.Count; k++)
                {
                    var t = tokens[k];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.TokenType == TSqlTokenType.LeftParenthesis) { depth++; continue; }
                    if (t.TokenType == TSqlTokenType.RightParenthesis) { if (depth > 0) depth--; continue; }
                    if (depth > 0) continue;
                    string kw = t.Text?.ToUpperInvariant();
                    if (kw == ";" || kw == "GO") return t.Offset;
                    if (kw == "UNION" || kw == "EXCEPT" || kw == "INTERSECT") { pendingUnion = true; continue; }
                    bool isDml = kw == "SELECT" || kw == "INSERT" || kw == "UPDATE" || kw == "DELETE" || kw == "MERGE";
                    if (!isDml) continue;
                    if (!sawMainQuery)
                    {
                        sawMainQuery = true;
                        mainIsInsert = kw == "INSERT";
                        pendingUnion = false;
                        continue;
                    }
                    if (pendingUnion) { pendingUnion = false; continue; } // UNION SELECT 是主查询延续
                    if (mainIsInsert && kw == "SELECT" && !sawInsertSelect)
                    {
                        sawInsertSelect = true;
                        continue; // INSERT INTO t SELECT 的源查询
                    }
                    return t.Offset;
                }
                return int.MaxValue;
            }

            /// <summary>解析 WITH 后的 CTE 列表（逗号分隔），返回扫描结束位置。</summary>
            private static int ParseCteList(List<TSqlParserToken> tokens, int withIdx, LocalSymbols local, string text, int limit)
            {
                int i = withIdx + 1;
                while (true)
                {
                    while (i < tokens.Count && (tokens[i] == null || IsInsignificantToken(tokens[i]))) i++;
                    if (i >= tokens.Count) return i;
                    var nameTok = tokens[i];
                    if (nameTok == null || nameTok.Offset >= limit) return i;
                    if (!(IsWordLikeToken(nameTok) || IsIdentifierLike(nameTok))) return i;
                    string name = UnbracketIdentifier(nameTok.Text);
                    i++;

                    // 可选显式列清单 (col1, col2)
                    var explicitCols = new List<string>();
                    while (i < tokens.Count && (tokens[i] == null || IsInsignificantToken(tokens[i]))) i++;
                    if (i < tokens.Count && tokens[i] != null && tokens[i].TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        int closeCols = SkipParenthesesForward(tokens, i);
                        for (int k = i + 1; k < closeCols && k < tokens.Count; k++)
                        {
                            var ct = tokens[k];
                            if (ct == null || IsInsignificantToken(ct)) continue;
                            if (ct.TokenType == TSqlTokenType.Comma) continue;
                            if (IsWordLikeToken(ct) || IsIdentifierLike(ct) || IsPartialObjectNameToken(ct))
                                explicitCols.Add(UnbracketIdentifier(ct.Text));
                        }
                        i = closeCols;
                    }

                    while (i < tokens.Count && (tokens[i] == null || IsInsignificantToken(tokens[i]))) i++;
                    if (i >= tokens.Count || tokens[i] == null || !string.Equals(tokens[i].Text, "AS", StringComparison.OrdinalIgnoreCase)) return i;
                    i++;

                    while (i < tokens.Count && (tokens[i] == null || IsInsignificantToken(tokens[i]))) i++;
                    if (i >= tokens.Count || tokens[i] == null || tokens[i].TokenType != TSqlTokenType.LeftParenthesis) return i;
                    int open = i;
                    int close = SkipParenthesesForward(tokens, open);

                    List<SelectListCol> cols;
                    if (explicitCols.Count > 0)
                    {
                        cols = new List<SelectListCol>();
                        foreach (var c in explicitCols) cols.Add(new SelectListCol { Name = c });
                    }
                    else
                    {
                        cols = CollectSelectListColumns(tokens, open + 1, close, text);
                    }
                    MergeCte(local, name, cols, null);

                    i = close;
                    while (i < tokens.Count && (tokens[i] == null || IsInsignificantToken(tokens[i]))) i++;
                    if (i >= tokens.Count || tokens[i] == null || tokens[i].TokenType != TSqlTokenType.Comma) return i;
                    i++; // 逗号后继续下一个 CTE
                }
            }

            /// <summary>相关子查询可见外层 FROM 别名；光标落在某层 FROM 派生表内则不收该层（避免漏出 KCZ）。</summary>
            private static void CollectEnclosingFromAliases(
                List<TSqlParserToken> tokens, int localOffset, LocalSymbols local, string text, int currentFromIdx)
            {
                var enclosing = GetEnclosingSelectIndices(tokens, localOffset, out _, out _);
                for (int s = 0; s < enclosing.Count; s++)
                {
                    int outerFrom = FindFromAfterSelect(tokens, enclosing[s]);
                    if (outerFrom < 0 || outerFrom == currentFromIdx) continue;
                    if (IsCaretInsideFromRegion(tokens, outerFrom, localOffset))
                        continue;
                    CollectAliasesInFromRegion(tokens, outerFrom, local, text);
                }
            }

            private static bool IsCaretInsideFromRegion(List<TSqlParserToken> tokens, int fromIdx, int localOffset)
            {
                if (tokens == null || fromIdx < 0 || fromIdx >= tokens.Count || tokens[fromIdx] == null)
                    return false;
                int start = tokens[fromIdx].Offset;
                int end = FindFromRegionEnd(tokens, fromIdx);
                return localOffset >= start && localOffset < end;
            }

            private static void CollectAliasesInFromRegion(
                List<TSqlParserToken> tokens, int fromIdx, LocalSymbols local, string text)
            {
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
                    bool registeredDerived = false;
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
                            if (string.IsNullOrEmpty(table) && NextIsQueryStart(tokens, i + 1, regionEnd))
                            {
                                int close = SkipParenthesesForward(tokens, i);
                                var cols = CollectSelectListColumns(tokens, i + 1, close, text);
                                string definitionSql = SliceSource(text, tokens, i, close);
                                i = close;
                                string derivedAlias = alias;
                                int j = i;
                                while (j < tokens.Count)
                                {
                                    var at = tokens[j];
                                    if (at == null || IsInsignificantToken(at)) { j++; continue; }
                                    if (at.Offset >= regionEnd) break;
                                    string akw = at.Text?.ToUpperInvariant();
                                    if (akw == "AS") { j++; continue; }
                                    if (akw == "WITH")
                                    {
                                        j++;
                                        continue;
                                    }
                                    if (at.TokenType == TSqlTokenType.LeftParenthesis)
                                    {
                                        j = SkipParenthesesForward(tokens, j);
                                        continue;
                                    }
                                    if (IsWordLikeToken(at) || IsPartialObjectNameToken(at) || IsIdentifierLike(at))
                                    {
                                        string an = UnbracketIdentifier(at.Text);
                                        if (!string.IsNullOrEmpty(an) && !IsSqlTableKeyword(an))
                                            derivedAlias = an;
                                        j++;
                                    }
                                    break;
                                }
                                i = j;
                                if (!string.IsNullOrEmpty(derivedAlias))
                                    MergeDerivedTable(local, derivedAlias, cols, definitionSql);
                                registeredDerived = true;
                                break;
                            }
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
                    if (!registeredDerived && (string.IsNullOrEmpty(table) || IsSqlTableKeyword(table)))
                    {
                        if (i <= segmentStartI) i = segmentStartI + 1;
                        continue;
                    }
                    if (!registeredDerived)
                    {
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
                    }

                    // 仅逗号 / JOIN 可接下一表；残留 AS alias / WITH (NOLOCK) 则跳过继续扫
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
                        if (lkw == "WITH")
                        {
                            look++;
                            while (look < tokens.Count)
                            {
                                var wt = tokens[look];
                                if (wt == null || IsInsignificantToken(wt)) { look++; continue; }
                                if (wt.TokenType == TSqlTokenType.LeftParenthesis)
                                {
                                    look = SkipParenthesesForward(tokens, look);
                                    continue;
                                }
                                break;
                            }
                            continue;
                        }
                        return;
                    }
                }
            }

            private static bool NextIsQueryStart(List<TSqlParserToken> tokens, int fromIdx, int regionEnd)
            {
                for (int j = fromIdx; j < tokens.Count; j++)
                {
                    var t = tokens[j];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= regionEnd) return false;
                    string kw = t.Text?.ToUpperInvariant();
                    return kw == "SELECT" || kw == "WITH";
                }
                return false;
            }

            private sealed class SelectListCol
            {
                public string Name;
                public string Sql;
            }

            private static List<SelectListCol> CollectSelectListColumns(
                List<TSqlParserToken> tokens, int fromIdx, int closeIdx, string text)
            {
                var cols = new List<SelectListCol>();
                if (tokens == null || fromIdx < 0) return cols;
                int end = closeIdx < 0 ? tokens.Count : Math.Min(closeIdx, tokens.Count);
                int depth = 0;
                bool inSelectList = false;
                bool afterAs = false;
                string lastIdent = null;
                string pending = null;
                int itemStart = -1;
                for (int i = fromIdx; i < end; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        if (inSelectList && depth == 0 && itemStart < 0)
                            itemStart = t.Offset;
                        depth++;
                        continue;
                    }
                    if (t.TokenType == TSqlTokenType.RightParenthesis)
                    {
                        if (depth > 0) depth--;
                        continue;
                    }
                    if (depth > 0)
                    {
                        // 无 AS 时用括号内最后一个标识符：sum(stock) → stock
                        if ((IsWordLikeToken(t) || IsIdentifierLike(t))
                            && t.TokenType != TSqlTokenType.Integer
                            && t.TokenType != TSqlTokenType.Real
                            && t.TokenType != TSqlTokenType.Numeric)
                        {
                            string inner = UnbracketIdentifier(t.Text);
                            if (!string.IsNullOrEmpty(inner) && !IsSqlTableKeyword(inner) && inner != "*")
                                lastIdent = inner;
                        }
                        continue;
                    }
                    string kw = t.Text?.ToUpperInvariant();
                    if (!inSelectList)
                    {
                        if (kw == "SELECT") inSelectList = true;
                        continue;
                    }
                    if (kw == "FROM" || kw == "WHERE" || kw == "GROUP" || kw == "HAVING"
                        || kw == "UNION" || kw == "EXCEPT" || kw == "INTERSECT")
                    {
                        AddSelectListColumn(cols, pending, lastIdent, SliceSource(text, itemStart, t.Offset));
                        break;
                    }
                    if (kw == "DISTINCT" || kw == "ALL" || kw == "TOP" || kw == "PERCENT" || kw == "TIES")
                        continue;
                    if (kw == "AS")
                    {
                        afterAs = true;
                        if (itemStart < 0) itemStart = t.Offset;
                        continue;
                    }
                    if (kw == ",")
                    {
                        AddSelectListColumn(cols, pending, lastIdent, SliceSource(text, itemStart, t.Offset));
                        pending = null;
                        lastIdent = null;
                        afterAs = false;
                        itemStart = -1;
                        continue;
                    }
                    if (t.TokenType == TSqlTokenType.Dot)
                    {
                        if (itemStart < 0) itemStart = t.Offset;
                        continue;
                    }
                    if (IsWordLikeToken(t) || IsPartialObjectNameToken(t) || IsIdentifierLike(t))
                    {
                        string name = UnbracketIdentifier(t.Text);
                        if (string.IsNullOrEmpty(name) || IsSqlTableKeyword(name) || name == "*")
                            continue;
                        if (itemStart < 0) itemStart = t.Offset;
                        if (afterAs)
                        {
                            pending = name;
                            afterAs = false;
                            lastIdent = null;
                            continue;
                        }
                        lastIdent = name;
                    }
                    else if (itemStart < 0)
                    {
                        itemStart = t.Offset;
                    }
                }
                int tailEnd = -1;
                if (closeIdx > 0 && closeIdx <= tokens.Count)
                {
                    var closeTok = tokens[closeIdx - 1];
                    if (closeTok != null) tailEnd = closeTok.Offset;
                }
                AddSelectListColumn(cols, pending, lastIdent, SliceSource(text, itemStart, tailEnd));
                return cols;
            }

            private static void AddSelectListColumn(List<SelectListCol> cols, string pending, string lastIdent, string sql)
            {
                string name = !string.IsNullOrEmpty(pending) ? pending : lastIdent;
                if (string.IsNullOrEmpty(name) || cols == null) return;
                foreach (var c in cols)
                {
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return;
                }
                cols.Add(new SelectListCol { Name = name, Sql = sql });
            }

            private static string SliceSource(string text, List<TSqlParserToken> tokens, int startIdx, int endExclusive)
            {
                if (string.IsNullOrEmpty(text) || tokens == null || startIdx < 0 || startIdx >= tokens.Count)
                    return null;
                var startTok = tokens[startIdx];
                if (startTok == null) return null;
                int start = startTok.Offset;
                int last = Math.Min(endExclusive, tokens.Count) - 1;
                while (last >= startIdx && (tokens[last] == null || IsInsignificantToken(tokens[last])))
                    last--;
                if (last < startIdx) return null;
                var endTok = tokens[last];
                int end = endTok.Offset + (endTok.Text == null ? 0 : endTok.Text.Length);
                return SliceSource(text, start, end);
            }

            private static string SliceSource(string text, int start, int end)
            {
                if (string.IsNullOrEmpty(text) || start < 0) return null;
                if (end < 0 || end > text.Length) end = text.Length;
                if (end <= start) return null;
                return text.Substring(start, end - start).Trim();
            }

            private static void MergeDerivedTable(LocalSymbols local, string alias, List<string> cols)
            {
                if (local == null || string.IsNullOrEmpty(alias) || IsSqlTableKeyword(alias)) return;
                var mapped = new List<SelectListCol>();
                if (cols != null)
                {
                    foreach (var col in cols)
                    {
                        if (!string.IsNullOrEmpty(col))
                            mapped.Add(new SelectListCol { Name = col });
                    }
                }
                MergeDerivedTable(local, alias, mapped, null);
            }

            private static void MergeDerivedTable(LocalSymbols local, string alias, List<SelectListCol> cols, string definitionSql)
            {
                if (local == null || string.IsNullOrEmpty(alias) || IsSqlTableKeyword(alias)) return;
                MergeAlias(local, alias, new TableRef());
                MergeCte(local, alias, cols, definitionSql);
            }

            private static void MergeCte(LocalSymbols local, string name, List<SelectListCol> cols, string definitionSql)
            {
                if (local?.Ctes == null || string.IsNullOrEmpty(name)) return;
                var existing = local.Ctes.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    var info = new CteInfo { Name = name, DefinitionSql = definitionSql };
                    if (cols != null)
                    {
                        foreach (var col in cols)
                        {
                            if (col == null || string.IsNullOrEmpty(col.Name)) continue;
                            info.ColumnNames.Add(col.Name);
                            info.ColumnSqls.Add(col.Sql ?? string.Empty);
                        }
                    }
                    local.Ctes.Add(info);
                    return;
                }
                if (string.IsNullOrEmpty(existing.DefinitionSql) && !string.IsNullOrEmpty(definitionSql))
                    existing.DefinitionSql = definitionSql;
                if (existing.ColumnNames.Count == 0 && cols != null)
                {
                    foreach (var col in cols)
                    {
                        if (col == null || string.IsNullOrEmpty(col.Name)) continue;
                        existing.ColumnNames.Add(col.Name);
                        existing.ColumnSqls.Add(col.Sql ?? string.Empty);
                    }
                }
                else if (existing.ColumnSqls.Count == 0 && cols != null)
                {
                    foreach (var col in cols)
                    {
                        if (col == null || string.IsNullOrEmpty(col.Name)) continue;
                        existing.ColumnSqls.Add(col.Sql ?? string.Empty);
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
                    case "PIVOT":
                    case "UNPIVOT":
                    case "TABLESAMPLE":
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
                else if (tableRef is QueryDerivedTable qdt)
                {
                    string alias = qdt.Alias?.Value;
                    if (string.IsNullOrEmpty(alias)) return;
                    var cols = new List<string>();
                    CollectQuerySelectColumns(qdt.QueryExpression, cols);
                    MergeDerivedTable(local, alias, cols);
                }
            }

            private static void CollectQuerySelectColumns(QueryExpression query, List<string> cols)
            {
                if (query == null || cols == null) return;
                if (query is QuerySpecification qs)
                {
                    foreach (var el in qs.SelectElements ?? Enumerable.Empty<SelectElement>())
                    {
                        if (el is SelectScalarExpression scalar)
                        {
                            string name = scalar.ColumnName?.Value
                                          ?? scalar.ColumnName?.Identifier?.Value;
                            if (string.IsNullOrEmpty(name))
                                name = GetScalarExpressionName(scalar.Expression);
                            if (!string.IsNullOrEmpty(name) &&
                                !cols.Exists(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
                                cols.Add(name);
                        }
                    }
                    return;
                }
                if (query is BinaryQueryExpression bq)
                    CollectQuerySelectColumns(bq.FirstQueryExpression, cols);
                else if (query is QueryParenthesisExpression pq)
                    CollectQuerySelectColumns(pq.QueryExpression, cols);
            }

            /// <summary>从标量表达式提取可用作列名的最后一个标识符（sum(stock)→stock、a+b→b、CASE…END→内部列）。</summary>
            private static string GetScalarExpressionName(ScalarExpression expr)
            {
                if (expr == null) return null;
                if (expr is ColumnReferenceExpression col)
                {
                    var ids = col.MultiPartIdentifier?.Identifiers;
                    if (ids != null && ids.Count > 0)
                        return ids[ids.Count - 1].Value;
                    return null;
                }
                if (expr is FunctionCall fc)
                {
                    if (fc.Parameters != null)
                    {
                        for (int i = fc.Parameters.Count - 1; i >= 0; i--)
                        {
                            string n = GetScalarExpressionName(fc.Parameters[i]);
                            if (!string.IsNullOrEmpty(n)) return n;
                        }
                    }
                    return null;
                }
                if (expr is ParenthesisExpression pe)
                    return GetScalarExpressionName(pe.Expression);
                return null;
            }

            #endregion
        }
    }
}
