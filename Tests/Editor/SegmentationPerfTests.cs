using System;
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace LightSide.Tests
{
    /// <summary>
    /// Performance and allocation budget for dictionary segmentation on a large Thai
    /// document. The segmenter reuses its DP and grapheme scratch across calls, so after
    /// a warm-up the steady-state per-call managed allocation must be near zero.
    /// </summary>
    [TestFixture]
    public class SegmentationPerfTests
    {
        [OneTimeSetUp]
        public void Setup() => SegHelper.EnsureUnicode();

        private static int[] Build100KbThai()
        {
            // ~100 KB of UTF-16 Thai text built by repeating fixture sentences.
            var sb = new StringBuilder(64 * 1024);
            int i = 0;
            while (sb.Length < 50 * 1024) // 50K UTF-16 chars ~= 100 KB
            {
                sb.Append(SegmentationFixtures.Thai[i % SegmentationFixtures.Thai.Length].Text);
                i++;
            }
            return SegHelper.ToCodepoints(sb.ToString());
        }

        [Test]
        public void Segment100KbThai_UnderBudget_NearZeroAlloc()
        {
            var cps = Build100KbThai();
            var breaks = new LineBreakType[cps.Length + 1];
            var lba = SharedPipelineComponents.LineBreakAlgorithm;

            // Warm up: loads the Thai trie and grows all scratch buffers.
            lba.GetBreakOpportunitiesWithSegmentation(cps, breaks);
            lba.GetBreakOpportunitiesWithSegmentation(cps, breaks);

            // ---- Allocation budget (steady state) ----
            long before = GC.GetAllocatedBytesForCurrentThread();
            const int allocIters = 5;
            for (int k = 0; k < allocIters; k++)
                lba.GetBreakOpportunitiesWithSegmentation(cps, breaks);
            long after = GC.GetAllocatedBytesForCurrentThread();
            long perCall = (after - before) / allocIters;

            Debug.Log($"[SegmentationPerf] codepoints={cps.Length} steady-state alloc/call={perCall} bytes");
            // "Near zero" — allow a small slack for JIT/measurement noise. The algorithm
            // itself allocates nothing per call once scratch is warm.
            Assert.LessOrEqual(perCall, 4096,
                $"Per-call allocation {perCall} bytes exceeds the near-zero budget.");

            // ---- Time budget ----
            const int timeIters = 20;
            var sw = Stopwatch.StartNew();
            for (int k = 0; k < timeIters; k++)
                lba.GetBreakOpportunitiesWithSegmentation(cps, breaks);
            sw.Stop();
            double msPerCall = sw.Elapsed.TotalMilliseconds / timeIters;

            Debug.Log($"[SegmentationPerf] {msPerCall:F2} ms per 100KB segmentation pass");
            // Generous, machine-independent budget: 100 KB in well under a quarter second.
            Assert.Less(msPerCall, 250.0,
                $"Segmentation of 100 KB took {msPerCall:F2} ms/call, over the 250 ms budget.");
        }
    }
}
