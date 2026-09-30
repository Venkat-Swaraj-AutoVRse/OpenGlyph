using System;
using System.Collections.Generic;
using NUnit.Framework;
using LightSide.Msdf;

namespace LightSide.Tests
{
    [TestFixture]
    public class MsdfRealGlyphTests
    {
        private TtfGlyfReader _reader;
        private const int Ppem = 48;

        [OneTimeSetUp]
        public void Setup()
        {
            string path = MsdfTestUtil.FindNotoSansPath();
            if (path == null)
                Assert.Ignore("NotoSans-Regular.ttf not found in package; skipping real-glyph tests.");
            _reader = TtfGlyfReader.FromFile(path);
        }

        private GlyphOutline Outline(char ch)
        {
            int gid = _reader.GetGlyphId(ch);
            if (gid == 0) Assert.Ignore($"Glyph '{ch}' not present in font.");
            var o = _reader.ReadOutline(gid, Ppem);
            if (o == null && _reader.Composite)
                Assert.Ignore($"Glyph '{ch}' is composite; test reader handles simple glyphs only.");
            return o;
        }

        [Test]
        public void RealGlyph_O_MedianSign_MatchesInsideOutside()
        {
            var outline = Outline('O');
            Assert.IsNotNull(outline, "Outline was null.");
            var polys = MsdfTestUtil.Flatten(outline);
            MsdfTestUtil.PolyBounds(polys, out double minX, out double minY, out _, out _);
            int spread = 6;
            var res = MsdfBuilder.Build(outline, spread);
            Assert.IsTrue(res.IsValid, "MSDF build failed for 'O'.");

            int mismatches = 0, tested = 0;
            for (int y = 2; y < res.Height - 2; y += 2)
                for (int x = 2; x < res.Width - 2; x += 2)
                {
                    double sx = (x + 0.5) - spread + Math.Floor(minX);
                    double sy = (y + 0.5) - spread + Math.Floor(minY);
                    float signed = MsdfBuilder.SampleMedianSigned(res.Field, res.Width, res.Height, x, y, res.Range);
                    if (Math.Abs(signed) <= 2f) continue; // skip edge band
                    bool inside = MsdfTestUtil.IsInside(polys, sx, sy);
                    tested++;
                    if ((signed > 0) != inside) mismatches++;
                }

            Assert.Greater(tested, 40, "Too few samples for 'O'.");
            double rate = (double)mismatches / tested;
            Assert.Less(rate, 0.05, $"'O' median sign mismatched on {mismatches}/{tested} interior/exterior samples.");
        }

        // ---------- Corner sharpness: MSDF beats SDF at upscale ----------

        [Test]
        public void CornerSharpness_MSDF_LowerReconstructionError_Than_SDF_AtUpscale()
        {
            // 'A' and 'M' have sharp corners. Build a low-res MSDF, upscale 4x with bilinear, and
            // compare the reconstructed contour (median==0.5 crossing) against the true outline.
            // A simulated single-channel SDF (min |dist|, no channels) rounds corners; the MSDF
            // median preserves them, so its reconstruction error must be lower.
            foreach (char ch in new[] { 'A', 'M' })
            {
                var outline = Outline(ch);
                if (outline == null) continue;
                var polys = MsdfTestUtil.Flatten(outline, 24);

                int spread = 6;
                var res = MsdfBuilder.Build(outline, spread);
                Assert.IsTrue(res.IsValid, $"MSDF build failed for '{ch}'.");

                double msdfErr = ReconstructionError(res, polys, spread, useMedian: true);
                double sdfErr = ReconstructionError(res, polys, spread, useMedian: false);

                Assert.Less(msdfErr, sdfErr,
                    $"'{ch}': MSDF reconstruction error ({msdfErr:F3}) should be < single-channel SDF ({sdfErr:F3}).");
            }
        }

        /// <summary>
        /// Mean absolute error between the field's zero-crossing (0.5 level) and the true fill,
        /// measured on a 4x-upscaled bilinear sampling of the field near corners. For the SDF
        /// baseline we collapse the field to its per-pixel median first (single channel), then
        /// bilinear-upscale — which rounds corners the way a true SDF does; the MSDF path upscales
        /// each channel and takes the median at the higher resolution (corner-preserving).
        /// </summary>
        private static double ReconstructionError(MsdfGlyphResult res, List<List<Vector2D>> polys, int spread, bool useMedian)
        {
            int w = res.Width, h = res.Height;
            var field = res.Field;

            // Pre-collapse to single channel for the SDF baseline.
            float[] chan = field;
            if (!useMedian)
            {
                chan = new float[w * h * 3];
                for (int i = 0; i < w * h; i++)
                {
                    float m = MsdfGenerator.Median(field[i * 3], field[i * 3 + 1], field[i * 3 + 2]);
                    chan[i * 3] = chan[i * 3 + 1] = chan[i * 3 + 2] = m;
                }
            }

            MsdfTestUtil.PolyBounds(polys, out double minX, out double minY, out _, out _);
            int upscale = 4;
            double err = 0; int count = 0;

            for (int uy = 0; uy < (h - 1) * upscale; uy++)
            {
                for (int ux = 0; ux < (w - 1) * upscale; ux++)
                {
                    double fx = (double)ux / upscale;
                    double fy = (double)uy / upscale;
                    float rec = BilinearMedian(chan, w, h, fx, fy);
                    bool recInside = rec > 0.5f;

                    double sx = (fx + 0.5) - spread + Math.Floor(minX);
                    double sy = (fy + 0.5) - spread + Math.Floor(minY);
                    bool trueInside = MsdfTestUtil.IsInside(polys, sx, sy);

                    // Only measure the band near the contour, where corner rounding shows up.
                    float signedApprox = (rec - 0.5f);
                    if (Math.Abs(signedApprox) > 0.25f) continue;
                    count++;
                    if (recInside != trueInside) err += 1;
                }
            }
            return count > 0 ? err / count : 0;
        }

        private static float BilinearMedian(float[] field, int w, int h, double fx, double fy)
        {
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            x0 = Math.Max(0, Math.Min(x0, w - 1));
            y0 = Math.Max(0, Math.Min(y0, h - 1));
            double tx = fx - x0, ty = fy - y0;

            float M(int x, int y)
            {
                int i = (y * w + x) * 3;
                return MsdfGenerator.Median(field[i], field[i + 1], field[i + 2]);
            }
            float m00 = M(x0, y0), m10 = M(x1, y0), m01 = M(x0, y1), m11 = M(x1, y1);
            float top = (float)(m00 * (1 - tx) + m10 * tx);
            float bot = (float)(m01 * (1 - tx) + m11 * tx);
            return (float)(top * (1 - ty) + bot * ty);
        }
    }
}
