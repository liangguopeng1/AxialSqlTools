using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AxialSqlTools
{
    namespace IntelliSense
    {
        /// <summary>
        /// ScriptDOM 解析 + 上下文判定 + 候选生成。
        /// 按 GO 分批；大批次再切到光标所在语句。解析结果进程内共享缓存。
        /// 任何解析失败返回 Unknown 上下文，不抛异常。
        /// </summary>
        public partial class CompletionEngine
        {
            private TSqlScript _lastScript;
            private List<TSqlParserToken> _lastTokens;

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
                    if (!TryGetParseSlice(fullText, caretOffset, out batchText, out batchStart))
                    {
                        return result;
                    }

                    var script = ParseCached(batchText);
                    int localOffset = caretOffset - batchStart;
                    if (localOffset < 0) localOffset = 0;

                    // 用全文判断注释：切片会丢掉前面未闭合的 /*
                    if (IsInsideStringOrComment(fullText, caretOffset))
                        return result;

                    string useDb = GetActiveUseDatabase(fullText, caretOffset);
                    if (!string.IsNullOrEmpty(useDb) &&
                        (catalog == null || !string.Equals(catalog.Database, useDb, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (connInfo != null)
                        {
                            var useCat = MetadataCatalogService.Instance.GetCachedCatalog(connInfo, useDb);
                            if (useCat != null)
                                catalog = useCat;
                        }
                    }

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
                        var localEarly = BuildLocalSymbols(script, fullText, caretOffset);
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

                    // FROM/JOIN 原文兜底：未闭合 [xxx 时 ScriptDom token 流为空，必须走原文
                    FromObjectNameContext fromNameRaw;
                    bool hasFromRaw = TryParseFromObjectNameRaw(batchText, localOffset, out fromNameRaw);

                    if (script == null || ((_lastTokens == null || _lastTokens.Count == 0) && hasFromRaw))
                    {
                        if (hasFromRaw)
                        {
                            string fromPrefix = fromNameRaw.Partial ?? string.Empty;
                            if (string.IsNullOrEmpty(fromPrefix)) fromPrefix = rawPrefix;
                            result.Context = CompletionContext.FromClause;
                            result.Prefix = fromPrefix;
                            result.Items = BuildItems(CompletionContext.FromClause, fromPrefix, fromNameRaw, BuildLocalSymbols(null, fullText, caretOffset), catalog, settings, connInfo);
                            result.Items = FilterAndSort(result.Items, fromPrefix, settings, connInfo, fromNameRaw);
                            result.ReplaceStartOffset = ComputeReplaceStartOffset(batchStart, localOffset, fromPrefix, fromNameRaw, _lastTokens);
                            result.ReplaceEndOffset = caretOffset;
                            return result;
                        }
                        if (script == null)
                        {
                            if ((afterStmtBreak || atLineStart) && !string.IsNullOrEmpty(rawPrefix))
                            {
                                result.Context = CompletionContext.BatchStart;
                                result.Prefix = rawPrefix;
                                result.Items = BuildItems(CompletionContext.BatchStart, rawPrefix, null, BuildLocalSymbols(null, fullText, caretOffset), catalog, settings, connInfo);
                                result.Items = FilterAndSort(result.Items, rawPrefix, settings, connInfo, null);
                                result.ReplaceStartOffset = caretOffset - rawPrefix.Length;
                                result.ReplaceEndOffset = caretOffset;
                            }
                            return result;
                        }
                    }

                    var tokens = _lastTokens ?? new List<TSqlParserToken>();
                    var local = BuildLocalSymbols(script, fullText, caretOffset);
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

                    // 行首输入：多表 JOIN 后写 in/where，或 WHERE 后写 ord/gr，或新语句写 s
                    int caretTokenIndex = FindTokenIndexForPrefix(tokens, localOffset);
                    if (atLineStart && !string.IsNullOrEmpty(rawPrefix))
                    {
                        int beforeIdx = FindTokenIndexBefore(tokens, localOffset);
                        // 跳过 AND/OR，区分 JOIN ON 后续写 vs WHERE 后写
                        string majorKw = beforeIdx >= 0
                            ? FindNearestMajorClauseKeyword(tokens, beforeIdx, localOffset)
                            : null;
                        bool inJoinOn = majorKw == "ON";
                        bool pastFromClause = majorKw == "WHERE" || majorKw == "HAVING"
                            || majorKw == "GROUP" || majorKw == "ORDER"
                            || majorKw == "UNION" || majorKw == "EXCEPT" || majorKw == "INTERSECT";
                        if (inJoinOn && !afterStmtBreak)
                        {
                            // ON 后 ss/se 开新语句；in/wh/le 仍续写 JOIN/WHERE
                            if (LooksLikeNewBatchPrefix(rawPrefix))
                            {
                                result.Context = CompletionContext.BatchStart;
                                result.Prefix = rawPrefix;
                                result.Items = BuildItems(CompletionContext.BatchStart, rawPrefix, null, local, catalog, settings, connInfo);
                                result.Items = FilterAndSort(result.Items, rawPrefix, settings, connInfo, null);
                                result.ReplaceStartOffset = caretOffset - rawPrefix.Length;
                                result.ReplaceEndOffset = caretOffset;
                                return result;
                            }
                            // ON 条件后仍可 INNER JOIN / WHERE，勿当成 WhereClause（否则 in→IN 不是 INNER JOIN）
                            bool hasFrom = HasFromKeywordBefore(tokens, localOffset) || hasFromRaw;
                            result.Context = CompletionContext.FromClause;
                            result.Prefix = rawPrefix;
                            FromObjectNameContext fromNameOn = ParseFromObjectName(tokens, localOffset);
                            if (!fromNameOn.InFromClause)
                            {
                                fromNameOn = hasFromRaw
                                    ? fromNameRaw
                                    : new FromObjectNameContext { InFromClause = true, Partial = rawPrefix };
                            }
                            else if (string.IsNullOrEmpty(fromNameOn.Partial))
                                fromNameOn.Partial = rawPrefix;
                            if (!fromNameOn.InFromClause && hasFrom)
                                fromNameOn.InFromClause = true;
                            result.Items = BuildItems(CompletionContext.FromClause, rawPrefix, fromNameOn, local, catalog, settings, connInfo);
                            result.Items = FilterAndSort(result.Items, rawPrefix, settings, connInfo, fromNameOn);
                            result.ReplaceStartOffset = caretOffset - rawPrefix.Length;
                            result.ReplaceEndOffset = caretOffset;
                            return result;
                        }
                        if (pastFromClause && !afterStmtBreak)
                        {
                            // 无分号时行首常开新语句：se→SELECT、ss→ssf；ord/gr/un 仍续写当前句
                            if (LooksLikeNewBatchPrefix(rawPrefix))
                            {
                                result.Context = CompletionContext.BatchStart;
                                result.Prefix = rawPrefix;
                                result.Items = BuildItems(CompletionContext.BatchStart, rawPrefix, null, local, catalog, settings, connInfo);
                                result.Items = FilterAndSort(result.Items, rawPrefix, settings, connInfo, null);
                                result.ReplaceStartOffset = caretOffset - rawPrefix.Length;
                                result.ReplaceEndOffset = caretOffset;
                                return result;
                            }
                            // 勿再走 IsAtLineStartTypingKeyword→BatchStart，否则 ord 不进 WhereClause
                            var pastCtx = (majorKw == "GROUP" || majorKw == "ORDER")
                                ? CompletionContext.OrderByGroupBy
                                : CompletionContext.WhereClause;
                            result.Context = pastCtx;
                            result.Prefix = rawPrefix;
                            result.Items = BuildItems(pastCtx, rawPrefix, null, local, catalog, settings, connInfo);
                            result.Items = FilterAndSort(result.Items, rawPrefix, settings, connInfo, null);
                            result.ReplaceStartOffset = caretOffset - rawPrefix.Length;
                            result.ReplaceEndOffset = caretOffset;
                            return result;
                        }
                        if (!pastFromClause)
                        {
                            bool hasFrom = HasFromKeywordBefore(tokens, localOffset) || hasFromRaw;
                            // FROM/JOIN 表写完后行首：ss/se/upd 开新句；in/wh/le 续写 JOIN/WHERE
                            bool newBatch = LooksLikeNewBatchPrefix(rawPrefix);
                            result.Context = hasFrom && !afterStmtBreak && !newBatch
                                ? CompletionContext.FromClause
                                : CompletionContext.BatchStart;
                            result.Prefix = rawPrefix;
                            FromObjectNameContext fromNameEarly = null;
                            if (result.Context == CompletionContext.FromClause)
                            {
                                // 行首 in/wh 时表名常在上一行：必须用 token 解析拿 Segments，不能只用 Partial 空壳
                                fromNameEarly = ParseFromObjectName(tokens, localOffset);
                                if (!fromNameEarly.InFromClause)
                                {
                                    fromNameEarly = hasFromRaw
                                        ? fromNameRaw
                                        : new FromObjectNameContext { InFromClause = true, Partial = rawPrefix };
                                }
                                else if (string.IsNullOrEmpty(fromNameEarly.Partial))
                                    fromNameEarly.Partial = rawPrefix;
                            }
                            if (result.Context == CompletionContext.FromClause)
                            {
                                result.Items = BuildItems(CompletionContext.FromClause, rawPrefix, fromNameEarly, local, catalog, settings, connInfo);
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
                    }

                    // FROM 多段名（库.表 / 库.架构.表）须先于成员访问，否则 jichushuju.cip 会被当成别名.列
                    var fromName = ParseFromObjectName(tokens, localOffset);
                    // 未闭合 [ 时 token 缺 partial；仅当 token 也认为在 FROM 内，或 token 完全没认出 FROM 时才用原文补齐
                    if (hasFromRaw)
                    {
                        // ScriptDom 把 192. 收成 Numeric，无独立 Dot；IP 链接服务器以原文解析为准
                        if (fromNameRaw.InFromClause && JoinNumericFromSegments(fromNameRaw) != null)
                            fromName = fromNameRaw;
                        else if (!fromName.InFromClause)
                            fromName = fromNameRaw;
                        else if (string.IsNullOrEmpty(fromName.Partial) && !string.IsNullOrEmpty(fromNameRaw.Partial))
                        {
                            fromName.Partial = fromNameRaw.Partial;
                            fromName.PartialStartOffset = fromNameRaw.PartialStartOffset;
                            if (fromNameRaw.Segments != null && fromNameRaw.Segments.Count > 0
                                && (fromName.Segments == null || fromName.Segments.Count == 0))
                                fromName.Segments = fromNameRaw.Segments;
                        }
                        fromName.AfterDot = fromName.AfterDot || fromNameRaw.AfterDot;
                        fromName.UsesDoubleDot = fromName.UsesDoubleDot || fromNameRaw.UsesDoubleDot;
                        fromName.ExcessiveDots = fromName.ExcessiveDots || fromNameRaw.ExcessiveDots;
                        if (fromName.NameStartOffset < 0)
                            fromName.NameStartOffset = fromNameRaw.NameStartOffset;
                    }
                    bool fromQualifiedName = fromName != null && fromName.InFromClause
                        && (fromName.AfterDot || (fromName.Segments != null && fromName.Segments.Count > 0)
                            || !string.IsNullOrEmpty(fromName.Partial));

                    // JOIN ON / WHERE 多条件：and dd.| 是别名.列，不能被 FROM 限定名抢走
                    if (TryGetMemberAccessPrefix(tokens, localOffset, out string memberPrefix))
                    {
                        string owner = GetOwnerBeforeDot(memberPrefix);
                        if (!string.IsNullOrEmpty(owner) && !local.Aliases.ContainsKey(owner))
                            CollectAliasesFromTokens(tokens, localOffset, local);

                        bool aliasMember = !string.IsNullOrEmpty(owner) && local.Aliases.ContainsKey(owner);
                        int memBeforeIdx = FindTokenIndexBefore(tokens, localOffset);
                        string majorForMember = memBeforeIdx >= 0
                            ? FindNearestMajorClauseKeyword(tokens, memBeforeIdx, localOffset)
                            : null;
                        bool inExprClause = majorForMember == "ON" || majorForMember == "WHERE"
                            || majorForMember == "HAVING" || majorForMember == "GROUP"
                            || majorForMember == "ORDER" || majorForMember == "SET";
                        bool inDmlTargetName = majorForMember == "UPDATE" || majorForMember == "DELETE"
                            || majorForMember == "INSERT" || majorForMember == "INTO";
                        bool ipLinkedServer = JoinNumericFromSegments(fromName) != null
                            || (fromName != null && fromName.UsesDoubleDot);

                        if (!inDmlTargetName && !ipLinkedServer && (aliasMember || inExprClause || !fromQualifiedName))
                        {
                            result.Context = CompletionContext.MemberAccess;
                            result.Prefix = memberPrefix;
                            result.Items = BuildItems(CompletionContext.MemberAccess, memberPrefix, null, local, catalog, settings, connInfo);
                            result.Items = FilterAndSort(result.Items, memberPrefix, settings, connInfo, null);
                            result.ReplaceStartOffset = ComputeReplaceStartOffset(batchStart, localOffset, memberPrefix, null, tokens);
                            result.ReplaceEndOffset = caretOffset;
                            return result;
                        }
                    }

                    if (caretTokenIndex >= 0 && IsAtLineStartTypingKeyword(tokens, localOffset, caretTokenIndex)
                        && (afterStmtBreak || !HasFromKeywordBefore(tokens, localOffset)))
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

            /// <summary>超过此长度的 GO 批次只解析光标所在语句，避免整页几千行 ScriptDOM。</summary>
            internal const int LargeBatchParseChars = 24 * 1024;

            private static readonly object ParseGate = new object();
            private static readonly TSql170Parser SharedParser = new TSql170Parser(true);
            private static string SharedSlice;
            private static TSqlScript SharedScript;
            private static List<TSqlParserToken> SharedTokens;
            private static bool SharedHasAst;

            internal static bool TryGetParseSlice(string fullText, int caretOffset, out string slice, out int sliceStart)
            {
                slice = fullText;
                sliceStart = 0;
                if (string.IsNullOrEmpty(fullText)) return false;
                if (!TryGetBatchAt(fullText, caretOffset, out string batch, out int batchStart))
                    return false;
                if (batch.Length <= LargeBatchParseChars)
                {
                    slice = batch;
                    sliceStart = batchStart;
                    return true;
                }
                int localCaret = caretOffset - batchStart;
                if (localCaret < 0) localCaret = 0;
                if (localCaret > batch.Length) localCaret = batch.Length;
                FindCurrentStatementBounds(batch, localCaret, out int stmtStart, out int stmtEnd);
                if (stmtStart < 0) stmtStart = 0;
                if (stmtEnd > batch.Length) stmtEnd = batch.Length;
                if (stmtEnd < stmtStart) stmtEnd = stmtStart;
                sliceStart = batchStart + stmtStart;
                slice = batch.Substring(stmtStart, stmtEnd - stmtStart);
                return true;
            }

            private static bool TryGetBatchAt(string fullText, int caretOffset, out string batchText, out int batchStart)
            {
                batchText = fullText;
                batchStart = 0;
                if (fullText.IndexOf("GO", StringComparison.OrdinalIgnoreCase) < 0)
                    return true;

                var matches = GoBatchSplit.Matches(fullText);
                if (matches.Count == 0)
                    return true;

                int start = 0;
                foreach (Match m in matches)
                {
                    if (m.Index > caretOffset)
                        break;
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

            private static void FindCurrentStatementBounds(string text, int caret, out int stmtStart, out int stmtEnd)
            {
                stmtStart = 0;
                stmtEnd = text.Length;
                if (string.IsNullOrEmpty(text))
                    return;
                int n = text.Length;
                int probe = Math.Min(Math.Max(0, caret), n);
                bool inLineComment = false;
                bool inBlockComment = false;
                bool inString = false;
                int paren = 0;
                int lastStmt = 0;
                bool afterSetOp = false;
                bool afterCte = false;
                bool afterInsert = false;
                int i = 0;
                while (i < probe)
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
                        if (c == '*' && next == '/')
                        {
                            inBlockComment = false;
                            i += 2;
                            continue;
                        }
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
                    if (c == '[')
                    {
                        i++;
                        while (i < n && text[i] != ']') i++;
                        if (i < n) i++;
                        continue;
                    }
                    if (c == '(') { paren++; i++; continue; }
                    if (c == ')') { if (paren > 0) paren--; i++; continue; }
                    if (c == ';' && paren == 0)
                    {
                        lastStmt = i + 1;
                        afterSetOp = false;
                        afterCte = false;
                        afterInsert = false;
                        i++;
                        continue;
                    }
                    if (paren != 0 || !IsIdentChar(c))
                    {
                        i++;
                        continue;
                    }
                    int kwLen;
                    StmtKw kw = MatchStmtKeyword(text, i, n, out kwLen);
                    if (kw == StmtKw.None)
                    {
                        i++;
                        continue;
                    }
                    if (kw == StmtKw.Union || kw == StmtKw.Except || kw == StmtKw.Intersect)
                    {
                        afterSetOp = true;
                        i += kwLen;
                        continue;
                    }
                    if (kw == StmtKw.Values)
                    {
                        afterInsert = false;
                        i += kwLen;
                        continue;
                    }
                    if (kw == StmtKw.With && IsWithTableHint(text, i + kwLen, n))
                    {
                        i += kwLen;
                        continue;
                    }
                    bool continuation = afterSetOp
                        || (afterCte && (kw == StmtKw.Select || kw == StmtKw.Insert || kw == StmtKw.Update || kw == StmtKw.Delete || kw == StmtKw.Merge))
                        || (afterInsert && (kw == StmtKw.Select || kw == StmtKw.Exec));
                    if (!continuation)
                        lastStmt = i;
                    afterSetOp = false;
                    afterCte = kw == StmtKw.With;
                    afterInsert = kw == StmtKw.Insert;
                    i += kwLen;
                }
                stmtStart = lastStmt;
                i = probe;
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
                        if (c == '*' && next == '/')
                        {
                            inBlockComment = false;
                            i += 2;
                            continue;
                        }
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
                    if (c == '[')
                    {
                        i++;
                        while (i < n && text[i] != ']') i++;
                        if (i < n) i++;
                        continue;
                    }
                    if (c == '(') { paren++; i++; continue; }
                    if (c == ')') { if (paren > 0) paren--; i++; continue; }
                    if (c == ';' && paren == 0)
                    {
                        stmtEnd = i;
                        return;
                    }
                    if (paren != 0 || !IsIdentChar(c))
                    {
                        i++;
                        continue;
                    }
                    int kwLen;
                    StmtKw kw = MatchStmtKeyword(text, i, n, out kwLen);
                    if (kw == StmtKw.None)
                    {
                        i++;
                        continue;
                    }
                    if (kw == StmtKw.Union || kw == StmtKw.Except || kw == StmtKw.Intersect)
                    {
                        afterSetOp = true;
                        i += kwLen;
                        continue;
                    }
                    if (kw == StmtKw.Values)
                    {
                        afterInsert = false;
                        i += kwLen;
                        continue;
                    }
                    if (kw == StmtKw.With && IsWithTableHint(text, i + kwLen, n))
                    {
                        i += kwLen;
                        continue;
                    }
                    bool continuation = afterSetOp
                        || (afterCte && (kw == StmtKw.Select || kw == StmtKw.Insert || kw == StmtKw.Update || kw == StmtKw.Delete || kw == StmtKw.Merge))
                        || (afterInsert && (kw == StmtKw.Select || kw == StmtKw.Exec));
                    if (!continuation && i > probe)
                    {
                        stmtEnd = i;
                        return;
                    }
                    afterSetOp = false;
                    afterCte = kw == StmtKw.With;
                    afterInsert = kw == StmtKw.Insert;
                    i += kwLen;
                }
                stmtEnd = n;
            }

            private enum StmtKw
            {
                None, Select, Insert, Update, Delete, Merge, With, Exec, Create, Alter, Drop, Declare, Use, Truncate, Union, Except, Intersect, Values
            }

            private static StmtKw MatchStmtKeyword(string text, int i, int n, out int len)
            {
                len = 0;
                char c = char.ToUpperInvariant(text[i]);
                switch (c)
                {
                    case 'S':
                        if (KeywordAt(text, i, n, "SELECT")) { len = 6; return StmtKw.Select; }
                        break;
                    case 'I':
                        if (KeywordAt(text, i, n, "INSERT")) { len = 6; return StmtKw.Insert; }
                        if (KeywordAt(text, i, n, "INTERSECT")) { len = 9; return StmtKw.Intersect; }
                        break;
                    case 'U':
                        if (KeywordAt(text, i, n, "UPDATE")) { len = 6; return StmtKw.Update; }
                        if (KeywordAt(text, i, n, "USE")) { len = 3; return StmtKw.Use; }
                        if (KeywordAt(text, i, n, "UNION")) { len = 5; return StmtKw.Union; }
                        break;
                    case 'D':
                        if (KeywordAt(text, i, n, "DELETE")) { len = 6; return StmtKw.Delete; }
                        if (KeywordAt(text, i, n, "DROP")) { len = 4; return StmtKw.Drop; }
                        if (KeywordAt(text, i, n, "DECLARE")) { len = 7; return StmtKw.Declare; }
                        break;
                    case 'M':
                        if (KeywordAt(text, i, n, "MERGE")) { len = 5; return StmtKw.Merge; }
                        break;
                    case 'W':
                        if (KeywordAt(text, i, n, "WITH")) { len = 4; return StmtKw.With; }
                        break;
                    case 'E':
                        if (KeywordAt(text, i, n, "EXECUTE")) { len = 7; return StmtKw.Exec; }
                        if (KeywordAt(text, i, n, "EXEC")) { len = 4; return StmtKw.Exec; }
                        if (KeywordAt(text, i, n, "EXCEPT")) { len = 6; return StmtKw.Except; }
                        break;
                    case 'C':
                        if (KeywordAt(text, i, n, "CREATE")) { len = 6; return StmtKw.Create; }
                        break;
                    case 'A':
                        if (KeywordAt(text, i, n, "ALTER")) { len = 5; return StmtKw.Alter; }
                        break;
                    case 'T':
                        if (KeywordAt(text, i, n, "TRUNCATE")) { len = 8; return StmtKw.Truncate; }
                        break;
                    case 'V':
                        if (KeywordAt(text, i, n, "VALUES")) { len = 6; return StmtKw.Values; }
                        break;
                }
                return StmtKw.None;
            }

            private static bool KeywordAt(string text, int i, int n, string upper)
            {
                int len = upper.Length;
                if (i + len > n) return false;
                for (int k = 0; k < len; k++)
                {
                    if (char.ToUpperInvariant(text[i + k]) != upper[k])
                        return false;
                }
                if (i > 0 && IsIdentChar(text[i - 1])) return false;
                if (i + len < n && IsIdentChar(text[i + len])) return false;
                return true;
            }

            private static bool IsWithTableHint(string text, int afterWith, int n)
            {
                int j = afterWith;
                while (j < n && char.IsWhiteSpace(text[j])) j++;
                return j < n && text[j] == '(';
            }

            internal static void GetTokensForSlice(string slice, out List<TSqlParserToken> tokens)
            {
                tokens = new List<TSqlParserToken>();
                if (string.IsNullOrEmpty(slice)) return;
                lock (ParseGate)
                {
                    if (string.Equals(SharedSlice, slice, StringComparison.Ordinal) && SharedTokens != null)
                    {
                        tokens = SharedTokens;
                        return;
                    }
                    try
                    {
                        using (var reader = new StringReader(slice))
                        {
                            var ts = SharedParser.GetTokenStream(reader, out var _);
                            tokens = ts?.ToList() ?? new List<TSqlParserToken>();
                        }
                    }
                    catch
                    {
                        tokens = new List<TSqlParserToken>();
                    }
                    SharedSlice = slice;
                    SharedTokens = tokens;
                    SharedScript = null;
                    SharedHasAst = false;
                }
            }

            internal static void ParseSlice(string slice, out TSqlScript script, out List<TSqlParserToken> tokens)
            {
                script = null;
                tokens = new List<TSqlParserToken>();
                if (string.IsNullOrEmpty(slice)) return;
                lock (ParseGate)
                {
                    if (string.Equals(SharedSlice, slice, StringComparison.Ordinal) && SharedTokens != null && SharedHasAst)
                    {
                        script = SharedScript;
                        tokens = SharedTokens;
                        return;
                    }
                    try
                    {
                        using (var reader = new StringReader(slice))
                        {
                            script = SharedParser.Parse(reader, out var _) as TSqlScript;
                            tokens = script?.ScriptTokenStream?.ToList() ?? new List<TSqlParserToken>();
                        }
                        if (tokens.Count == 0)
                        {
                            using (var reader = new StringReader(slice))
                            {
                                var ts = SharedParser.GetTokenStream(reader, out var _);
                                tokens = ts?.ToList() ?? new List<TSqlParserToken>();
                            }
                        }
                    }
                    catch
                    {
                        script = null;
                        tokens = new List<TSqlParserToken>();
                    }
                    SharedSlice = slice;
                    SharedScript = script;
                    SharedTokens = tokens;
                    SharedHasAst = true;
                }
            }

            private TSqlScript ParseCached(string batchText)
            {
                ParseSlice(batchText, out _lastScript, out _lastTokens);
                return _lastScript;
            }

            #endregion
        }
    }
}
