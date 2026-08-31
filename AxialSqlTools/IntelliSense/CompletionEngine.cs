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
        /// 按 GO 分批，仅解析光标所在批次，缓存上次解析结果。
        /// 任何解析失败返回 Unknown 上下文，不抛异常。
        /// </summary>
        public partial class CompletionEngine
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
                            result.Items = BuildItems(CompletionContext.FromClause, fromPrefix, fromNameRaw, new LocalSymbols(), catalog, settings, connInfo);
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
                                result.Items = BuildItems(CompletionContext.BatchStart, rawPrefix, null, new LocalSymbols(), catalog, settings, connInfo);
                                result.Items = FilterAndSort(result.Items, rawPrefix, settings, connInfo, null);
                                result.ReplaceStartOffset = caretOffset - rawPrefix.Length;
                                result.ReplaceEndOffset = caretOffset;
                            }
                            return result;
                        }
                    }

                    var tokens = _lastTokens ?? new List<TSqlParserToken>();
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
                        if (!fromName.InFromClause)
                            fromName = fromNameRaw;
                        else if (string.IsNullOrEmpty(fromName.Partial) && !string.IsNullOrEmpty(fromNameRaw.Partial))
                        {
                            fromName.Partial = fromNameRaw.Partial;
                            fromName.PartialStartOffset = fromNameRaw.PartialStartOffset;
                            if (fromNameRaw.Segments != null && fromNameRaw.Segments.Count > 0
                                && (fromName.Segments == null || fromName.Segments.Count == 0))
                                fromName.Segments = fromNameRaw.Segments;
                            fromName.AfterDot = fromName.AfterDot || fromNameRaw.AfterDot;
                            fromName.UsesDoubleDot = fromName.UsesDoubleDot || fromNameRaw.UsesDoubleDot;
                        }
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

                        if (!inDmlTargetName && (aliasMember || inExprClause || !fromQualifiedName))
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
                    // 未闭合 [identifier 时 Parse 常返回空 ScriptTokenStream；用 GetTokenStream 兜底
                    if (_lastTokens == null || _lastTokens.Count == 0)
                        _lastTokens = TokenizeFallback(batchText);
                }
                catch
                {
                    _lastScript = null;
                    _lastTokens = TokenizeFallback(batchText);
                }

                return _lastScript;
            }

            private List<TSqlParserToken> TokenizeFallback(string batchText)
            {
                try
                {
                    using (var reader = new StringReader(batchText ?? string.Empty))
                    {
                        var ts = _parser.GetTokenStream(reader, out var _);
                        return ts?.ToList() ?? new List<TSqlParserToken>();
                    }
                }
                catch
                {
                    return new List<TSqlParserToken>();
                }
            }

            #endregion
        }
    }
}
