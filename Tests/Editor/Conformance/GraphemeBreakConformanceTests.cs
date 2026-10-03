using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace LightSide.Tests.Conformance
{
    /// <summary>UAX #29 grapheme cluster boundaries vs auxiliary/GraphemeBreakTest.txt (one case per data line).</summary>
    [TestFixture, Category("Conformance")]
    internal sealed class GraphemeBreakConformanceTests
    {
        [Test]
        public void GraphemeBreakTest_Unicode17()
        {
            var path = ConformanceSupport.RequireData("GraphemeBreakTest.txt");
            ConformanceSupport.EnsureUnicode();
            var breaker = new GraphemeBreaker(UnicodeData.Provider);
            var report = new ConformanceReport("UAX29-grapheme", "Unicode Text Segmentation (grapheme clusters)", "GraphemeBreakTest.txt");

            var cps = new List<int>();
            var exp = new List<bool>();
            var lineNo = 0;
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                lineNo++;
                if (!ConformanceSupport.ParseBreakLine(line, cps, exp)) continue;
                var arr = cps.ToArray();
                var got = breaker.GetBreakOpportunities(arr);
                var ok = got.Length == exp.Count;
                if (ok) for (var i = 0; i < got.Length; i++) if (got[i] != exp[i]) { ok = false; break; }
                if (ok) { report.Pass(); continue; }
                report.Fail("boundary-mismatch", lineNo, ConformanceSupport.Cps(arr), Render(arr, exp), Render(arr, got));
            }
            report.WriteAndAssert();
        }

        private static string Render(int[] cps, IReadOnlyList<bool> b)
        {
            var sb = new StringBuilder();
            for (var i = 0; i <= cps.Length; i++)
            {
                if (i < b.Count) sb.Append(b[i] ? "÷ " : "× ");
                if (i < cps.Length) sb.Append(cps[i].ToString("X4")).Append(' ');
            }
            return sb.ToString().TrimEnd();
        }
    }
}
