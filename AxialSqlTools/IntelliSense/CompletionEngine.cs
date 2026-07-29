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
                    if (script == null)
                    {
                        return result;
                    }

                    var tokens = _lastTokens;
                    int localOffset = caretOffset - batchStart;
                    if (localOffset < 0) localOffset = 0;

                    var local = ExtractLocalSymbols(script);
                    CollectAliases(script, localOffset, local);

                    string prefix;
                    int caretTokenIndex = FindTokenIndexBefore(tokens, localOffset);
                    if (caretTokenIndex >= 0 && IsAtLineStartTypingKeyword(tokens, localOffset, caretTokenIndex))
                    {
                        prefix = ExtractPrefix(tokens, localOffset);
                        result.Context = CompletionContext.BatchStart;
                        result.Prefix = prefix;
                        result.Items = BuildItems(CompletionContext.BatchStart, prefix, null, local, catalog, settings, connInfo);
                        result.Items = FilterAndSort(result.Items, prefix, settings, connInfo, null);
                        result.ReplaceStartOffset = ComputeReplaceStartOffset(batchStart, localOffset, prefix, null, tokens);
                        result.ReplaceEndOffset = caretOffset;
                        return result;
                    }

                    var fromName = ParseFromObjectName(tokens, localOffset);
                    var ctx = GetContext(tokens, localOffset, fromName, out prefix);
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
            private void CollectAliases(TSqlScript script, int localOffset, LocalSymbols local)
            {
                local.Aliases.Clear();
                if (script == null) return;

                foreach (var batch in script.Batches)
                {
                    foreach (var stmt in batch.Statements)
                    {
                        var from = GetFromClause(stmt);
                        if (from == null) continue;

                        // 仅收集覆盖光标位置的语句（近似：token 区间）
                        if (!StatementCoversOffset(stmt, localOffset)) continue;

                        foreach (var tr in from.TableReferences ?? Enumerable.Empty<TableReference>())
                        {
                            CollectFromClause(tr, local);
                        }
                    }
                }
            }

            private bool StatementCoversOffset(TSqlStatement stmt, int localOffset)
            {
                try
                {
                    return localOffset >= stmt.StartOffset && localOffset <= stmt.StartOffset + stmt.FragmentLength + 1;
                }
                catch
                {
                    return true;
                }
            }

            private FromClause GetFromClause(TSqlStatement stmt)
            {
                if (stmt is SelectStatement ss) return (ss.QueryExpression as QuerySpecification)?.FromClause;
                if (stmt is UpdateStatement us) return us.UpdateSpecification?.FromClause;
                if (stmt is DeleteStatement ds) return ds.DeleteSpecification?.FromClause;
                return null;
            }

            private void CollectFromClause(TableReference tableRef, LocalSymbols local)
            {
                if (tableRef == null) return;

                if (tableRef is NamedTableReference ntr)
                {
                    var alias = ntr.Alias?.Value;
                    var sobj = ntr.SchemaObject;
                    var tref = new TableRef
                    {
                        Database = GetIndex(sobj, 0),
                        Schema = GetIndex(sobj, 1),
                        Name = GetIndex(sobj, sobj?.Identifiers.Count - 1 ?? 0)
                    };
                    if (tref.Name == null)
                    {
                        // 单段时 Name 应取第一个
                        tref.Name = GetIndex(sobj, 0);
                        tref.Schema = null;
                        tref.Database = null;
                    }
                    if (!string.IsNullOrEmpty(alias))
                    {
                        local.Aliases[alias] = tref;
                    }
                    // 同时登记表名本身作为别名（支持 "dbo.t." 和 "t."）
                    if (!string.IsNullOrEmpty(tref.Name))
                    {
                        local.Aliases[tref.Name] = tref;
                        // 三段名时也登记全名
                        if (!string.IsNullOrEmpty(tref.Database))
                        {
                            local.Aliases[tref.Database + "." + tref.Schema + "." + tref.Name] = tref;
                        }
                    }
                }
                else if (tableRef is JoinTableReference jtr)
                {
                    CollectFromClause(jtr.FirstTableReference, local);
                    CollectFromClause(jtr.SecondTableReference, local);
                }
                else if (tableRef is QualifiedJoin qj)
                {
                    CollectFromClause(qj.FirstTableReference, local);
                    CollectFromClause(qj.SecondTableReference, local);
                }
            }

            private static string GetIndex(SchemaObjectName sobj, int fromEnd)
            {
                if (sobj?.Identifiers == null || sobj.Identifiers.Count == 0) return null;
                int count = sobj.Identifiers.Count;
                if (fromEnd < 0 || fromEnd >= count) return null;
                return sobj.Identifiers[fromEnd].Value;
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

            private CompletionContext GetContext(List<TSqlParserToken> tokens, int localOffset, FromObjectNameContext fromName, out string prefix)
            {
                prefix = ExtractPrefix(tokens, localOffset);

                if (fromName != null && fromName.InFromClause)
                {
                    prefix = fromName.Partial ?? string.Empty;
                    return CompletionContext.FromClause;
                }

                // 找光标前最近的非空白 token
                int caretTokenIndex = FindTokenIndexBefore(tokens, localOffset);
                if (caretTokenIndex < 0)
                {
                    return CompletionContext.BatchStart;
                }

                var prev = PreviousSignificantToken(tokens, caretTokenIndex);

                // 成员访问：前一个 token 是 "."
                if (prev != null && prev.TokenType == TSqlTokenType.Dot)
                {
                    // "." 前的标识符
                    var beforeDot = PreviousSignificantToken(tokens, tokens.IndexOf(prev));
                    if (beforeDot != null && IsIdentifierLike(beforeDot))
                    {
                        return CompletionContext.MemberAccess;
                    }
                    return CompletionContext.LocalMemberAccess;
                }

                // @ 触发本地变量
                if (prev != null && prev.TokenType == TSqlTokenType.Variable)
                {
                    return CompletionContext.LocalVariable;
                }

                // 前一个 token 是标识符（正在输入词），再往前找关键字
                var keywordToken = prev;
                if (IsIdentifierLike(prev))
                {
                    keywordToken = PreviousSignificantToken(tokens, tokens.IndexOf(prev));
                }

                string kw = keywordToken?.Text?.ToUpperInvariant();

                switch (kw)
                {
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
                    case ",":
                        return CompletionContext.FromClause;
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

            /// <summary>新行/分号后行首正在输入语句关键字前缀（如 s → SELECT）。</summary>
            private bool IsAtLineStartTypingKeyword(List<TSqlParserToken> tokens, int localOffset, int caretTokenIndex)
            {
                if (caretTokenIndex < 0) return false;
                var cur = tokens[caretTokenIndex];
                if (!IsIdentifierLike(cur)) return false;
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
                if (!IsIdentifierLike(cur)) return false;
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
                else if (cur != null && IsIdentifierLike(cur) && cur.Offset + cur.Text.Length >= localOffset)
                {
                    partial = cur.Text.Substring(0, Math.Max(0, localOffset - cur.Offset));
                    ctx.PartialStartOffset = cur.Offset;
                    i--;
                }
                else if (cur != null && IsIdentifierLike(cur))
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
                        if (i >= 0 && IsIdentifierLike(tokens[i]))
                        {
                            parts.Insert(0, UnbracketIdentifier(tokens[i].Text));
                            i--;
                        }
                        afterDot = true;
                        continue;
                    }

                    if (IsIdentifierLike(tokens[i]))
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
                        text == "INTO" || text == "UPDATE" || text == "DELETE" || text == "INSERT")
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
                if (fromName != null && fromName.InFromClause)
                {
                    if (fromName.PartialStartOffset >= 0)
                        return batchStart + fromName.PartialStartOffset;
                    if (fromName.AfterDot && string.IsNullOrEmpty(fromName.Partial))
                        return batchStart + localOffset;
                }

                if (!string.IsNullOrEmpty(prefix))
                {
                    int idx = FindTokenIndexBefore(tokens, localOffset);
                    if (idx >= 0)
                    {
                        var t = tokens[idx];
                        if (t != null && IsIdentifierLike(t) && t.Offset + t.Text.Length >= localOffset)
                            return batchStart + t.Offset;
                    }
                }
                return batchStart + localOffset;
            }

            private static string BracketIdent(string name)
            {
                if (string.IsNullOrEmpty(name)) return name;
                if (name.StartsWith("[")) return name;
                return "[" + name + "]";
            }

            private string BuildTableInsertText(FromObjectNameContext fromName, DatabaseObjectInfo obj, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                string inner = obj.BracketedName;
                string tableOnly = "[" + obj.Name + "]";
                if (fromName == null || !fromName.InFromClause || obj == null)
                    return inner;

                int segCount = fromName.Segments?.Count ?? 0;

                // 文档中已有库/架构前缀（.. 或 . 后），只插入对象名部分
                if (fromName.AfterDot || fromName.PartialStartOffset >= 0)
                {
                    // db.. 语法省略默认架构 dbo，只补表名
                    if (fromName.UsesDoubleDot)
                        return tableOnly;
                    if (segCount >= 2)
                    {
                        return tableOnly;
                    }
                    if (segCount == 1)
                    {
                        string first = fromName.Segments[0];
                        if (IsDatabaseName(connInfo, first))
                        {
                            return inner;
                        }
                        return tableOnly;
                    }
                    return inner;
                }

                if (segCount == 0)
                    return inner;

                string dbOrSchema = fromName.Segments[0];
                bool isDb = IsDatabaseName(connInfo, dbOrSchema);

                if (isDb && fromName.UsesDoubleDot)
                    return BracketIdent(dbOrSchema) + ".." + tableOnly;

                if (isDb && segCount >= 2)
                    return BracketIdent(dbOrSchema) + "." + BracketIdent(fromName.Segments[1]) + "." + tableOnly;

                if (isDb)
                    return BracketIdent(dbOrSchema) + "." + inner;

                if (segCount == 1)
                    return BracketIdent(dbOrSchema) + "." + tableOnly;

                return inner;
            }

            private static string UnbracketIdentifier(string text)
            {
                if (string.IsNullOrEmpty(text)) return string.Empty;
                return text.Trim('[', ']', '"');
            }

            private bool IsDatabaseName(ScriptFactoryAccess.ConnectionInfo connInfo, string name)
            {
                if (connInfo == null || string.IsNullOrEmpty(name)) return false;
                try
                {
                    var dbs = ScriptFactoryAccess.GetDatabases(connInfo);
                    return dbs != null && dbs.Any(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            }

            private string ExtractPrefix(List<TSqlParserToken> tokens, int localOffset)
            {
                int idx = FindTokenIndexBefore(tokens, localOffset);
                if (idx < 0) return string.Empty;
                var t = tokens[idx];
                if (t == null) return string.Empty;

                if (IsIdentifierLike(t) && t.Offset + t.Text.Length >= localOffset)
                {
                    return t.Text ?? string.Empty;
                }
                return string.Empty;
            }

            private int FindTokenIndexBefore(List<TSqlParserToken> tokens, int offset)
            {
                for (int i = tokens.Count - 1; i >= 0; i--)
                {
                    var t = tokens[i];
                    if (t == null) continue;
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
                        if (settings.includeKeywords) AddKeywords(items, TopLevelKeywords);
                        break;

                    case CompletionContext.AfterCreate:
                        if (settings.includeKeywords) AddKeywords(items, CreateObjectKeywords);
                        break;

                    case CompletionContext.AfterAlter:
                        if (settings.includeKeywords) AddKeywords(items, AlterObjectKeywords);
                        break;

                    case CompletionContext.FromClause:
                        AddFromClauseItems(items, fromName, catalog, settings, connInfo);
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
                        AddColumnsAndFunctions(items, catalog, local, settings);
                        break;

                    case CompletionContext.WhereClause:
                    case CompletionContext.OrderByGroupBy:
                    case CompletionContext.UpdateSet:
                        AddColumnsFromFromAliases(items, catalog, local, settings);
                        if (settings.includeKeywords) AddKeywords(items, OperatorKeywords);
                        break;

                    case CompletionContext.AfterExec:
                        AddProceduresAndScalarFunctions(items, catalog, settings);
                        break;

                    case CompletionContext.MemberAccess:
                        AddMemberColumns(items, prefix, catalog, local, settings);
                        break;

                    case CompletionContext.LocalMemberAccess:
                        AddLocalMemberColumnsFromPrefix(items, prefix, local, settings);
                        break;

                    case CompletionContext.LocalVariable:
                        AddLocalVariables(items, local, settings);
                        break;

                    case CompletionContext.AfterUse:
                        AddDatabases(items, connInfo);
                        break;
                }

                return items;
            }

            private void AddFromClauseItems(
                List<CompletionItem> items,
                FromObjectNameContext fromName,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (fromName == null || !fromName.InFromClause)
                {
                    AddTablesViewsAndRoutines(items, catalog, settings, includeTableFunctions: true);
                    AddDatabases(items, connInfo);
                    return;
                }

                int segCount = fromName.Segments?.Count ?? 0;
                bool qualified = segCount > 0 || fromName.AfterDot;
                if (segCount == 0)
                {
                    AddTablesViewsAndRoutines(items, catalog, settings, includeTableFunctions: true, fromName: fromName, connInfo: connInfo);
                    AddDatabases(items, connInfo);
                    return;
                }

                if (fromName.AfterDot)
                {
                    if (segCount == 1 && IsDatabaseName(connInfo, fromName.Segments[0]))
                    {
                        var remote = MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo, fromName.Segments[0]);
                        AddTablesInSchema(items, remote, null, settings, fromName, connInfo, includeSystemObjects: false);
                        return;
                    }
                    if (segCount == 1)
                    {
                        AddTablesInSchema(items, catalog, fromName.Segments[0], settings, fromName, connInfo, includeSystemObjects: false);
                        return;
                    }
                    if (segCount == 2 && IsDatabaseName(connInfo, fromName.Segments[0]))
                    {
                        var remote = MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo, fromName.Segments[0]);
                        AddTablesInSchema(items, remote, fromName.Segments[1], settings, fromName, connInfo, includeSystemObjects: false);
                        return;
                    }
                    if (segCount == 2)
                    {
                        AddTablesInSchema(items, catalog, fromName.Segments[0], settings, fromName, connInfo, includeSystemObjects: false);
                        return;
                    }
                }

                if (segCount == 1 && IsDatabaseName(connInfo, fromName.Segments[0]))
                {
                    var remote = MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo, fromName.Segments[0]);
                    AddTablesInSchema(items, remote, null, settings, fromName, connInfo, includeSystemObjects: false);
                    return;
                }

                if (segCount == 2 && IsDatabaseName(connInfo, fromName.Segments[0]))
                {
                    var remote = MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo, fromName.Segments[0]);
                    AddTablesInSchema(items, remote, fromName.Segments[1], settings, fromName, connInfo, includeSystemObjects: false);
                    return;
                }

                if (segCount == 1)
                {
                    AddTablesInSchema(items, catalog, fromName.Segments[0], settings, fromName, connInfo, includeSystemObjects: false);
                    return;
                }

                AddTablesViewsAndRoutines(items, catalog, settings, includeTableFunctions: true, fromName: fromName, connInfo: connInfo);
                if (!qualified)
                    AddDatabases(items, connInfo);
            }

            private void AddSchemasForDatabase(List<CompletionItem> items, ScriptFactoryAccess.ConnectionInfo connInfo, string database)
            {
                var cat = MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo, database);
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

            private void AddTablesInSchema(List<CompletionItem> items, MetadataCatalog catalog, string schema, IntelliSenseSettings settings, FromObjectNameContext fromName, ScriptFactoryAccess.ConnectionInfo connInfo, bool includeSystemObjects)
            {
                if (catalog == null) return;
                foreach (var t in catalog.Tables)
                {
                    if (string.IsNullOrEmpty(schema) || string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase))
                    {
                        string insert = BuildTableInsertText(fromName, t, connInfo);
                        items.Add(new CompletionItem(t.QualifiedName, insert, CompletionKind.Table, BuildTableDescription(t)));
                    }
                }
                foreach (var v in catalog.Views)
                {
                    if (string.IsNullOrEmpty(schema) || string.Equals(v.Schema, schema, StringComparison.OrdinalIgnoreCase))
                    {
                        string insert = BuildTableInsertText(fromName, v, connInfo);
                        items.Add(new CompletionItem(v.QualifiedName, insert, CompletionKind.View, BuildTableDescription(v)));
                    }
                }
                if (includeSystemObjects && settings.includeSystemObjects && string.IsNullOrEmpty(schema))
                {
                    AddSystemObjects(items);
                }
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

            private void AddTablesViewsAndRoutines(List<CompletionItem> items, MetadataCatalog catalog, IntelliSenseSettings settings, bool includeTableFunctions, FromObjectNameContext fromName = null, ScriptFactoryAccess.ConnectionInfo connInfo = null)
            {
                AddTablesAndViews(items, catalog, settings, fromName, connInfo);
                if (catalog == null) return;
                foreach (var s in catalog.Synonyms)
                {
                    items.Add(new CompletionItem(s.QualifiedName, s.BracketedName, CompletionKind.Synonym, "同义词"));
                }
                if (includeTableFunctions)
                {
                    foreach (var f in catalog.TableFunctions)
                    {
                        string insert = fromName != null ? BuildTableInsertText(fromName, f, connInfo) : f.BracketedName;
                        items.Add(new CompletionItem(f.QualifiedName, insert, CompletionKind.TableFunction,
                            BuildRoutineDescription(f)));
                    }
                }
            }

            private void AddTablesAndViews(List<CompletionItem> items, MetadataCatalog catalog, IntelliSenseSettings settings, FromObjectNameContext fromName = null, ScriptFactoryAccess.ConnectionInfo connInfo = null)
            {
                if (catalog == null) return;
                foreach (var t in catalog.Tables)
                {
                    string insert = fromName != null ? BuildTableInsertText(fromName, t, connInfo) : t.BracketedName;
                    items.Add(new CompletionItem(t.QualifiedName, insert, CompletionKind.Table,
                        BuildTableDescription(t)));
                }
                foreach (var v in catalog.Views)
                {
                    string insert = fromName != null ? BuildTableInsertText(fromName, v, connInfo) : v.BracketedName;
                    items.Add(new CompletionItem(v.QualifiedName, insert, CompletionKind.View,
                        BuildTableDescription(v)));
                }
                if (settings.includeSystemObjects && fromName == null)
                {
                    AddSystemObjects(items);
                }
            }

            private void AddColumnsAndFunctions(List<CompletionItem> items, MetadataCatalog catalog, LocalSymbols local, IntelliSenseSettings settings)
            {
                // 列：来自 FROM 别名表 + CTE + 临时表 + 表变量
                AddColumnsFromFromAliases(items, catalog, local, settings);
                AddLocalColumns(items, local, settings);
                // 星号
                items.Add(new CompletionItem("*", "*", CompletionKind.Column, "所有列"));
                // 内建函数（常用）
                if (settings.includeKeywords)
                {
                    foreach (var fn in BuiltInFunctions)
                    {
                        items.Add(new CompletionItem(fn, fn + "(", CompletionKind.ScalarFunction, "内建函数"));
                    }
                    AddKeywords(items, new[] { "CASE" });
                }
            }

            private void AddColumnsFromFromAliases(List<CompletionItem> items, MetadataCatalog catalog, LocalSymbols local, IntelliSenseSettings settings)
            {
                if (catalog == null) return;
                foreach (var kv in local.Aliases)
                {
                    var tref = kv.Value;
                    var tcol = catalog.FindTableOrView(tref.Schema, tref.Name);
                    if (tcol != null)
                    {
                        foreach (var col in tcol.Columns)
                        {
                            items.Add(new CompletionItem(col.Name, col.BracketedName, CompletionKind.Column,
                                BuildColumnDescription(col)));
                        }
                    }
                }
            }

            private void AddLocalColumns(List<CompletionItem> items, LocalSymbols local, IntelliSenseSettings settings)
            {
                foreach (var cte in local.Ctes)
                {
                    foreach (var col in cte.ColumnNames)
                    {
                        items.Add(new CompletionItem(col, "[" + col + "]", CompletionKind.Column, "CTE 列: " + cte.Name));
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
                            items.Add(new CompletionItem(col, "[" + col + "]", CompletionKind.Column, "临时表列: " + tt.Name));
                        }
                    }
                }
                if (settings.includeLocalVariables)
                {
                    foreach (var tv in local.TableVariables)
                    {
                        foreach (var col in tv.ColumnNames)
                        {
                            items.Add(new CompletionItem(col, "[" + col + "]", CompletionKind.Column, "表变量列: " + tv.Name));
                        }
                    }
                }
            }

            private void AddProceduresAndScalarFunctions(List<CompletionItem> items, MetadataCatalog catalog, IntelliSenseSettings settings)
            {
                if (catalog == null) return;
                foreach (var p in catalog.Procedures)
                {
                    items.Add(new CompletionItem(p.QualifiedName, p.BracketedName, CompletionKind.Procedure,
                        BuildRoutineDescription(p)));
                }
                foreach (var f in catalog.ScalarFunctions)
                {
                    items.Add(new CompletionItem(f.QualifiedName, f.BracketedName, CompletionKind.ScalarFunction,
                        BuildRoutineDescription(f)));
                }
                if (settings.includeSystemObjects)
                {
                    foreach (var s in MetadataCatalogService.CommonSystemObjects)
                    {
                        if (s.StartsWith("sp_"))
                        {
                            items.Add(new CompletionItem(s, s, CompletionKind.Procedure, "系统过程"));
                        }
                    }
                }
            }

            private void AddMemberColumns(List<CompletionItem> items, string prefix, MetadataCatalog catalog, LocalSymbols local, IntelliSenseSettings settings)
            {
                // prefix 形如 "alias." 或 "dbo." 或 "t."，取 "." 前的标识符
                string owner = GetOwnerBeforeDot(prefix);
                if (string.IsNullOrEmpty(owner)) return;

                // 本地对象优先
                if (AddLocalMemberColumns(items, owner, local, settings))
                {
                    return;
                }

                if (catalog == null) return;

                // 别名映射
                TableRef tref;
                if (local.Aliases.TryGetValue(owner, out tref))
                {
                    var tcol = catalog.FindTableOrView(tref.Schema, tref.Name);
                    if (tcol != null)
                    {
                        foreach (var col in tcol.Columns)
                        {
                            items.Add(new CompletionItem(col.Name, col.BracketedName, CompletionKind.Column,
                                BuildColumnDescription(col)));
                        }
                    }
                    return;
                }

                // schema 名 → 列出该 schema 下表
                var tablesOfSchema = new List<(TableColumnInfo Obj, CompletionKind Kind)>();
                tablesOfSchema.AddRange(catalog.Tables.Where(t => string.Equals(t.Schema, owner, StringComparison.OrdinalIgnoreCase)).Select(t => (t, CompletionKind.Table)));
                tablesOfSchema.AddRange(catalog.Views.Where(v => string.Equals(v.Schema, owner, StringComparison.OrdinalIgnoreCase)).Select(v => (v, CompletionKind.View)));
                if (tablesOfSchema.Count > 0)
                {
                    foreach (var entry in tablesOfSchema)
                    {
                        items.Add(new CompletionItem(entry.Obj.Name, entry.Obj.BracketedName, entry.Kind, BuildTableDescription(entry.Obj)));
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
                        items.Add(new CompletionItem(col, "[" + col + "]", CompletionKind.Column, "CTE 列: " + cte.Name));
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
                            items.Add(new CompletionItem(col, "[" + col + "]", CompletionKind.Column, "临时表列: " + tt.Name));
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
                            items.Add(new CompletionItem(col, "[" + col + "]", CompletionKind.Column, "表变量列: " + tv.Name));
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

            private void AddDatabases(List<CompletionItem> items, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (connInfo == null) return;
                try
                {
                    var dbs = ScriptFactoryAccess.GetDatabases(connInfo);
                    foreach (var db in dbs)
                    {
                        items.Add(new CompletionItem(db, "[" + db + "]", CompletionKind.Database, "数据库"));
                    }
                }
                catch
                {
                    // 取库列表失败 → 静默
                }
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
                    case CompletionKind.Snippet: return -2;
                    case CompletionKind.Table: return 0;
                    case CompletionKind.View: return 1;
                    case CompletionKind.Synonym: return 2;
                    case CompletionKind.TableFunction: return 3;
                    case CompletionKind.Schema: return 4;
                    case CompletionKind.Procedure: return 5;
                    case CompletionKind.ScalarFunction: return 6;
                    case CompletionKind.Database: return 8;
                    case CompletionKind.Keyword: return 9;
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
                string name = item?.DisplayText ?? string.Empty;
                if (item != null && item.Kind == CompletionKind.Snippet &&
                    !string.IsNullOrEmpty(item.SnippetPrefix))
                {
                    name = item.SnippetPrefix;
                }
                else if (item != null &&
                         (item.Kind == CompletionKind.Table || item.Kind == CompletionKind.View ||
                          item.Kind == CompletionKind.Synonym || item.Kind == CompletionKind.TableFunction))
                {
                    name = GetMatchableName(name);
                }

                if (name.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return item != null && item.Kind == CompletionKind.Snippet ? 120 : 100;
                }
                if (name.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase)) return 100;
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

            private static string BuildTableDescription(TableColumnInfo t)
            {
                var sb = new StringBuilder();
                sb.Append("表/视图: ").AppendLine(t.QualifiedName);
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
                "EXEC", "USE", "DECLARE", "SET", "IF", "BEGIN", "END", "TRUNCATE", "MERGE",
                "GO"
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

            public static readonly string[] BuiltInFunctions =
            {
                "COUNT", "SUM", "AVG", "MIN", "MAX", "GETDATE", "GETUTCDATE", "SYSDATETIME",
                "DATEADD", "DATEDIFF", "DATENAME", "DATEPART", "YEAR", "MONTH", "DAY",
                "LEN", "DATALENGTH", "SUBSTRING", "LEFT", "RIGHT", "CHARINDEX", "PATINDEX",
                "REPLACE", "STUFF", "UPPER", "LOWER", "LTRIM", "RTRIM", "CONCAT",
                "COALESCE", "NULLIF", "ISNULL", "CAST", "CONVERT", "ROW_NUMBER", "RANK",
                "DENSE_RANK", "NTILE", "LEAD", "LAG", "NEWID", "SCOPE_IDENTITY", "@@ROWCOUNT",
                "@@IDENTITY", "DB_NAME", "OBJECT_ID", "OBJECT_NAME", "USER_NAME", "SUSER_NAME"
            };

            #endregion
        }
    }
}
