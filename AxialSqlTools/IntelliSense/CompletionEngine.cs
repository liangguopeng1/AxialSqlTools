using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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

        /// <summary>
        /// ScriptDOM 解析 + 上下文判定 + 候选生成。
        /// 按 GO 分批，仅解析光标所在批次，缓存上次解析结果。
        /// 任何解析失败返回 Unknown 上下文，不抛异常。
        /// </summary>
        public class CompletionEngine
        {
            private string _lastBatchText;
            private TSqlScript _lastScript;
            private List<TSqlParserToken> _lastTokens;

            private readonly TSql170Parser _parser = new TSql170Parser(true);

            public CompletionResult GetCompletion(
                string fullText,
                int caretOffset,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                var result = new CompletionResult
                {
                    Context = CompletionContext.Unknown,
                    Prefix = string.Empty,
                    Items = new List<CompletionItem>()
                };

                if (string.IsNullOrEmpty(fullText) || caretOffset < 0)
                {
                    return result;
                }

                try
                {
                    string batchText;
                    int batchStart;
                    if (!TryGetBatchAt(fullText, caretOffset, out batchText, out batchStart))
                    {
                        return result;
                    }

                    var script = ParseCached(batchText);
                    int localOffset = caretOffset - batchStart;
                    if (localOffset < 0) localOffset = 0;

                    // 原文兜底：分号后 / 行首输入关键字（不依赖 ScriptDom token，避免残缺 SQL 丢 token）
                    string rawPrefix;
                    bool afterStmtBreak;
                    bool atLineStart;
                    TryGetRawTypingPrefix(batchText, localOffset, out rawPrefix, out afterStmtBreak, out atLineStart);

                    // EXEC 只依赖原文，ScriptDom 失败时也要能补过程（caiwu..com 等残缺语句易 parse 失败）
                    FromObjectNameContext execNameEarly;
                    if (TryParseExecObjectName(batchText, localOffset, out execNameEarly))
                    {
                        string execPrefix = execNameEarly.Partial ?? string.Empty;
                        if (string.IsNullOrEmpty(execPrefix)) execPrefix = rawPrefix;
                        var localEarly = script != null ? ExtractLocalSymbols(script) : new LocalSymbols();
                        if (script != null)
                            CollectAliases(script, localOffset, localEarly, _lastTokens);
                        result.Context = CompletionContext.AfterExec;
                        result.Prefix = execPrefix;
                        result.Items = BuildItems(CompletionContext.AfterExec, execPrefix, execNameEarly, localEarly, catalog, settings, connInfo);
                        result.Items = FilterAndSort(result.Items, execPrefix, settings, connInfo, execNameEarly);
                        result.ReplaceStartOffset = ComputeReplaceStartOffset(batchStart, localOffset, execPrefix, execNameEarly, _lastTokens);
                        result.ReplaceEndOffset = caretOffset;
                        return result;
                    }

                    if (script == null)
                    {
                        if ((afterStmtBreak || atLineStart) && !string.IsNullOrEmpty(rawPrefix))
                        {
                            result.Context = CompletionContext.BatchStart;
                            result.Prefix = rawPrefix;
                            result.Items = BuildItems(CompletionContext.BatchStart, rawPrefix, null, new LocalSymbols(), catalog, settings, connInfo);
                            result.Items = FilterAndSort(result.Items, rawPrefix, settings, connInfo, null);
                            result.ReplaceStartOffset = caretOffset - rawPrefix.Length;
                            result.ReplaceEndOffset = caretOffset;
                        }
                        return result;
                    }

                    var tokens = _lastTokens;
                    var local = ExtractLocalSymbols(script);
                    CollectAliases(script, localOffset, local, tokens);

                    string prefix;

                    // 分号/GO 后：强制语句起始（SELECT 等）——须先于成员访问，避免被上一句 a.col 误抢
                    if (afterStmtBreak && !string.IsNullOrEmpty(rawPrefix))
                    {
                        result.Context = CompletionContext.BatchStart;
                        result.Prefix = rawPrefix;
                        result.Items = BuildItems(CompletionContext.BatchStart, rawPrefix, null, local, catalog, settings, connInfo);
                        result.Items = FilterAndSort(result.Items, rawPrefix, settings, connInfo, null);
                        result.ReplaceStartOffset = caretOffset - rawPrefix.Length;
                        result.ReplaceEndOffset = caretOffset;
                        return result;
                    }

                    // 行首输入：多表 JOIN 后写 in/where，或新语句写 s
                    int caretTokenIndex = FindTokenIndexForPrefix(tokens, localOffset);
                    if (atLineStart && !string.IsNullOrEmpty(rawPrefix))
                    {
                        bool hasFrom = HasFromKeywordBefore(tokens, localOffset);
                        result.Context = hasFrom && !afterStmtBreak
                            ? CompletionContext.FromClause
                            : CompletionContext.BatchStart;
                        result.Prefix = rawPrefix;
                        var fromNameEarly = hasFrom && !afterStmtBreak
                            ? new FromObjectNameContext { InFromClause = true, Partial = rawPrefix }
                            : null;
                        if (result.Context == CompletionContext.FromClause)
                        {
                            result.Items = new List<CompletionItem>();
                            if (settings.includeKeywords)
                                AddKeywords(result.Items, AfterFromKeywords);
                        }
                        else
                        {
                            result.Items = BuildItems(CompletionContext.BatchStart, rawPrefix, null, local, catalog, settings, connInfo);
                        }
                        result.Items = FilterAndSort(result.Items, rawPrefix, settings, connInfo, fromNameEarly);
                        result.ReplaceStartOffset = caretOffset - rawPrefix.Length;
                        result.ReplaceEndOffset = caretOffset;
                        return result;
                    }

                    // FROM 多段名（库.表 / 库.架构.表）须先于成员访问，否则 jichushuju.cip 会被当成别名.列
                    var fromName = ParseFromObjectName(tokens, localOffset);
                    bool fromQualifiedName = fromName != null && fromName.InFromClause
                        && (fromName.AfterDot || (fromName.Segments != null && fromName.Segments.Count > 0));

                    if (!fromQualifiedName && TryGetMemberAccessPrefix(tokens, localOffset, out string memberPrefix))
                    {
                        string owner = GetOwnerBeforeDot(memberPrefix);
                        if (!string.IsNullOrEmpty(owner) && !local.Aliases.ContainsKey(owner))
                            CollectAliasesFromTokens(tokens, localOffset, local);

                        result.Context = CompletionContext.MemberAccess;
                        result.Prefix = memberPrefix;
                        result.Items = BuildItems(CompletionContext.MemberAccess, memberPrefix, null, local, catalog, settings, connInfo);
                        result.Items = FilterAndSort(result.Items, memberPrefix, settings, connInfo, null);
                        result.ReplaceStartOffset = ComputeReplaceStartOffset(batchStart, localOffset, memberPrefix, null, tokens);
                        result.ReplaceEndOffset = caretOffset;
                        return result;
                    }

                    if (caretTokenIndex >= 0 && IsAtLineStartTypingKeyword(tokens, localOffset, caretTokenIndex))
                    {
                        prefix = ExtractPrefix(tokens, localOffset);
                        if (string.IsNullOrEmpty(prefix)) prefix = rawPrefix;
                        result.Context = CompletionContext.BatchStart;
                        result.Prefix = prefix;
                        result.Items = BuildItems(CompletionContext.BatchStart, prefix, null, local, catalog, settings, connInfo);
                        result.Items = FilterAndSort(result.Items, prefix, settings, connInfo, null);
                        result.ReplaceStartOffset = ComputeReplaceStartOffset(batchStart, localOffset, prefix, null, tokens);
                        result.ReplaceEndOffset = caretOffset;
                        return result;
                    }

                    var ctx = GetContext(tokens, localOffset, fromName, out prefix);
                    if (string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(rawPrefix))
                        prefix = rawPrefix;
                    // EXEC caiwu..pro → 过滤用点号后的部分
                    if (ctx == CompletionContext.AfterExec && fromName != null && fromName.AfterDot
                        && fromName.Partial != null)
                        prefix = fromName.Partial;
                    result.Context = ctx;
                    result.Prefix = prefix;

                    result.Items = BuildItems(ctx, prefix, fromName, local, catalog, settings, connInfo);
                    result.Items = FilterAndSort(result.Items, prefix, settings, connInfo, fromName);
                    result.ReplaceStartOffset = ComputeReplaceStartOffset(batchStart, localOffset, prefix, fromName, tokens);
                    result.ReplaceEndOffset = caretOffset;
                    return result;
                }
                catch
                {
                    return result;
                }
            }

            #region 批次与解析

            private static readonly Regex GoBatchSplit =
                new Regex(@"(?im)^[ \t]*GO[ \t]*(?:;)?[ \t]*\r?\n", RegexOptions.Compiled);

            private bool TryGetBatchAt(string fullText, int caretOffset, out string batchText, out int batchStart)
            {
                batchText = fullText;
                batchStart = 0;

                var matches = GoBatchSplit.Matches(fullText);
                if (matches.Count == 0)
                {
                    return true;
                }

                int start = 0;
                foreach (Match m in matches)
                {
                    if (m.Index > caretOffset)
                    {
                        break;
                    }
                    start = m.Index + m.Length;
                }

                int end = fullText.Length;
                foreach (Match m in matches)
                {
                    if (m.Index >= start && m.Index >= caretOffset)
                    {
                        end = m.Index;
                        break;
                    }
                }

                batchStart = start;
                batchText = fullText.Substring(start, Math.Max(0, Math.Min(end, fullText.Length) - start));
                return true;
            }

            private TSqlScript ParseCached(string batchText)
            {
                if (string.Equals(_lastBatchText, batchText, StringComparison.Ordinal) && _lastScript != null)
                {
                    return _lastScript;
                }

                _lastBatchText = batchText;
                _lastScript = null;
                _lastTokens = null;

                try
                {
                    using (var reader = new StringReader(batchText))
                    {
                        var fragment = _parser.Parse(reader, out var errors);
                        _lastScript = fragment as TSqlScript;
                        if (_lastScript != null)
                        {
                            _lastTokens = _lastScript.ScriptTokenStream?.ToList() ?? new List<TSqlParserToken>();
                        }
                    }
                }
                catch
                {
                    _lastScript = null;
                    _lastTokens = new List<TSqlParserToken>();
                }

                return _lastScript;
            }

            #endregion

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
                            if (GetFromClause(stmt) == null) continue;
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
                if (target != null)
                {
                    var from = GetFromClause(target);
                    foreach (var tr in from.TableReferences ?? Enumerable.Empty<TableReference>())
                    {
                        CollectFromClause(tr, local);
                    }
                }
                // 始终用 token 补全 JOIN 别名（ScriptDom 对 kucun(nolock) 等可能丢别名）
                if (tokens != null)
                    CollectAliasesFromTokens(tokens, localOffset, local);
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
                        // 允许越过误写的分号去找 FROM（FROM 一定在分号前）
                        if (string.Equals(t.Text, "FROM", StringComparison.OrdinalIgnoreCase))
                            return i;
                        string kw = t.Text?.ToUpperInvariant();
                        if (kw == "WHERE" || kw == "GROUP" || kw == "ORDER" || kw == "HAVING"
                            || kw == "UNION" || kw == "EXCEPT" || kw == "INTERSECT")
                            break;
                    }
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

                    string database = null;
                    string schema = null;
                    string table = null;
                    string alias = null;
                    int dotRun = 0;
                    int partCount = 0;
                    while (i < tokens.Count)
                    {
                        var t = tokens[i];
                        if (t == null || IsInsignificantToken(t)) { i++; continue; }
                        if (t.Offset >= regionEnd) break;
                        string kw = t.Text?.ToUpperInvariant();
                        if (kw == "WHERE" || kw == "GROUP" || kw == "ORDER" || kw == "HAVING"
                            || kw == "UNION" || kw == "EXCEPT" || kw == "INTERSECT" || kw == ";"
                            || kw == "INNER" || kw == "LEFT" || kw == "RIGHT" || kw == "FULL"
                            || kw == "CROSS" || kw == "OUTER" || kw == "JOIN" || kw == "," || kw == "ON" || kw == "AS")
                        {
                            break;
                        }
                        if (kw == "WITH") { i++; continue; }
                        if (t.TokenType == TSqlTokenType.LeftParenthesis)
                        {
                            // 跳过 (nolock) 等表提示
                            int depth = 0;
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
                            continue;
                        }
                        if (t.TokenType == TSqlTokenType.Dot)
                        {
                            dotRun++;
                            i++;
                            continue;
                        }
                        if (!(IsWordLikeToken(t) || IsPartialObjectNameToken(t) || IsIdentifierLike(t)))
                        {
                            i++;
                            continue;
                        }
                        string name = UnbracketIdentifier(t.Text);
                        if (IsSqlTableKeyword(name)) { i++; continue; }
                        if (table != null && dotRun == 0)
                        {
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
                        if (dotRun >= 1 && partCount >= 1)
                        {
                            if (partCount == 1)
                            {
                                schema = table;
                                table = name;
                                partCount = 2;
                            }
                            else if (partCount == 2 && database == null)
                            {
                                database = schema;
                                schema = table;
                                table = name;
                                partCount = 3;
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
                    if (string.IsNullOrEmpty(table) || IsSqlTableKeyword(table)) continue;
                    var tref = new TableRef { Database = database, Schema = schema, Name = table };
                    NormalizeTableRef(tref);
                    if (!string.IsNullOrEmpty(alias) && !IsSqlTableKeyword(alias))
                        MergeAlias(local, alias, tref);
                    MergeAlias(local, tref.Name, tref);
                    if (!string.IsNullOrEmpty(tref.Schema))
                        MergeAlias(local, tref.Schema + "." + tref.Name, tref);
                    if (!string.IsNullOrEmpty(tref.Database))
                        MergeAlias(local, tref.Database + "." + (tref.Schema ?? "dbo") + "." + tref.Name, tref);
                }
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
                if (string.IsNullOrEmpty(existing.Database) && !string.IsNullOrEmpty(tref.Database))
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
                    else
                    {
                        tref.Database = sobj.Identifiers[0].Value;
                        tref.Schema = sobj.Identifiers[1].Value;
                        tref.Name = sobj.Identifiers[count - 1].Value;
                    }
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

            #region 上下文判定

            private sealed class FromObjectNameContext
            {
                public bool InFromClause;
                public List<string> Segments = new List<string>();
                public string Partial = string.Empty;
                public bool AfterDot;
                public bool UsesDoubleDot;
                public int PartialStartOffset = -1;
            }

            /// <summary>
            /// 原文解析 EXEC/EXECUTE 后的目标名：caiwu..pro / caiwu.dbo.pro / pro
            /// 不依赖 ScriptDom 对 .. 的分词。
            /// </summary>
            private static bool TryParseExecObjectName(string text, int caretOffset, out FromObjectNameContext ctx)
            {
                ctx = new FromObjectNameContext();
                if (string.IsNullOrEmpty(text) || caretOffset <= 0) return false;

                int i = Math.Min(caretOffset, text.Length) - 1;
                // 正在输入的过程名前缀
                int wordEnd = i + 1;
                while (i >= 0 && IsIdentChar(text[i])) i--;
                int partialStart = i + 1;
                if (partialStart < wordEnd)
                {
                    ctx.Partial = text.Substring(partialStart, wordEnd - partialStart);
                    ctx.PartialStartOffset = partialStart;
                }

                var segments = new List<string>();
                bool sawDot = false;
                bool doubleDot = false;
                while (i >= 0)
                {
                    while (i >= 0 && (text[i] == ' ' || text[i] == '\t')) i--;
                    if (i < 0) break;
                    if (text[i] == '\n' || text[i] == '\r' || text[i] == ';') break;

                    if (text[i] == '.')
                    {
                        sawDot = true;
                        int dots = 0;
                        while (i >= 0 && text[i] == '.')
                        {
                            dots++;
                            i--;
                        }
                        if (dots >= 2) doubleDot = true;
                        continue;
                    }

                    if (!IsIdentChar(text[i]) && text[i] != '[' && text[i] != ']')
                        break;

                    int segEnd = i + 1;
                    if (text[i] == ']')
                    {
                        while (i >= 0 && text[i] != '[') i--;
                        if (i >= 0) i--;
                    }
                    else
                    {
                        while (i >= 0 && IsIdentChar(text[i])) i--;
                    }
                    int segStart = i + 1;
                    if (segStart < segEnd)
                    {
                        string seg = UnbracketIdentifier(text.Substring(segStart, segEnd - segStart));
                        // EXEC 是关键字不是对象段；若当段吃掉，后面 i<0 会误判失败
                        if (string.Equals(seg, "EXEC", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(seg, "EXECUTE", StringComparison.OrdinalIgnoreCase))
                        {
                            i = segEnd - 1;
                            break;
                        }
                        if (!string.IsNullOrEmpty(seg))
                            segments.Insert(0, seg);
                    }
                }

                while (i >= 0 && (text[i] == ' ' || text[i] == '\t')) i--;
                if (i < 0) return false;
                int kwEnd = i + 1;
                while (i >= 0 && IsIdentChar(text[i])) i--;
                string kw = text.Substring(i + 1, kwEnd - (i + 1));
                if (!string.Equals(kw, "EXEC", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(kw, "EXECUTE", StringComparison.OrdinalIgnoreCase))
                    return false;

                // EXEC 与目标之间若没有任何段/点/前缀，也算命中（刚输入 EXEC ）
                ctx.Segments = segments;
                ctx.AfterDot = sawDot;
                ctx.UsesDoubleDot = doubleDot;
                return true;
            }

            private bool TryGetMemberAccessPrefix(List<TSqlParserToken> tokens, int localOffset, out string prefix)
            {
                prefix = null;
                int dotIdx = -1;
                for (int i = tokens.Count - 1; i >= 0; i--)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= localOffset) continue;
                    if (t.TokenType == TSqlTokenType.Dot)
                    {
                        dotIdx = i;
                        break;
                    }
                }
                if (dotIdx < 0) return false;

                var ownerTok = PreviousSignificantToken(tokens, dotIdx);
                if (ownerTok == null || !IsWordLikeToken(ownerTok)) return false;

                int dotEnd = tokens[dotIdx].Offset + tokens[dotIdx].Text.Length;
                // 点号与光标之间只能有「正在输入的那一个标识符」，不能跨行/跨关键字
                // 否则 JOIN 行的 f.col 会把下一行的 in/s 误判成成员访问
                TSqlParserToken colTok = null;
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset < dotEnd) continue;
                    if (t.Offset >= localOffset) break;
                    // 分号在光标前才算打断；光标在 d.| 而 ; 在后面时不影响
                    if (t.Text == ";" || string.Equals(t.Text, "GO", StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (t.TokenType == TSqlTokenType.Dot) return false;
                    if (!IsWordLikeToken(t) && !IsPartialObjectNameToken(t))
                        return false;
                    if (colTok != null)
                        return false; // 点后已有完整标识，后面又有别的词 → 不是 a.b|
                    colTok = t;
                }
                string colPart;
                if (colTok == null)
                {
                    colPart = string.Empty;
                }
                else if (colTok.Offset + colTok.Text.Length <= localOffset)
                {
                    // 光标在该标识符之后：仅当紧贴词尾（无其它内容）才算 a.col|
                    colPart = colTok.Text;
                }
                else
                {
                    colPart = colTok.Text.Substring(0, Math.Max(0, localOffset - colTok.Offset));
                }
                // 再用原文确认点号与光标间无换行（token 空白可能合并多行）
                if (HasNewlineBetween(tokens, dotEnd, localOffset))
                    return false;

                prefix = UnbracketIdentifier(ownerTok.Text) + "." + colPart;
                return true;
            }

            private static bool HasNewlineBetween(List<TSqlParserToken> tokens, int startOffset, int endOffset)
            {
                if (tokens == null) return false;
                foreach (var t in tokens)
                {
                    if (t == null || t.Text == null) continue;
                    if (t.Offset + t.Text.Length <= startOffset) continue;
                    if (t.Offset >= endOffset) break;
                    if (t.TokenType == TSqlTokenType.WhiteSpace &&
                        (t.Text.IndexOf('\n') >= 0 || t.Text.IndexOf('\r') >= 0))
                        return true;
                }
                return false;
            }

            private static string ExtractPartialWordAfter(List<TSqlParserToken> tokens, int startOffset, int endOffset)
            {
                foreach (var t in tokens)
                {
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset < startOffset) continue;
                    if (t.Offset >= endOffset) break;
                    if (!IsWordLikeToken(t)) continue;
                    if (t.Offset + t.Text.Length <= endOffset)
                        return t.Text;
                    return t.Text.Substring(0, Math.Max(0, endOffset - t.Offset));
                }
                return string.Empty;
            }

            private TableColumnInfo ResolveTableRef(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                MetadataCatalog catalog,
                TableRef tref)
            {
                if (tref == null || string.IsNullOrEmpty(tref.Name)) return null;
                MetadataCatalog target = catalog;
                if (connInfo != null && !string.IsNullOrWhiteSpace(tref.Database))
                {
                    if (catalog == null || !string.Equals(catalog.Database, tref.Database, StringComparison.OrdinalIgnoreCase))
                    {
                        target = GetCatalogNonBlocking(connInfo, tref.Database);
                    }
                }
                if (target != null)
                {
                    var found = target.FindTableOrView(tref.Schema, tref.Name)
                                ?? target.FindTableOrView(null, tref.Name);
                    if (found != null) return found;
                }
                // 目标库缓存未就绪时，勿直接失败：按表名在当前库再试一次（同名表）
                if (catalog != null && !ReferenceEquals(catalog, target))
                {
                    var fallback = catalog.FindTableOrView(tref.Schema, tref.Name)
                                   ?? catalog.FindTableOrView(null, tref.Name);
                    if (fallback != null) return fallback;
                }
                return null;
            }

            /// <summary>补全用：只读缓存，未命中则后台构建，避免 UI 同步连库卡死。</summary>
            private static MetadataCatalog GetCatalogNonBlocking(
                ScriptFactoryAccess.ConnectionInfo connInfo, string database = null)
            {
                var cached = MetadataCatalogService.Instance.GetCachedCatalog(connInfo, database);
                if (cached != null) return cached;
                MetadataCatalogService.Instance.EnsureCatalogBuilding(connInfo, database);
                return null;
            }

            private CompletionContext GetContext(List<TSqlParserToken> tokens, int localOffset, FromObjectNameContext fromName, out string prefix)
            {
                prefix = ExtractPrefix(tokens, localOffset);

                if (fromName != null && fromName.InFromClause)
                {
                    prefix = fromName.Partial ?? string.Empty;
                    return CompletionContext.FromClause;
                }

                if (TryGetMemberAccessPrefix(tokens, localOffset, out string memberPrefix))
                {
                    prefix = memberPrefix;
                    return CompletionContext.MemberAccess;
                }

                // 等号/比较符右侧尚未输入内容：不提示（等值待填字面量）
                if (string.IsNullOrEmpty(prefix) && IsImmediatelyAfterComparisonOperator(tokens, localOffset))
                    return CompletionContext.Unknown;

                // 已闭合的 ) ] 之后空前缀：不提示（SELECT NEWID()| 按 F5 后勿再弹）
                // 注意：函数实参内仍要提示列（ISNULL(h|, )），插入后防重弹靠 KeyHandler 短时抑制
                if (string.IsNullOrEmpty(prefix) && IsImmediatelyAfterClosedGroup(tokens, localOffset))
                    return CompletionContext.Unknown;

                // 找光标前最近的非空白 token（FindTokenIndexBefore 已跳过空白）
                int caretTokenIndex = FindTokenIndexBefore(tokens, localOffset);
                if (caretTokenIndex < 0)
                {
                    return CompletionContext.BatchStart;
                }

                var prev = tokens[caretTokenIndex];

                // @ 触发本地变量
                if (prev != null && prev.TokenType == TSqlTokenType.Variable)
                {
                    return CompletionContext.LocalVariable;
                }

                // prev 是正在输入的前缀时，再往前取关键字（EXEC ji → EXEC；EXEC␠ → EXEC）
                var keywordToken = prev;
                if (!IsContextKeywordToken(prev) && (IsWordLikeToken(prev) || IsPartialObjectNameToken(prev)))
                {
                    var before = PreviousSignificantToken(tokens, caretTokenIndex);
                    if (before != null)
                        keywordToken = before;
                }

                string kw = keywordToken?.Text?.ToUpperInvariant();

                switch (kw)
                {
                    case ";":
                    case "GO":
                        return CompletionContext.BatchStart;
                    case "SELECT":
                        // SELECT 后若已有 FROM，则可能在列区；简化：SELECT 后未到 FROM 视为 SelectElements
                        if (!SeenKeywordAfter(tokens, caretTokenIndex, "FROM"))
                        {
                            return CompletionContext.SelectElements;
                        }
                        return CompletionContext.WhereClause; // 简化
                    case "FROM":
                    case "JOIN":
                    case "APPLY":
                    case "INNER":
                    case "LEFT":
                    case "RIGHT":
                    case "FULL":
                    case "CROSS":
                    case "OUTER":
                        return CompletionContext.FromClause;
                    case ",":
                        {
                            var commaCtx = TryGetSelectBeforeFromContext(tokens, localOffset);
                            if (commaCtx == CompletionContext.SelectElements)
                                return CompletionContext.SelectElements;
                            return CompletionContext.FromClause;
                        }
                    case "WHERE":
                    case "ON":
                    case "HAVING":
                    case "AND":
                    case "OR":
                        return CompletionContext.WhereClause;
                    case "ORDER":
                    case "GROUP":
                        return CompletionContext.OrderByGroupBy;
                    case "EXEC":
                    case "EXECUTE":
                        return CompletionContext.AfterExec;
                    case "INSERT":
                        return CompletionContext.InsertTarget;
                    case "UPDATE":
                        return CompletionContext.UpdateTarget;
                    case "DELETE":
                        return CompletionContext.DeleteTarget;
                    case "USE":
                        return CompletionContext.AfterUse;
                    case "SET":
                        return CompletionContext.UpdateSet;
                    case "CREATE":
                        return CompletionContext.AfterCreate;
                    case "ALTER":
                        return CompletionContext.AfterAlter;
                }

                // 批次起始（仅当光标前无有效 token）
                var selectCtx = TryGetSelectBeforeFromContext(tokens, localOffset);
                if (selectCtx.HasValue)
                {
                    return selectCtx.Value;
                }

                if (IsAtBatchStart(tokens, localOffset))
                {
                    return CompletionContext.BatchStart;
                }

                if (IsAtLineStartTypingKeyword(tokens, localOffset, caretTokenIndex))
                {
                    return CompletionContext.BatchStart;
                }

                if (IsTypingStatementKeywordAtCaret(tokens, localOffset, caretTokenIndex))
                {
                    return CompletionContext.BatchStart;
                }

                return CompletionContext.Unknown;
            }

            /// <summary>从原文取当前正在输入的前缀，并判断是否在分号/GO 后、是否行首。</summary>
            private static void TryGetRawTypingPrefix(
                string text, int caretOffset,
                out string prefix, out bool afterStmtBreak, out bool atLineStart)
            {
                prefix = string.Empty;
                afterStmtBreak = false;
                atLineStart = false;
                if (string.IsNullOrEmpty(text) || caretOffset <= 0)
                {
                    afterStmtBreak = true;
                    atLineStart = true;
                    return;
                }
                int i = Math.Min(caretOffset, text.Length) - 1;
                int wordEnd = i + 1;
                while (i >= 0 && IsIdentChar(text[i]))
                    i--;
                int wordStart = i + 1;
                if (wordStart < wordEnd)
                    prefix = text.Substring(wordStart, wordEnd - wordStart);

                int j = i;
                while (j >= 0 && (text[j] == ' ' || text[j] == '\t'))
                    j--;
                atLineStart = j < 0 || text[j] == '\n' || text[j] == '\r';

                while (j >= 0 && char.IsWhiteSpace(text[j]))
                    j--;
                if (j < 0)
                {
                    afterStmtBreak = true;
                    return;
                }
                if (text[j] == ';')
                {
                    afterStmtBreak = true;
                    return;
                }
                // GO 批分隔
                if ((text[j] == 'O' || text[j] == 'o') && j >= 1)
                {
                    char g = text[j - 1];
                    if ((g == 'G' || g == 'g') &&
                        (j == 1 || char.IsWhiteSpace(text[j - 2]) || text[j - 2] == ';'))
                    {
                        afterStmtBreak = true;
                    }
                }
            }

            private static bool IsIdentChar(char c)
            {
                return char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#';
            }

            private static bool HasFromKeywordBefore(List<TSqlParserToken> tokens, int localOffset)
            {
                if (tokens == null) return false;
                for (int i = tokens.Count - 1; i >= 0; i--)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= localOffset) continue;
                    string text = t.Text?.ToUpperInvariant();
                    if (text == ";" || text == "GO") return false;
                    if (text == "FROM") return true;
                    if (text == "SELECT" || text == "INSERT" || text == "UPDATE" || text == "DELETE"
                        || text == "MERGE" || text == "CREATE" || text == "ALTER")
                        return false;
                }
                return false;
            }

            /// <summary>新行/分号后行首正在输入语句关键字前缀（如 s → SELECT）。</summary>
            private bool IsAtLineStartTypingKeyword(List<TSqlParserToken> tokens, int localOffset, int caretTokenIndex)
            {
                if (caretTokenIndex < 0) return false;
                var cur = tokens[caretTokenIndex];
                if (!IsWordLikeToken(cur)) return false;
                if (cur.Offset + cur.Text.Length < localOffset) return false;

                int lineStartOffset = GetLineStartOffset(tokens, cur.Offset);

                var prev = PreviousSignificantToken(tokens, caretTokenIndex);
                if (prev != null && string.Equals(prev.Text, ";", StringComparison.Ordinal))
                {
                    return true;
                }

                for (int i = 0; i < caretTokenIndex; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset >= lineStartOffset && t.Offset < cur.Offset)
                    {
                        return false;
                    }
                }
                return true;
            }

            /// <summary>行首/语句起始处正在输入关键字前缀（如 s → SELECT）。</summary>
            private bool IsTypingStatementKeywordAtCaret(List<TSqlParserToken> tokens, int localOffset, int caretTokenIndex)
            {
                if (caretTokenIndex < 0) return false;
                var cur = tokens[caretTokenIndex];
                if (!IsWordLikeToken(cur)) return false;
                if (cur.Offset + cur.Text.Length < localOffset) return false;

                for (int i = 0; i < caretTokenIndex; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    string text = t.Text?.ToUpperInvariant();
                    if (text == ";" || text == "GO") continue;
                    return false;
                }
                return true;
            }

            /// <summary>光标前是否只有空白/注释（批次起始）。</summary>
            private bool IsAtBatchStart(List<TSqlParserToken> tokens, int localOffset)
            {
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null) continue;
                    if (t.Offset >= localOffset) break;
                    if (!IsInsignificantToken(t)) return false;
                }
                return true;
            }

            private static bool IsInsignificantToken(TSqlParserToken t)
            {
                if (t == null) return true;
                var type = t.TokenType;
                return type == TSqlTokenType.WhiteSpace ||
                       type == TSqlTokenType.MultilineComment ||
                       type == TSqlTokenType.SingleLineComment ||
                       type == TSqlTokenType.EndOfFile;
            }

            /// <summary>光标是否在 SELECT 与 FROM 之间（含 SELECT * 后等待 FROM）。</summary>
            private CompletionContext? TryGetSelectBeforeFromContext(List<TSqlParserToken> tokens, int localOffset)
            {
                int startIdx = FindTokenIndexBefore(tokens, localOffset);
                if (startIdx < 0) return null;

                int parenDepth = 0;
                bool foundFrom = false;
                for (int i = startIdx; i >= 0; i--)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;

                    if (t.TokenType == TSqlTokenType.RightParenthesis)
                    {
                        parenDepth++;
                        continue;
                    }
                    if (t.TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        parenDepth--;
                        if (parenDepth < 0) parenDepth = 0;
                        continue;
                    }
                    if (parenDepth > 0) continue;

                    string text = t.Text?.ToUpperInvariant();
                    if (text == "FROM") foundFrom = true;

                    if (text == "SELECT")
                    {
                        if (!foundFrom)
                        {
                            return CompletionContext.SelectElements;
                        }
                        return null;
                    }

                    if (text == "DISTINCT" || text == "TOP" || text == "ALL" || text == "PERCENT")
                    {
                        continue;
                    }

                    if (text == "INSERT" || text == "UPDATE" || text == "DELETE" ||
                        text == "WITH" || text == "CREATE" || text == "ALTER" ||
                        text == "WHERE" || text == "GROUP" || text == "ORDER" ||
                        text == "HAVING" || text == "UNION" || text == "EXCEPT" ||
                        text == "INTERSECT" || text == ";")
                    {
                        return null;
                    }
                }
                return null;
            }

            private static int GetLineStartOffset(List<TSqlParserToken> tokens, int offset)
            {
                int lineStart = 0;
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null) continue;
                    if (t.Offset >= offset) break;
                    if (t.TokenType == TSqlTokenType.WhiteSpace && t.Text != null && t.Text.IndexOf('\n') >= 0)
                    {
                        lineStart = t.Offset + t.Text.Length;
                    }
                }
                return lineStart;
            }

            private FromObjectNameContext ParseFromObjectName(List<TSqlParserToken> tokens, int localOffset)
            {
                var ctx = new FromObjectNameContext();
                int idx = FindTokenIndexBefore(tokens, localOffset);
                if (idx < 0) return ctx;

                int lineStartOffset = GetLineStartOffset(tokens, localOffset);

                var parts = new List<string>();
                string partial = string.Empty;
                bool afterDot = false;
                int i = idx;

                var cur = tokens[i];
                if (cur != null && cur.TokenType == TSqlTokenType.Dot)
                {
                    afterDot = true;
                    i--;
                }
                else if (cur != null && IsPartialObjectNameToken(cur) && cur.Offset + cur.Text.Length >= localOffset)
                {
                    partial = cur.Text.Substring(0, Math.Max(0, localOffset - cur.Offset));
                    ctx.PartialStartOffset = cur.Offset;
                    i--;
                }
                else if (cur != null && IsPartialObjectNameToken(cur))
                {
                    parts.Insert(0, UnbracketIdentifier(cur.Text));
                    i--;
                }

                while (i >= 0)
                {
                    while (i >= 0 && IsInsignificantToken(tokens[i])) i--;
                    if (i < 0) break;
                    if (tokens[i].Offset < lineStartOffset) break;

                    if (tokens[i].TokenType == TSqlTokenType.Dot)
                    {
                        i--;
                        while (i >= 0 && IsInsignificantToken(tokens[i])) i--;
                        if (i >= 0 && tokens[i].TokenType == TSqlTokenType.Dot)
                        {
                            ctx.UsesDoubleDot = true;
                            i--;
                            while (i >= 0 && IsInsignificantToken(tokens[i])) i--;
                        }
                        if (i >= 0 && IsPartialObjectNameToken(tokens[i]))
                        {
                            parts.Insert(0, UnbracketIdentifier(tokens[i].Text));
                            i--;
                        }
                        afterDot = true;
                        continue;
                    }

                    if (IsPartialObjectNameToken(tokens[i]))
                    {
                        parts.Insert(0, UnbracketIdentifier(tokens[i].Text));
                        i--;
                        continue;
                    }

                    string text = tokens[i].Text?.ToUpperInvariant();
                    if (text == ";" || text == "GO")
                    {
                        break;
                    }
                    if (text == "FROM" || text == "JOIN" || text == "APPLY" || text == ",")
                    {
                        ctx.InFromClause = true;
                        break;
                    }
                    if (text == "SELECT" || text == "WHERE" || text == "SET" || text == "ON" ||
                        text == "INTO" || text == "UPDATE" || text == "DELETE" || text == "INSERT" ||
                        text == "EXEC" || text == "EXECUTE" || text == "USE" || text == "CREATE" || text == "ALTER")
                    {
                        break;
                    }
                    break;
                }

                ctx.Segments = parts;
                ctx.Partial = partial;
                ctx.AfterDot = afterDot;
                return ctx;
            }

            private int ComputeReplaceStartOffset(int batchStart, int localOffset, string prefix, FromObjectNameContext fromName, List<TSqlParserToken> tokens)
            {
                // FROM / EXEC 多段名：只替换点号后的部分
                if (fromName != null && fromName.AfterDot)
                {
                    if (fromName.PartialStartOffset >= 0)
                        return batchStart + fromName.PartialStartOffset;
                    if (string.IsNullOrEmpty(fromName.Partial))
                        return batchStart + localOffset;
                }
                if (fromName != null && fromName.InFromClause && fromName.PartialStartOffset >= 0)
                    return batchStart + fromName.PartialStartOffset;

                if (!string.IsNullOrEmpty(prefix))
                {
                    int idx = FindTokenIndexForPrefix(tokens, localOffset);
                    if (idx < 0)
                        idx = FindTokenIndexBefore(tokens, localOffset);
                    if (idx >= 0)
                    {
                        var t = tokens[idx];
                        // 含 IN 等关键字 token，否则 in + INNER JOIN 会变成 inINNER JOIN
                        if (t != null && IsWordLikeToken(t) && t.Offset + t.Text.Length >= localOffset
                            && t.Offset <= localOffset)
                            return batchStart + t.Offset;
                    }
                    // a. / a.col：InsertText 只有列名，替换起点只能是点号后（勿吞掉 a.）
                    int lastDot = prefix.LastIndexOf('.');
                    if (lastDot >= 0)
                    {
                        int afterLen = prefix.Length - lastDot - 1;
                        return batchStart + localOffset - afterLen;
                    }
                    if (localOffset >= prefix.Length)
                        return batchStart + localOffset - prefix.Length;
                }
                return batchStart + localOffset;
            }

            private static string BracketIdent(string name)
            {
                if (string.IsNullOrEmpty(name)) return name;
                if (name.StartsWith("[")) return name;
                return "[" + name + "]";
            }

            private static string FormatIdentifier(string name, IntelliSenseSettings settings)
            {
                if (string.IsNullOrEmpty(name)) return name;
                string bare = UnbracketIdentifier(name);
                if (settings != null && !settings.bracketIdentifiers)
                    return bare;
                return BracketIdent(bare);
            }

            private static string FormatObjectInsert(DatabaseObjectInfo obj, IntelliSenseSettings settings)
            {
                if (obj == null) return string.Empty;
                if (settings != null && !settings.bracketIdentifiers)
                    return obj.QualifiedName;
                return obj.BracketedName;
            }

            private static string FormatColumnInsert(string colName, IntelliSenseSettings settings)
            {
                return FormatIdentifier(colName, settings);
            }

            private string BuildTableInsertText(FromObjectNameContext fromName, DatabaseObjectInfo obj, ScriptFactoryAccess.ConnectionInfo connInfo, IntelliSenseSettings settings, TableInsertMode? mode = null)
            {
                string inner = FormatObjectInsert(obj, settings);
                string tableOnly = FormatIdentifier(obj.Name, settings);
                if (fromName == null || !fromName.InFromClause || obj == null)
                    return inner;
                var insertMode = mode ?? ResolveTableInsertMode(fromName, connInfo);
                switch (insertMode)
                {
                    case TableInsertMode.TableOnly:
                        return tableOnly;
                    case TableInsertMode.SchemaAndTable:
                        return inner;
                    case TableInsertMode.DbDoubleDotTable:
                        return FormatIdentifier(fromName.Segments[0], settings) + ".." + tableOnly;
                    case TableInsertMode.DbDotSchemaTable:
                        return FormatIdentifier(fromName.Segments[0], settings) + "."
                            + FormatIdentifier(fromName.Segments[1], settings) + "." + tableOnly;
                    case TableInsertMode.DbDotSchemaAndTable:
                        return FormatIdentifier(fromName.Segments[0], settings) + "." + inner;
                    case TableInsertMode.SchemaDotTable:
                        return FormatIdentifier(fromName.Segments[0], settings) + "." + tableOnly;
                    default:
                        return inner;
                }
            }

            private static string UnbracketIdentifier(string text)
            {
                if (string.IsNullOrEmpty(text)) return string.Empty;
                return text.Trim('[', ']', '"');
            }

            private bool IsDatabaseName(ScriptFactoryAccess.ConnectionInfo connInfo, string name)
            {
                if (connInfo == null || string.IsNullOrEmpty(name)) return false;
                if (IsLinkedServerName(connInfo, name)) return false;
                try
                {
                    return MetadataCatalogService.Instance.ContainsDatabase(connInfo, name);
                }
                catch
                {
                    return false;
                }
            }

            private bool IsLinkedServerName(ScriptFactoryAccess.ConnectionInfo connInfo, string name)
            {
                return MetadataCatalogService.Instance.IsLinkedServerName(connInfo, name);
            }

            private bool IsLinkedServerDatabase(ScriptFactoryAccess.ConnectionInfo connInfo, string linkedServer, string database)
            {
                return MetadataCatalogService.Instance.IsLinkedServerDatabase(connInfo, linkedServer, database);
            }

            /// <summary>光标紧跟在 = / &lt;&gt; / != 等比较运算符之后（右侧尚无标识符）。</summary>
            private bool IsImmediatelyAfterComparisonOperator(List<TSqlParserToken> tokens, int localOffset)
            {
                int idx = FindTokenIndexBefore(tokens, localOffset);
                if (idx < 0) return false;
                var t = tokens[idx];
                if (t == null || string.IsNullOrEmpty(t.Text)) return false;
                // 已在输入词则不算「等号后空」
                if (IsWordLikeToken(t) || IsIdentifierLike(t) || IsPartialObjectNameToken(t))
                    return false;
                switch (t.Text)
                {
                    case "=":
                    case "<>":
                    case "!=":
                    case "<":
                    case ">":
                    case "<=":
                    case ">=":
                        return true;
                    default:
                        return false;
                }
            }

            /// <summary>光标紧跟在已闭合的 ) 或 ] 之后（右侧尚无新标识符）。</summary>
            private bool IsImmediatelyAfterClosedGroup(List<TSqlParserToken> tokens, int localOffset)
            {
                int idx = FindTokenIndexBefore(tokens, localOffset);
                if (idx < 0) return false;
                var t = tokens[idx];
                if (t == null || string.IsNullOrEmpty(t.Text)) return false;
                if (IsWordLikeToken(t) || IsIdentifierLike(t) || IsPartialObjectNameToken(t))
                    return false;
                return t.TokenType == TSqlTokenType.RightParenthesis
                    || t.Text == ")"
                    || t.Text == "]";
            }

            private string ExtractPrefix(List<TSqlParserToken> tokens, int localOffset)
            {
                int idx = FindTokenIndexForPrefix(tokens, localOffset);
                if (idx < 0) return string.Empty;
                var t = tokens[idx];
                if (t == null) return string.Empty;

                if (IsWordLikeToken(t) && t.Offset + t.Text.Length >= localOffset)
                {
                    return t.Text.Substring(0, Math.Max(0, localOffset - t.Offset));
                }
                return string.Empty;
            }

            /// <summary>取光标处正在输入的 token（含 IN/IF 等被解析为关键字的词）。</summary>
            private int FindTokenIndexForPrefix(List<TSqlParserToken> tokens, int localOffset)
            {
                for (int i = tokens.Count - 1; i >= 0; i--)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset <= localOffset && t.Offset + t.Text.Length >= localOffset)
                        return i;
                }
                return FindTokenIndexBefore(tokens, localOffset);
            }

            private int FindTokenIndexBefore(List<TSqlParserToken> tokens, int offset)
            {
                for (int i = tokens.Count - 1; i >= 0; i--)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.Offset < offset)
                    {
                        return i;
                    }
                }
                return -1;
            }

            private TSqlParserToken PreviousSignificantToken(List<TSqlParserToken> tokens, int fromIndex)
            {
                for (int i = fromIndex - 1; i >= 0; i--)
                {
                    var t = tokens[i];
                    if (t == null) continue;
                    var type = t.TokenType;
                    if (type == TSqlTokenType.WhiteSpace ||
                        type == TSqlTokenType.MultilineComment || type == TSqlTokenType.SingleLineComment)
                    {
                        continue;
                    }
                    return t;
                }
                return null;
            }

            private static bool IsIdentifierLike(TSqlParserToken t)
            {
                if (t == null) return false;
                return t.TokenType == TSqlTokenType.Identifier ||
                       t.TokenType == TSqlTokenType.QuotedIdentifier;
            }

            /// <summary>FROM 四段名/IP 中可能被解析为 Integer 的片段（如 192）。</summary>
            private static bool IsPartialObjectNameToken(TSqlParserToken t)
            {
                if (t == null) return false;
                if (IsIdentifierLike(t)) return true;
                return t.TokenType == TSqlTokenType.Integer || t.TokenType == TSqlTokenType.Real;
            }

            /// <summary>光标处正在输入的词（含 IN/IF 等被 ScriptDom 解析为关键字的 token）。</summary>
            private static bool IsWordLikeToken(TSqlParserToken t)
            {
                if (t == null || string.IsNullOrEmpty(t.Text) || IsInsignificantToken(t)) return false;
                if (IsPartialObjectNameToken(t)) return true;
                foreach (char c in t.Text)
                {
                    if (!char.IsLetter(c) && c != '_') return false;
                }
                return true;
            }

            /// <summary>可作为补全上下文锚点的关键字（不再当作「正在输入的前缀」往前跳）。</summary>
            private static bool IsContextKeywordToken(TSqlParserToken t)
            {
                if (t == null || string.IsNullOrEmpty(t.Text)) return false;
                switch (t.Text.ToUpperInvariant())
                {
                    case "SELECT":
                    case "FROM":
                    case "JOIN":
                    case "APPLY":
                    case "INNER":
                    case "LEFT":
                    case "RIGHT":
                    case "FULL":
                    case "CROSS":
                    case "OUTER":
                    case "WHERE":
                    case "ON":
                    case "HAVING":
                    case "AND":
                    case "OR":
                    case "ORDER":
                    case "GROUP":
                    case "EXEC":
                    case "EXECUTE":
                    case "INSERT":
                    case "UPDATE":
                    case "DELETE":
                    case "USE":
                    case "SET":
                    case "CREATE":
                    case "ALTER":
                    case "GO":
                        return true;
                    default:
                        return t.Text == ";";
                }
            }

            private static bool IsNumericOrIpPrefix(string prefix)
            {
                if (string.IsNullOrEmpty(prefix) || !char.IsDigit(prefix[0])) return false;
                for (int i = 0; i < prefix.Length; i++)
                {
                    char c = prefix[i];
                    if (!char.IsDigit(c) && c != '.') return false;
                }
                return true;
            }

            private bool SeenKeywordAfter(List<TSqlParserToken> tokens, int fromIndex, string keyword)
            {
                string kw = keyword.ToUpperInvariant();
                for (int i = 0; i <= fromIndex && i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t != null && !string.IsNullOrEmpty(t.Text) &&
                        string.Equals(t.Text, kw, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }

            private bool IsOnlyWhitespaceBefore(List<TSqlParserToken> tokens, int fromIndex)
            {
                for (int i = 0; i < fromIndex && i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null) continue;
                    var type = t.TokenType;
                    if (type == TSqlTokenType.WhiteSpace ||
                        type == TSqlTokenType.MultilineComment || type == TSqlTokenType.SingleLineComment ||
                        type == TSqlTokenType.EndOfFile)
                    {
                        continue;
                    }
                    return false;
                }
                return true;
            }

            #endregion

            #region 候选生成

            private List<CompletionItem> BuildItems(
                CompletionContext ctx,
                string prefix,
                FromObjectNameContext fromName,
                LocalSymbols local,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                var items = new List<CompletionItem>();

                switch (ctx)
                {
                    case CompletionContext.BatchStart:
                        AddSnippets(items, prefix);
                        if (settings.includeKeywords)
                        {
                            AddKeywords(items, TopLevelKeywords);
                            AddKeywords(items, AfterFromKeywords);
                        }
                        break;

                    case CompletionContext.AfterCreate:
                        if (settings.includeKeywords) AddKeywords(items, CreateObjectKeywords);
                        break;

                    case CompletionContext.AfterAlter:
                        if (settings.includeKeywords) AddKeywords(items, AlterObjectKeywords);
                        break;

                    case CompletionContext.FromClause:
                        AddFromClauseItems(items, fromName, catalog, settings, connInfo, prefix);
                        if (settings.includeKeywords && ShouldSuggestAfterFromKeywords(fromName, prefix))
                            AddKeywords(items, AfterFromKeywords);
                        break;

                    case CompletionContext.InsertTarget:
                    case CompletionContext.UpdateTarget:
                    case CompletionContext.DeleteTarget:
                        AddTablesAndViews(items, catalog, settings);
                        break;

                    case CompletionContext.SelectElements:
                        if (settings.includeKeywords)
                        {
                            AddKeywords(items, SelectClauseKeywords);
                        }
                        AddColumnsAndFunctions(items, catalog, local, settings, connInfo);
                        break;

                    case CompletionContext.WhereClause:
                    case CompletionContext.OrderByGroupBy:
                    case CompletionContext.UpdateSet:
                        AddColumnsFromFromAliases(items, catalog, local, settings, connInfo);
                        if (settings.includeKeywords && !string.IsNullOrEmpty(prefix))
                            AddKeywords(items, OperatorKeywords);
                        break;

                    case CompletionContext.AfterExec:
                        AddExecItems(items, fromName, prefix, catalog, settings, connInfo);
                        break;

                    case CompletionContext.MemberAccess:
                        AddMemberColumns(items, prefix, catalog, local, settings, connInfo);
                        break;

                    case CompletionContext.LocalMemberAccess:
                        AddLocalMemberColumnsFromPrefix(items, prefix, local, settings);
                        break;

                    case CompletionContext.LocalVariable:
                        AddLocalVariables(items, local, settings);
                        break;

                    case CompletionContext.AfterUse:
                        AddDatabases(items, connInfo, settings);
                        break;
                }

                return items;
            }

            private void AddFromClauseItems(
                List<CompletionItem> items,
                FromObjectNameContext fromName,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string namePrefix = null)
            {
                if (fromName == null || !fromName.InFromClause)
                {
                    AddDatabases(items, connInfo, settings);
                    AddLinkedServers(items, connInfo, settings);
                    AddTablesViewsAndRoutines(items, catalog, settings, includeTableFunctions: true, fromName: fromName, connInfo: connInfo, namePrefix: namePrefix);
                    return;
                }

                int segCount = fromName.Segments?.Count ?? 0;
                if (segCount > 0 && IsLinkedServerName(connInfo, fromName.Segments[0]))
                {
                    string linkedServer = fromName.Segments[0];
                    if (fromName.AfterDot)
                    {
                        if (segCount == 1)
                        {
                            AddLinkedServerDatabases(items, connInfo, linkedServer, fromName, settings);
                            return;
                        }
                        if (segCount == 2)
                        {
                            var remote = MetadataCatalogService.Instance.GetOrBuildLinkedCatalog(connInfo, linkedServer, fromName.Segments[1]);
                            AddTablesInSchema(items, remote, null, settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                            return;
                        }
                        if (segCount == 3)
                        {
                            var remote = MetadataCatalogService.Instance.GetOrBuildLinkedCatalog(connInfo, linkedServer, fromName.Segments[1]);
                            AddTablesInSchema(items, remote, fromName.Segments[2], settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                            return;
                        }
                    }
                    if (segCount == 1)
                    {
                        AddLinkedServerDatabases(items, connInfo, linkedServer, fromName, settings);
                        return;
                    }
                    if (segCount == 2 && IsLinkedServerDatabase(connInfo, linkedServer, fromName.Segments[1]))
                    {
                        var remote = MetadataCatalogService.Instance.GetOrBuildLinkedCatalog(connInfo, linkedServer, fromName.Segments[1]);
                        AddTablesInSchema(items, remote, null, settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                        return;
                    }
                }

                bool qualified = segCount > 0 || fromName.AfterDot;
                if (segCount == 0)
                {
                    if (IsNumericOrIpPrefix(fromName.Partial))
                    {
                        AddLinkedServers(items, connInfo, settings, fromName.Partial);
                        return;
                    }
                    // 数据库优先加入，避免被表列表占满 maxCompletionItems
                    AddDatabases(items, connInfo, settings);
                    AddLinkedServers(items, connInfo, settings);
                    AddTablesViewsAndRoutines(items, catalog, settings, includeTableFunctions: true, fromName: fromName, connInfo: connInfo, namePrefix: namePrefix);
                    return;
                }

                if (segCount == 1 && fromName.AfterDot && IsNumericOrIpPrefix(fromName.Segments[0])
                    && !IsLinkedServerName(connInfo, fromName.Segments[0]))
                {
                    AddLinkedServers(items, connInfo, settings, fromName.Segments[0]);
                    return;
                }

                if (fromName.AfterDot)
                {
                    if (segCount == 1 && fromName.UsesDoubleDot && IsDatabaseName(connInfo, fromName.Segments[0]))
                    {
                        var remote = GetCatalogNonBlocking(connInfo, fromName.Segments[0]);
                        AddTablesInSchema(items, remote, "dbo", settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                        return;
                    }
                    if (segCount == 1 && IsDatabaseName(connInfo, fromName.Segments[0]))
                    {
                        var remote = GetCatalogNonBlocking(connInfo, fromName.Segments[0]);
                        AddTablesInSchema(items, remote, null, settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                        return;
                    }
                    if (segCount == 1)
                    {
                        AddTablesInSchema(items, catalog, fromName.Segments[0], settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                        return;
                    }
                    if (segCount == 2 && IsDatabaseName(connInfo, fromName.Segments[0]))
                    {
                        var remote = GetCatalogNonBlocking(connInfo, fromName.Segments[0]);
                        AddTablesInSchema(items, remote, fromName.Segments[1], settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                        return;
                    }
                    if (segCount == 2)
                    {
                        AddTablesInSchema(items, catalog, fromName.Segments[0], settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                        return;
                    }
                }

                if (segCount == 1 && fromName.UsesDoubleDot && IsDatabaseName(connInfo, fromName.Segments[0]))
                {
                    var remote = GetCatalogNonBlocking(connInfo, fromName.Segments[0]);
                    AddTablesInSchema(items, remote, "dbo", settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                    return;
                }

                if (segCount == 1 && IsDatabaseName(connInfo, fromName.Segments[0]))
                {
                    var remote = GetCatalogNonBlocking(connInfo, fromName.Segments[0]);
                    AddTablesInSchema(items, remote, null, settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                    return;
                }

                if (segCount == 2 && IsDatabaseName(connInfo, fromName.Segments[0]))
                {
                    var remote = GetCatalogNonBlocking(connInfo, fromName.Segments[0]);
                    AddTablesInSchema(items, remote, fromName.Segments[1], settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                    return;
                }

                if (segCount == 1)
                {
                    AddTablesInSchema(items, catalog, fromName.Segments[0], settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                    return;
                }

                AddTablesViewsAndRoutines(items, catalog, settings, includeTableFunctions: true, fromName: fromName, connInfo: connInfo, namePrefix: namePrefix);
                if (!qualified)
                    AddDatabases(items, connInfo, settings);
            }

            private void AddSchemasForDatabase(List<CompletionItem> items, ScriptFactoryAccess.ConnectionInfo connInfo, string database)
            {
                var cat = GetCatalogNonBlocking(connInfo, database);
                if (cat == null) return;
                var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in cat.Tables)
                {
                    if (!string.IsNullOrEmpty(t.Schema)) schemas.Add(t.Schema);
                }
                foreach (var v in cat.Views)
                {
                    if (!string.IsNullOrEmpty(v.Schema)) schemas.Add(v.Schema);
                }
                foreach (var s in schemas.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(new CompletionItem(s, "[" + s + "]", CompletionKind.Schema, "架构 (" + database + ")"));
                }
            }

            private void AddTablesInSchema(List<CompletionItem> items, MetadataCatalog catalog, string schema, IntelliSenseSettings settings, FromObjectNameContext fromName, ScriptFactoryAccess.ConnectionInfo connInfo, bool includeSystemObjects, string namePrefix = null)
            {
                if (catalog == null) return;
                bool tableNameOnly = fromName != null && fromName.InFromClause && fromName.UsesDoubleDot;
                string filter = GetLastSegment(namePrefix ?? fromName?.Partial);
                int cap = Math.Max(50, settings.maxCompletionItems * 3);
                // 插入文本策略只算一次，避免每张表都 IsDatabaseName/连库
                var insertMode = ResolveTableInsertMode(fromName, connInfo);
                int added = 0;
                foreach (var t in catalog.Tables)
                {
                    if (!string.IsNullOrEmpty(schema) && !string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!ObjectNameMatchesFilter(t.Name, filter))
                        continue;
                    string insert = BuildTableInsertText(fromName, t, connInfo, settings, insertMode);
                    string display = tableNameOnly ? t.Name : t.QualifiedName;
                    items.Add(new CompletionItem(display, insert, CompletionKind.Table, BuildTableDescription(t)));
                    if (++added >= cap) return;
                }
                foreach (var v in catalog.Views)
                {
                    if (!string.IsNullOrEmpty(schema) && !string.Equals(v.Schema, schema, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!ObjectNameMatchesFilter(v.Name, filter))
                        continue;
                    string insert = BuildTableInsertText(fromName, v, connInfo, settings, insertMode);
                    string display = tableNameOnly ? v.Name : v.QualifiedName;
                    items.Add(new CompletionItem(display, insert, CompletionKind.View, BuildTableDescription(v)));
                    if (++added >= cap) return;
                }
                if (includeSystemObjects && settings.includeSystemObjects && string.IsNullOrEmpty(schema)
                    && string.IsNullOrEmpty(filter))
                {
                    AddSystemObjects(items);
                }
            }

            private static bool ObjectNameMatchesFilter(string objectName, string filter)
            {
                if (string.IsNullOrEmpty(filter)) return true;
                if (string.IsNullOrEmpty(objectName)) return false;
                if (objectName.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) return true;
                if (objectName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                string collapsedName = CollapseForMatch(objectName);
                string collapsedFilter = CollapseForMatch(filter);
                return !string.IsNullOrEmpty(collapsedFilter)
                    && collapsedName.IndexOf(collapsedFilter, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private enum TableInsertMode
            {
                FullQualified,
                TableOnly,
                SchemaAndTable,
                DbDoubleDotTable,
                DbDotSchemaTable,
                DbDotSchemaAndTable,
                SchemaDotTable
            }

            private TableInsertMode ResolveTableInsertMode(FromObjectNameContext fromName, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (fromName == null || !fromName.InFromClause)
                    return TableInsertMode.FullQualified;
                int segCount = fromName.Segments?.Count ?? 0;
                bool linkedServerContext = segCount > 0 && connInfo != null && IsLinkedServerName(connInfo, fromName.Segments[0]);
                if (fromName.AfterDot || fromName.PartialStartOffset >= 0)
                {
                    if (linkedServerContext)
                    {
                        if (segCount == 1) return TableInsertMode.TableOnly;
                        if (fromName.UsesDoubleDot || segCount >= 2) return TableInsertMode.TableOnly;
                    }
                    if (fromName.UsesDoubleDot) return TableInsertMode.TableOnly;
                    if (segCount >= 2) return TableInsertMode.TableOnly;
                    if (segCount == 1)
                    {
                        if (IsDatabaseName(connInfo, fromName.Segments[0]))
                            return TableInsertMode.SchemaAndTable;
                        return TableInsertMode.TableOnly;
                    }
                    return TableInsertMode.FullQualified;
                }
                if (segCount == 0) return TableInsertMode.FullQualified;
                string dbOrSchema = fromName.Segments[0];
                bool isDb = IsDatabaseName(connInfo, dbOrSchema);
                if (isDb && fromName.UsesDoubleDot) return TableInsertMode.DbDoubleDotTable;
                if (isDb && segCount >= 2) return TableInsertMode.DbDotSchemaTable;
                if (isDb) return TableInsertMode.DbDotSchemaAndTable;
                if (segCount == 1) return TableInsertMode.SchemaDotTable;
                return TableInsertMode.FullQualified;
            }

            private void AddSnippets(List<CompletionItem> items, string prefix)
            {
                if (!SettingsManager.GetUseSnippets()) return;

                var snippetSettings = SettingsManager.GetSnippetSettings();
                string marker = snippetSettings.cursorMarker ?? "#";
                foreach (var snippet in SnippetService.GetAllSnippets())
                {
                    if (string.IsNullOrEmpty(snippet.Prefix)) continue;
                    var processed = SnippetVariableProcessor.ProcessVariables(snippet.Body, marker);
                    string desc = string.IsNullOrEmpty(snippet.Description)
                        ? "代码片段"
                        : "片段: " + snippet.Description;
                    var item = new CompletionItem(snippet.Prefix, processed.ProcessedText, CompletionKind.Snippet, desc)
                    {
                        SnippetCursorOffset = processed.CursorOffset,
                        SnippetPrefix = snippet.Prefix
                    };
                    items.Add(item);
                }
            }

            private void AddKeywords(List<CompletionItem> items, IEnumerable<string> keywords)
            {
                foreach (var kw in keywords)
                {
                    items.Add(new CompletionItem(kw, kw, CompletionKind.Keyword, "关键字"));
                }
            }

            private void AddTablesViewsAndRoutines(List<CompletionItem> items, MetadataCatalog catalog, IntelliSenseSettings settings, bool includeTableFunctions, FromObjectNameContext fromName = null, ScriptFactoryAccess.ConnectionInfo connInfo = null, string namePrefix = null)
            {
                AddTablesAndViews(items, catalog, settings, fromName, connInfo, namePrefix);
                if (catalog == null) return;
                string filter = GetLastSegment(namePrefix ?? fromName?.Partial);
                var insertMode = ResolveTableInsertMode(fromName, connInfo);
                foreach (var s in catalog.Synonyms)
                {
                    if (!ObjectNameMatchesFilter(s.Name, filter)) continue;
                    items.Add(new CompletionItem(s.QualifiedName, FormatObjectInsert(s, settings), CompletionKind.Synonym, "同义词"));
                }
                if (includeTableFunctions)
                {
                    foreach (var f in catalog.TableFunctions)
                    {
                        if (!ObjectNameMatchesFilter(f.Name, filter)) continue;
                        string insert = fromName != null ? BuildTableInsertText(fromName, f, connInfo, settings, insertMode) : FormatObjectInsert(f, settings);
                        items.Add(new CompletionItem(f.QualifiedName, insert, CompletionKind.TableFunction,
                            BuildRoutineDescription(f)));
                    }
                }
            }

            private void AddTablesAndViews(List<CompletionItem> items, MetadataCatalog catalog, IntelliSenseSettings settings, FromObjectNameContext fromName = null, ScriptFactoryAccess.ConnectionInfo connInfo = null, string namePrefix = null)
            {
                if (catalog == null) return;
                if (connInfo != null && !string.IsNullOrWhiteSpace(connInfo.Database)
                    && !string.Equals(catalog.Database, connInfo.Database, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                string filter = GetLastSegment(namePrefix ?? fromName?.Partial);
                int cap = Math.Max(50, settings.maxCompletionItems * 3);
                var insertMode = ResolveTableInsertMode(fromName, connInfo);
                int added = 0;
                foreach (var t in catalog.Tables)
                {
                    if (!ObjectNameMatchesFilter(t.Name, filter)) continue;
                    string insert = fromName != null ? BuildTableInsertText(fromName, t, connInfo, settings, insertMode) : FormatObjectInsert(t, settings);
                    items.Add(new CompletionItem(t.QualifiedName, insert, CompletionKind.Table,
                        BuildTableDescription(t)));
                    if (++added >= cap) return;
                }
                foreach (var v in catalog.Views)
                {
                    if (!ObjectNameMatchesFilter(v.Name, filter)) continue;
                    string insert = fromName != null ? BuildTableInsertText(fromName, v, connInfo, settings, insertMode) : FormatObjectInsert(v, settings);
                    items.Add(new CompletionItem(v.QualifiedName, insert, CompletionKind.View,
                        BuildTableDescription(v)));
                    if (++added >= cap) return;
                }
                if (settings.includeSystemObjects && fromName == null && string.IsNullOrEmpty(filter))
                {
                    AddSystemObjects(items);
                }
            }

            private void AddColumnsAndFunctions(List<CompletionItem> items, MetadataCatalog catalog, LocalSymbols local, IntelliSenseSettings settings, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                // 列：来自 FROM 别名表 + CTE + 临时表 + 表变量
                AddColumnsFromFromAliases(items, catalog, local, settings, connInfo);
                AddLocalColumns(items, local, settings);
                // 星号
                items.Add(new CompletionItem("*", "*", CompletionKind.Column, "所有列"));
                // 内建函数（常用）
                if (settings.includeKeywords)
                {
                    foreach (var fn in BuiltInFunctionInfos)
                    {
                        var item = new CompletionItem(fn.Name, fn.InsertText, CompletionKind.ScalarFunction, BuildBuiltInFunctionDescription(fn));
                        item.SnippetCursorOffset = fn.CursorOffset;
                        items.Add(item);
                    }
                    AddKeywords(items, new[] { "CASE" });
                }
            }

            private static string BuildBuiltInFunctionDescription(BuiltInFunctionInfo fn)
            {
                var sb = new StringBuilder();
                sb.AppendLine("内建函数");
                sb.Append(fn.Signature);
                if (fn.Parameters != null && fn.Parameters.Length > 0)
                {
                    sb.AppendLine().AppendLine().AppendLine("参数:");
                    foreach (var p in fn.Parameters)
                        sb.Append("  ").AppendLine(p);
                }
                return sb.ToString().TrimEnd();
            }

            private void AddColumnsFromFromAliases(List<CompletionItem> items, MetadataCatalog catalog, LocalSymbols local, IntelliSenseSettings settings, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                // 每张表选一个短别名（非表名、非 schema.table）；无别名则不带前缀
                var preferredAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var tables = new Dictionary<string, TableRef>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in local.Aliases)
                {
                    var tref = kv.Value;
                    if (tref == null || string.IsNullOrEmpty(tref.Name)) continue;
                    string tableKey = (tref.Database ?? string.Empty) + "|" + (tref.Schema ?? string.Empty) + "|" + tref.Name;
                    tables[tableKey] = tref;
                    string aliasKey = kv.Key;
                    if (string.IsNullOrEmpty(aliasKey) || aliasKey.IndexOf('.') >= 0) continue;
                    if (string.Equals(aliasKey, tref.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    string existing;
                    if (!preferredAlias.TryGetValue(tableKey, out existing) || aliasKey.Length < existing.Length)
                        preferredAlias[tableKey] = aliasKey;
                }
                foreach (var kv in tables)
                {
                    var tcol = ResolveTableRef(connInfo, catalog, kv.Value);
                    if (tcol == null) continue;
                    string alias;
                    preferredAlias.TryGetValue(kv.Key, out alias);
                    foreach (var col in tcol.Columns)
                    {
                        string colInsert = FormatColumnInsert(col.Name, settings);
                        string insert = string.IsNullOrEmpty(alias)
                            ? colInsert
                            : FormatIdentifier(alias, settings) + "." + colInsert;
                        items.Add(CreateColumnItem(col.Name, insert, col, kv.Value, tcol, catalog, connInfo));
                    }
                }
            }

            private void AddLocalColumns(List<CompletionItem> items, LocalSymbols local, IntelliSenseSettings settings)
            {
                foreach (var cte in local.Ctes)
                {
                    foreach (var col in cte.ColumnNames)
                    {
                        items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "CTE 列: " + cte.Name));
                    }
                    if (cte.ColumnNames.Count == 0)
                    {
                        items.Add(new CompletionItem(cte.Name, cte.Name, CompletionKind.Table, "CTE"));
                    }
                }
                if (settings.includeLocalTempTables)
                {
                    foreach (var tt in local.TempTables)
                    {
                        foreach (var col in tt.ColumnNames)
                        {
                            items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "临时表列: " + tt.Name));
                        }
                    }
                }
                if (settings.includeLocalVariables)
                {
                    foreach (var tv in local.TableVariables)
                    {
                        foreach (var col in tv.ColumnNames)
                        {
                            items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "表变量列: " + tv.Name));
                        }
                    }
                }
            }

            private void AddExecItems(
                List<CompletionItem> items,
                FromObjectNameContext fromName,
                string prefix,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                string database = null;
                string schema = null;
                bool nameOnlyInsert = false;
                // 库.前缀（单点）→ 插入 dbo.name；库.. / 库.dbo. → 只插过程名
                bool insertSchemaWithName = false;
                int segCount = fromName?.Segments?.Count ?? 0;

                // EXEC caiwu..pro / EXEC caiwu.dbo.pro / EXEC caiwu.pro / EXEC pro
                if (fromName != null && (segCount > 0 || fromName.AfterDot))
                {
                    nameOnlyInsert = fromName.AfterDot;
                    if (segCount >= 1 && fromName.UsesDoubleDot)
                    {
                        // db..proc → 默认 dbo，首段即库名（勿再依赖 IsDatabaseName）
                        database = fromName.Segments[0];
                        schema = "dbo";
                    }
                    else if (segCount == 1 && fromName.AfterDot)
                    {
                        if (IsDatabaseName(connInfo, fromName.Segments[0]))
                        {
                            database = fromName.Segments[0];
                            insertSchemaWithName = true;
                        }
                        else
                            schema = fromName.Segments[0];
                    }
                    else if (segCount >= 2)
                    {
                        database = fromName.Segments[0];
                        schema = fromName.Segments[1];
                    }
                    else if (segCount == 1 && !fromName.AfterDot)
                    {
                        AddDatabases(items, connInfo, settings);
                        var cur = ResolveExecCatalog(connInfo, null, catalog);
                        AddProceduresFromCatalog(items, cur, settings, null, false, false);
                        return;
                    }
                }

                MetadataCatalog target = ResolveExecCatalog(connInfo, database, catalog);
                if (target == null)
                    return;

                AddProceduresFromCatalog(items, target, settings, schema, nameOnlyInsert, insertSchemaWithName);
                if (string.IsNullOrEmpty(database) && !nameOnlyInsert)
                    AddDatabases(items, connInfo, settings);
            }

            /// <summary>EXEC 必须有过程目录：跨库按库名取/建；当前库缓存未好或 Routines 残缺则同步补齐。</summary>
            private static MetadataCatalog ResolveExecCatalog(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string database,
                MetadataCatalog currentCatalog)
            {
                if (connInfo == null) return currentCatalog;
                if (!string.IsNullOrEmpty(database))
                    return MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo, database, requireRoutines: true);
                if (currentCatalog != null && currentCatalog.RoutinesLoaded)
                    return currentCatalog;
                return MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo, null, requireRoutines: true)
                       ?? currentCatalog;
            }

            private void AddProceduresFromCatalog(
                List<CompletionItem> items,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                string schemaFilter,
                bool nameOnlyInsert,
                bool insertSchemaWithName = false)
            {
                if (catalog == null) return;
                foreach (var p in catalog.Procedures)
                {
                    if (!string.IsNullOrEmpty(schemaFilter)
                        && !string.Equals(p.Schema, schemaFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string insert;
                    if (insertSchemaWithName)
                    {
                        string sch = string.IsNullOrEmpty(p.Schema) ? "dbo" : p.Schema;
                        insert = FormatIdentifier(sch, settings) + "." + FormatIdentifier(p.Name, settings);
                    }
                    else if (nameOnlyInsert)
                        insert = FormatIdentifier(p.Name, settings);
                    else
                        insert = FormatObjectInsert(p, settings);
                    int cursorOffset;
                    insert = AppendProcedureParameters(insert, p, out cursorOffset);
                    var item = new CompletionItem(p.Name, insert, CompletionKind.Procedure, BuildRoutineDescription(p));
                    item.SourceDatabase = catalog.Database;
                    item.SourceTable = string.IsNullOrEmpty(p.Schema) ? p.Name : p.Schema + "." + p.Name;
                    item.SnippetCursorOffset = cursorOffset;
                    items.Add(item);
                }
                if (!nameOnlyInsert && !insertSchemaWithName && settings.includeSystemObjects)
                {
                    foreach (var s in MetadataCatalogService.CommonSystemObjects)
                    {
                        if (s.StartsWith("sp_"))
                            items.Add(new CompletionItem(s, s, CompletionKind.Procedure, "系统过程"));
                    }
                }
            }

            /// <summary>
            /// 追加命名参数：@Company = , @Name = , @Out = OUTPUT。
            /// cursorOffset 指向第一个待填值位置（InsertText 内偏移）。
            /// </summary>
            private static string AppendProcedureParameters(string insertBase, RoutineInfo routine, out int cursorOffset)
            {
                cursorOffset = -1;
                if (string.IsNullOrEmpty(insertBase) || routine?.Parameters == null || routine.Parameters.Count == 0)
                    return insertBase;

                var sb = new StringBuilder(insertBase);
                bool first = true;
                foreach (var param in routine.Parameters)
                {
                    if (string.IsNullOrEmpty(param.Name)) continue;
                    if (first)
                    {
                        sb.Append(' ');
                        first = false;
                    }
                    else
                        sb.Append(", ");

                    sb.Append(param.Name);
                    if (param.IsOutput)
                    {
                        sb.Append(" = OUTPUT");
                    }
                    else
                    {
                        sb.Append(" = ");
                        if (cursorOffset < 0)
                            cursorOffset = sb.Length;
                    }
                }
                return sb.ToString();
            }

            private void AddProceduresAndScalarFunctions(List<CompletionItem> items, MetadataCatalog catalog, IntelliSenseSettings settings)
            {
                AddProceduresFromCatalog(items, catalog, settings, null, false);
                if (catalog == null) return;
                foreach (var f in catalog.ScalarFunctions)
                {
                    items.Add(new CompletionItem(f.Name, FormatObjectInsert(f, settings), CompletionKind.ScalarFunction,
                        BuildRoutineDescription(f)));
                }
            }

            private void AddMemberColumns(List<CompletionItem> items, string prefix, MetadataCatalog catalog, LocalSymbols local, IntelliSenseSettings settings, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                // prefix 形如 "alias." 或 "alias.col"，取 "." 前的标识符
                string owner = GetOwnerBeforeDot(prefix);
                if (string.IsNullOrEmpty(owner)) return;

                // 本地对象优先
                if (AddLocalMemberColumns(items, owner, local, settings))
                {
                    return;
                }

                // 别名映射
                TableRef tref;
                if (local.Aliases.TryGetValue(owner, out tref))
                {
                    var tcol = ResolveTableRef(connInfo, catalog, tref);
                    if (tcol != null)
                    {
                        foreach (var col in tcol.Columns)
                        {
                            items.Add(CreateColumnItem(col.Name, FormatColumnInsert(col.Name, settings), col, tref, tcol, catalog, connInfo));
                        }
                    }
                    return;
                }

                if (catalog == null) return;

                // schema 名 → 列出该 schema 下表
                var tablesOfSchema = new List<(TableColumnInfo Obj, CompletionKind Kind)>();
                tablesOfSchema.AddRange(catalog.Tables.Where(t => string.Equals(t.Schema, owner, StringComparison.OrdinalIgnoreCase)).Select(t => (t, CompletionKind.Table)));
                tablesOfSchema.AddRange(catalog.Views.Where(v => string.Equals(v.Schema, owner, StringComparison.OrdinalIgnoreCase)).Select(v => (v, CompletionKind.View)));
                if (tablesOfSchema.Count > 0)
                {
                    foreach (var entry in tablesOfSchema)
                    {
                        var item = new CompletionItem(entry.Obj.Name, FormatObjectInsert(entry.Obj, settings), entry.Kind, BuildTableDescription(entry.Obj));
                        ApplyObjectSource(item, catalog?.Database, entry.Obj);
                        items.Add(item);
                    }
                    return;
                }
            }

            private void AddLocalMemberColumnsFromPrefix(List<CompletionItem> items, string prefix, LocalSymbols local, IntelliSenseSettings settings)
            {
                string owner = GetOwnerBeforeDot(prefix);
                if (string.IsNullOrEmpty(owner)) return;
                AddLocalMemberColumns(items, owner, local, settings);
            }

            private bool AddLocalMemberColumns(List<CompletionItem> items, string owner, LocalSymbols local, IntelliSenseSettings settings)
            {
                // CTE
                var cte = local.Ctes.FirstOrDefault(c => string.Equals(c.Name, owner, StringComparison.OrdinalIgnoreCase));
                if (cte != null)
                {
                    foreach (var col in cte.ColumnNames)
                    {
                        items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "CTE 列: " + cte.Name));
                    }
                    return true;
                }
                // 临时表
                if (settings.includeLocalTempTables)
                {
                    var tt = local.TempTables.FirstOrDefault(t => string.Equals(t.Name, owner, StringComparison.OrdinalIgnoreCase));
                    if (tt != null)
                    {
                        foreach (var col in tt.ColumnNames)
                        {
                            items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "临时表列: " + tt.Name));
                        }
                        return true;
                    }
                }
                // 表变量
                if (settings.includeLocalVariables)
                {
                    var tv = local.TableVariables.FirstOrDefault(t => string.Equals(t.Name, owner, StringComparison.OrdinalIgnoreCase));
                    if (tv != null)
                    {
                        foreach (var col in tv.ColumnNames)
                        {
                            items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "表变量列: " + tv.Name));
                        }
                        return true;
                    }
                }
                return false;
            }

            private void AddLocalVariables(List<CompletionItem> items, LocalSymbols local, IntelliSenseSettings settings)
            {
                if (!settings.includeLocalVariables) return;
                foreach (var v in local.ScalarVariables)
                {
                    items.Add(new CompletionItem(v, v, CompletionKind.Variable, "局部变量"));
                }
                foreach (var tv in local.TableVariables)
                {
                    items.Add(new CompletionItem(tv.Name, tv.Name, CompletionKind.Variable, "表变量"));
                }
            }

            private void AddDatabases(List<CompletionItem> items, ScriptFactoryAccess.ConnectionInfo connInfo, IntelliSenseSettings settings)
            {
                if (connInfo == null) return;
                try
                {
                    var dbs = MetadataCatalogService.Instance.GetDatabasesCached(connInfo);
                    foreach (var db in dbs)
                    {
                        items.Add(new CompletionItem(db, FormatIdentifier(db, settings), CompletionKind.Database, "数据库"));
                    }
                }
                catch
                {
                    // 取库列表失败 → 静默
                }
            }

            private void AddLinkedServers(List<CompletionItem> items, ScriptFactoryAccess.ConnectionInfo connInfo, IntelliSenseSettings settings, string prefixFilter = null)
            {
                if (connInfo == null) return;
                foreach (var server in MetadataCatalogService.Instance.GetLinkedServers(connInfo))
                {
                    if (!string.IsNullOrEmpty(prefixFilter)
                        && !server.StartsWith(prefixFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    items.Add(new CompletionItem(server, FormatIdentifier(server, settings), CompletionKind.Database, "链接服务器"));
                }
            }

            private void AddLinkedServerDatabases(
                List<CompletionItem> items,
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string linkedServer,
                FromObjectNameContext fromName,
                IntelliSenseSettings settings)
            {
                if (connInfo == null || string.IsNullOrEmpty(linkedServer)) return;
                foreach (var db in MetadataCatalogService.Instance.GetLinkedServerDatabases(connInfo, linkedServer))
                {
                    string insert = BuildLinkedServerDatabaseInsertText(fromName, db, settings);
                    items.Add(new CompletionItem(db, insert, CompletionKind.Database, "链接服务器数据库 (" + linkedServer + ")"));
                }
            }

            private string BuildLinkedServerDatabaseInsertText(FromObjectNameContext fromName, string database, IntelliSenseSettings settings)
            {
                return FormatIdentifier(database, settings);
            }

            private void AddSystemObjects(List<CompletionItem> items)
            {
                foreach (var s in MetadataCatalogService.CommonSystemObjects)
                {
                    items.Add(new CompletionItem(s, s, CompletionKind.Table, "系统对象"));
                }
            }

            private List<CompletionItem> FilterAndSort(List<CompletionItem> items, string prefix, IntelliSenseSettings settings, ScriptFactoryAccess.ConnectionInfo connInfo, FromObjectNameContext fromName)
            {
                var filtered = new List<CompletionItem>();
                string p = (prefix ?? string.Empty);
                string filterPrefix = GetLastSegment(p);

                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(filterPrefix) || ItemMatchesPrefix(item, filterPrefix))
                    {
                        filtered.Add(item);
                    }
                    if (filtered.Count >= settings.maxCompletionItems * 2)
                    {
                        break;
                    }
                }

                filtered.Sort((a, b) =>
                {
                    int scoreA = GetMatchScore(a, filterPrefix);
                    int scoreB = GetMatchScore(b, filterPrefix);
                    if (scoreA != scoreB) return scoreB.CompareTo(scoreA);

                    int kindA = GetKindSortOrder(a.Kind);
                    int kindB = GetKindSortOrder(b.Kind);
                    if (kindA != kindB) return kindA.CompareTo(kindB);

                    bool aDbo = IsDboQualifiedName(a.DisplayText);
                    bool bDbo = IsDboQualifiedName(b.DisplayText);
                    if (aDbo != bDbo) return aDbo ? -1 : 1;

                    bool aSystem = IsSystemObjectName(a.DisplayText);
                    bool bSystem = IsSystemObjectName(b.DisplayText);
                    if (aSystem != bSystem) return aSystem ? 1 : -1;

                    return string.Compare(a.DisplayText, b.DisplayText, StringComparison.OrdinalIgnoreCase);
                });

                if (filtered.Count > settings.maxCompletionItems)
                {
                    filtered = filtered.Take(settings.maxCompletionItems).ToList();
                }
                return filtered;
            }

            private static int GetKindSortOrder(CompletionKind kind)
            {
                switch (kind)
                {
                    case CompletionKind.Column: return -3;
                    case CompletionKind.Procedure: return -2;
                    case CompletionKind.Database: return -1;
                    case CompletionKind.Keyword: return 0;
                    case CompletionKind.Snippet: return 1;
                    case CompletionKind.Table: return 2;
                    case CompletionKind.View: return 3;
                    case CompletionKind.Synonym: return 4;
                    case CompletionKind.TableFunction: return 5;
                    case CompletionKind.Schema: return 6;
                    case CompletionKind.ScalarFunction: return 7;
                    default: return 10;
                }
            }

            private static bool IsDboQualifiedName(string displayText)
            {
                if (string.IsNullOrEmpty(displayText)) return false;
                return displayText.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsSystemObjectName(string displayText)
            {
                if (string.IsNullOrEmpty(displayText)) return false;
                string name = GetMatchableName(displayText);
                return name.StartsWith("INFORMATION_SCHEMA.", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("sys.", StringComparison.OrdinalIgnoreCase);
            }

            private static int GetMatchScore(CompletionItem item, string filterPrefix)
            {
                if (string.IsNullOrEmpty(filterPrefix)) return 0;
                string displayText = item?.DisplayText ?? string.Empty;
                if (IsNumericOrIpPrefix(filterPrefix))
                {
                    if (displayText.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase)) return 100;
                    return 0;
                }
                string name = displayText;
                if (item != null && item.Kind == CompletionKind.Snippet &&
                    !string.IsNullOrEmpty(item.SnippetPrefix))
                {
                    name = item.SnippetPrefix;
                }
                else if (item != null &&
                         (item.Kind == CompletionKind.Table || item.Kind == CompletionKind.View ||
                          item.Kind == CompletionKind.Synonym || item.Kind == CompletionKind.TableFunction ||
                          item.Kind == CompletionKind.Procedure || item.Kind == CompletionKind.ScalarFunction))
                {
                    name = GetMatchableName(name);
                }

                if (name.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return item != null && item.Kind == CompletionKind.Snippet ? 110 : 100;
                }
                if (name.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase)) return 100;
                // 多词关键字：in → INNER JOIN、group → GROUP BY
                if (item != null && item.Kind == CompletionKind.Keyword && name.IndexOf(' ') >= 0)
                {
                    int sp = name.IndexOf(' ');
                    string first = sp > 0 ? name.Substring(0, sp) : name;
                    if (first.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase)) return 95;
                }
                // 片段/关键字/内建函数只做前缀匹配，避免 pr 命中 DATEPART 等缩写误匹配
                if (item != null && (item.Kind == CompletionKind.Snippet || item.Kind == CompletionKind.Keyword
                    || item.Kind == CompletionKind.ScalarFunction))
                    return 0;
                if (name.IndexOf(filterPrefix, StringComparison.OrdinalIgnoreCase) >= 0) return 80;

                string collapsedName = CollapseForMatch(name);
                string collapsedFilter = CollapseForMatch(filterPrefix);
                if (!string.IsNullOrEmpty(collapsedFilter))
                {
                    if (collapsedName.StartsWith(collapsedFilter, StringComparison.OrdinalIgnoreCase)) return 85;
                    if (collapsedName.IndexOf(collapsedFilter, StringComparison.OrdinalIgnoreCase) >= 0) return 65;
                    if (MatchesAbbreviation(collapsedName, collapsedFilter)) return 50;
                }

                var segments = name.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var seg in segments)
                {
                    if (seg.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase)) return 60;
                    if (seg.IndexOf(filterPrefix, StringComparison.OrdinalIgnoreCase) >= 0) return 40;
                }
                return 0;
            }

            private static string CollapseForMatch(string text)
            {
                if (string.IsNullOrEmpty(text)) return string.Empty;
                var sb = new StringBuilder(text.Length);
                foreach (char c in text)
                {
                    if (c != '_' && c != '.') sb.Append(c);
                }
                return sb.ToString();
            }

            /// <summary>缩写匹配：filter 字符按序出现在 name 中（如 tpr → t_products）。</summary>
            private static bool MatchesAbbreviation(string name, string abbr)
            {
                if (string.IsNullOrEmpty(abbr)) return true;
                if (string.IsNullOrEmpty(name)) return false;
                int j = 0;
                for (int i = 0; i < name.Length && j < abbr.Length; i++)
                {
                    if (char.ToLowerInvariant(name[i]) == char.ToLowerInvariant(abbr[j]))
                    {
                        j++;
                    }
                }
                return j == abbr.Length;
            }

            private static int GetMatchScore(string displayText, string filterPrefix)
            {
                return GetMatchScore(new CompletionItem { DisplayText = displayText }, filterPrefix);
            }

            private static string GetMatchableName(string displayText)
            {
                if (string.IsNullOrEmpty(displayText)) return string.Empty;
                int dot = displayText.LastIndexOf('.');
                return dot >= 0 ? displayText.Substring(dot + 1) : displayText;
            }

            private static bool ItemMatchesPrefix(CompletionItem item, string filterPrefix)
            {
                if (string.IsNullOrEmpty(filterPrefix)) return true;
                if (item == null) return false;
                return GetMatchScore(item, filterPrefix) > 0;
            }

            private static bool ItemMatchesPrefix(string displayText, string filterPrefix)
            {
                if (string.IsNullOrEmpty(filterPrefix)) return true;
                if (string.IsNullOrEmpty(displayText)) return false;
                return GetMatchScore(displayText, filterPrefix) > 0;
            }

            private static string GetOwnerBeforeDot(string prefix)
            {
                if (string.IsNullOrEmpty(prefix)) return null;
                int dot = prefix.LastIndexOf('.');
                if (dot < 0) return prefix;
                return prefix.Substring(0, dot);
            }

            private static string GetLastSegment(string prefix)
            {
                if (string.IsNullOrEmpty(prefix)) return string.Empty;
                int dot = prefix.LastIndexOf('.');
                if (dot < 0) return prefix;
                return prefix.Substring(dot + 1);
            }

            #endregion

            #region 描述与常量

            private static CompletionItem CreateColumnItem(
                string display,
                string insert,
                ColumnInfo col,
                TableRef tref,
                TableColumnInfo table,
                MetadataCatalog catalog,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                var item = new CompletionItem(display, insert, CompletionKind.Column, BuildColumnDescription(col));
                item.SourceDatabase = ResolveSourceDatabase(tref, catalog, connInfo);
                item.SourceTable = FormatSourceTable(table, tref);
                return item;
            }

            private static void ApplyObjectSource(CompletionItem item, string database, TableColumnInfo obj)
            {
                if (item == null || obj == null) return;
                item.SourceDatabase = database;
                item.SourceTable = FormatSourceTable(obj, null);
            }

            private static string ResolveSourceDatabase(
                TableRef tref,
                MetadataCatalog catalog,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (tref != null && !string.IsNullOrEmpty(tref.Database))
                    return tref.Database;
                if (catalog != null && !string.IsNullOrEmpty(catalog.Database))
                    return catalog.Database;
                return connInfo?.Database;
            }

            private static string FormatSourceTable(TableColumnInfo table, TableRef tref)
            {
                if (table != null && !string.IsNullOrEmpty(table.Name))
                {
                    return string.IsNullOrEmpty(table.Schema)
                        ? table.Name
                        : table.Schema + "." + table.Name;
                }
                if (tref != null && !string.IsNullOrEmpty(tref.Name))
                {
                    return string.IsNullOrEmpty(tref.Schema)
                        ? tref.Name
                        : tref.Schema + "." + tref.Name;
                }
                return null;
            }

            private static string BuildTableDescription(TableColumnInfo t)
            {
                var sb = new StringBuilder();
                sb.Append(t.IsView ? "视图: " : "表: ").AppendLine(t.QualifiedName);
                if (!string.IsNullOrEmpty(t.Description))
                {
                    sb.AppendLine().AppendLine(t.Description);
                }
                sb.Append("列数: ").Append(t.Columns?.Count ?? 0);
                return sb.ToString();
            }

            private static string BuildColumnDescription(ColumnInfo c)
            {
                var sb = new StringBuilder();
                sb.Append(c.DataType);
                sb.Append(c.Nullable ? " NULL" : " NOT NULL");
                if (!string.IsNullOrEmpty(c.DefaultValue))
                {
                    sb.Append(" DEFAULT ").Append(c.DefaultValue);
                }
                if (!string.IsNullOrEmpty(c.Description))
                {
                    sb.AppendLine().AppendLine().Append(c.Description);
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
                        if (p.HasDefault && !string.IsNullOrEmpty(p.DefaultValue))
                        {
                            sb.Append(" = ").Append(p.DefaultValue);
                        }
                        sb.AppendLine();
                    }
                }
                if (!string.IsNullOrEmpty(r.Description))
                {
                    sb.AppendLine().AppendLine(r.Description);
                }
                return sb.ToString();
            }

            public static readonly string[] SelectClauseKeywords = { "FROM", "INTO" };

            public static readonly string[] TopLevelKeywords =
            {
                "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP",
                "EXEC", "USE", "DECLARE", "SET", "IF", "BEGIN", "END", "TRUNCATE", "MERGE", "GO"
            };

            /// <summary>FROM 表之后常见子句/连接关键字。</summary>
            public static readonly string[] AfterFromKeywords =
            {
                "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "JOIN",
                "LEFT OUTER JOIN", "RIGHT OUTER JOIN", "FULL OUTER JOIN",
                "CROSS APPLY", "OUTER APPLY",
                "WHERE", "GROUP BY", "ORDER BY", "HAVING",
                "UNION", "UNION ALL", "EXCEPT", "INTERSECT"
            };

            public static readonly string[] CreateObjectKeywords =
            {
                "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "INDEX", "SCHEMA", "TYPE", "TRIGGER"
            };

            public static readonly string[] AlterObjectKeywords =
            {
                "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "INDEX", "SCHEMA", "TRIGGER"
            };

            public static readonly string[] OperatorKeywords =
            {
                "AND", "OR", "NOT", "IN", "BETWEEN", "LIKE", "IS", "NULL", "EXISTS", "CASE", "WHEN", "THEN", "ELSE", "END"
            };

            private static bool ShouldSuggestAfterFromKeywords(FromObjectNameContext fromName, string prefix)
            {
                if (fromName != null && fromName.AfterDot) return false;
                // 仍在敲 FROM 后第一个对象名：只提示表/库，勿夹 LEFT JOIN 等
                if (fromName == null || fromName.Segments == null || fromName.Segments.Count == 0)
                    return false;
                // 已有表名后再提示 JOIN / WHERE（如 FROM t le）
                return !string.IsNullOrEmpty(prefix);
            }

            private sealed class BuiltInFunctionInfo
            {
                public string Name;
                public string Signature;
                public string InsertText;
                public int CursorOffset;
                public string[] Parameters;
            }

            /// <summary>内建函数：插入完整括号/参数占位，右侧展示签名。</summary>
            private static readonly BuiltInFunctionInfo[] BuiltInFunctionInfos = BuildBuiltInFunctionInfos();

            private static BuiltInFunctionInfo[] BuildBuiltInFunctionInfos()
            {
                return new[]
                {
                    Fn("COUNT", "COUNT(expression)", "COUNT()", 6, "expression"),
                    Fn("SUM", "SUM(expression)", "SUM()", 4, "expression"),
                    Fn("AVG", "AVG(expression)", "AVG()", 4, "expression"),
                    Fn("MIN", "MIN(expression)", "MIN()", 4, "expression"),
                    Fn("MAX", "MAX(expression)", "MAX()", 4, "expression"),
                    Fn("GETDATE", "GETDATE()", "GETDATE()", -1),
                    Fn("GETUTCDATE", "GETUTCDATE()", "GETUTCDATE()", -1),
                    Fn("SYSDATETIME", "SYSDATETIME()", "SYSDATETIME()", -1),
                    Fn("DATEADD", "DATEADD(datepart, number, date)", "DATEADD(, , )", 8,
                        "datepart", "number", "date"),
                    Fn("DATEDIFF", "DATEDIFF(datepart, startdate, enddate)", "DATEDIFF(, , )", 9,
                        "datepart", "startdate", "enddate"),
                    Fn("DATENAME", "DATENAME(datepart, date)", "DATENAME(, )", 9,
                        "datepart", "date"),
                    Fn("DATEPART", "DATEPART(datepart, date)", "DATEPART(, )", 9,
                        "datepart", "date"),
                    Fn("YEAR", "YEAR(date)", "YEAR()", 5, "date"),
                    Fn("MONTH", "MONTH(date)", "MONTH()", 6, "date"),
                    Fn("DAY", "DAY(date)", "DAY()", 4, "date"),
                    Fn("LEN", "LEN(string_expression)", "LEN()", 4, "string_expression"),
                    Fn("DATALENGTH", "DATALENGTH(expression)", "DATALENGTH()", 11, "expression"),
                    Fn("SUBSTRING", "SUBSTRING(expression, start, length)", "SUBSTRING(, , )", 10,
                        "expression", "start", "length"),
                    Fn("LEFT", "LEFT(character_expression, integer_expression)", "LEFT(, )", 5,
                        "character_expression", "integer_expression"),
                    Fn("RIGHT", "RIGHT(character_expression, integer_expression)", "RIGHT(, )", 6,
                        "character_expression", "integer_expression"),
                    Fn("CHARINDEX", "CHARINDEX(expressionToFind, expressionToSearch [, start_location])", "CHARINDEX(, )", 10,
                        "expressionToFind", "expressionToSearch", "start_location (optional)"),
                    Fn("PATINDEX", "PATINDEX('%pattern%', expression)", "PATINDEX(, )", 9,
                        "pattern", "expression"),
                    Fn("REPLACE", "REPLACE(string_expression, string_pattern, string_replacement)", "REPLACE(, , )", 8,
                        "string_expression", "string_pattern", "string_replacement"),
                    Fn("STUFF", "STUFF(character_expression, start, length, replaceWith_expression)", "STUFF(, , , )", 6,
                        "character_expression", "start", "length", "replaceWith_expression"),
                    Fn("UPPER", "UPPER(character_expression)", "UPPER()", 6, "character_expression"),
                    Fn("LOWER", "LOWER(character_expression)", "LOWER()", 6, "character_expression"),
                    Fn("LTRIM", "LTRIM(character_expression)", "LTRIM()", 6, "character_expression"),
                    Fn("RTRIM", "RTRIM(character_expression)", "RTRIM()", 6, "character_expression"),
                    Fn("CONCAT", "CONCAT(string_value1, string_value2 [, ...n])", "CONCAT(, )", 7,
                        "string_value1", "string_value2", "..."),
                    Fn("COALESCE", "COALESCE(expression1, expression2 [, ...n])", "COALESCE(, )", 9,
                        "expression1", "expression2", "..."),
                    Fn("NULLIF", "NULLIF(expression, expression)", "NULLIF(, )", 7,
                        "expression", "expression"),
                    Fn("ISNULL", "ISNULL(check_expression, replacement_value)", "ISNULL(, )", 7,
                        "check_expression", "replacement_value"),
                    Fn("CAST", "CAST(expression AS data_type)", "CAST( AS )", 5,
                        "expression", "data_type"),
                    Fn("CONVERT", "CONVERT(data_type, expression [, style])", "CONVERT(, )", 8,
                        "data_type", "expression", "style (optional)"),
                    Fn("ROW_NUMBER", "ROW_NUMBER() OVER (ORDER BY ...)", "ROW_NUMBER() OVER (ORDER BY )", 27),
                    Fn("RANK", "RANK() OVER (ORDER BY ...)", "RANK() OVER (ORDER BY )", 21),
                    Fn("DENSE_RANK", "DENSE_RANK() OVER (ORDER BY ...)", "DENSE_RANK() OVER (ORDER BY )", 27),
                    Fn("NTILE", "NTILE(integer_expression) OVER (ORDER BY ...)", "NTILE() OVER (ORDER BY )", 6,
                        "integer_expression"),
                    Fn("LEAD", "LEAD(scalar_expression [, offset [, default]]) OVER (ORDER BY ...)", "LEAD() OVER (ORDER BY )", 5,
                        "scalar_expression", "offset (optional)", "default (optional)"),
                    Fn("LAG", "LAG(scalar_expression [, offset [, default]]) OVER (ORDER BY ...)", "LAG() OVER (ORDER BY )", 4,
                        "scalar_expression", "offset (optional)", "default (optional)"),
                    Fn("NEWID", "NEWID()", "NEWID()", -1),
                    Fn("SCOPE_IDENTITY", "SCOPE_IDENTITY()", "SCOPE_IDENTITY()", -1),
                    Fn("@@ROWCOUNT", "@@ROWCOUNT", "@@ROWCOUNT", -1),
                    Fn("@@IDENTITY", "@@IDENTITY", "@@IDENTITY", -1),
                    Fn("DB_NAME", "DB_NAME([database_id])", "DB_NAME()", 8, "database_id (optional)"),
                    Fn("OBJECT_ID", "OBJECT_ID(object_name [, object_type])", "OBJECT_ID()", 10,
                        "object_name", "object_type (optional)"),
                    Fn("OBJECT_NAME", "OBJECT_NAME(object_id [, database_id])", "OBJECT_NAME()", 12,
                        "object_id", "database_id (optional)"),
                    Fn("USER_NAME", "USER_NAME([id])", "USER_NAME()", 10, "id (optional)"),
                    Fn("SUSER_NAME", "SUSER_NAME([server_user_id])", "SUSER_NAME()", 11, "server_user_id (optional)")
                };
            }

            private static BuiltInFunctionInfo Fn(string name, string signature, string insert, int cursorOffset, params string[] parameters)
            {
                return new BuiltInFunctionInfo
                {
                    Name = name,
                    Signature = signature,
                    InsertText = insert,
                    CursorOffset = cursorOffset,
                    Parameters = parameters ?? Array.Empty<string>()
                };
            }

            #endregion
        }
    }
}
