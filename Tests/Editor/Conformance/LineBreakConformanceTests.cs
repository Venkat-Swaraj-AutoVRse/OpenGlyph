using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace LightSide.Tests.Conformance
{
    /// <summary>
    /// UAX #14 line breaking vs auxiliary/LineBreakTest.txt (one case per data line). Uses the plain
    /// UAX #14 entry point (no dictionary segmentation; that is an intentional opt-in extension on top).
    /// A break opportunity is LineBreakType != None (Optional and Mandatory both map to the test's break mark).
    /// </summary>
    [TestFixture, Category("Conformance")]
    internal sealed class LineBreakConformanceTests
    {
        [Test]
        public void LineBreakTest_Unicode17()
        {
            var path = ConformanceSupport.RequireData("LineBreakTest.txt");
            ConformanceSupport.EnsureUnicode();
            var algo = new LineBreakAlgorithm(UnicodeData.Provider);
            var report = new ConformanceReport("UAX14-linebreak", "Unicode Line Breaking Algorithm", "LineBreakTest.txt");

            var cps = new List<int>();
            var exp = new List<bool>();
            var lineNo = 0;
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                lineNo++;
                if (!ConformanceSupport.ParseBreakLine(line, cps, exp)) continue;
                var arr = cps.ToArray();
                var got = algo.GetBreakOpportunities(arr);
                // Index 0 (sot) is never a break; compare 1..n (n = end of text).
                var ok = got.Length == exp.Count;
                var cls = "boundary-count-mismatch";
                if (ok)
                {
                    for (var i = 1; i < got.Length; i++)
                    {
                        var g = got[i] != LineBreakType.None;
                        if (g != exp[i]) { ok = false; cls = g ? "extra-break" : "missing-break"; break; }
                    }
                }
                if (ok) { report.Pass(); continue; }
                report.Fail(cls, lineNo, ConformanceSupport.Cps(arr), Render(arr, exp, null), Render(arr, null, got));
            }
            report.WriteAndAssert();
        }

        private static string Render(int[] cps, List<bool> b, LineBreakType[] t)
        {
            var sb = new StringBuilder();
            var n = b != null ? b.Count : t.Length;
            for (var i = 0; i <= cps.Length; i++)
            {
                if (i < n)
                {
                    var brk = b != null ? b[i] : (i != 0 && t[i] != LineBreakType.None);
                    sb.Append(brk ? "÷ " : "× ");
                }
                if (i < cps.Length) sb.Append(cps[i].ToString("X4")).Append(' ');
            }
            return sb.ToString().TrimEnd();
        }
    }
}
