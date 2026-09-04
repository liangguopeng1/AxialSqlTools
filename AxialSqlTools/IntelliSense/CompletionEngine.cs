using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

                    // 用全文判断注释：切片会丢掉前面未闭合的 /*（一次扫描同时取 USE 库，避免重复扫全文）
                    if (ScanPrefixState(fullText, caretOffset, out string useDb))
                        return result;
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
                            CollectAliases(script, localOffset, localEarly, _lastTokens, fullText, caretOffset);
                        result.Context = CompletionContext.AfterExec;
                        result.Prefix = execPrefix;
                        result.Items = BuildItems(CompletionContext.AfterExec, execPrefix, execNameEarly, localEarly, catalog, settings, connInfo);
                        result.Items = FilterAndSort(result.Items, execPrefix, settings, connInfo, execNameEarly);
                        if (result.Items.Count == 0 && connInfo != null && (catalog == null || !catalog.RoutinesLoaded))
                        {
                            // routine 目录仍在后台加载：给占位提示，避免 EXEC 补全静默为空
                            result.Items.Add(new CompletionItem("(正在加载存储过程…)", string.Empty, CompletionKind.Keyword, "存储过程目录后台加载中"));
                        }
                        result.ReplaceStartOffset = ComputeReplaceStartOffset(batchStart, localOffset, execPrefix, execNameEarly, _lastTokens);
                        result.ReplaceEndOffset = caretOffset;
                        return result;
                    }

                    // FROM/JOIN 原文兜底：未闭合 [xxx 时 ScriptDom token 流为空，必须走原文
                    FromObjectNameContext fromNameRaw;
                    bool hasFromRaw = TryParseFromObjectNameRaw(batchText, localOffset, out fromNameRaw);

                    bool noTokens = _lastTokens == null || _lastTokens.Count == 0;
                    if (script == null || (noTokens && hasFromRaw))
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
                        // CREATE OR ALTER 等 AST 失败时 token 仍在，继续走 GetContext
                        if (script == null && noTokens)
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
                    CollectAliases(script, localOffset, local, tokens, fullText, caretOffset);

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

            /// <summary>超过此长度的 GO 批次只解析光标所在语句，避免整页几千行 ScriptDOM。</summary>
            internal const int LargeBatchParseChars = 24 * 1024;

            /// <summary>大脚本找 GO 批次时只回看这么多字符，避免全文 IndexOf("GO") 误中 GongYingShang 后再 Regex.Matches 扫 1MB+。</summary>
            private const int LargeBatchLookbackChars = 256 * 1024;

            /// <summary>切片超过此长度不缓存 AST（TSqlScript 对象图内存高），只缓存扁平 token，控制大单语句的内存峰值。</summary>
            internal const int LargeSliceAstCacheChars = 64 * 1024;
            /// <summary>无分号时从光标回看找顶层语句起点；须大于常见派生表子查询，否则会切到内层 SELECT、丢掉 CARTNEW 等外层别名。</summary>
            private const int StmtLookbackChars = 128 * 1024;
            /// <summary>光标后延伸找 FROM / 语句结束。悬停在 SELECT 列表时定义在后面的别名仍要进切片。</summary>
            private const int StmtLookAheadChars = 128 * 1024;

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
                if (!TryGetBatchRange(fullText, caretOffset, out int batchStart, out int batchEnd))
                    return false;
                int stmtStart = batchStart;
                int stmtEnd = batchEnd;
                if (batchEnd - batchStart > LargeBatchParseChars)
                    FindCurrentStatementBounds(fullText, batchStart, batchEnd, caretOffset, out stmtStart, out stmtEnd);
                if (stmtStart < batchStart) stmtStart = batchStart;
                if (stmtEnd > batchEnd) stmtEnd = batchEnd;
                if (stmtEnd < stmtStart) stmtEnd = stmtStart;
                ExpandBoundsToCoverTopLevelFrom(fullText, caretOffset, batchStart, ref stmtStart, ref stmtEnd);
                if (stmtStart < batchStart) stmtStart = batchStart;
                if (stmtEnd > batchEnd) stmtEnd = batchEnd;
                if (stmtEnd < stmtStart) stmtEnd = stmtStart;
                // 切完仍过大：只在括号深度 0 再收一刀。光标在相关子查询里时切到内层 SELECT
                // 会丢掉后面的 FROM … AS KC；没找到顶层语句关键字则保持现状。
                if (stmtEnd - stmtStart > LargeBatchParseChars
                    && CountParenDepth(fullText, batchStart, caretOffset) == 0)
                {
                    int lookFrom = caretOffset - StmtLookbackChars;
                    if (lookFrom < stmtStart) lookFrom = stmtStart;
                    lookFrom = LineStartAt(fullText, lookFrom);
                    int tighter = FindLineStartStmtBefore(fullText, lookFrom, caretOffset, fullText.Length);
                    if (tighter >= 0 && tighter > stmtStart && tighter <= caretOffset)
                        stmtStart = tighter;
                    ExpandBoundsToCoverTopLevelFrom(fullText, caretOffset, batchStart, ref stmtStart, ref stmtEnd);
                    if (stmtStart < batchStart) stmtStart = batchStart;
                    if (stmtEnd > batchEnd) stmtEnd = batchEnd;
                }
                sliceStart = stmtStart;
                int sliceLen = stmtEnd - stmtStart;
                if (sliceLen <= 0)
                    slice = string.Empty;
                else if (stmtStart == 0 && sliceLen == fullText.Length)
                    slice = fullText;
                else
                    slice = fullText.Substring(stmtStart, sliceLen);
                return true;
            }

            /// <summary>当前 GO 批次在原文中的区间。大脚本只在光标附近回看，不 Substring 拷贝整批。</summary>
            internal static bool TryGetBatchRange(string fullText, int caretOffset, out int batchStart, out int batchEnd)
            {
                batchStart = 0;
                batchEnd = 0;
                if (string.IsNullOrEmpty(fullText)) return false;
                int n = fullText.Length;
                int caret = caretOffset;
                if (caret < 0) caret = 0;
                if (caret > n) caret = n;
                int searchFrom = caret - LargeBatchLookbackChars;
                if (searchFrom < 0) searchFrom = 0;
                else if (searchFrom > 0)
                    searchFrom = LineStartAt(fullText, searchFrom);

                int start = searchFrom;
                int pos = caret;
                while (pos > searchFrom)
                {
                    int ls = LineStartAt(fullText, pos > 0 ? pos - 1 : 0);
                    if (ls < searchFrom) break;
                    if (IsGoBatchLine(fullText, ls, n))
                    {
                        int afterGo = SkipLine(fullText, ls, n);
                        if (afterGo <= caret)
                        {
                            start = afterGo;
                            break;
                        }
                    }
                    if (ls <= searchFrom) break;
                    pos = ls;
                }

                int searchTo = caret + StmtLookAheadChars;
                if (searchTo > n) searchTo = n;
                int end = searchTo;
                int fwd = caret < n ? LineStartAt(fullText, caret) : n;
                while (fwd < searchTo)
                {
                    if (fwd >= caret && IsGoBatchLine(fullText, fwd, n))
                    {
                        end = fwd;
                        break;
                    }
                    int next = SkipLine(fullText, fwd, n);
                    if (next <= fwd) break;
                    fwd = next;
                }

                batchStart = start;
                batchEnd = end;
                return true;
            }

            /// <summary>
            /// 回看行首语句关键字。括号内的 SELECT（派生表/子查询）不算当前句，
            /// 否则大脚本会切到内层 SELECT，外层 AS CARTNEW / KC 进不了切片。
            /// 找不到则返回 -1，调用方不得把 rangeStart 当成语句起点前移。
            /// </summary>
            private static int FindLineStartStmtBefore(string text, int rangeStart, int probe, int n)
            {
                if (string.IsNullOrEmpty(text) || probe <= rangeStart) return -1;
                int depth = CountParenDepth(text, rangeStart, probe);
                for (int i = probe - 1; i >= rangeStart; i--)
                {
                    char c = text[i];
                    if (c == ')') { depth++; continue; }
                    if (c == '(')
                    {
                        if (depth > 0) depth--;
                        continue;
                    }
                    if (depth != 0) continue;
                    if (i > 0 && text[i - 1] != '\n' && text[i - 1] != '\r') continue;
                    int j = i;
                    while (j < probe && (text[j] == ' ' || text[j] == '\t')) j++;
                    int kwLen;
                    StmtKw kw = MatchStmtKeyword(text, j, n, out kwLen);
                    if (kw != StmtKw.None && kw != StmtKw.Union && kw != StmtKw.Except
                        && kw != StmtKw.Intersect && kw != StmtKw.Values)
                    {
                        if (!IsSetOpBefore(text, j, rangeStart, n))
                            return j;
                    }
                }
                return -1;
            }

            /// <summary>切片必须盖住当前查询及外层 FROM，否则 SELECT 列表/WHERE 里的 KC、CARTNEW 对不上别名定义。</summary>
            private static void ExpandBoundsToCoverTopLevelFrom(string text, int caret, int topLevelOrigin, ref int stmtStart, ref int stmtEnd)
            {
                if (string.IsNullOrEmpty(text)) return;
                int n = text.Length;
                if (topLevelOrigin < 0) topLevelOrigin = 0;
                int searchFrom = topLevelOrigin;
                if (caret - searchFrom > LargeBatchLookbackChars)
                {
                    int clipped = LineStartAt(text, caret - LargeBatchLookbackChars);
                    if (clipped > searchFrom && CountParenDepth(text, topLevelOrigin, clipped) == 0)
                        searchFrom = clipped;
                }
                int searchTo = caret + StmtLookAheadChars;
                if (stmtEnd > searchTo) searchTo = stmtEnd;
                if (searchTo > n) searchTo = n;
                int from = FindTopLevelFrom(text, caret, searchFrom, searchTo);
                if (from >= 0 && from <= caret && from < stmtStart)
                    stmtStart = LineStartAt(text, from);
                if (from >= 0)
                {
                    int regionEnd = ScanTopLevelFromRegionEnd(text, from, n);
                    if (regionEnd > stmtEnd) stmtEnd = regionEnd;
                }
                int pos = caret;
                for (int level = 0; level < 6; level++)
                {
                    int open = FindUnmatchedOpenParen(text, searchFrom, pos);
                    if (open < 0) break;
                    int sel = FindSelectBefore(text, open, searchFrom);
                    int parentFrom = sel >= 0 ? FindFromAfterSelectInText(text, sel, n) : -1;
                    if (parentFrom < 0)
                        parentFrom = FindTopLevelFrom(text, open, searchFrom, searchTo);
                    if (parentFrom >= 0)
                    {
                        if (parentFrom <= caret && parentFrom < stmtStart)
                            stmtStart = LineStartAt(text, parentFrom);
                        int pend = ScanTopLevelFromRegionEnd(text, parentFrom, n);
                        if (pend > stmtEnd) stmtEnd = pend;
                    }
                    pos = open;
                }
            }

            private static int FindTopLevelFrom(string text, int caret, int from, int to)
            {
                if (string.IsNullOrEmpty(text)) return -1;
                if (to > text.Length) to = text.Length;
                if (from < 0) from = 0;
                if (to <= from) return -1;
                int probe = caret;
                if (probe < from) probe = from;
                if (probe > to) probe = to;
                int targetDepth = CountParenDepth(text, from, probe);
                int n = text.Length;
                int backDepth = targetDepth;
                for (int i = probe - 1; i >= from; i--)
                {
                    char c = text[i];
                    if (c == ')') { backDepth++; continue; }
                    if (c == '(') { if (backDepth > 0) backDepth--; continue; }
                    if (backDepth != targetDepth) continue;
                    if (KeywordAt(text, i, n, "FROM"))
                        return i;
                    // 无分号下一句：不要把上一句 FROM 的 aa/bb 当成当前 SELECT 列表的别名
                    if (IsStmtBoundaryKeyword(text, i, n))
                        break;
                }
                int fwdDepth = targetDepth;
                for (int i = probe; i < to; i++)
                {
                    char c = text[i];
                    if (c == '(') { fwdDepth++; continue; }
                    if (c == ')') { if (fwdDepth > 0) fwdDepth--; continue; }
                    if (fwdDepth != targetDepth) continue;
                    if (KeywordAt(text, i, n, "FROM"))
                        return i;
                    if (IsStmtBoundaryKeyword(text, i, n) && !IsSetOpBefore(text, i, from, n))
                        break;
                }
                return -1;
            }

            private static int FindUnmatchedOpenParen(string text, int rangeStart, int pos)
            {
                if (string.IsNullOrEmpty(text) || pos <= rangeStart) return -1;
                int depth = 0;
                for (int i = pos - 1; i >= rangeStart; i--)
                {
                    char c = text[i];
                    if (c == ')') { depth++; continue; }
                    if (c == '(')
                    {
                        if (depth > 0) depth--;
                        else return i;
                    }
                }
                return -1;
            }

            private static int FindSelectBefore(string text, int pos, int rangeStart)
            {
                if (string.IsNullOrEmpty(text) || pos <= rangeStart) return -1;
                int depth = 0;
                for (int i = pos - 1; i >= rangeStart; i--)
                {
                    char c = text[i];
                    if (c == ')') { depth++; continue; }
                    if (c == '(') { if (depth > 0) depth--; continue; }
                    if (depth != 0) continue;
                    if (KeywordAt(text, i, text.Length, "SELECT"))
                        return i;
                }
                return -1;
            }

            private static int FindFromAfterSelectInText(string text, int selectAt, int n)
            {
                if (string.IsNullOrEmpty(text) || selectAt < 0) return -1;
                if (n > text.Length) n = text.Length;
                int depth = 0;
                int i = selectAt;
                while (i < n)
                {
                    char c = text[i];
                    if (c == '(') { depth++; i++; continue; }
                    if (c == ')') { if (depth > 0) depth--; i++; continue; }
                    if (depth == 0 && KeywordAt(text, i, n, "FROM"))
                        return i;
                    if (depth == 0 && (KeywordAt(text, i, n, "WHERE") || KeywordAt(text, i, n, "GROUP")
                        || KeywordAt(text, i, n, "ORDER") || KeywordAt(text, i, n, "HAVING")
                        || KeywordAt(text, i, n, "UNION")))
                        return -1;
                    i++;
                }
                return -1;
            }

            /// <summary>from 到 to（不含）之间未闭合的 '(' 数量；跳过字符串/注释。</summary>
            private static int CountParenDepth(string text, int from, int to)
            {
                if (string.IsNullOrEmpty(text) || to <= from) return 0;
                int n = text.Length;
                if (from < 0) from = 0;
                if (to > n) to = n;
                int depth = 0;
                bool inLine = false, inBlock = false, inStr = false;
                int i = from;
                while (i < to)
                {
                    char c = text[i];
                    char next = i + 1 < n ? text[i + 1] : '\0';
                    if (inLine)
                    {
                        if (c == '\n' || c == '\r') inLine = false;
                        i++;
                        continue;
                    }
                    if (inBlock)
                    {
                        if (c == '*' && next == '/') { inBlock = false; i += 2; continue; }
                        i++;
                        continue;
                    }
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
                    if (c == '-' && next == '-') { inLine = true; i += 2; continue; }
                    if (c == '/' && next == '*') { inBlock = true; i += 2; continue; }
                    if (c == '\'') { inStr = true; i++; continue; }
                    if ((c == 'N' || c == 'n') && next == '\'')
                    {
                        bool identBefore = i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
                        if (!identBefore) { inStr = true; i += 2; continue; }
                    }
                    if (c == '[')
                    {
                        i++;
                        while (i < to && text[i] != ']') i++;
                        if (i < to) i++;
                        continue;
                    }
                    if (c == '(') depth++;
                    else if (c == ')' && depth > 0) depth--;
                    i++;
                }
                return depth;
            }

            private static int ScanTopLevelFromRegionEnd(string text, int fromKw, int n)
            {
                int depth = 0;
                int i = fromKw + 4;
                while (i < n)
                {
                    char c = text[i];
                    if (c == '(') { depth++; i++; continue; }
                    if (c == ')') { if (depth > 0) depth--; i++; continue; }
                    if (depth != 0) { i++; continue; }
                    if (c == ';') return i;
                    int kwLen;
                    StmtKw kw = MatchStmtKeyword(text, i, n, out kwLen);
                    if (kw == StmtKw.Select || kw == StmtKw.Insert || kw == StmtKw.Update
                        || kw == StmtKw.Delete || kw == StmtKw.Merge || kw == StmtKw.With
                        || kw == StmtKw.Create || kw == StmtKw.Alter || kw == StmtKw.Drop
                        || kw == StmtKw.Use || kw == StmtKw.Declare || kw == StmtKw.Truncate)
                    {
                        if (i > fromKw + 4 && !IsSetOpBefore(text, i, fromKw, n))
                            return i;
                    }
                    if (KeywordAt(text, i, n, "WHERE") || KeywordAt(text, i, n, "GROUP")
                        || KeywordAt(text, i, n, "ORDER") || KeywordAt(text, i, n, "HAVING")
                        || KeywordAt(text, i, n, "UNION") || KeywordAt(text, i, n, "EXCEPT")
                        || KeywordAt(text, i, n, "INTERSECT"))
                        return i;
                    i++;
                }
                return n;
            }

            private static bool IsSetOpBefore(string text, int stmtAt, int rangeStart, int n)
            {
                int i = stmtAt - 1;
                while (i >= rangeStart && char.IsWhiteSpace(text[i])) i--;
                if (i < rangeStart) return false;
                while (i >= rangeStart && IsIdentChar(text[i])) i--;
                i++;
                int kwLen;
                StmtKw kw = MatchStmtKeyword(text, i, n, out kwLen);
                if (kw == StmtKw.Union || kw == StmtKw.Except || kw == StmtKw.Intersect)
                    return true;
                if (KeywordAt(text, i, n, "ALL"))
                {
                    int k = i - 1;
                    while (k >= rangeStart && char.IsWhiteSpace(text[k])) k--;
                    while (k >= rangeStart && IsIdentChar(text[k])) k--;
                    k++;
                    return MatchStmtKeyword(text, k, n, out kwLen) == StmtKw.Union;
                }
                return false;
            }

            private static int LineStartAt(string text, int i)
            {
                if (i <= 0) return 0;
                if (i > text.Length) i = text.Length;
                while (i > 0 && text[i - 1] != '\n') i--;
                return i;
            }

            private static int SkipLine(string text, int lineStart, int n)
            {
                int i = lineStart;
                while (i < n && text[i] != '\n') i++;
                if (i < n) i++;
                return i;
            }

            private static bool IsGoBatchLine(string text, int lineStart, int n)
            {
                int j = lineStart;
                while (j < n && (text[j] == ' ' || text[j] == '\t')) j++;
                if (j + 2 > n) return false;
                if ((text[j] != 'G' && text[j] != 'g') || (text[j + 1] != 'O' && text[j + 1] != 'o'))
                    return false;
                int after = j + 2;
                if (after < n && IsIdentChar(text[after])) return false;
                while (after < n && (text[after] == ' ' || text[after] == '\t')) after++;
                if (after < n && text[after] == ';')
                {
                    after++;
                    while (after < n && (text[after] == ' ' || text[after] == '\t')) after++;
                }
                return after >= n || text[after] == '\r' || text[after] == '\n';
            }

            private static void FindCurrentStatementBounds(string text, int rangeStart, int rangeEnd, int caret, out int stmtStart, out int stmtEnd)
            {
                stmtStart = rangeStart;
                stmtEnd = rangeEnd;
                if (string.IsNullOrEmpty(text) || rangeEnd <= rangeStart)
                    return;
                if (rangeStart < 0) rangeStart = 0;
                if (rangeEnd > text.Length) rangeEnd = text.Length;
                int n = rangeEnd;
                int probe = Math.Min(Math.Max(rangeStart, caret), n);
                // 就近定位扫描起点：从 probe 往前找最近的顶层分号，语句起点必在其后。
                // 字符串/注释内的分号会被近似忽略，仅使起点略偏后（补全早被字符串检查拦截），不破坏正确性。
                int scanFrom = rangeStart;
                int depth = 0;
                for (int k = probe - 1; k >= rangeStart; k--)
                {
                    char ch = text[k];
                    if (ch == ')') { depth++; continue; }
                    if (ch == '(') { if (depth > 0) depth--; continue; }
                    if (ch == ';' && depth == 0) { scanFrom = k + 1; break; }
                }
                // 无分号的大窗口不要从 256KB 外正向扫：ScriptDOM 只会吃第一句，WHERE k. 会丢别名。
                // 裁剪点落在括号内时不要前移，否则会把 FROM … AS KC 裁掉。
                if (probe - scanFrom > StmtLookbackChars)
                {
                    int clipped = LineStartAt(text, probe - StmtLookbackChars);
                    if (clipped > scanFrom && CountParenDepth(text, scanFrom, clipped) == 0)
                        scanFrom = clipped;
                }
                int lineStmt = FindLineStartStmtBefore(text, scanFrom, probe, n);
                if (lineStmt > scanFrom) scanFrom = lineStmt;
                bool inLineComment = false;
                bool inBlockComment = false;
                bool inString = false;
                int paren = 0;
                int lastStmt = scanFrom;
                bool afterSetOp = false;
                bool afterCte = false;
                bool afterInsert = false;
                int i = scanFrom;
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

            /// <summary>顶层新语句关键字（不含 UNION/FROM）。用于无分号多语句切断 FROM 回看。</summary>
            private static bool IsStmtBoundaryKeyword(string text, int i, int n)
            {
                int kwLen;
                StmtKw kw = MatchStmtKeyword(text, i, n, out kwLen);
                return kw != StmtKw.None && kw != StmtKw.Union && kw != StmtKw.Except
                    && kw != StmtKw.Intersect && kw != StmtKw.Values;
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

            private static void TokenizeNoCache(string slice, out List<TSqlParserToken> tokens)
            {
                tokens = new List<TSqlParserToken>();
                if (string.IsNullOrEmpty(slice)) return;
                lock (ParseGate)
                {
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
                }
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
                    if (slice.Length > LargeSliceAstCacheChars)
                    {
                        SharedSlice = null;
                        SharedTokens = null;
                    }
                }
            }

            internal static void ParseSlice(string slice, out TSqlScript script, out List<TSqlParserToken> tokens)
            {
                script = null;
                tokens = new List<TSqlParserToken>();
                if (string.IsNullOrEmpty(slice)) return;
                lock (ParseGate)
                {
                    if (string.Equals(SharedSlice, slice, StringComparison.Ordinal) && SharedTokens != null
                        && (SharedHasAst || slice.Length > LargeBatchParseChars))
                    {
                        script = SharedScript;
                        tokens = SharedTokens;
                        return;
                    }
                    try
                    {
                        // 大切片只做词法：TSqlScript 对象图随语句数膨胀，1MB 填充句会把内存打满。
                        bool parseAst = slice.Length <= LargeBatchParseChars;
                        if (parseAst)
                        {
                            using (var reader = new StringReader(slice))
                            {
                                script = SharedParser.Parse(reader, out var _) as TSqlScript;
                                tokens = script?.ScriptTokenStream?.ToList() ?? new List<TSqlParserToken>();
                            }
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
                    SharedTokens = tokens;
                    // 大切片不缓存 AST/原文，避免把 64KB+ 的 slice 和 token 列表钉在静态字段上
                    if (slice.Length <= LargeSliceAstCacheChars)
                    {
                        SharedScript = script;
                        SharedHasAst = script != null;
                    }
                    else
                    {
                        SharedSlice = null;
                        SharedTokens = null;
                        SharedScript = null;
                        SharedHasAst = false;
                    }
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
