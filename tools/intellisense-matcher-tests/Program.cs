using System;
using System.Collections.Generic;
using AxialSqlTools.IntelliSense;

namespace IntelliSenseMatcherTests
{
    /// <summary>
    /// CompletionMatcher 匹配规则回归测试。
    /// 直接编译生产类 AxialSqlTools.IntelliSense.CompletionMatcher（纯函数，无目录/连接依赖）。
    ///
    /// 覆盖用户报告：WHERE 子句里输入 h / h_ 不出列（CG_H_ID 的 H_ID 段没被匹配）。
    /// 退出码 0 = 全部通过。
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Failures = new List<string>();
        private static int _passed;

        private static int Main()
        {
            Console.WriteLine("== CompletionMatcher 匹配规则回归测试 ==");
            Console.WriteLine();

            ReportedProblem();
            NewRuleCoverage();
            MustNotOverMatch();
            ExistingRulesPreserved();
            Ranking();
            TextHelpers();

            Console.WriteLine();
            Console.WriteLine("passed=" + _passed + "  failed=" + Failures.Count);
            foreach (var f in Failures) Console.WriteLine("FAIL: " + f);
            Console.WriteLine(Failures.Count == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            return Failures.Count == 0 ? 0 : 1;
        }

        // ---------- 用户报告的场景：WHERE 里输入 h / h_ 必须出列 ----------

        private static void ReportedProblem()
        {
            Console.WriteLine("[报告场景] SELECT * FROM rt_fenjian.CGD_WaiCai_Items where h|");

            var cgHId = Col("CG_H_ID");
            var dhHId = Col("DH_H_ID");
            var dhItemCode = Col("DH_Item_PrimaryCode");

            int[] idx;
            int score = Score(cgHId, "h", out idx);
            Console.WriteLine("  [01] h    -> CG_H_ID  score=" + score + " highlight=[" + Join(idx) + "]");
            Check(score > 0, "01a 单字符 h 命中 CG_H_ID（H_ID 段）");
            Check(IdxEquals(idx, 3), "01b 高亮落在 H 段首（下标 3）");

            score = Score(dhHId, "h", out idx);
            Check(score > 0, "01c 单字符 h 命中 DH_H_ID（score=" + score + "）");

            score = Score(cgHId, "h_", out idx);
            Console.WriteLine("  [02] h_   -> CG_H_ID  score=" + score + " highlight=[" + Join(idx) + "]");
            Check(score > 0, "02a h_ 命中 CG_H_ID");
            Check(IdxEquals(idx, 3, 4), "02b h_ 高亮 H_ 两个字符");

            Check(Score(dhItemCode, "h") == 0, "03   h 不命中 DH_Item_PrimaryCode（无段以 h 开头）");
        }

        // ---------- 新规则覆盖面：任意段首都能用单字符命中（列） ----------

        private static void NewRuleCoverage()
        {
            Console.WriteLine("[新规则覆盖面] mock 目录列：id / k_id / h_id / CreateRen / oper / operid");

            Check(Score(Col("k_id"), "i") > 0, "08a 单字符 i 命中 k_id（id 段）");
            Check(Score(Col("h_id"), "i") > 0, "08b 单字符 i 命中 h_id（id 段）");
            Check(Score(Col("id"), "i") == 100, "08c i 前缀命中 id（不回归）");
            Check(Score(Col("k_id"), "k_") > 0, "08d k_ 命中 k_id");
            Check(Score(Col("oper"), "o") == 100, "08e o 前缀命中 oper");
            Check(Score(Col("operid"), "op") == 100, "08f op 前缀命中 operid");
        }

        // ---------- 不能因此过度匹配 ----------

        private static void MustNotOverMatch()
        {
            Console.WriteLine("[不过度匹配]");

            Check(Score(Col("CreateDate"), "h") == 0, "04a h 不命中 CreateDate");
            Check(Score(Col("CreateDate"), "a") == 0, "04b 单字符 a 不做包含/缩写匹配");
            Check(Score(Col("HX_ID"), "h_") == 0, "04c h_ 不命中 HX_ID（分隔符必须真实存在）");
            Check(Score(Table("RT_YeWuKaoHe"), "y") == 0, "04d 单字符不参与对象名的段匹配");
            Check(Score(Col("H_ID"), "h_") > 0, "04e h_ 命中 H_ID");
        }

        // ---------- 原有规则不回归 ----------

        private static void ExistingRulesPreserved()
        {
            Console.WriteLine("[原有规则]");

            Check(Score(Table("rt_fenjian"), "fe") > 0, "05a 双字符段前缀 fe 命中 rt_fenjian（表 60 分档）");
            Check(Score(Col("CG_H_ID"), "hid") > 0, "05b 包含匹配 hid 仍命中 CG_H_ID");
            Check(Score(Col("CG_H_ID"), "h_id") > 0, "05c h_id 仍命中 CG_H_ID");
            Check(Score(Col("CG_H_ID"), "cg_h") > 0, "05d cg_h 仍命中 CG_H_ID");
            Check(Score(Col("CG_H_ID"), "cg") > 0, "05e cg 仍前缀命中 CG_H_ID");
            Check(Score(Kw("WHERE"), "wh") == 105, "05f 子句尾随关键字仍是 105 分（wh -> WHERE 优先于 WHEN）");
            Check(Score(Kw("HAVING"), "h") == 105, "05g HAVING 属于子句尾随关键字，105 分");
            Check(Score(Kw("HAVING"), "h_") == 0, "05h 关键字不参与段匹配");
        }

        // ---------- 排序：列要排在关键字前面 ----------

        private static void Ranking()
        {
            Console.WriteLine("[排序]");

            int col = Score(Col("CG_H_ID"), "h");        // 列段命中
            int plainKeyword = Score(Kw("HELP"), "h");   // 普通关键字前缀命中
            int clauseKeyword = Score(Kw("HAVING"), "h");// 子句尾随关键字前缀命中
            int scalarFn = Score(new CompletionItem { DisplayText = "HASHBYTES", Kind = CompletionKind.ScalarFunction }, "h");
            Console.WriteLine("  [06] CG_H_ID=" + col + "  HELP=" + plainKeyword
                + "  HAVING=" + clauseKeyword + "  HASHBYTES=" + scalarFn);

            // 引擎排序：分数降序，同分再按 CompletionKind（Column -3 < Keyword 0 < ScalarFunction 1）
            Check(col == 100, "06a 列段命中取 100 分（非精确匹配的最高档）");
            Check(col == plainKeyword && col == scalarFn,
                "06b 与普通关键字/内建函数前缀命中同档 -> 同档内 Column 排在 Keyword/ScalarFunction 之前");
            Check(col < clauseKeyword,
                "06c 子句尾随关键字（HAVING 等）保持 105 分仍居首（原有取舍，本次不改）");
        }

        // ---------- 文本工具 ----------

        private static void TextHelpers()
        {
            Console.WriteLine("[文本工具]");
            Check(CompletionMatcher.GetLastSegment("dbo.t") == "t", "07a GetLastSegment 取最后一段");
            Check(CompletionMatcher.GetLastSegment("t") == "t", "07b GetLastSegment 无点原样返回");
            Check(CompletionMatcher.UnbracketIdentifier("[t]") == "t", "07c UnbracketIdentifier 去方括号");
            Check(CompletionMatcher.UnbracketIdentifier("t") == "t", "07d UnbracketIdentifier 无括号原样返回");
            Check(CompletionMatcher.IsNumericOrIpPrefix("192.168"), "07e IsNumericOrIpPrefix 识别 IP 段");
            Check(!CompletionMatcher.IsNumericOrIpPrefix("t1"), "07f IsNumericOrIpPrefix 非数字开头为 false");
        }

        // ---------- 基础设施 ----------

        private static CompletionItem Col(string name) =>
            new CompletionItem { DisplayText = name, InsertText = name, Kind = CompletionKind.Column };

        private static CompletionItem Table(string name) =>
            new CompletionItem { DisplayText = name, InsertText = name, Kind = CompletionKind.Table };

        private static CompletionItem Kw(string name) =>
            new CompletionItem { DisplayText = name, Kind = CompletionKind.Keyword };

        private static int Score(CompletionItem item, string prefix)
        {
            int[] unused;
            return CompletionMatcher.GetMatchScore(item, prefix, out unused);
        }

        private static int Score(CompletionItem item, string prefix, out int[] indices) =>
            CompletionMatcher.GetMatchScore(item, prefix, out indices);

        private static bool IdxEquals(int[] actual, params int[] expected)
        {
            if (actual == null || actual.Length != expected.Length) return false;
            for (int i = 0; i < expected.Length; i++)
                if (actual[i] != expected[i]) return false;
            return true;
        }

        private static string Join(int[] a) => a == null ? "<null>" : string.Join(",", a);

        private static void Check(bool ok, string name)
        {
            if (ok) { _passed++; Console.WriteLine("  ok  " + name); }
            else { Failures.Add(name); Console.WriteLine("  BAD " + name); }
        }
    }
}
