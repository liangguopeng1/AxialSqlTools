using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>从光标位置解析悬停 token 与 FROM/JOIN 表上下文。</summary>
    internal static class QuickInfoSqlContext
    {
        public sealed class HoverToken
        {
            public string Name;
            public string Owner;
            public bool HasOwner;
        }

        /// <summary>EXEC/EXECUTE 目标过程引用。</summary>
        public sealed class RoutineRef
        {
            public string Database;
            public string Schema;
            public string Name;
        }

        private sealed class TableSegment
        {
            public TableRef Ref;
            public string Alias;
            public int StartOffset;
            public int EndOffset;
            public HashSet<string> NameTokens;
        }

        public static HoverToken TryGetHoverToken(List<TSqlParserToken> tokens, int offset)
        {
            if (tokens == null || tokens.Count == 0) return null;
            int starIdx = FindStarTokenAt(tokens, offset);
            if (starIdx >= 0)
            {
                if (!IsSelectStarAt(tokens, starIdx, out string qualifier))
                    return null;
                return new HoverToken
                {
                    Name = "*",
                    Owner = qualifier,
                    HasOwner = !string.IsNullOrEmpty(qualifier)
                };
            }
            int idx = FindTokenIndexAt(tokens, offset);
            if (idx < 0) return null;
            var tok = tokens[idx];
            if (!IsHoverableToken(tok))
            {
                tok = PreviousHoverableToken(tokens, idx);
                if (tok == null) return null;
                idx = IndexOfToken(tokens, tok);
            }
            string name = Unbracket(tok.Text);
            if (string.IsNullOrEmpty(name)) return null;
            var result = new HoverToken { Name = name };
            var prev = PreviousSignificant(tokens, idx);
            if (prev != null && prev.TokenType == TSqlTokenType.Dot)
            {
                var ownerTok = PreviousSignificant(tokens, IndexOfToken(tokens, prev));
                // db..table：owner 是第二个点，再往前取库名不当作列所属别名
                if (ownerTok != null && ownerTok.TokenType == TSqlTokenType.Dot)
                    return result;
                if (ownerTok != null && IsWordLike(ownerTok))
                {
                    result.Owner = Unbracket(ownerTok.Text);
                    result.HasOwner = !string.IsNullOrEmpty(result.Owner);
                }
            }
            return result;
        }

        /// <summary>悬停词后一个有效 token 是否为 '('（函数调用）。光标落在 '(' 上也算。</summary>
        public static bool NextSignificantIsLeftParen(List<TSqlParserToken> tokens, int offset)
        {
            if (tokens == null || tokens.Count == 0) return false;
            int idx = FindTokenIndexAt(tokens, offset);
            if (idx < 0) return false;
            if (tokens[idx].TokenType == TSqlTokenType.LeftParenthesis)
                return true;
            for (int i = idx + 1; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
                return t.TokenType == TSqlTokenType.LeftParenthesis;
            }
            return false;
        }

        /// <summary>
        /// 原文解析 EXEC/EXECUTE 目标（支持 caiwu..proc / caiwu.dbo.proc）。
        /// hoverName 须为过程名最后一段（如 000_pro_...）。
        /// </summary>
        public static RoutineRef TryResolveExecRoutine(string text, int offset, string hoverName)
        {
            return TryResolveRoutineCore(text, offset, hoverName, requireExec: true);
        }

        /// <summary>
        /// 无 EXEC 时解析限定过程名：db.schema.proc / schema.proc（须至少一段限定，避免裸标识误当过程）。
        /// </summary>
        public static RoutineRef TryResolveQualifiedRoutine(string text, int offset, string hoverName)
        {
            var rref = TryResolveRoutineCore(text, offset, hoverName, requireExec: false);
            if (rref == null) return null;
            // 无 EXEC 时要求至少 schema.proc 或 db..proc，避免普通词误匹配
            if (string.IsNullOrEmpty(rref.Schema) && string.IsNullOrEmpty(rref.Database))
                return null;
            return rref;
        }

        private static RoutineRef TryResolveRoutineCore(string text, int offset, string hoverName, bool requireExec)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(hoverName) || offset < 0)
                return null;

            int probe = Math.Min(Math.Max(0, offset), text.Length);
            while (probe < text.Length && IsIdentChar(text[probe])) probe++;
            int i = probe - 1;
            while (i >= 0 && IsIdentChar(text[i])) i--;
            int nameStart = i + 1;
            int nameEnd = probe;
            if (nameStart >= nameEnd) return null;
            string nameFromText = Unbracket(text.Substring(nameStart, nameEnd - nameStart));
            string hoverClean = hoverName.Trim('[', ']', '"');
            if (string.IsNullOrEmpty(nameFromText) && string.IsNullOrEmpty(hoverClean))
                return null;
            // ScriptDOM 可能把 000_pro 拆成 Integer+Ident；以原文整词为准
            string name;
            if (string.IsNullOrEmpty(nameFromText))
                name = hoverClean;
            else if (string.IsNullOrEmpty(hoverClean)
                     || string.Equals(nameFromText, hoverClean, StringComparison.OrdinalIgnoreCase)
                     || nameFromText.IndexOf(hoverClean, StringComparison.OrdinalIgnoreCase) >= 0
                     || hoverClean.IndexOf(nameFromText, StringComparison.OrdinalIgnoreCase) >= 0)
                name = nameFromText.Length >= (hoverClean?.Length ?? 0) ? nameFromText : hoverClean;
            else
                return null;
            if (string.IsNullOrEmpty(name)) return null;

            i = nameStart - 1;
            var segments = new List<string>();
            bool doubleDot = false;
            int guard = 0;
            int maxGuard = Math.Max(32, (offset + 1) * 4);
            while (i >= 0)
            {
                if (++guard > maxGuard)
                    break;
                while (i >= 0 && (text[i] == ' ' || text[i] == '\t')) i--;
                if (i < 0) break;
                if (text[i] == '\n' || text[i] == '\r' || text[i] == ';') break;
                if (text[i] == '.')
                {
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
                else if (text[i] == '[')
                {
                    // 未闭合 [identifier：跳过 '['，否则 i 不前进会死循环
                    i--;
                }
                else
                {
                    while (i >= 0 && IsIdentChar(text[i])) i--;
                }
                int segStart = i + 1;
                if (segStart >= segEnd) continue;
                string seg = Unbracket(text.Substring(segStart, segEnd - segStart));
                if (string.Equals(seg, "EXEC", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(seg, "EXECUTE", StringComparison.OrdinalIgnoreCase))
                {
                    i = segEnd - 1;
                    break;
                }
                if (!string.IsNullOrEmpty(seg))
                    segments.Insert(0, seg);
            }

            while (i >= 0 && (text[i] == ' ' || text[i] == '\t')) i--;
            bool hasExec = false;
            if (i >= 0)
            {
                int kwEnd = i + 1;
                int kwStart = i;
                while (kwStart >= 0 && IsIdentChar(text[kwStart])) kwStart--;
                string kw = text.Substring(kwStart + 1, kwEnd - (kwStart + 1));
                hasExec = string.Equals(kw, "EXEC", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(kw, "EXECUTE", StringComparison.OrdinalIgnoreCase);
            }
            if (requireExec && !hasExec)
                return null;

            var result = new RoutineRef { Name = name };
            if (doubleDot && segments.Count >= 1)
            {
                result.Database = segments[0];
                result.Schema = "dbo";
            }
            else if (segments.Count >= 2)
            {
                result.Database = segments[0];
                result.Schema = segments[1];
            }
            else if (segments.Count == 1)
            {
                result.Schema = segments[0];
            }
            return result;
        }

        private static bool IsIdentChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#';
        }

            /// <summary>解析光标所在（或最近）FROM/JOIN 表引用；优先命中悬停位置所在表段，否则用当前查询第一张表。</summary>
            public static TableRef TryResolveFromTable(List<TSqlParserToken> tokens, int offset)
            {
                var segments = CollectTableSegments(tokens, offset);
                if (segments == null || segments.Count == 0) return null;
                foreach (var seg in segments)
                {
                    if (offset >= seg.StartOffset && offset <= seg.EndOffset)
                        return seg.Ref;
                }
                return segments[0].Ref;
            }

        /// <summary>按悬停词在 FROM/JOIN 中定位表（支持 db.schema.table / db..table / JOIN 表）。</summary>
        public static TableRef TryResolveTableByHoverName(List<TSqlParserToken> tokens, int offset, string hoverName)
        {
            if (string.IsNullOrEmpty(hoverName)) return null;
            var segments = CollectTableSegments(tokens, offset);
            if (segments == null || segments.Count == 0) return null;
            foreach (var seg in segments)
            {
                if (offset >= seg.StartOffset && offset <= seg.EndOffset)
                {
                    if (seg.NameTokens != null && seg.NameTokens.Contains(hoverName))
                        return seg.Ref;
                    if (string.Equals(seg.Ref?.Name, hoverName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(seg.Alias, hoverName, StringComparison.OrdinalIgnoreCase))
                        return seg.Ref;
                }
            }
            foreach (var seg in segments)
            {
                if (string.Equals(seg.Ref?.Name, hoverName, StringComparison.OrdinalIgnoreCase))
                    return seg.Ref;
                if (string.Equals(seg.Alias, hoverName, StringComparison.OrdinalIgnoreCase))
                    return seg.Ref;
            }
            return null;
        }

        /// <summary>按别名解析表引用（含 JOIN 别名）。</summary>
        public static TableRef TryResolveAlias(List<TSqlParserToken> tokens, int offset, string alias)
        {
            if (string.IsNullOrEmpty(alias)) return null;
            var segments = CollectTableSegments(tokens, offset);
            if (segments == null) return null;
            foreach (var seg in segments)
            {
                if (string.Equals(seg.Alias, alias, StringComparison.OrdinalIgnoreCase))
                    return seg.Ref;
                if (string.Equals(seg.Ref?.Name, alias, StringComparison.OrdinalIgnoreCase))
                    return seg.Ref;
            }
            return null;
        }

        private static List<TableSegment> CollectTableSegments(List<TSqlParserToken> tokens, int offset)
        {
            int fromIdx = FindFromClauseTokenIndex(tokens, offset);
            if (fromIdx < 0) return null;
            int regionEnd = FindFromRegionEnd(tokens, fromIdx);
            var segments = new List<TableSegment>();
            int i = fromIdx + 1;
            while (i < tokens.Count)
            {
                while (i < tokens.Count)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificant(t)) { i++; continue; }
                    if (t.Offset >= regionEnd) return segments;
                    string kw = t.Text?.ToUpperInvariant();
                    if (IsJoinKeyword(kw)) { i++; continue; }
                    if (kw == "ON")
                    {
                        i++;
                        while (i < tokens.Count)
                        {
                            var ot = tokens[i];
                            if (ot == null || IsInsignificant(ot)) { i++; continue; }
                            if (ot.Offset >= regionEnd) return segments;
                            string okw = ot.Text?.ToUpperInvariant();
                            if (IsJoinKeyword(okw)) break;
                            i++;
                        }
                        continue;
                    }
                    if (kw == "AS") { i++; continue; }
                    break;
                }
                if (i >= tokens.Count) break;

                int segStartIdx = i;
                int segStartOffset = tokens[i]?.Offset ?? 0;
                var names = new List<string>();
                var nameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool afterDot = false;
                while (i < tokens.Count)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificant(t)) { i++; continue; }
                    if (t.Offset >= regionEnd) break;
                    string kw = t.Text?.ToUpperInvariant();
                    if (IsJoinKeyword(kw) || kw == "ON" || kw == "," ) break;
                    // AS alias：消费别名后结束本段（勿把 AS 留给 look-ahead 提前 return）
                    if (kw == "AS")
                    {
                        int j = i + 1;
                        while (j < tokens.Count)
                        {
                            var at = tokens[j];
                            if (at == null || IsInsignificant(at)) { j++; continue; }
                            if (at.Offset >= regionEnd) break;
                            if (IsWordLike(at) || IsPartialObjectName(at))
                            {
                                string an = Unbracket(at.Text);
                                if (!IsTableHintOrNoise(an) && !IsSqlKeyword(an))
                                {
                                    names.Add(an);
                                    nameSet.Add(an);
                                }
                                j++;
                            }
                            break;
                        }
                        i = j;
                        break;
                    }
                    // 语句/子句边界：禁止跨到下一句 SELECT（无分号时尤其常见）
                    if (IsFromRegionStopKeyword(kw)) break;
                    // 跳过 WITH (NOLOCK) / kucun(nolock) 等表提示，避免把 nolock 当成表名
                    if (kw == "WITH") { i++; continue; }
                    if (t.TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        i = SkipParenthesisGroup(tokens, i);
                        afterDot = false;
                        continue;
                    }
                    if (t.TokenType == TSqlTokenType.Dot)
                    {
                        afterDot = true;
                        i++;
                        continue;
                    }
                    if (IsWordLike(t) || IsPartialObjectName(t))
                    {
                        string n = Unbracket(t.Text);
                        if (!IsTableHintOrNoise(n))
                        {
                            // 表名已完整（前一词非点）：后面要么是别名，要么是下行裸限定名
                            if (names.Count > 0 && !afterDot)
                            {
                                // 后接 a.b → 新限定名/新语句，结束本段且不消费
                                if (NextSignificantIsDot(tokens, i + 1, regionEnd))
                                    break;
                                names.Add(n);
                                nameSet.Add(n);
                                i++;
                                break;
                            }
                            names.Add(n);
                            nameSet.Add(n);
                        }
                        afterDot = false;
                        i++;
                        continue;
                    }
                    i++;
                }
                int segEndOffset = i < tokens.Count && tokens[i] != null
                    ? tokens[i].Offset
                    : regionEnd;
                if (i > segStartIdx)
                {
                    var lastTok = tokens[i - 1];
                    if (lastTok != null && !IsInsignificant(lastTok))
                        segEndOffset = Math.Max(segEndOffset, lastTok.Offset + lastTok.Text.Length);
                }

                // i 未前进时强制步进，避免外层死循环
                if (i <= segStartIdx) i = segStartIdx + 1;
                var parsed = ParseTableSegment(names, tokens, segStartIdx, segEndOffset);
                if (parsed != null)
                {
                    segments.Add(new TableSegment
                    {
                        Ref = parsed.Item1,
                        Alias = parsed.Item2,
                        StartOffset = segStartOffset,
                        EndOffset = segEndOffset,
                        NameTokens = nameSet
                    });
                }

                // 仅逗号 / JOIN 可接下一表；AS alias / WITH (NOLOCK) 已在上段消费，若残留则跳过
                int look = i;
                while (look < tokens.Count)
                {
                    var lt = tokens[look];
                    if (lt == null || IsInsignificant(lt)) { look++; continue; }
                    if (lt.Offset >= regionEnd) return segments;
                    string lkw = lt.Text?.ToUpperInvariant();
                    if (lkw == "," || IsJoinKeyword(lkw) || lkw == "ON")
                        break;
                    if (lkw == "AS")
                    {
                        look++;
                        while (look < tokens.Count)
                        {
                            var at = tokens[look];
                            if (at == null || IsInsignificant(at)) { look++; continue; }
                            if (IsWordLike(at) || IsPartialObjectName(at)) look++;
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
                            if (wt == null || IsInsignificant(wt)) { look++; continue; }
                            if (wt.TokenType == TSqlTokenType.LeftParenthesis)
                            {
                                look = SkipParenthesisGroup(tokens, look);
                                continue;
                            }
                            break;
                        }
                        continue;
                    }
                    return segments;
                }
            }
            return segments;
        }

        private static bool NextSignificantIsDot(List<TSqlParserToken> tokens, int fromIdx, int regionEnd)
        {
            for (int j = fromIdx; j < tokens.Count; j++)
            {
                var t = tokens[j];
                if (t == null || IsInsignificant(t)) continue;
                if (t.Offset >= regionEnd) return false;
                return t.TokenType == TSqlTokenType.Dot;
            }
            return false;
        }

        private static Tuple<TableRef, string> ParseTableSegment(
            List<string> names, List<TSqlParserToken> tokens, int segStartIdx, int scanEnd)
        {
            if (names == null || names.Count == 0) return null;
            int fromIdx = Math.Max(0, segStartIdx - 1);

            if (names.Count == 1)
            {
                if (IsSqlKeyword(names[0])) return null;
                var bare = new TableRef { Name = names[0] };
                NormalizeTableRef(bare);
                return Tuple.Create(bare, (string)null);
            }

            // db.schema.table alias → 4 段；或 server.db.schema.table（链接服务器四段名）
            if (names.Count >= 4)
            {
                string linkedServer4;
                string linkedDb4;
                if (TryFindServerDatabaseDoubleDot(tokens, fromIdx, scanEnd, names[names.Count - 2], out linkedServer4, out linkedDb4))
                {
                    var linkedDd = new TableRef
                    {
                        LinkedServer = linkedServer4,
                        Database = linkedDb4,
                        Schema = "dbo",
                        Name = names[names.Count - 2]
                    };
                    NormalizeTableRef(linkedDd);
                    string aliasDd = names[names.Count - 1];
                    if (IsSqlKeyword(aliasDd)) aliasDd = null;
                    return Tuple.Create(linkedDd, aliasDd);
                }
                // server.db.schema.table [alias]
                bool fourPartLinked = names.Count == 4 && IsLikelySchemaName(names[2]);
                bool fivePartLinked = names.Count >= 5 && IsLikelySchemaName(names[names.Count - 3]);
                if (fourPartLinked || fivePartLinked)
                {
                    int base0 = fivePartLinked ? names.Count - 5 : 0;
                    var linked = new TableRef
                    {
                        LinkedServer = names[base0],
                        Database = names[base0 + 1],
                        Schema = names[base0 + 2],
                        Name = names[base0 + 3]
                    };
                    NormalizeTableRef(linked);
                    string aliasL = fivePartLinked ? names[names.Count - 1] : null;
                    if (IsSqlKeyword(aliasL)) aliasL = null;
                    return Tuple.Create(linked, aliasL);
                }
                var tref = new TableRef
                {
                    Database = names[names.Count - 4],
                    Schema = names[names.Count - 3],
                    Name = names[names.Count - 2]
                };
                NormalizeTableRef(tref);
                string alias4 = names[names.Count - 1];
                if (IsSqlKeyword(alias4)) alias4 = null;
                return Tuple.Create(tref, alias4);
            }

            // db.schema.table（无别名）或 schema.table alias 或 db..table alias
            if (names.Count == 3)
            {
                string linkedServer3;
                string linkedDb3;
                if (TryFindServerDatabaseDoubleDot(tokens, fromIdx, scanEnd, names[2], out linkedServer3, out linkedDb3))
                {
                    var linkedNoAlias = new TableRef
                    {
                        LinkedServer = linkedServer3,
                        Database = linkedDb3,
                        Schema = "dbo",
                        Name = names[2]
                    };
                    NormalizeTableRef(linkedNoAlias);
                    return Tuple.Create(linkedNoAlias, (string)null);
                }
                string database;
                string schema;
                if (TryFindDoubleDotRef(tokens, fromIdx, scanEnd, names[1], out database, out schema))
                {
                    var tref = new TableRef { Database = database, Schema = schema ?? "dbo", Name = names[1] };
                    NormalizeTableRef(tref);
                    string alias = IsSqlKeyword(names[2]) ? null : names[2];
                    return Tuple.Create(tref, alias);
                }
                // db.schema.table（第三段是表名，无别名）—— 中间段常为 dbo
                if (IsLikelySchemaName(names[1]))
                {
                    var threePart = new TableRef
                    {
                        Database = names[0],
                        Schema = names[1],
                        Name = names[2]
                    };
                    NormalizeTableRef(threePart);
                    return Tuple.Create(threePart, (string)null);
                }
                // schema.table alias
                var schemaAlias = new TableRef { Schema = names[0], Name = names[1] };
                NormalizeTableRef(schemaAlias);
                string a = IsSqlKeyword(names[2]) ? null : names[2];
                return Tuple.Create(schemaAlias, a);
            }

            // db..table 或 schema.table 或 table alias
            if (names.Count == 2)
            {
                string database;
                string schema;
                if (TryFindDoubleDotRef(tokens, fromIdx, scanEnd, names[1], out database, out schema))
                {
                    var tref = new TableRef { Database = database, Schema = schema ?? "dbo", Name = names[1] };
                    NormalizeTableRef(tref);
                    return Tuple.Create(tref, (string)null);
                }
                if (TryFindDoubleDotRef(tokens, fromIdx, scanEnd, names[0], out database, out schema))
                {
                    var tref = new TableRef { Database = database, Schema = schema ?? "dbo", Name = names[0] };
                    NormalizeTableRef(tref);
                    string alias = IsSqlKeyword(names[1]) ? null : names[1];
                    return Tuple.Create(tref, alias);
                }
                // table alias（无限定）
                if (!IsLikelySchemaName(names[0]) || IsSqlKeyword(names[1]))
                {
                    // 更常见：table alias
                    var tableAlias = new TableRef { Name = names[0] };
                    NormalizeTableRef(tableAlias);
                    string alias = IsSqlKeyword(names[1]) ? null : names[1];
                    return Tuple.Create(tableAlias, alias);
                }
                var schemaTable = new TableRef { Schema = names[0], Name = names[1] };
                NormalizeTableRef(schemaTable);
                return Tuple.Create(schemaTable, (string)null);
            }

            return null;
        }

        private static bool IsLikelySchemaName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            switch (name.ToUpperInvariant())
            {
                case "DBO":
                case "SYS":
                case "GUEST":
                case "INFORMATION_SCHEMA":
                    return true;
                default:
                    return false;
            }
        }

        private static int FindFromClauseTokenIndex(List<TSqlParserToken> tokens, int offset)
        {
            int selectIdx = FindCurrentQuerySelectIndex(tokens, offset);
            if (selectIdx >= 0)
            {
                int fromIdx = FindFromAfterSelect(tokens, selectIdx);
                if (fromIdx >= 0) return fromIdx;
                return -1;
            }
            int lastFrom = -1;
            int depth = 0;
            int caretDepth = ComputeParenDepthBefore(tokens, offset);
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
                if (t.Offset >= offset) break;
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
                    lastFrom = -1;
                    depth = 0;
                    continue;
                }
                if (text == "FROM" && depth == caretDepth)
                    lastFrom = i;
            }
            return lastFrom;
        }

        private static int FindCurrentQuerySelectIndex(List<TSqlParserToken> tokens, int offset)
        {
            if (tokens == null) return -1;
            int depth = 0;
            var selectIdxs = new List<int>();
            var selectDepths = new List<int>();
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
                if (t.Offset >= offset) break;
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
                }
            }
            return selectIdxs.Count > 0 ? selectIdxs[selectIdxs.Count - 1] : -1;
        }

        private static int FindFromAfterSelect(List<TSqlParserToken> tokens, int selectIdx)
        {
            int depth = 0;
            for (int i = selectIdx + 1; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
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
                if (IsFromRegionStopKeyword(kw) && kw != "SELECT")
                    return -1;
            }
            return -1;
        }

        private static int ComputeParenDepthBefore(List<TSqlParserToken> tokens, int offset)
        {
            int depth = 0;
            if (tokens == null) return 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
                if (t.Offset >= offset) break;
                if (t.TokenType == TSqlTokenType.LeftParenthesis) depth++;
                else if (t.TokenType == TSqlTokenType.RightParenthesis && depth > 0) depth--;
            }
            return depth;
        }

        private static int FindFromRegionEnd(List<TSqlParserToken> tokens, int fromIdx)
        {
            int depth = 0;
            for (int i = fromIdx + 1; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
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
                if (IsFromRegionStopKeyword(kw))
                    return t.Offset;
            }
            for (int i = tokens.Count - 1; i >= 0; i--)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
                return t.Offset + t.Text.Length;
            }
            return int.MaxValue;
        }

        /// <summary>FROM 区间结束关键字（含下一语句起点，避免无分号时串句）。</summary>
        private static bool IsFromRegionStopKeyword(string kw)
        {
            if (string.IsNullOrEmpty(kw)) return false;
            switch (kw)
            {
                case "WHERE":
                case "GROUP":
                case "ORDER":
                case "HAVING":
                case "UNION":
                case "EXCEPT":
                case "INTERSECT":
                case ";":
                case "GO":
                case "SELECT":
                case "INSERT":
                case "UPDATE":
                case "DELETE":
                case "MERGE":
                case "EXEC":
                case "EXECUTE":
                case "CREATE":
                case "ALTER":
                case "DROP":
                case "TRUNCATE":
                case "USE":
                case "DECLARE":
                case "SET":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsJoinKeyword(string kw)
        {
            if (string.IsNullOrEmpty(kw)) return false;
            switch (kw)
            {
                case "JOIN":
                case "INNER":
                case "LEFT":
                case "RIGHT":
                case "FULL":
                case "CROSS":
                case "OUTER":
                case ",":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>识别 server.db..table（省略架构的四段名）。</summary>
        private static bool TryFindServerDatabaseDoubleDot(
            List<TSqlParserToken> tokens, int fromIdx, int scanEnd, string tableName,
            out string linkedServer, out string database)
        {
            linkedServer = null;
            database = null;
            if (string.IsNullOrEmpty(tableName) || tokens == null) return false;
            for (int i = fromIdx + 1; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
                if (t.Offset >= scanEnd) break;
                if (!IsPartialObjectName(t) && !IsWordLike(t)) continue;
                string serverCandidate = Unbracket(t.Text);
                int j = i + 1;
                int dotsAfterServer = 0;
                while (j < tokens.Count)
                {
                    var nt = tokens[j];
                    if (nt == null || IsInsignificant(nt)) { j++; continue; }
                    if (nt.Offset >= scanEnd) break;
                    if (nt.TokenType == TSqlTokenType.Dot) { dotsAfterServer++; j++; continue; }
                    if (dotsAfterServer != 1) break;
                    string dbCandidate = Unbracket(nt.Text);
                    j++;
                    int dotsAfterDb = 0;
                    while (j < tokens.Count)
                    {
                        var nt2 = tokens[j];
                        if (nt2 == null || IsInsignificant(nt2)) { j++; continue; }
                        if (nt2.Offset >= scanEnd) break;
                        if (nt2.TokenType == TSqlTokenType.Dot) { dotsAfterDb++; j++; continue; }
                        if (dotsAfterDb < 2) break;
                        if (string.Equals(Unbracket(nt2.Text), tableName, StringComparison.OrdinalIgnoreCase))
                        {
                            linkedServer = serverCandidate;
                            database = dbCandidate;
                            return true;
                        }
                        break;
                    }
                    break;
                }
            }
            return false;
        }

        private static bool TryFindDoubleDotRef(
            List<TSqlParserToken> tokens, int fromIdx, int scanEnd, string tableName,
            out string database, out string schema)
        {
            database = null;
            schema = null;
            for (int i = fromIdx + 1; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
                if (t.Offset >= scanEnd) break;
                if (!IsPartialObjectName(t) && !IsWordLike(t)) continue;
                string dbCandidate = Unbracket(t.Text);
                int j = i + 1;
                int dotCount = 0;
                while (j < tokens.Count)
                {
                    var nt = tokens[j];
                    if (nt == null || IsInsignificant(nt)) { j++; continue; }
                    if (nt.Offset >= scanEnd) break;
                    if (nt.TokenType == TSqlTokenType.Dot) { dotCount++; j++; continue; }
                    if (dotCount < 2) break;
                    string ident = Unbracket(nt.Text);
                    if (string.Equals(ident, tableName, StringComparison.OrdinalIgnoreCase))
                    {
                        database = dbCandidate;
                        schema = "dbo";
                        return true;
                    }
                    schema = ident;
                    j++;
                    while (j < tokens.Count)
                    {
                        var nt2 = tokens[j];
                        if (nt2 == null || IsInsignificant(nt2)) { j++; continue; }
                        if (nt2.Offset >= scanEnd) break;
                        if (nt2.TokenType == TSqlTokenType.Dot) { j++; continue; }
                        if (string.Equals(Unbracket(nt2.Text), tableName, StringComparison.OrdinalIgnoreCase))
                        {
                            database = dbCandidate;
                            return true;
                        }
                        break;
                    }
                    break;
                }
            }
            return false;
        }

        private static void NormalizeTableRef(TableRef tref)
        {
            if (tref == null) return;
            if (!string.IsNullOrEmpty(tref.Database) && string.IsNullOrEmpty(tref.Schema))
                tref.Schema = "dbo";
        }

        private static bool IsSqlKeyword(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            switch (name.ToUpperInvariant())
            {
                case "AS": case "ON": case "WITH": case "NOLOCK": case "READUNCOMMITTED":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsTableHintOrNoise(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (IsSqlKeyword(name)) return true;
            switch (name.ToUpperInvariant())
            {
                case "READCOMMITTED":
                case "REPEATABLEREAD":
                case "SERIALIZABLE":
                case "SNAPSHOT":
                case "HOLDLOCK":
                case "UPDLOCK":
                case "XLOCK":
                case "ROWLOCK":
                case "PAGLOCK":
                case "TABLOCK":
                case "TABLOCKX":
                case "NOWAIT":
                case "READPAST":
                case "FORCESEEK":
                case "FORCESCAN":
                case "IGNORE_CONSTRAINTS":
                case "IGNORE_TRIGGERS":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>跳过成对括号（含嵌套），返回右括号后的下一个索引。</summary>
        private static int SkipParenthesisGroup(List<TSqlParserToken> tokens, int openIdx)
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
            return openIdx + 1;
        }

        private static int FindTokenIndexAt(List<TSqlParserToken> tokens, int offset)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
                if (offset >= t.Offset && offset <= t.Offset + t.Text.Length)
                    return i;
            }
            for (int i = tokens.Count - 1; i >= 0; i--)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
                if (t.Offset < offset) return i;
            }
            return -1;
        }

        private static int IndexOfToken(List<TSqlParserToken> tokens, TSqlParserToken token)
        {
            if (tokens == null || token == null) return -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (ReferenceEquals(tokens[i], token)) return i;
            }
            return -1;
        }

        private static TSqlParserToken PreviousSignificant(List<TSqlParserToken> tokens, int fromIndex)
        {
            for (int i = fromIndex - 1; i >= 0; i--)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
                return t;
            }
            return null;
        }

        private static bool IsInsignificant(TSqlParserToken t)
        {
            if (t == null) return true;
            var type = t.TokenType;
            return type == TSqlTokenType.WhiteSpace ||
                   type == TSqlTokenType.MultilineComment ||
                   type == TSqlTokenType.SingleLineComment ||
                   type == TSqlTokenType.EndOfFile;
        }

        private static bool IsStarToken(TSqlParserToken t)
        {
            if (t == null || string.IsNullOrEmpty(t.Text)) return false;
            return t.Text.Length == 1 && t.Text[0] == '*';
        }

        /// <summary>相邻 '(' '*' 时，闭区间 FindTokenIndexAt 会落到括号；按半开区间命中 *。</summary>
        private static int FindStarTokenAt(List<TSqlParserToken> tokens, int offset)
        {
            if (tokens == null) return -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (!IsStarToken(t)) continue;
                int start = t.Offset;
                int end = start + t.Text.Length;
                if (offset >= start && offset < end)
                    return i;
            }
            return -1;
        }

        /// <summary>SELECT * / a.* / TOP 10 *，排除 COUNT(*) 与 1 * 2。</summary>
        private static bool IsSelectStarAt(List<TSqlParserToken> tokens, int starIdx, out string qualifier)
        {
            qualifier = null;
            if (tokens == null || starIdx < 0 || starIdx >= tokens.Count) return false;
            if (!IsStarToken(tokens[starIdx])) return false;
            var prev = PreviousSignificant(tokens, starIdx);
            if (prev == null) return false;
            if (prev.TokenType == TSqlTokenType.LeftParenthesis)
                return false;
            if (prev.TokenType == TSqlTokenType.Dot)
            {
                var ownerTok = PreviousSignificant(tokens, IndexOfToken(tokens, prev));
                if (ownerTok == null || !IsWordLike(ownerTok)) return false;
                qualifier = Unbracket(ownerTok.Text);
                return !string.IsNullOrEmpty(qualifier);
            }
            if (prev.TokenType == TSqlTokenType.Comma)
                return true;
            if (IsSelectStarKeyword(prev))
                return true;
            if (prev.TokenType == TSqlTokenType.Integer || prev.TokenType == TSqlTokenType.Real)
            {
                var beforeNum = PreviousSignificant(tokens, IndexOfToken(tokens, prev));
                return beforeNum != null && IsSelectStarKeyword(beforeNum)
                    && string.Equals(Unbracket(beforeNum.Text), "TOP", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool IsSelectStarKeyword(TSqlParserToken t)
        {
            if (t == null || string.IsNullOrEmpty(t.Text)) return false;
            switch (Unbracket(t.Text).ToUpperInvariant())
            {
                case "SELECT":
                case "DISTINCT":
                case "ALL":
                case "TOP":
                case "PERCENT":
                case "TIES":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsHoverableToken(TSqlParserToken t)
        {
            return IsIdentifierToken(t) || IsWordLike(t);
        }

        private static TSqlParserToken PreviousHoverableToken(List<TSqlParserToken> tokens, int fromIndex)
        {
            for (int i = fromIndex - 1; i >= 0; i--)
            {
                var t = tokens[i];
                if (t == null || IsInsignificant(t)) continue;
                if (IsHoverableToken(t)) return t;
                if (t.TokenType != TSqlTokenType.Dot) break;
            }
            return null;
        }

        private static bool IsIdentifierToken(TSqlParserToken t)
        {
            if (t == null) return false;
            return t.TokenType == TSqlTokenType.Identifier || t.TokenType == TSqlTokenType.QuotedIdentifier;
        }

        private static bool IsPartialObjectName(TSqlParserToken t)
        {
            if (t == null) return false;
            if (IsIdentifierToken(t)) return true;
            return t.TokenType == TSqlTokenType.Integer || t.TokenType == TSqlTokenType.Real;
        }

        private static bool IsWordLike(TSqlParserToken t)
        {
            if (t == null || string.IsNullOrEmpty(t.Text) || IsInsignificant(t)) return false;
            if (IsPartialObjectName(t)) return true;
            foreach (char c in t.Text)
            {
                if (!char.IsLetter(c) && c != '_') return false;
            }
            return true;
        }

        private static string Unbracket(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Trim('[', ']', '"');
        }
    }
}
