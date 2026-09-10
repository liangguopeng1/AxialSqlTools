using System;
using System.Collections.Generic;
using System.Text;

namespace AxialSqlTools
{
    namespace IntelliSense
    {
        /// <summary>
        /// 补全候选的匹配打分与命中下标计算。
        /// 从 CompletionEngine 抽出的纯函数：只依赖 CompletionItem，不碰目录/连接/VS API，
        /// 因此可以直接单元测试（见 tools/intellisense-matcher-tests）。
        /// 打分约定：分值越高越靠前；相同分值再按 CompletionKind 排序（Column 优先于 Keyword）。
        /// </summary>
        internal static class CompletionMatcher
        {
            internal static int GetMatchScore(CompletionItem item, string filterPrefix)
            {
                int[] unused;
                return GetMatchScore(item, filterPrefix, out unused);
            }

            internal static int GetMatchScore(CompletionItem item, string filterPrefix, out int[] matchIndices)
            {
                matchIndices = null;
                if (string.IsNullOrEmpty(filterPrefix)) return 0;
                string displayText = item?.DisplayText ?? string.Empty;
                if (IsNumericOrIpPrefix(filterPrefix))
                {
                    if (displayText.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = ContiguousMatchIndices(0, filterPrefix.Length);
                        return 100;
                    }
                    return 0;
                }
                // 全字段：空前缀已在外层保留；有前缀时任一列名前缀命中则保留（略低于精确列）
                if (item != null && item.Kind == CompletionKind.AllColumns)
                {
                    if (displayText.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = ContiguousMatchIndices(0, filterPrefix.Length);
                        return 90;
                    }
                    string insert = item.InsertText ?? string.Empty;
                    foreach (var part in insert.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string col = UnbracketIdentifier(GetLastSegment(part.Trim()));
                        if (string.IsNullOrEmpty(col)) continue;
                        if (col.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            matchIndices = ContiguousMatchIndices(0, Math.Min(filterPrefix.Length, displayText.Length));
                            return 95;
                        }
                        if (col.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            matchIndices = ContiguousMatchIndices(0, Math.Min(filterPrefix.Length, displayText.Length));
                            return 90;
                        }
                    }
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
                          item.Kind == CompletionKind.Procedure || item.Kind == CompletionKind.ScalarFunction ||
                          item.Kind == CompletionKind.Schema))
                {
                    name = GetMatchableName(name);
                }

                // 表别名精确/前缀命中优先于 AND 等关键字
                if (item != null && item.Kind == CompletionKind.Schema
                    && !string.IsNullOrEmpty(item.Description)
                    && item.Description.StartsWith("表别名", StringComparison.Ordinal))
                {
                    if (name.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(0, name.Length));
                        return 130;
                    }
                    if (name.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(0, filterPrefix.Length));
                        return 120;
                    }
                }

                if (name.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(0, name.Length));
                    return item != null && item.Kind == CompletionKind.Snippet ? 110 : 100;
                }
                if (name.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(0, filterPrefix.Length));
                    // 子句关键字略高于 CASE 片段：wh → WHERE 优先于 WHEN
                    if (item != null && item.Kind == CompletionKind.Keyword && IsClauseTrailingKeyword(name))
                        return 105;
                    return 100;
                }
                // sys.tables：前缀 sys 要对整段限定名计分，不能只拿最后一段 tables
                if (!string.Equals(displayText, name, StringComparison.OrdinalIgnoreCase)
                    && displayText.IndexOf('.') >= 0)
                {
                    if (displayText.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = ContiguousMatchIndices(0, displayText.Length);
                        return 100;
                    }
                    if (displayText.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = ContiguousMatchIndices(0, filterPrefix.Length);
                        return 95;
                    }
                }
                // 多词关键字：in → INNER JOIN、group → GROUP BY
                if (item != null && item.Kind == CompletionKind.Keyword && name.IndexOf(' ') >= 0)
                {
                    int sp = name.IndexOf(' ');
                    string first = sp > 0 ? name.Substring(0, sp) : name;
                    if (first.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(0, filterPrefix.Length));
                        return IsClauseTrailingKeyword(name) ? 100 : 95;
                    }
                }
                // 片段：ss → sess（包含）；仍低于前缀命中，ssf 排在 sess 前
                if (item != null && item.Kind == CompletionKind.Snippet)
                {
                    int snippetAt = name.IndexOf(filterPrefix, StringComparison.OrdinalIgnoreCase);
                    if (snippetAt >= 0)
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(snippetAt, filterPrefix.Length));
                        return 80;
                    }
                    return 0;
                }
                // 关键字/内建函数只做前缀匹配，避免 pr 命中 DATEPART 等缩写误匹配
                if (item != null && (item.Kind == CompletionKind.Keyword
                    || item.Kind == CompletionKind.ScalarFunction))
                    return 0;

                // 下划线段匹配：把前缀与名称都按 _ 切段，前缀逐段对齐名称的连续段。
                //   rt_fenjian ← fe / fen；CG_H_ID ← h / h_ / h_id / cg_h
                // 列（Column）允许单字符：列清单已限定在当前表内，不会跨库泛滥；
                // 对象名（表/视图/过程/库…）仍要求 ≥2，避免单字符扫到过多段。
                int minSegmentPrefixLen = item != null && item.Kind == CompletionKind.Column ? 1 : 2;
                if (filterPrefix.Length >= minSegmentPrefixLen && name.IndexOf('_') >= 0)
                {
                    int[] segmentIndices;
                    if (TryMatchUnderscoreSegments(name, filterPrefix, out segmentIndices))
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, segmentIndices);
                        // 列命中词首（H_ID 的 H）等同前缀命中：与关键字同为 100 分，
                        // 再由 CompletionKind 排序（Column 先于 Keyword）把列排在 HAVING 之类之前。
                        return item != null && item.Kind == CompletionKind.Column ? 100 : 60;
                    }
                }

                // 短前缀不做缩写/包含匹配，避免 aa 命中 CreateDate、a 命中大量列；
                // 数据库通常很少（十几个），不设此限制。
                if (filterPrefix.Length <= 2
                    && (item == null || item.Kind != CompletionKind.Database))
                    return 0;

                int containsAt = name.IndexOf(filterPrefix, StringComparison.OrdinalIgnoreCase);
                if (containsAt >= 0)
                {
                    matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(containsAt, filterPrefix.Length));
                    return 80;
                }

                string collapsedName = CollapseForMatch(name);
                string collapsedFilter = CollapseForMatch(filterPrefix);
                if (!string.IsNullOrEmpty(collapsedFilter))
                {
                    if (collapsedName.StartsWith(collapsedFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name,
                            MapCollapsedRangeToName(name, 0, collapsedFilter.Length));
                        return 85;
                    }
                    int collapsedAt = collapsedName.IndexOf(collapsedFilter, StringComparison.OrdinalIgnoreCase);
                    if (collapsedAt >= 0)
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name,
                            MapCollapsedRangeToName(name, collapsedAt, collapsedFilter.Length));
                        return 65;
                    }
                    int[] subIndices;
                    if (TryMatchSubsequence(name, filterPrefix, out subIndices))
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, subIndices);
                        return 50;
                    }
                }

                var segs = name.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                int searchFrom = 0;
                foreach (var seg in segs)
                {
                    int segAt = name.IndexOf(seg, searchFrom, StringComparison.OrdinalIgnoreCase);
                    if (segAt < 0) segAt = searchFrom;
                    int inner = seg.IndexOf(filterPrefix, StringComparison.OrdinalIgnoreCase);
                    if (inner >= 0)
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(segAt + inner, filterPrefix.Length));
                        return 40;
                    }
                    searchFrom = segAt + seg.Length;
                }
                return 0;
            }

            private static bool IsClauseTrailingKeyword(string name)
            {
                if (string.IsNullOrEmpty(name)) return false;
                switch (name.ToUpperInvariant())
                {
                    case "WHERE":
                    case "HAVING":
                    case "GROUP BY":
                    case "ORDER BY":
                    case "UNION":
                    case "UNION ALL":
                    case "EXCEPT":
                    case "INTERSECT":
                    case "JOIN":
                    case "INNER JOIN":
                    case "LEFT JOIN":
                    case "RIGHT JOIN":
                    case "FULL JOIN":
                    case "CROSS JOIN":
                    case "LEFT OUTER JOIN":
                    case "RIGHT OUTER JOIN":
                    case "FULL OUTER JOIN":
                    case "CROSS APPLY":
                    case "OUTER APPLY":
                    case "DISTINCT":
                    case "TOP":
                        return true;
                    default:
                        return false;
                }
            }

            /// <summary>
            /// 下划线段匹配：前缀按 _ 切段，逐段与名称的连续段对齐（段首比较）。
            ///   h     → CG_H_ID（命中 H 段）
            ///   h_    → CG_H_ID（要求名称里字面出现 H_，因此不会命中 HX_ID）
            ///   h_id  → CG_H_ID（H + ID 两段）
            ///   cg_h  → CG_H_ID（CG + H 两段）
            /// 出参为名称里命中区间的字符下标，供列表高亮。
            /// </summary>
            private static bool TryMatchUnderscoreSegments(string name, string filterPrefix, out int[] nameIndices)
            {
                nameIndices = null;
                string[] prefixSegs = filterPrefix.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                if (prefixSegs.Length == 0) return false;

                var segStarts = new List<int>();
                var segLens = new List<int>();
                int segStart = 0;
                for (int i = 0; i <= name.Length; i++)
                {
                    if (i < name.Length && name[i] != '_') continue;
                    if (i > segStart)
                    {
                        segStarts.Add(segStart);
                        segLens.Add(i - segStart);
                    }
                    segStart = i + 1;
                }
                if (segStarts.Count == 0) return false;

                bool prefixEndsWithSeparator = filterPrefix[filterPrefix.Length - 1] == '_';

                for (int k = 0; k + prefixSegs.Length <= segStarts.Count; k++)
                {
                    bool ok = true;
                    for (int j = 0; j < prefixSegs.Length; j++)
                    {
                        string seg = name.Substring(segStarts[k + j], segLens[k + j]);
                        string want = prefixSegs[j];
                        // 非末段：用户已敲过 _，该段必须整段相同；末段允许前缀式
                        if (j < prefixSegs.Length - 1
                            && !seg.Equals(want, StringComparison.OrdinalIgnoreCase))
                        {
                            ok = false;
                            break;
                        }
                        if (seg.Length < want.Length
                            || string.Compare(seg, 0, want, 0, want.Length, StringComparison.OrdinalIgnoreCase) != 0)
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (!ok) continue;

                    int start = segStarts[k];
                    // 前缀以 _ 结尾：该分隔符也必须真实落在名称里（h_ 不命中 HX_ID），高亮含分隔符
                    if (prefixEndsWithSeparator)
                    {
                        if (start + filterPrefix.Length > name.Length
                            || string.Compare(name, start, filterPrefix, 0, filterPrefix.Length, StringComparison.OrdinalIgnoreCase) != 0)
                            continue;
                        nameIndices = ContiguousMatchIndices(start, filterPrefix.Length);
                        return true;
                    }

                    int lastSeg = k + prefixSegs.Length - 1;
                    int end = segStarts[lastSeg] + prefixSegs[prefixSegs.Length - 1].Length;
                    if (end <= start) continue;
                    nameIndices = ContiguousMatchIndices(start, end - start);
                    return true;
                }
                return false;
            }

            internal static string CollapseForMatch(string text)
            {
                if (string.IsNullOrEmpty(text)) return string.Empty;
                var sb = new StringBuilder(text.Length);
                foreach (char c in text)
                {
                    if (c != '_' && c != '.') sb.Append(c);
                }
                return sb.ToString();
            }

            /// <summary>按序子序列匹配：filter 字符按出现顺序命中 name（如 ddi / daoitem → DH_DaoHuoItem）。优先选连续段更多的命中，便于高亮。</summary>
            internal static bool TryMatchSubsequence(string name, string filter, out int[] indices)
            {
                indices = null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(filter)) return false;
                string compactFilter = CollapseForMatch(filter);
                if (string.IsNullOrEmpty(compactFilter)) return false;

                int[] best = null;
                int bestAdjacent = -1;
                for (int start = 0; start < name.Length; start++)
                {
                    char sc = name[start];
                    if (sc == '_' || sc == '.') continue;
                    if (!CharsEqualIgnoreCase(sc, compactFilter[0])) continue;

                    var acc = new int[compactFilter.Length];
                    acc[0] = start;
                    int fi = 1;
                    for (int i = start + 1; i < name.Length && fi < compactFilter.Length; i++)
                    {
                        char nc = name[i];
                        if (nc == '_' || nc == '.') continue;
                        if (!CharsEqualIgnoreCase(nc, compactFilter[fi])) continue;
                        acc[fi] = i;
                        fi++;
                    }
                    if (fi < compactFilter.Length) continue;
                    int adjacent = CountAdjacentMatches(name, acc);
                    if (adjacent > bestAdjacent)
                    {
                        bestAdjacent = adjacent;
                        best = acc;
                        if (bestAdjacent >= compactFilter.Length - 1) break;
                    }
                }
                if (best == null) return false;
                indices = best;
                return true;
            }

            private static bool CharsEqualIgnoreCase(char a, char b)
            {
                return char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
            }

            private static int CountAdjacentMatches(string name, int[] idx)
            {
                int n = 0;
                for (int i = 1; i < idx.Length; i++)
                {
                    bool adjacent = true;
                    for (int p = idx[i - 1] + 1; p < idx[i]; p++)
                    {
                        char c = name[p];
                        if (c != '_' && c != '.')
                        {
                            adjacent = false;
                            break;
                        }
                    }
                    if (adjacent) n++;
                }
                return n;
            }

            private static int[] ContiguousMatchIndices(int start, int length)
            {
                if (length <= 0) return null;
                var a = new int[length];
                for (int i = 0; i < length; i++) a[i] = start + i;
                return a;
            }

            private static int[] MapCollapsedRangeToName(string name, int collapsedStart, int collapsedLen)
            {
                if (string.IsNullOrEmpty(name) || collapsedLen <= 0) return null;
                var list = new int[collapsedLen];
                int ci = 0;
                int filled = 0;
                for (int i = 0; i < name.Length && filled < collapsedLen; i++)
                {
                    if (name[i] == '_' || name[i] == '.') continue;
                    if (ci >= collapsedStart)
                    {
                        list[filled] = i;
                        filled++;
                    }
                    ci++;
                }
                if (filled < collapsedLen) return null;
                return list;
            }

            private static int[] MapMatchIndicesToDisplay(string displayText, string matchedName, int[] nameIndices)
            {
                if (nameIndices == null || nameIndices.Length == 0) return nameIndices;
                if (string.IsNullOrEmpty(displayText) || string.Equals(displayText, matchedName, StringComparison.OrdinalIgnoreCase))
                    return nameIndices;
                int offset = 0;
                if (!string.IsNullOrEmpty(matchedName) && displayText.Length >= matchedName.Length)
                {
                    if (displayText.EndsWith(matchedName, StringComparison.OrdinalIgnoreCase)
                        && (displayText.Length == matchedName.Length
                            || displayText[displayText.Length - matchedName.Length - 1] == '.'))
                    {
                        offset = displayText.Length - matchedName.Length;
                    }
                    else
                    {
                        int at = displayText.IndexOf(matchedName, StringComparison.OrdinalIgnoreCase);
                        if (at >= 0) offset = at;
                    }
                }
                if (offset == 0) return nameIndices;
                var mapped = new int[nameIndices.Length];
                for (int i = 0; i < nameIndices.Length; i++)
                    mapped[i] = nameIndices[i] + offset;
                return mapped;
            }

            internal static int GetMatchScore(string displayText, string filterPrefix)
            {
                return GetMatchScore(new CompletionItem { DisplayText = displayText }, filterPrefix);
            }

            private static string GetMatchableName(string displayText)
            {
                if (string.IsNullOrEmpty(displayText)) return string.Empty;
                int dot = displayText.LastIndexOf('.');
                return dot >= 0 ? displayText.Substring(dot + 1) : displayText;
            }

            internal static bool IsNumericOrIpPrefix(string prefix)
            {
                if (string.IsNullOrEmpty(prefix) || !char.IsDigit(prefix[0])) return false;
                for (int i = 0; i < prefix.Length; i++)
                {
                    char c = prefix[i];
                    if (!char.IsDigit(c) && c != '.') return false;
                }
                return true;
            }

            /// <summary>去掉标识符两侧的方括号与双引号。</summary>
            internal static string UnbracketIdentifier(string text)
            {
                if (string.IsNullOrEmpty(text)) return string.Empty;
                return text.Trim('[', ']', '"');
            }

            /// <summary>取点号分隔的最后一段（dbo.t → t）。</summary>
            internal static string GetLastSegment(string prefix)
            {
                if (string.IsNullOrEmpty(prefix)) return string.Empty;
                int dot = prefix.LastIndexOf('.');
                if (dot < 0) return prefix;
                return prefix.Substring(dot + 1);
            }
        }
    }
}
