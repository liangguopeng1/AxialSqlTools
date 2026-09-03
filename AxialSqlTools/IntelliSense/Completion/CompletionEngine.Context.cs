using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.Text;

namespace AxialSqlTools
{
    namespace IntelliSense
    {
        public partial class CompletionEngine
        {
            #region 上下文判定

            private sealed class FromObjectNameContext
            {
                public bool InFromClause;
                public List<string> Segments = new List<string>();
                public string Partial = string.Empty;
                public bool AfterDot;
                public bool UsesDoubleDot;
                /// <summary>连续三个及以上点（newdaku...）不是合法限定名，不应再出对象补全。</summary>
                public bool ExcessiveDots;
                public int PartialStartOffset = -1;
                /// <summary>限定名第一个片段的原文起点（含未闭合 [）。IP 链接服务器整段替换用。</summary>
                public int NameStartOffset = -1;
            }

            /// <summary>
            /// 原文解析 EXEC/EXECUTE 后的目标名：caiwu..pro / caiwu.dbo.pro / pro
            /// 不依赖 ScriptDom 对 .. 的分词。
            /// </summary>
            private static bool TryParseExecObjectName(string text, int caretOffset, out FromObjectNameContext ctx)
            {
                return TryParseQualifiedObjectNameRaw(text, caretOffset, isExec: true, out ctx);
            }

            /// <summary>
            /// 原文解析 FROM/JOIN 后的对象名。未闭合 [xxx 时 ScriptDom 丢 token / 空流，必须走原文。
            /// </summary>
            private static bool TryParseFromObjectNameRaw(string text, int caretOffset, out FromObjectNameContext ctx)
            {
                return TryParseQualifiedObjectNameRaw(text, caretOffset, isExec: false, out ctx);
            }

            /// <summary>
            /// 从光标向前扫 库.架构.对象 / [quoted] 片段。
            /// isExec=true 锚定 EXEC/EXECUTE；false 锚定 FROM/JOIN/APPLY。
            /// </summary>
            private static bool TryParseQualifiedObjectNameRaw(
                string text, int caretOffset, bool isExec, out FromObjectNameContext ctx)
            {
                ctx = new FromObjectNameContext();
                if (string.IsNullOrEmpty(text) || caretOffset <= 0) return false;

                int i = Math.Min(caretOffset, text.Length) - 1;
                int wordEnd = i + 1;
                while (i >= 0 && IsIdentChar(text[i])) i--;
                int partialStart = i + 1;
                // 未闭合 [partial：把 '[' 算进替换起点，避免提交时变成 [[name]
                if (i >= 0 && text[i] == '[')
                {
                    if (partialStart < wordEnd)
                        ctx.Partial = text.Substring(partialStart, wordEnd - partialStart);
                    ctx.PartialStartOffset = i;
                    i--;
                }
                else if (partialStart < wordEnd)
                {
                    ctx.Partial = text.Substring(partialStart, wordEnd - partialStart);
                    ctx.PartialStartOffset = partialStart;
                }

                var segments = new List<string>();
                int nameStart = -1;
                // AfterDot = 光标/partial 紧跟在点号后（dbo.| / dbo.Ta|），不是「限定名里曾经出现过点」
                // 否则 schema.table gr| 会被误判，GROUP BY / WHERE 等关键字被抑制
                bool afterDot = i >= 0 && text[i] == '.';
                bool doubleDot = false;
                bool extraDots = false;
                int guard = 0;
                int maxGuard = Math.Max(32, (caretOffset + 1) * 4);
                while (i >= 0)
                {
                    if (++guard > maxGuard)
                        break;
                    while (i >= 0 && (text[i] == ' ' || text[i] == '\t' || text[i] == '\n' || text[i] == '\r')) i--;
                    if (i < 0) break;
                    if (text[i] == ';') break;

                    if (text[i] == '.')
                    {
                        int dots = 0;
                        while (i >= 0 && text[i] == '.')
                        {
                            dots++;
                            i--;
                        }
                        if (dots > 2) extraDots = true;
                        else if (dots == 2) doubleDot = true;
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
                    if (segStart >= segEnd)
                        continue;
                    string seg = UnbracketIdentifier(text.Substring(segStart, segEnd - segStart));
                    if (IsRawObjectNameAnchorKeyword(seg, isExec))
                    {
                        i = segEnd - 1;
                        break;
                    }
                    // FROM 目标名区域已结束（WHERE/ORDER/...）：不能再当 FROM 补全
                    if (!isExec && IsFromObjectNameTerminatorKeyword(seg))
                        return false;
                    if (!string.IsNullOrEmpty(seg))
                    {
                        segments.Insert(0, seg);
                        nameStart = segStart;
                    }
                }

                while (i >= 0 && (text[i] == ' ' || text[i] == '\t' || text[i] == '\n' || text[i] == '\r')) i--;
                if (i < 0) return false;

                // 逗号分隔的下一张表：FROM a, [b
                if (!isExec && text[i] == ',')
                {
                    if (!HasFromKeywordInStatementRaw(text, i))
                        return false;
                    ctx.InFromClause = true;
                    ctx.Segments = segments;
                    ctx.AfterDot = afterDot;
                    ctx.UsesDoubleDot = doubleDot;
                    ctx.ExcessiveDots = extraDots;
                    ctx.NameStartOffset = AdjustNameStartForOpenBracket(text, nameStart >= 0 ? nameStart : ctx.PartialStartOffset);
                    return true;
                }

                int kwEnd = i + 1;
                while (i >= 0 && IsIdentChar(text[i])) i--;
                string kw = text.Substring(i + 1, kwEnd - (i + 1));
                if (!IsRawObjectNameAnchorKeyword(kw, isExec))
                    return false;

                // 「FROM t where|」：partial 本身是子句关键字，且前面已有表名 → 不是在输表名
                // in/le/ri 等可能是 INNER/LEFT/RIGHT JOIN 前缀，仍算 FROM 子句
                if (!isExec && segments.Count > 0
                    && IsFromObjectNameTerminatorKeyword(ctx.Partial)
                    && !IsFromJoinOrClausePrefix(ctx.Partial))
                    return false;

                if (!isExec)
                    ctx.InFromClause = true;
                ctx.Segments = segments;
                ctx.AfterDot = afterDot;
                ctx.UsesDoubleDot = doubleDot;
                ctx.ExcessiveDots = extraDots;
                ctx.NameStartOffset = AdjustNameStartForOpenBracket(text, nameStart >= 0 ? nameStart : ctx.PartialStartOffset);
                return true;
            }

            /// <summary>FROM 后 JOIN/子句关键字的前缀（in→INNER JOIN、wh→WHERE、ord→ORDER BY）。</summary>
            private static bool IsFromJoinOrClausePrefix(string partial)
            {
                if (string.IsNullOrEmpty(partial)) return false;
                string p = partial.ToUpperInvariant();
                string[] candidates =
                {
                    "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "JOIN", "APPLY",
                    "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT",
                    "PIVOT", "UNPIVOT"
                };
                foreach (var c in candidates)
                {
                    if (c.StartsWith(p, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }

            /// <summary>
            /// 行首像新开一批：语句关键字（se→SELECT）或片段（ss→ssf）。
            /// JOIN/WHERE/ORDER 续写前缀不算（in/wh/ord 仍留在当前句）。
            /// ex 同时是 EXEC 与 EXCEPT：行首按新语句，EXCEPT 仍在 BatchStart 的 AfterFrom 里。
            /// </summary>
            private static bool LooksLikeNewBatchPrefix(string partial)
            {
                if (string.IsNullOrEmpty(partial)) return false;
                if (IsExecStatementPrefix(partial))
                    return true;
                if (IsFromJoinOrClausePrefix(partial) || IsClauseContinuationPrefix(partial))
                    return false;
                return IsTopLevelStatementPrefix(partial) || HasMatchingSnippetPrefix(partial);
            }

            /// <summary>ex/exe/exec → EXEC；单字母 e 仍留给 EXCEPT/EXISTS。</summary>
            private static bool IsExecStatementPrefix(string partial)
            {
                if (string.IsNullOrEmpty(partial) || partial.Length < 2) return false;
                string p = partial.ToUpperInvariant();
                return "EXEC".StartsWith(p, StringComparison.Ordinal)
                    || "EXECUTE".StartsWith(p, StringComparison.Ordinal);
            }

            /// <summary>行首像新语句：se→SELECT、up→UPDATE（相对 SESSION_USER 等函数优先）。</summary>
            private static bool IsTopLevelStatementPrefix(string partial)
            {
                if (string.IsNullOrEmpty(partial)) return false;
                string p = partial.ToUpperInvariant();
                foreach (var kw in TopLevelKeywords)
                {
                    if (kw.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            /// <summary>是否有代码片段前缀匹配（ss→ssf）；用于无分号行首保持 BatchStart。</summary>
            private static bool HasMatchingSnippetPrefix(string partial)
            {
                if (string.IsNullOrEmpty(partial) || !SettingsManager.GetUseSnippets())
                    return false;
                foreach (var snippet in SnippetService.GetAllSnippets())
                {
                    if (string.IsNullOrEmpty(snippet.Prefix)) continue;
                    if (snippet.Prefix.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            /// <summary>WHERE/ORDER 后行首续写前缀（ord→ORDER BY），与新开 SELECT 区分。</summary>
            private static bool IsClauseContinuationPrefix(string partial)
            {
                if (string.IsNullOrEmpty(partial)) return false;
                string p = partial.ToUpperInvariant();
                string[] candidates =
                {
                    "ORDER", "GROUP", "HAVING", "UNION", "EXCEPT", "INTERSECT",
                    "AND", "OR", "WHERE", "ASC", "DESC", "OFFSET", "FETCH"
                };
                foreach (var c in candidates)
                {
                    if (c.StartsWith(p, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }

            private static bool IsRawObjectNameAnchorKeyword(string kw, bool isExec)
            {
                if (string.IsNullOrEmpty(kw)) return false;
                if (isExec)
                {
                    return string.Equals(kw, "EXEC", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(kw, "EXECUTE", StringComparison.OrdinalIgnoreCase);
                }
                return string.Equals(kw, "FROM", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kw, "JOIN", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kw, "APPLY", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kw, "UPDATE", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kw, "DELETE", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kw, "INSERT", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kw, "INTO", StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>出现在 FROM 表名之后的子句关键字：原文扫到这些则不是在输表名。</summary>
            private static bool IsFromObjectNameTerminatorKeyword(string kw)
            {
                if (string.IsNullOrEmpty(kw)) return false;
                switch (kw.ToUpperInvariant())
                {
                    case "WHERE":
                    case "GROUP":
                    case "ORDER":
                    case "HAVING":
                    case "UNION":
                    case "EXCEPT":
                    case "INTERSECT":
                    case "ON":
                    case "SET":
                    case "OUTPUT":
                    case "OPTION":
                    case "SELECT":
                    case "INSERT":
                    case "UPDATE":
                    case "DELETE":
                    case "MERGE":
                    case "VALUES":
                    case "INTO":
                    case "WHEN":
                    case "ELSE":
                    case "END":
                    case "AND":
                    case "OR":
                    case "NOT":
                    case "IN":
                    case "EXISTS":
                    case "BETWEEN":
                    case "LIKE":
                    case "IS":
                    case "AS":
                    case "INNER":
                    case "LEFT":
                    case "RIGHT":
                    case "FULL":
                    case "CROSS":
                    case "OUTER":
                    case "JOIN":
                    case "APPLY":
                    case "PIVOT":
                    case "UNPIVOT":
                        return true;
                    default:
                        return false;
                }
            }

            /// <summary>从当前位置向左找是否仍在同一语句的 FROM 之后（用于 FROM a, b）。</summary>
            private static bool HasFromKeywordInStatementRaw(string text, int beforeOffset)
            {
                int i = Math.Min(beforeOffset, text.Length) - 1;
                while (i >= 0)
                {
                    if (text[i] == ';' || text[i] == '\n' || text[i] == '\r')
                        return false;
                    if (i >= 3
                        && (text[i] == 'M' || text[i] == 'm')
                        && (text[i - 1] == 'O' || text[i - 1] == 'o')
                        && (text[i - 2] == 'R' || text[i - 2] == 'r')
                        && (text[i - 3] == 'F' || text[i - 3] == 'f')
                        && (i == 3 || !IsIdentChar(text[i - 4]))
                        && (i + 1 >= text.Length || !IsIdentChar(text[i + 1])))
                    {
                        return true;
                    }
                    i--;
                }
                return false;
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
                if (connInfo != null && !string.IsNullOrWhiteSpace(tref.LinkedServer)
                    && !string.IsNullOrWhiteSpace(tref.Database))
                {
                    target = MetadataCatalogService.Instance.GetOrBuildLinkedCatalog(
                        connInfo, tref.LinkedServer, tref.Database);
                }
                else if (connInfo != null && !string.IsNullOrWhiteSpace(tref.Database))
                {
                    if (catalog == null || !string.Equals(catalog.Database, tref.Database, StringComparison.OrdinalIgnoreCase))
                    {
                        // 跨库：只读缓存 + 后台构建，不在补全热路径同步连库/等待
                        MetadataCatalogService.Instance.EnsureCatalogBuilding(connInfo, tref.Database);
                        target = MetadataCatalogService.Instance.GetCachedCatalogOrDisk(connInfo, tref.Database);
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
                return MetadataCatalogService.Instance.GetCachedCatalog(connInfo, database);
            }

            private CompletionContext GetContext(List<TSqlParserToken> tokens, int localOffset, FromObjectNameContext fromName, out string prefix)
            {
                prefix = ExtractPrefix(tokens, localOffset);
                int caretTokenIndex = FindTokenIndexBefore(tokens, localOffset);

                // JOIN ON / WHERE 中 alias. → 列（含 and dd.| 多条件），优先于 FROM 限定名
                if (TryGetMemberAccessPrefix(tokens, localOffset, out string memberPrefixEarly))
                {
                    string majorForDot = caretTokenIndex >= 0
                        ? FindNearestMajorClauseKeyword(tokens, caretTokenIndex, localOffset)
                        : null;
                    bool exprDot = majorForDot == "ON" || majorForDot == "WHERE" || majorForDot == "HAVING"
                        || majorForDot == "GROUP" || majorForDot == "ORDER" || majorForDot == "SET";
                    if (exprDot)
                    {
                        prefix = memberPrefixEarly;
                        return CompletionContext.MemberAccess;
                    }
                }

                // INSERT INTO t (|) 列清单优先于 INSERT 目标名（INTO 现为对象名锚点）
                if (TryParseInsertColumnList(tokens, localOffset, out _))
                    return CompletionContext.InsertColumnList;

                // FROM 目标名（含未闭合 [）优先；但若已越过 WHERE/GROUP/ORDER，不能再锁死 FromClause
                // JOIN ON 仍属 FROM 区域（可继续 INNER JOIN）——不含 alias. 列访问
                if (fromName != null && fromName.InFromClause)
                {
                    string majorKw = caretTokenIndex >= 0
                        ? FindNearestMajorClauseKeyword(tokens, caretTokenIndex, localOffset)
                        : null;
                    if (majorKw == "UPDATE")
                    {
                        prefix = fromName.Partial ?? string.Empty;
                        return CompletionContext.UpdateTarget;
                    }
                    if (majorKw == "DELETE")
                    {
                        prefix = fromName.Partial ?? string.Empty;
                        return CompletionContext.DeleteTarget;
                    }
                    if (majorKw == "INSERT" || majorKw == "INTO")
                    {
                        prefix = fromName.Partial ?? string.Empty;
                        return CompletionContext.InsertTarget;
                    }
                    if (majorKw == "ON")
                    {
                        if (LooksLikeContinuingFromKeyword(fromName.Partial ?? prefix))
                        {
                            prefix = fromName.Partial ?? string.Empty;
                            return CompletionContext.FromClause;
                        }
                    }
                    else if (majorKw != "WHERE" && majorKw != "HAVING" && majorKw != "GROUP" && majorKw != "ORDER"
                        && majorKw != "UNION" && majorKw != "EXCEPT" && majorKw != "INTERSECT")
                    {
                        prefix = fromName.Partial ?? string.Empty;
                        return CompletionContext.FromClause;
                    }
                }

                if (TryGetMemberAccessPrefix(tokens, localOffset, out string memberPrefix))
                {
                    prefix = memberPrefix;
                    return CompletionContext.MemberAccess;
                }

                // CAST/CONVERT/PARSE 的类型参数位置：提示数据类型
                if (TryGetDataTypeContext(tokens, localOffset))
                    return CompletionContext.DataType;

                // 等号/比较符右侧尚未输入内容：不提示（等值待填字面量）
                if (string.IsNullOrEmpty(prefix) && IsImmediatelyAfterComparisonOperator(tokens, localOffset))
                    return CompletionContext.Unknown;

                // 已闭合的 ) ] 之后空前缀：不提示（SELECT NEWID()| 按 F5 后勿再弹）
                // 注意：函数实参内仍要提示列（ISNULL(h|, )），插入后防重弹靠 KeyHandler 短时抑制
                if (string.IsNullOrEmpty(prefix) && IsImmediatelyAfterClosedGroup(tokens, localOffset))
                    return CompletionContext.Unknown;

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

                // 越过表达式噪声（=1、列名、点号等）找到最近的子句关键字
                // 否则 where col=1 an| 会落成 Unknown，a/an 不提示 AND
                string kw = FindNearestClauseKeyword(tokens, caretTokenIndex, localOffset);

                switch (kw)
                {
                    case ";":
                    case "GO":
                        return CompletionContext.BatchStart;
                    case "SELECT":
                        // 仅看「当前 SELECT」之后是否已有 FROM，勿扫上一句
                        if (!SeenFromAfterCurrentSelect(tokens, caretTokenIndex, localOffset))
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
                        return CompletionContext.WhereClause;
                    case "HAVING":
                        return CompletionContext.HavingClause;
                    case "ON":
                        if (LooksLikeContinuingFromKeyword(prefix))
                            return CompletionContext.FromClause;
                        return CompletionContext.WhereClause;
                    case "AND":
                    case "OR":
                        {
                            string major = FindNearestMajorClauseKeyword(tokens, caretTokenIndex, localOffset);
                            if (major == "ON")
                            {
                                if (LooksLikeContinuingFromKeyword(prefix))
                                    return CompletionContext.FromClause;
                                return CompletionContext.WhereClause;
                            }
                            if (major == "FROM" || major == "JOIN")
                                return CompletionContext.FromClause;
                            if (major == "HAVING")
                                return CompletionContext.HavingClause;
                            return CompletionContext.WhereClause;
                        }
                    case "ORDER":
                    case "GROUP":
                    case "PARTITION":
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
                    case "WITH":
                    case "MERGE":
                    case "TRUNCATE":
                    case "DECLARE":
                    case "IF":
                    case "BEGIN":
                    case "DROP":
                        return CompletionContext.BatchStart;
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

            /// <summary>光标在字符串字面量或注释内（输入值/注释不应出补全）。</summary>
            internal static bool IsInsideStringOrComment(string text, int caret)
            {
                return ScanPrefixState(text, caret, out _);
            }

            /// <summary>
            /// 一次扫描 0→caret：返回光标处是否在字符串/注释内，以及最后一条 USE 库名（跨 GO；注释/字符串内的 USE 忽略）。
            /// 合并 IsInsideStringOrComment + GetActiveUseDatabase 的两次全文扫描为一次，降低大脚本热路径开销。
            /// 增量缓存只保留指纹与扫描状态，不持有全文，避免 1MB+ 文档被静态引用钉住。
            /// </summary>
            private static readonly object PrefixScanGate = new object();
            private static int _psTo;
            private static int _psLen;
            private static char _ps0, _psQ1, _psMid, _psQ3, _psLast;
            private static int _psHq1, _psHmid, _psHq3;
            private static bool _psLine, _psBlock, _psStr;
            private static string _psUse;
            private const int PrefixCpGap = 64 * 1024;
            private const int PrefixCpMax = 40;
            private static int[] _psCpAt;
            private static bool[] _psCpLine, _psCpBlock, _psCpStr;
            private static string[] _psCpUse;
            private static int _psCpCount;

            internal static bool ScanPrefixState(string text, int caret, out string useDatabase)
            {
                return ScanPrefixState(text, caret, out useDatabase, cacheResult: true);
            }

            /// <param name="cacheResult">
            /// false：给目录头尾裁剪等短副本用，不读不写静态缓存，避免 96KB clip 冲掉 1MB 原文的增量扫描状态。
            /// </param>
            internal static bool ScanPrefixState(string text, int caret, out string useDatabase, bool cacheResult)
            {
                useDatabase = null;
                if (string.IsNullOrEmpty(text) || caret <= 0) return false;
                int n = Math.Min(caret, text.Length);
                if (!cacheResult)
                {
                    bool inLineU = false, inBlockU = false, inStrU = false;
                    string lastU = null;
                    ScanPrefixRange(text, 0, n, ref inLineU, ref inBlockU, ref inStrU, ref lastU);
                    useDatabase = lastU;
                    return inLineU || inBlockU || inStrU;
                }
                lock (PrefixScanGate)
                {
                    int start = 0;
                    bool inLineComment = false, inBlockComment = false, inString = false;
                    string last = null;
                    bool resumed = false;
                    if (_psTo > 0 && _psTo <= n && CanReusePrefixScan(text, n)
                        && PrefixFingerprintMatches(text, _psTo))
                    {
                        start = _psTo;
                        inLineComment = _psLine;
                        inBlockComment = _psBlock;
                        inString = _psStr;
                        last = _psUse;
                        resumed = true;
                    }
                    else if (_psTo > n && text.Length == _psLen && PrefixFingerprintMatches(text, _psTo)
                             && TryResumePrefixCheckpoint(n, out start, out inLineComment, out inBlockComment, out inString, out last))
                    {
                        resumed = true;
                    }
                    if (!resumed)
                        ClearPrefixCheckpoints();
                    int i = start;
                    while (i < n)
                    {
                        int gapAt = ((i / PrefixCpGap) + 1) * PrefixCpGap;
                        int chunkEnd = gapAt < n ? gapAt : n;
                        if (chunkEnd <= i) chunkEnd = n;
                        ScanPrefixRange(text, i, chunkEnd, ref inLineComment, ref inBlockComment, ref inString, ref last);
                        i = chunkEnd;
                        if (i < n)
                            RecordPrefixCheckpoint(i, inLineComment, inBlockComment, inString, last);
                    }
                    SavePrefixFingerprint(text, n, inLineComment, inBlockComment, inString, last);
                    useDatabase = last;
                    return inLineComment || inBlockComment || inString;
                }
            }

            private static void ClearPrefixCheckpoints()
            {
                _psCpCount = 0;
            }

            private static void RecordPrefixCheckpoint(int at, bool line, bool block, bool str, string use)
            {
                if (at <= 0) return;
                if (_psCpAt == null)
                {
                    _psCpAt = new int[PrefixCpMax];
                    _psCpLine = new bool[PrefixCpMax];
                    _psCpBlock = new bool[PrefixCpMax];
                    _psCpStr = new bool[PrefixCpMax];
                    _psCpUse = new string[PrefixCpMax];
                }
                for (int i = 0; i < _psCpCount; i++)
                {
                    if (_psCpAt[i] == at)
                    {
                        _psCpLine[i] = line;
                        _psCpBlock[i] = block;
                        _psCpStr[i] = str;
                        _psCpUse[i] = use;
                        return;
                    }
                }
                if (_psCpCount >= PrefixCpMax) return;
                int k = _psCpCount++;
                _psCpAt[k] = at;
                _psCpLine[k] = line;
                _psCpBlock[k] = block;
                _psCpStr[k] = str;
                _psCpUse[k] = use;
            }

            private static bool TryResumePrefixCheckpoint(
                int n, out int start, out bool line, out bool block, out bool str, out string use)
            {
                start = 0;
                line = false;
                block = false;
                str = false;
                use = null;
                int best = -1;
                for (int i = 0; i < _psCpCount; i++)
                {
                    int at = _psCpAt[i];
                    if (at <= n && (best < 0 || at > _psCpAt[best]))
                        best = i;
                }
                if (best < 0) return false;
                start = _psCpAt[best];
                line = _psCpLine[best];
                block = _psCpBlock[best];
                str = _psCpStr[best];
                use = _psCpUse[best];
                return start > 0;
            }

            /// <summary>仅在同文档微调（逐字输入）时复用扫描状态；换文档/大段粘贴必须重扫，避免误用注释状态。</summary>
            private static bool CanReusePrefixScan(string text, int n)
            {
                if (_psLen <= 0 || _psTo <= 0) return false;
                int len = text.Length;
                if (len == _psLen) return n >= _psTo;
                int grew = len - _psLen;
                return grew > 0 && grew < 256 && n >= _psTo;
            }

            private static bool PrefixFingerprintMatches(string text, int to)
            {
                if (to <= 0 || to > text.Length) return false;
                if (text[0] != _ps0 || text[to - 1] != _psLast) return false;
                if (text[to / 4] != _psQ1 || text[to / 2] != _psMid || text[(to * 3) / 4] != _psQ3)
                    return false;
                return WindowHash(text, to / 4, to) == _psHq1
                    && WindowHash(text, to / 2, to) == _psHmid
                    && WindowHash(text, (to * 3) / 4, to) == _psHq3;
            }

            private static void SavePrefixFingerprint(string text, int to, bool line, bool block, bool str, string use)
            {
                if (to <= 0 || to > text.Length)
                {
                    _psTo = 0;
                    _psLen = 0;
                    _psUse = null;
                    ClearPrefixCheckpoints();
                    return;
                }
                _psTo = to;
                _psLen = text.Length;
                _ps0 = text[0];
                _psQ1 = text[to / 4];
                _psMid = text[to / 2];
                _psQ3 = text[(to * 3) / 4];
                _psLast = text[to - 1];
                _psHq1 = WindowHash(text, to / 4, to);
                _psHmid = WindowHash(text, to / 2, to);
                _psHq3 = WindowHash(text, (to * 3) / 4, to);
                _psLine = line;
                _psBlock = block;
                _psStr = str;
                _psUse = use;
            }

            private static int WindowHash(string text, int center, int to)
            {
                int h = 17;
                int i = center - 32;
                if (i < 0) i = 0;
                int end = center + 32;
                if (end > to) end = to;
                for (; i < end; i++)
                    h = unchecked(h * 31 + text[i]);
                return h;
            }

            private static void ScanPrefixRange(
                string text, int start, int n,
                ref bool inLineComment, ref bool inBlockComment, ref bool inString, ref string last)
            {
                for (int i = start; i < n; i++)
                {
                    char c = text[i];
                    char next = i + 1 < text.Length ? text[i + 1] : '\0';
                    if (inLineComment)
                    {
                        if (c == '\n' || c == '\r') inLineComment = false;
                        continue;
                    }
                    if (inBlockComment)
                    {
                        if (c == '*' && next == '/')
                        {
                            inBlockComment = false;
                            i++;
                        }
                        continue;
                    }
                    if (inString)
                    {
                        if (c == '\'')
                        {
                            if (next == '\'') i++;
                            else inString = false;
                        }
                        continue;
                    }
                    if (c == '-' && next == '-')
                    {
                        inLineComment = true;
                        i++;
                        continue;
                    }
                    if (c == '/' && next == '*')
                    {
                        inBlockComment = true;
                        i++;
                        continue;
                    }
                    if (c == '\'')
                    {
                        inString = true;
                        continue;
                    }
                    if ((c == 'N' || c == 'n') && next == '\'')
                    {
                        bool identBefore = i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
                        if (!identBefore)
                        {
                            inString = true;
                            i++;
                            continue;
                        }
                    }
                    if (IsUseKeywordAt(text, i, text.Length))
                    {
                        int j = i + 3;
                        int len = text.Length;
                        while (j < len && char.IsWhiteSpace(text[j])) j++;
                        if (j >= len) continue;
                        string db = null;
                        if (text[j] == '[')
                        {
                            int close = text.IndexOf(']', j + 1);
                            if (close < 0 || close > len)
                                db = text.Substring(j + 1, Math.Max(0, len - j - 1)).Trim();
                            else
                            {
                                db = text.Substring(j + 1, close - j - 1).Trim();
                                i = close;
                            }
                        }
                        else
                        {
                            int k = j;
                            while (k < len && IsIdentChar(text[k])) k++;
                            if (k > j)
                            {
                                db = text.Substring(j, k - j);
                                i = k - 1;
                            }
                        }
                        if (!string.IsNullOrEmpty(db)) last = UnbracketIdentifier(db);
                    }
                }
            }

            /// <summary>光标前最后一条 USE 的库名（含未执行的脚本；跨 GO）。注释/字符串内的 USE 忽略。</summary>
            internal static string GetActiveUseDatabase(string text, int caret)
            {
                return GetActiveUseDatabase(text, caret, cacheResult: true);
            }

            internal static string GetActiveUseDatabase(string text, int caret, bool cacheResult)
            {
                ScanPrefixState(text, caret, out string useDb, cacheResult);
                return useDb;
            }

            private static bool IsUseKeywordAt(string text, int i, int n)
            {
                if (i + 3 > n) return false;
                if ((text[i] != 'U' && text[i] != 'u') ||
                    (text[i + 1] != 'S' && text[i + 1] != 's') ||
                    (text[i + 2] != 'E' && text[i + 2] != 'e'))
                    return false;
                if (i > 0 && IsIdentChar(text[i - 1])) return false;
                if (i + 3 < n && IsIdentChar(text[i + 3])) return false;
                return true;
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

                var parts = new List<string>();
                string partial = string.Empty;
                // AfterDot：仅表示光标在点号后继续输段（dbo.| / dbo.Ta|），
                // 不能因 schema.table 内部的点而置位，否则表后输 gr 不提示 GROUP BY
                bool afterDot = false;
                int nameStart = -1;
                int i = idx;

                var cur = tokens[i];
                if (cur != null && cur.TokenType == TSqlTokenType.Dot)
                {
                    afterDot = true;
                    i--;
                    int extra = ConsumeConsecutiveDots(tokens, ref i);
                    int run = 1 + extra;
                    if (run > 2) ctx.ExcessiveDots = true;
                    else if (run == 2) ctx.UsesDoubleDot = true;
                }
                else if (cur != null && IsWordLikeToken(cur) && cur.Offset + cur.Text.Length >= localOffset)
                {
                    // 含 or/in/gr 等：ScriptDom 可能已把 partial 标成关键字 token
                    partial = cur.Text.Substring(0, Math.Max(0, localOffset - cur.Offset));
                    ctx.PartialStartOffset = cur.Offset;
                    nameStart = cur.Offset;
                    var beforePartial = PreviousSignificantToken(tokens, idx);
                    if (beforePartial != null && beforePartial.TokenType == TSqlTokenType.Dot)
                        afterDot = true;
                    i--;
                }
                else if (cur != null && IsPartialObjectNameToken(cur))
                {
                    parts.Insert(0, UnbracketIdentifier(cur.Text));
                    nameStart = cur.Offset;
                    i--;
                }

                int parenDepth = 0;
                while (i >= 0)
                {
                    while (i >= 0 && IsInsignificantToken(tokens[i])) i--;
                    if (i < 0) break;

                    var tok = tokens[i];
                    if (tok.TokenType == TSqlTokenType.RightParenthesis)
                    {
                        parenDepth++;
                        i--;
                        continue;
                    }
                    if (tok.TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        if (parenDepth > 0) parenDepth--;
                        i--;
                        continue;
                    }

                    if (tok.TokenType == TSqlTokenType.Dot)
                    {
                        // 允许跨行收集：FROM db.table aa\nin| 的表名在上一行
                        if (parenDepth > 0) { i--; continue; }
                        i--;
                        int extra = ConsumeConsecutiveDots(tokens, ref i);
                        int run = 1 + extra;
                        if (run > 2) ctx.ExcessiveDots = true;
                        else if (run == 2) ctx.UsesDoubleDot = true;
                        if (i >= 0 && parenDepth == 0 && IsPartialObjectNameToken(tokens[i]))
                        {
                            parts.Insert(0, UnbracketIdentifier(tokens[i].Text));
                            nameStart = tokens[i].Offset;
                            i--;
                        }
                        continue;
                    }

                    if (IsPartialObjectNameToken(tok) || IsWordLikeToken(tok))
                    {
                        string text = tok.Text?.ToUpperInvariant();
                        if (parenDepth == 0 && IsFromClauseAnchorKeyword(text))
                        {
                            ctx.InFromClause = true;
                            break;
                        }
                        // ON 条件仍属 FROM：清空 ON 右侧表达式误收的标识符，继续找 JOIN 表
                        if (parenDepth == 0 && text == "ON")
                        {
                            ctx.InFromClause = true;
                            parts.Clear();
                            i--;
                            continue;
                        }
                        if (parenDepth == 0 && IsFromScanStopKeyword(text))
                            break;
                        // 逗号：继续向前找 FROM（SELECT a,b FR 不能当 FROM 子句）
                        if (parenDepth == 0 && text == ",")
                        {
                            i--;
                            continue;
                        }
                        // 表名/别名可跨行（FROM t aa\nin|）；partial 本身已在上方截取
                        if (parenDepth == 0 && IsPartialObjectNameToken(tok))
                        {
                            parts.Insert(0, UnbracketIdentifier(tok.Text));
                            nameStart = tok.Offset;
                        }
                        i--;
                        continue;
                    }

                    string sym = tok.Text?.ToUpperInvariant();
                    if (sym == ";" || sym == "GO")
                        break;
                    i--;
                }

                ctx.Segments = parts;
                ctx.Partial = partial;
                ctx.AfterDot = afterDot;
                ctx.NameStartOffset = nameStart >= 0 ? nameStart : ctx.PartialStartOffset;
                return ctx;
            }

            /// <summary>从当前 i 再吃连续 Dot（已跳过空白）。返回额外吃掉的点数；i 停在点号前的有效 token。</summary>
            private static int ConsumeConsecutiveDots(List<TSqlParserToken> tokens, ref int i)
            {
                int n = 0;
                while (i >= 0)
                {
                    while (i >= 0 && IsInsignificantToken(tokens[i])) i--;
                    if (i < 0 || tokens[i] == null || tokens[i].TokenType != TSqlTokenType.Dot)
                        break;
                    n++;
                    i--;
                }
                while (i >= 0 && IsInsignificantToken(tokens[i])) i--;
                return n;
            }

            private static bool IsFromClauseAnchorKeyword(string text)
            {
                return text == "FROM" || text == "JOIN" || text == "APPLY"
                    || text == "UPDATE" || text == "DELETE" || text == "INSERT" || text == "INTO";
            }

            private static bool IsFromScanStopKeyword(string text)
            {
                if (string.IsNullOrEmpty(text)) return false;
                switch (text)
                {
                    case "SELECT":
                    case "WHERE":
                    case "SET":
                    case "INTO":
                    case "UPDATE":
                    case "DELETE":
                    case "INSERT":
                    case "EXEC":
                    case "EXECUTE":
                    case "USE":
                    case "CREATE":
                    case "ALTER":
                    case "GROUP":
                    case "ORDER":
                    case "HAVING":
                    case "UNION":
                    case "EXCEPT":
                    case "INTERSECT":
                        return true;
                    default:
                        return false;
                }
            }

            private int ComputeReplaceStartOffset(int batchStart, int localOffset, string prefix, FromObjectNameContext fromName, List<TSqlParserToken> tokens)
            {
                // IP 链接服务器：192. / 192.168. 被点拆成多段，必须整段换成 [ip]，不能只替换最后一个点后
                if (fromName != null && fromName.InFromClause && JoinNumericFromSegments(fromName) != null && fromName.NameStartOffset >= 0)
                    return batchStart + fromName.NameStartOffset;
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

            /// <summary>链接服务器名含点或以数字开头，T-SQL 必须加 []。</summary>
            private static string FormatLinkedServerInsert(string name)
            {
                return BracketIdent(UnbracketIdentifier(name));
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

            private static readonly HashSet<string> DataTypeFunctionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CAST", "CONVERT", "PARSE", "TRY_CAST", "TRY_CONVERT", "TRY_PARSE"
            };

            /// <summary>光标是否在 CAST/CONVERT/PARSE/TRY_* 的类型参数位置（AS 后，或 CONVERT 首参）。</summary>
            private bool TryGetDataTypeContext(List<TSqlParserToken> tokens, int localOffset)
            {
                if (tokens == null || tokens.Count == 0) return false;
                int caretIdx = FindTokenIndexBefore(tokens, localOffset);
                if (caretIdx < 0) return false;
                int depth = 0;
                for (int i = caretIdx; i >= 0; i--)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    if (t.TokenType == TSqlTokenType.RightParenthesis) { depth++; continue; }
                    if (t.TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        if (depth > 0) { depth--; continue; }
                        var prev = PreviousSignificantToken(tokens, i);
                        if (prev == null) return false;
                        string fn = UnbracketIdentifier(prev.Text);
                        if (string.IsNullOrEmpty(fn) || !DataTypeFunctionNames.Contains(fn)) return false;
                        string fnUp = fn.ToUpperInvariant();
                        if (fnUp == "CONVERT" || fnUp == "TRY_CONVERT")
                        {
                            // 类型是第一个参数：左括号到光标之间不能有「顶层」逗号。
                            // 类型参数本身可能带精度括号（如 decimal(10,2)），其内部逗号不算越过类型。
                            int d = 0;
                            for (int j = i + 1; j <= caretIdx && j < tokens.Count; j++)
                            {
                                var tt = tokens[j];
                                if (tt == null || IsInsignificantToken(tt)) continue;
                                if (tt.Offset >= localOffset) break;
                                if (tt.TokenType == TSqlTokenType.LeftParenthesis) { d++; continue; }
                                if (tt.TokenType == TSqlTokenType.RightParenthesis) { if (d > 0) d--; continue; }
                                if (d == 0 && tt.TokenType == TSqlTokenType.Comma) return false;
                            }
                            return true;
                        }
                        // CAST/PARSE：类型在 AS 之后
                        bool sawAs = false;
                        for (int j = i + 1; j <= caretIdx && j < tokens.Count; j++)
                        {
                            var tt = tokens[j];
                            if (tt == null || IsInsignificantToken(tt)) continue;
                            if (tt.Offset >= localOffset) break;
                            if (string.Equals(tt.Text, "AS", StringComparison.OrdinalIgnoreCase))
                            {
                                sawAs = true;
                                continue;
                            }
                        }
                        return sawAs;
                    }
                    string kw = t.Text?.ToUpperInvariant();
                    if (kw == ";" || kw == "GO") return false;
                }
                return false;
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

            private static int FindTokenIndexBefore(List<TSqlParserToken> tokens, int offset)
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

            private static bool IsFromObjectToken(TSqlParserToken t)
            {
                if (t == null) return false;
                if (IsWordLikeToken(t) || IsPartialObjectNameToken(t) || IsIdentifierLike(t))
                    return true;
                return t.TokenType == TSqlTokenType.Variable;
            }

            /// <summary>FROM 四段名/IP 中可能被解析为 Integer/Numeric 的片段（如 192.）。</summary>
            private static bool IsPartialObjectNameToken(TSqlParserToken t)
            {
                if (t == null) return false;
                if (IsIdentifierLike(t)) return true;
                return t.TokenType == TSqlTokenType.Integer
                    || t.TokenType == TSqlTokenType.Real
                    || t.TokenType == TSqlTokenType.Numeric;
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
                    case "PARTITION":
                    case "EXEC":
                    case "EXECUTE":
                    case "INSERT":
                    case "UPDATE":
                    case "DELETE":
                    case "USE":
                    case "SET":
                    case "CREATE":
                    case "ALTER":
                    case "WITH":
                    case "MERGE":
                    case "TRUNCATE":
                    case "DECLARE":
                    case "IF":
                    case "BEGIN":
                    case "DROP":
                    case "GO":
                        return true;
                    default:
                        return t.Text == ";";
                }
            }

            /// <summary>
            /// 从光标 token 向前越过表达式（字面量/标识符/运算符），取最近的子句关键字。
            /// 例：WHERE cc.CreateDate=1 an| → WHERE。
            /// 正在输入的词本身被标成关键字时（or→OR）跳过；括号内的 FROM/SELECT 不作为外层锚点。
            /// </summary>
            private static string FindNearestClauseKeyword(List<TSqlParserToken> tokens, int caretTokenIndex, int localOffset)
            {
                return FindNearestClauseKeywordCore(tokens, caretTokenIndex, localOffset, skipAndOr: false);
            }

            /// <summary>跳过 AND/OR，用于区分 JOIN ON 区域与 WHERE 区域。</summary>
            private static string FindNearestMajorClauseKeyword(List<TSqlParserToken> tokens, int caretTokenIndex, int localOffset)
            {
                return FindNearestClauseKeywordCore(tokens, caretTokenIndex, localOffset, skipAndOr: true);
            }

            private static string FindNearestClauseKeywordCore(
                List<TSqlParserToken> tokens, int caretTokenIndex, int localOffset, bool skipAndOr)
            {
                if (tokens == null || caretTokenIndex < 0) return null;
                int parenDepth = 0;
                for (int i = caretTokenIndex; i >= 0; i--)
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
                        if (parenDepth > 0) parenDepth--;
                        continue;
                    }
                    if (parenDepth > 0) continue;
                    if (IsContextKeywordToken(t))
                    {
                        // 光标落在该词上（正在输入前缀）不当锚点：or| 可能是 ORDER BY
                        if (IsWordLikeToken(t)
                            && t.Offset < localOffset
                            && t.Offset + t.Text.Length >= localOffset)
                            continue;
                        string kw = t.Text.ToUpperInvariant();
                        if (skipAndOr && (kw == "AND" || kw == "OR"))
                            continue;
                        return kw;
                    }
                    string text = t.Text;
                    if (string.IsNullOrEmpty(text)) continue;
                    if (text == ";" || text.Equals("GO", StringComparison.OrdinalIgnoreCase))
                        break;
                }
                return null;
            }

            /// <summary>ON 之后继续写 INNER/LEFT/WHERE 等时仍走 FromClause。</summary>
            private static bool LooksLikeContinuingFromKeyword(string prefix)
            {
                if (string.IsNullOrEmpty(prefix)) return false;
                string p = prefix.TrimStart('[', ' ').ToUpperInvariant();
                // 单字母优先当别名（and g → GHS），in/gr/wh 仍走 JOIN/GROUP/WHERE
                if (p.Length < 2) return false;
                switch (p[0])
                {
                    case 'I':
                        return "INNER".StartsWith(p, StringComparison.Ordinal)
                            || "INTERSECT".StartsWith(p, StringComparison.Ordinal);
                    case 'L':
                        return "LEFT".StartsWith(p, StringComparison.Ordinal);
                    case 'R':
                        return "RIGHT".StartsWith(p, StringComparison.Ordinal);
                    case 'F':
                        return "FULL".StartsWith(p, StringComparison.Ordinal);
                    case 'C':
                        return "CROSS".StartsWith(p, StringComparison.Ordinal);
                    case 'J':
                        return "JOIN".StartsWith(p, StringComparison.Ordinal);
                    case 'O':
                        return "OUTER".StartsWith(p, StringComparison.Ordinal);
                    case 'W':
                        return "WHERE".StartsWith(p, StringComparison.Ordinal);
                    case 'G':
                        return "GROUP".StartsWith(p, StringComparison.Ordinal);
                    case 'U':
                        return "UNION".StartsWith(p, StringComparison.Ordinal);
                    case 'E':
                        return "EXCEPT".StartsWith(p, StringComparison.Ordinal);
                    default:
                        return false;
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

            /// <summary>把 FROM 里被点拆开的数字段拼回 192.168.1.148；含非数字段则不是 IP。</summary>
            private static string JoinNumericFromSegments(FromObjectNameContext fromName)
            {
                if (fromName == null || fromName.UsesDoubleDot) return null;
                var parts = new List<string>();
                if (fromName.Segments != null)
                {
                    for (int i = 0; i < fromName.Segments.Count; i++)
                    {
                        string s = StripNumericDots(fromName.Segments[i]);
                        if (string.IsNullOrEmpty(s) || !IsNumericOrIpPrefix(s))
                            return null;
                        parts.Add(s);
                    }
                }
                if (!string.IsNullOrEmpty(fromName.Partial))
                {
                    string p = StripNumericDots(fromName.Partial);
                    if (string.IsNullOrEmpty(p) || !IsNumericOrIpPrefix(p))
                        return null;
                    parts.Add(p);
                }
                if (parts.Count == 0) return null;
                return string.Join(".", parts);
            }

            private static string StripNumericDots(string s)
            {
                if (string.IsNullOrEmpty(s)) return s;
                return s.Trim('.');
            }

            private static bool IsCompleteIpv4(string name)
            {
                if (string.IsNullOrEmpty(name)) return false;
                string[] oct = name.Split('.');
                if (oct.Length != 4) return false;
                for (int i = 0; i < oct.Length; i++)
                {
                    string o = oct[i];
                    if (o.Length == 0 || o.Length > 3) return false;
                    for (int j = 0; j < o.Length; j++)
                    {
                        if (!char.IsDigit(o[j])) return false;
                    }
                }
                return true;
            }

            private static bool IsListingLinkedServerIp(FromObjectNameContext fromName)
            {
                string ip = JoinNumericFromSegments(fromName);
                if (ip == null) return false;
                if (IsCompleteIpv4(ip) && fromName.AfterDot && string.IsNullOrEmpty(fromName.Partial))
                    return false;
                return true;
            }

            private static int AdjustNameStartForOpenBracket(string text, int nameStart)
            {
                if (nameStart <= 0 || string.IsNullOrEmpty(text) || nameStart > text.Length)
                    return nameStart;
                if (text[nameStart - 1] == '[')
                    return nameStart - 1;
                return nameStart;
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

            /// <summary>当前 SELECT 与光标之间是否已出现 FROM（不跨越 ;/GO/上一句）。</summary>
            private static bool SeenFromAfterCurrentSelect(List<TSqlParserToken> tokens, int caretTokenIndex, int localOffset)
            {
                if (tokens == null || caretTokenIndex < 0) return false;
                int selectIdx = -1;
                int parenDepth = 0;
                for (int i = caretTokenIndex; i >= 0; i--)
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
                        if (parenDepth > 0) parenDepth--;
                        continue;
                    }
                    if (parenDepth > 0) continue;
                    string text = t.Text;
                    if (string.IsNullOrEmpty(text)) continue;
                    if (text == ";" || text.Equals("GO", StringComparison.OrdinalIgnoreCase))
                        break;
                    if (text.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsWordLikeToken(t) && t.Offset < localOffset && t.Offset + t.Text.Length >= localOffset)
                            continue;
                        selectIdx = i;
                        break;
                    }
                }
                if (selectIdx < 0) return false;
                for (int i = selectIdx + 1; i <= caretTokenIndex && i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t == null || IsInsignificantToken(t)) continue;
                    string text = t.Text?.ToUpperInvariant();
                    if (text == ";" || text == "GO" || text == "SELECT")
                        break;
                    if (text == "FROM")
                        return true;
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
        }
    }
}
