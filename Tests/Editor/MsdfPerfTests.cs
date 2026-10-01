using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using LightSide;
using LightSide.Msdf;

namespace LightSide.Tests
{
    /// <summary>
    /// Lightweight MSDF generation perf-regression guard. The msdfgen-faithful generator
    /// (OverlappingContourCombiner + MultiDistanceSelector) does more per-texel work than the old
    /// flat-min generator; a persistent per-edge cache + allocation-free combine selectors keep it
    /// at ~parity (0.96×–1.07× in the .NET bench, see NativeSource~/tests/msdfref/PERF_RESULTS_MSDF.md).
    /// This test asserts a GENEROUS per-glyph wall-clock budget so a future allocation/pruning
    /// regression (the naive port was ~4.5× on a batch) trips CI. The precise ratio evidence is the
    /// .NET bench + the PERF doc; this is the in-engine backstop, deliberately loose to avoid
    /// flakiness on shared CI runners. Windows editor x64 only (only that binary ships the outline
    /// export).
    /// </summary>
    [TestFixture]
    public class MsdfPerfTests
    {
        private byte[] _noto;

        [OneTimeSetUp]
        public void Setup()
        {
            string p = MsdfTestUtil.FindNotoSansPath();
            if (p == null) Assert.Ignore("NotoSans-Regular.ttf not found.");
            _noto = File.ReadAllBytes(p);
            Assert.IsTrue(FT.IsInitialized || FT.Initialize(), "FreeType init failed.");
        }

        private static GlyphOutline OutlineFor(IntPtr face, char ch, int ppem)
        {
            uint gi = FT.GetCharIndex(face, ch);
            if (gi == 0) return null;
            var src = new FreeTypeOutlineSource(face,
                (g, pel) => FT.SetPixelSize(face, pel) && FT.LoadGlyph(face, g, FT.LOAD_DEFAULT | FT.LOAD_NO_HINTING));
            if (!src.IsAvailable) return null;
            return src.GetOutline(gi, ppem);
        }

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void MsdfGeneration_PerGlyph_WithinBudget()
        {
            IntPtr face = FT.LoadFace(_noto);
            Assert.AreNotEqual(IntPtr.Zero, face);
            try
            {
                if (!FT.OutlineExportAvailable(face)) Assert.Ignore("outline export missing on this binary");

                char[] glyphs = { 'A', 'M', 'W', 'N', 'k', 'x', '&', 'g', '@' };
                int ppem = 64, spread = 6;

                var outlines = new List<GlyphOutline>();
                foreach (char ch in glyphs)
                {
                    var o = OutlineFor(face, ch, ppem);
                    if (o != null && !o.IsEmpty) outlines.Add(o);
                }
                Assert.Greater(outlines.Count, 0, "no outlines produced");

                // Warm up (JIT) and discard.
                foreach (var o in outlines) { var _ = MsdfBuilder.Build(o, spread); }

                // Median per-glyph over several runs.
                const int runs = 5;
                var batchMs = new List<double>();
                var sw = new Stopwatch();
                for (int r = 0; r < runs; r++)
                {
                    sw.Restart();
                    foreach (var o in outlines) { var res = MsdfBuilder.Build(o, spread); }
                    sw.Stop();
                    batchMs.Add(sw.Elapsed.TotalMilliseconds);
                }
                batchMs.Sort();
                double medianBatch = batchMs[batchMs.Count / 2];
                double perGlyph = medianBatch / outlines.Count;
                TestContext.WriteLine($"MSDF gen: {outlines.Count} glyphs, median batch {medianBatch:F2} ms, {perGlyph:F3} ms/glyph (ppem {ppem}, spread {spread}).");

                // Generous budget: optimized runs ~5 ms/glyph on dev HW; 60 ms/glyph gives ~12× head
                // room for a slow shared runner while still catching a catastrophic (allocation-storm)
                // regression. The exact old/new ratio is gated by the .NET bench + PERF doc.
                const double BudgetMsPerGlyph = 60.0;
                Assert.Less(perGlyph, BudgetMsPerGlyph,
                    $"MSDF generation {perGlyph:F2} ms/glyph exceeds the {BudgetMsPerGlyph} ms budget — likely a per-texel allocation or pruning regression.");
            }
            finally
            {
                FT.UnloadFace(face);
            }
        }
    }
}
