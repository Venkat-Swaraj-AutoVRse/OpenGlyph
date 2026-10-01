using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using LightSide;
using LightSide.Msdf;

namespace LightSide.Tests
{
    /// <summary>
    /// Real-export MSDF quality gates: corner/junction artifacts (issue 1) and fill polarity for
    /// glyphs with overlapping contours (issue 2). Uses the REAL rebuilt native binary via
    /// FreeTypeOutlineSource, so it catches defects the earlier synthetic-outline tests missed.
    /// Windows editor x64 only (only that binary ships the export).
    /// </summary>
    [TestFixture]
    public class MsdfArtifactTests
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

        // ---------------- Issue 1: no isolated spurs/specks ----------------

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void RealExport_NoIsolatedArtifacts_AtCornersAndJunctions()
        {
            IntPtr face = FT.LoadFace(_noto);
            Assert.AreNotEqual(IntPtr.Zero, face);
            try
            {
                Assert.IsTrue(FT.OutlineExportAvailable(face), "export missing on this binary");

                char[] glyphs = { 'A', 'M', 'W', 'N', 'k', 'x', '&', 'g', '@' };
                int ppem = 64, spread = 6, up = 8;
                var rows = new List<string>();
                var offenders = new List<string>();
                int evaluated = 0;

                foreach (char ch in glyphs)
                {
                    var outline = OutlineFor(face, ch, ppem);
                    if (outline == null) { rows.Add($"  '{ch}': unavailable — skipped"); continue; }

                    var shape = Shape.FromOutline(outline);
                    var msdf = MsdfBuilder.Build(outline, spread); // error correction ON (default)
                    Assert.IsTrue(msdf.IsValid, $"build failed '{ch}'");

                    var (blobs, maxBlob) = SilhouetteErrorBlobs(shape, msdf, spread, up);
                    int big = 0;
                    foreach (var s in blobs) if (s > 2) big++;
                    rows.Add($"  '{ch}': isolated-error-blobs>2px = {big} (largest {maxBlob}px)");
                    if (big > 0) offenders.Add($"'{ch}' has {big} isolated artifact blob(s) (largest {maxBlob}px)");
                    evaluated++;
                }

                string table = "Isolated MSDF artifacts (connected error components >2px, >2px from the true edge):\n" + string.Join("\n", rows);
                TestContext.WriteLine(table);
                Assert.GreaterOrEqual(evaluated, 5, "Too few glyphs evaluable:\n" + table);
                Assert.IsEmpty(offenders,
                    "MSDF has isolated corner/junction artifacts:\n" + string.Join("\n", offenders) + "\n" + table);
            }
            finally { FT.UnloadFace(face); }
        }

        /// <summary>
        /// Reconstructs the MSDF silhouette (median&gt;0.5) at <paramref name="up"/>x magnification,
        /// compares to the non-zero-winding ground truth, and returns the sizes of connected error
        /// components that sit more than 2 output px from the true edge, plus the largest.
        /// </summary>
        private static (List<int> blobs, int max) SilhouetteErrorBlobs(Shape shape, MsdfGlyphResult msdf, int spread, int up)
        {
            int w = msdf.Width, h = msdf.Height;
            int W = (w - 1) * up, H = (h - 1) * up;

            // Precompute the ground-truth non-zero-winding inside mask ONCE per supersample (one
            // shape.Contains per pixel). Everything else — the reconstruction error and the near-edge
            // band — is derived from this boolean mask, so we never call the (expensive) winding test
            // 100+ times per pixel.
            var trueInside = new bool[W * H];
            var error = new bool[W * H];
            for (int uy = 0; uy < H; uy++)
                for (int ux = 0; ux < W; ux++)
                {
                    double fx = (double)ux / up, fy = (double)uy / up;
                    // Ground-truth in shape space. MsdfBuilder frames the glyph box lower-left at
                    // (spread, spread) in field space, so shapeX = fieldX + BitmapLeft and
                    // shapeY = fieldY + (BitmapTop - h).
                    double sx = (fx + 0.5) + msdf.BitmapLeft;
                    double sy = (fy + 0.5) + (msdf.BitmapTop - h);
                    bool ti = shape.Contains(new Vector2D(sx, sy));
                    trueInside[uy * W + ux] = ti;

                    float med = BilinearMedian(msdf.Field, w, h, fx, fy);
                    if ((med > 0.5f) != ti) error[uy * W + ux] = true;
                }

            // Near-edge band straight from the mask: a pixel is near an edge when any neighbour in a
            // BAND-output-pixel window has the opposite inside/outside value. MSDF edges reconstruct
            // up to ~1 source pixel (= `up` output px) off the analytic winding at this low pxRange,
            // so a modest band keeps edge-coincident sub-pixel disagreement from being mistaken for
            // an isolated speck; a genuine speck in open field (no true edge within the band) still
            // counts.
            const int band = 5; // output pixels
            var nearEdge = new bool[W * H];
            for (int uy = 0; uy < H; uy++)
                for (int ux = 0; ux < W; ux++)
                {
                    bool c = trueInside[uy * W + ux];
                    bool near = false;
                    int y0 = Math.Max(0, uy - band), y1 = Math.Min(H - 1, uy + band);
                    int x0 = Math.Max(0, ux - band), x1 = Math.Min(W - 1, ux + band);
                    for (int ny = y0; ny <= y1 && !near; ny++)
                        for (int nx = x0; nx <= x1; nx++)
                            if (trueInside[ny * W + nx] != c) { near = true; break; }
                    if (near) nearEdge[uy * W + ux] = true;
                }

            var sizes = MsdfTestUtil.IsolatedErrorBlobSizes(error, nearEdge, W, H);
            int max = 0; foreach (var s in sizes) if (s > max) max = s;
            return (sizes, max);
        }

        private static float BilinearMedian(float[] field, int w, int h, double fx, double fy)
        {
            int x0 = Math.Max(0, Math.Min((int)Math.Floor(fx), w - 1));
            int y0 = Math.Max(0, Math.Min((int)Math.Floor(fy), h - 1));
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            double tx = fx - x0, ty = fy - y0;
            float M(int x, int y) { int i = (y * w + x) * 3; return MsdfGenerator.Median(field[i], field[i + 1], field[i + 2]); }
            float top = (float)(M(x0, y0) * (1 - tx) + M(x1, y0) * tx);
            float bot = (float)(M(x0, y1) * (1 - tx) + M(x1, y1) * tx);
            return (float)(top * (1 - ty) + bot * ty);
        }

        // ---------------- Issue 2: polarity vs winding ----------------

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void RealExport_FieldSign_MatchesWinding_ForOverlappingContourGlyphs()
        {
            IntPtr face = FT.LoadFace(_noto);
            Assert.AreNotEqual(IntPtr.Zero, face);
            try
            {
                Assert.IsTrue(FT.OutlineExportAvailable(face), "export missing");
                AssertSignMatchesWinding(face, new[] { '&', 'g', '@', 'O', 'B', '8' }, ppem: 48, spread: 6, label: "NotoSans");
            }
            finally { FT.UnloadFace(face); }
        }

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void RealExport_FieldSign_MatchesWinding_VariableFont_RobotoFlex()
        {
            string vf = MsdfTestUtil.FindRobotoFlexPath();
            if (vf == null) Assert.Ignore("RobotoFlex-VF.ttf not fetched (run NativeSource~/tests/fetch_fonts.ps1).");
            byte[] bytes = File.ReadAllBytes(vf);
            IntPtr face = FT.LoadFace(bytes);
            Assert.AreNotEqual(IntPtr.Zero, face);
            try
            {
                Assert.IsTrue(FT.OutlineExportAvailable(face), "export missing");
                AssertSignMatchesWinding(face, new[] { '&', 'g', '@', 'a', 'e', 'B' }, ppem: 48, spread: 6, label: "RobotoFlex-VF");
            }
            finally { FT.UnloadFace(face); }
        }

        private static void AssertSignMatchesWinding(IntPtr face, char[] glyphs, int ppem, int spread, string label)
        {
            var rows = new List<string>();
            var offenders = new List<string>();
            int evaluated = 0;

            foreach (char ch in glyphs)
            {
                var outline = OutlineFor(face, ch, ppem);
                if (outline == null) { rows.Add($"  '{ch}': unavailable — skipped"); continue; }
                var shape = Shape.FromOutline(outline);
                var msdf = MsdfBuilder.Build(outline, spread, errorCorrection: false);
                Assert.IsTrue(msdf.IsValid, $"build failed '{ch}'");

                int w = msdf.Width, h = msdf.Height;
                int mismatch = 0, tested = 0, insideCount = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        // Field is bottom-up; median>0.5 == field says inside.
                        float med = MsdfBuilder.SampleMedianSigned(msdf.Field, w, h, x, y, msdf.Range);
                        bool fieldInside = med > 0f;

                        double sx = (x + 0.5) + msdf.BitmapLeft;
                        double sy = (y + 0.5) + (msdf.BitmapTop - h);
                        bool trueInside = shape.Contains(new Vector2D(sx, sy));

                        // Skip the ~1px ambiguous edge band.
                        if (Math.Abs(med) <= 1.0f) continue;
                        tested++;
                        if (fieldInside) insideCount++;
                        if (fieldInside != trueInside) mismatch++;
                    }

                double rate = tested > 0 ? (double)mismatch / tested : 0;
                double insideFrac = tested > 0 ? (double)insideCount / tested : 0;
                rows.Add($"  '{ch}': sign-mismatch {mismatch}/{tested} ({rate:P1}), inside-fraction {insideFrac:P0}");
                // Overlap/polarity bug shows as either a high mismatch rate or a nearly-all-inside field.
                if (rate > 0.02 || insideFrac > 0.75)
                    offenders.Add($"'{ch}': mismatch {rate:P1}, inside {insideFrac:P0}");
                evaluated++;
            }

            string table = $"[{label}] field sign vs non-zero winding:\n" + string.Join("\n", rows);
            TestContext.WriteLine(table);
            Assert.GreaterOrEqual(evaluated, 4, "Too few glyphs evaluable:\n" + table);
            Assert.IsEmpty(offenders, $"[{label}] polarity/sign errors:\n" + string.Join("\n", offenders) + "\n" + table);
        }
    }
}
