using System;
using System.Diagnostics;
using NUnit.Framework;
using Debug = UnityEngine.Debug;

namespace LightSide.Tests
{
    /// <summary>
    /// Line wrapping must be linear in text length. The wrapper used to rescan every glyph of a run
    /// for every line that run touches; one long run (a single-font paragraph) touches every line, so
    /// wrapping cost was O(lines × glyphs) = O(n²). These tests drive <see cref="LineBreaker"/>
    /// directly with synthetic shaped data.
    /// </summary>
    public class LineWrapScalingTests
    {
        private const float Advance = 10f;

        private struct Input
        {
            public int[] cps;
            public ShapedRun[] runs;
            public ShapedGlyph[] glyphs;
            public float[] widths;
            public LineBreakType[] breaks;
            public BidiParagraph[] paras;
            public float[] margins;
        }

        // n codepoints, a space every 6th; runs of runLen codepoints (0 = one run), with every
        // rtlEvery-th run shaped RTL (descending clusters).
        private static Input Build(int n, int runLen = 0, int rtlEvery = 0)
        {
            var inp = new Input
            {
                cps = new int[n], glyphs = new ShapedGlyph[n], widths = new float[n],
                breaks = new LineBreakType[n + 1], margins = new float[n],
                paras = new[] { new BidiParagraph(0, n - 1, 0) },
            };
            for (int i = 0; i < n; i++)
            {
                bool space = i % 6 == 5;
                inp.cps[i] = space ? ' ' : 'a';
                inp.widths[i] = Advance;
                if (space) inp.breaks[i + 1] = LineBreakType.Optional;
            }

            if (runLen <= 0) runLen = n;
            int runCount = (n + runLen - 1) / runLen;
            inp.runs = new ShapedRun[runCount];
            for (int r = 0; r < runCount; r++)
            {
                int start = r * runLen, len = Math.Min(runLen, n - start);
                bool rtl = rtlEvery > 0 && r % rtlEvery == rtlEvery - 1;
                for (int g = 0; g < len; g++)
                {
                    int cluster = rtl ? start + len - 1 - g : start + g;
                    inp.glyphs[start + g] = new ShapedGlyph { glyphId = 1, cluster = cluster, advanceX = Advance };
                }
                inp.runs[r] = new ShapedRun
                {
                    range = new TextRange(start, len), glyphStart = start, glyphCount = len,
                    width = len * Advance,
                    direction = rtl ? TextDirection.RightToLeft : TextDirection.LeftToRight,
                    bidiLevel = (byte)(rtl ? 1 : 0),
                };
            }
            return inp;
        }

        private static void Wrap(LineBreaker lb, in Input inp, float maxWidth,
            ref TextLine[] lines, ref int lineCount, ref ShapedRun[] ordered, ref int orderedCount)
        {
            lb.BreakLines(inp.cps, inp.runs, inp.glyphs, inp.widths, inp.breaks, maxWidth, inp.paras,
                ref lines, ref lineCount, ref ordered, ref orderedCount, inp.margins);
        }

        private static double MsPerWrap(int n)
        {
            var inp = Build(n);
            var lb = new LineBreaker();
            TextLine[] lines = null; ShapedRun[] ordered = null; int lc = 0, oc = 0;
            Wrap(lb, inp, 300f, ref lines, ref lc, ref ordered, ref oc); // warm-up
            double best = double.MaxValue;
            for (int s = 0; s < 5; s++)
            {
                int calls = 0;
                var sw = Stopwatch.StartNew();
                do { Wrap(lb, inp, 300f, ref lines, ref lc, ref ordered, ref oc); calls++; }
                while (sw.Elapsed.TotalMilliseconds < 25);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds / calls);
            }
            Assert.Greater(lc, n / 40, "text actually wrapped into many lines");
            return best;
        }

        [Test]
        public void WrapTime_ScalesLinearly()
        {
            const int n = 20000;
            double t1 = MsPerWrap(n);
            double t4 = MsPerWrap(4 * n);
            double ratio = t4 / t1;
            Debug.Log($"[PERF2][WRAP] n={n}: {t1:F3} ms, 4n={4 * n}: {t4:F3} ms, ratio={ratio:F2} (linear ~4, quadratic ~16)");
            Assert.Less(ratio, 6.0,
                $"wrapping 4x the text took {ratio:F1}x as long ({t1:F2} ms -> {t4:F2} ms); expected ~4x (linear)");
        }

        // Reference: the original per-line full scan of each run's glyphs.
        private static void NaiveRange(ShapedGlyph[] glyphs, in ShapedRun run, int startCp, int endCp, out int first, out int last)
        {
            first = last = -1;
            for (int g = 0; g < run.glyphCount; g++)
            {
                int c = glyphs[run.glyphStart + g].cluster;
                if (c >= startCp && c <= endCp) { if (first < 0) first = g; last = g; }
            }
        }

        [TestCase(0, 0)]   // one LTR run
        [TestCase(37, 0)]  // many LTR runs crossing line boundaries
        [TestCase(37, 2)]  // alternating LTR / RTL (descending clusters) runs
        [TestCase(500, 1)] // all-RTL long runs
        public void WrappedRuns_MatchFullScanReference(int runLen, int rtlEvery)
        {
            var inp = Build(3000, runLen, rtlEvery);
            // Make one run non-monotonic so the fallback path is exercised too.
            if (inp.runs.Length > 2)
            {
                var r = inp.runs[1];
                (inp.glyphs[r.glyphStart].cluster, inp.glyphs[r.glyphStart + 2].cluster) =
                    (inp.glyphs[r.glyphStart + 2].cluster, inp.glyphs[r.glyphStart].cluster);
            }

            var lb = new LineBreaker();
            TextLine[] lines = null; ShapedRun[] ordered = null; int lc = 0, oc = 0;
            Wrap(lb, inp, 230f, ref lines, ref lc, ref ordered, ref oc);
            Assert.Greater(lc, 50);

            int total = 0;
            for (int li = 0; li < lc; li++)
            {
                var line = lines[li];
                int startCp = line.range.start, endCp = line.range.End - 1;
                // Expected runs for this line, in logical order, via the naive scan.
                int expectedCount = 0;
                float expectedWidth = 0;
                for (int ri = 0; ri < inp.runs.Length; ri++)
                {
                    var run = inp.runs[ri];
                    if (run.range.End - 1 < startCp || run.range.start > endCp) continue;
                    NaiveRange(inp.glyphs, run, startCp, endCp, out int f, out int l);
                    if (f < 0) continue;
                    expectedCount++;
                    float w = 0;
                    for (int g = f; g <= l; g++) w += inp.glyphs[run.glyphStart + g].advanceX;
                    expectedWidth += w;

                    bool found = false;
                    for (int k = line.runStart; k < line.runStart + line.runCount; k++)
                        if (ordered[k].range.start == run.range.start)
                        {
                            Assert.AreEqual(run.glyphStart + f, ordered[k].glyphStart, $"line {li} run {ri} glyphStart");
                            Assert.AreEqual(l - f + 1, ordered[k].glyphCount, $"line {li} run {ri} glyphCount");
                            Assert.AreEqual(w, ordered[k].width, 1e-3f, $"line {li} run {ri} width");
                            found = true;
                        }
                    Assert.IsTrue(found, $"line {li} is missing run {ri}");
                }
                Assert.AreEqual(expectedCount, line.runCount, $"line {li} run count");
                Assert.AreEqual(expectedWidth, line.width, 1e-2f, $"line {li} width");
                total += line.range.length;
            }
            Assert.AreEqual(inp.cps.Length, total, "lines cover the whole text");
        }
    }
}
