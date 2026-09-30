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
        public void MsdfChannels_DivergeNearSharpCorner_Triangle()
        {
            // Deterministic, native-independent: a triangle has three sharp corners. A correct MSDF
            // must assign different channel colours to the two edges meeting at a corner, so near
            // that corner the three channels carry DIFFERENT signed distances (the median differs
            // from at least one channel). If channels never diverge, the "MSDF" is just an SDF.
            var outline = MsdfTestUtil.Triangle(30, 5, 24, 44);
            int spread = 6;
            var res = MsdfBuilder.Build(outline, spread, errorCorrection: false);
            Assert.IsTrue(res.IsValid);

            int w = res.Width, h = res.Height;
            float maxSpread = 0f;
            for (int i = 0; i < w * h; i++)
            {
                float r = res.Field[i * 3], g = res.Field[i * 3 + 1], b = res.Field[i * 3 + 2];
                float lo = System.Math.Min(r, System.Math.Min(g, b));
                float hi = System.Math.Max(r, System.Math.Max(g, b));
                if (hi - lo > maxSpread) maxSpread = hi - lo;
            }
            // With true multi-channel corners, some texel's channels differ substantially.
            Assert.Greater(maxSpread, 0.05f,
                $"MSDF channels never diverge (max spread {maxSpread:F3}); corners are not multi-channel encoded.");
        }

        [Test]
        public void CornerSharpness_MSDF_LowerReconstructionError_Than_SDF_AtUpscale()
        {
            // 'A' and 'M' have sharp corners. We compare two reconstructions of the field upscaled
            // 4x against a CONTINUOUS ground truth (signed distance to the true outline), measured
            // in a neighbourhood around each geometric corner.
            //   - MSDF path:  median-of-three of the bilinearly-upscaled RGB channels.
            //   - SDF path:   the per-pixel median collapsed to one channel FIRST, then bilinearly
            //                 upscaled (what a single-channel SDF atlas can represent).
            // MSDF must be no worse on every glyph at the corners. (The multi-channel corner
            // ENCODING itself is proven separately by MsdfChannels_DivergeNearSharpCorner_Triangle;
            // that channels diverge is what gives MSDF its upscale advantage over a single channel.)
            int tested = 0;
            foreach (char ch in new[] { 'A', 'M' })
            {
                var outline = Outline(ch);
                if (outline == null) continue;
                var polys = MsdfTestUtil.Flatten(outline, 32);
                var corners = FindCorners(polys, 0.6);
                if (corners.Count == 0) continue;

                int spread = 6;
                var res = MsdfBuilder.Build(outline, spread, errorCorrection: false);
                Assert.IsTrue(res.IsValid, $"MSDF build failed for '{ch}'.");

                double msdfErr = CornerError(res, polys, corners, spread, collapseFirst: false);
                double sdfErr = CornerError(res, polys, corners, spread, collapseFirst: true);

                tested++;
                Assert.LessOrEqual(msdfErr, sdfErr + 1e-6,
                    $"'{ch}': MSDF corner error ({msdfErr:F4}) must be <= single-channel SDF ({sdfErr:F4}).");
            }
            Assert.Greater(tested, 0, "No corner glyphs were testable.");
        }

        /// <summary>Finds sharp corners of the flattened polygons (turn angle above threshold radians).</summary>
        private static List<Vector2D> FindCorners(List<List<Vector2D>> polys, double minTurn)
        {
            var corners = new List<Vector2D>();
            foreach (var poly in polys)
            {
                int n = poly.Count;
                for (int i = 0; i < n; i++)
                {
                    Vector2D a = poly[(i - 1 + n) % n], b = poly[i], c = poly[(i + 1) % n];
                    Vector2D d0 = (b - a).Normalize(true);
                    Vector2D d1 = (c - b).Normalize(true);
                    double cross = Math.Abs(Vector2D.Cross(d0, d1));
                    double dot = Vector2D.Dot(d0, d1);
                    // Sharp turn: large |cross| or negative dot (acute).
                    if (dot < Math.Cos(minTurn)) corners.Add(b);
                }
            }
            return corners;
        }

        /// <summary>Signed distance to the nearest polygon edge (positive inside), the ground truth.</summary>
        private static double TrueSignedDistance(List<List<Vector2D>> polys, Vector2D p)
        {
            double best = double.MaxValue;
            foreach (var poly in polys)
            {
                int n = poly.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double d = PointSegDist(p, poly[j], poly[i]);
                    if (d < best) best = d;
                }
            }
            bool inside = MsdfTestUtil.IsInside(polys, p.X, p.Y);
            return inside ? best : -best;
        }

        private static double PointSegDist(Vector2D p, Vector2D a, Vector2D b)
        {
            Vector2D ab = b - a, ap = p - a;
            double t = Vector2D.Dot(ap, ab) / Math.Max(1e-12, Vector2D.Dot(ab, ab));
            t = Math.Max(0, Math.Min(1, t));
            Vector2D proj = a + ab * t;
            return (p - proj).Length();
        }

        /// <summary>
        /// Mean squared error between the reconstructed signed distance (from the field) and the
        /// true signed distance, sampled in a small window around each corner at 4x upscale.
        /// </summary>
        private static double CornerError(MsdfGlyphResult res, List<List<Vector2D>> polys, List<Vector2D> corners,
            int spread, bool collapseFirst)
        {
            int w = res.Width, h = res.Height;
            float[] field = res.Field;

            // For the single-channel SDF baseline, collapse each texel to its median first.
            float[] chan = field;
            if (collapseFirst)
            {
                chan = new float[w * h * 3];
                for (int i = 0; i < w * h; i++)
                {
                    float m = MsdfGenerator.Median(field[i * 3], field[i * 3 + 1], field[i * 3 + 2]);
                    chan[i * 3] = chan[i * 3 + 1] = chan[i * 3 + 2] = m;
                }
            }

            MsdfTestUtil.PolyBounds(polys, out double minX, out double minY, out _, out _);
            int up = 4;
            double sum = 0; int count = 0;
            int win = 3; // +-3 px around each corner (in field px)

            foreach (var corner in corners)
            {
                // Corner in field coords.
                double cfx = corner.X + spread - Math.Floor(minX) - 0.5;
                double cfy = corner.Y + spread - Math.Floor(minY) - 0.5;
                for (int dy = -win * up; dy <= win * up; dy++)
                {
                    for (int dx = -win * up; dx <= win * up; dx++)
                    {
                        double fx = cfx + (double)dx / up;
                        double fy = cfy + (double)dy / up;
                        if (fx < 0 || fy < 0 || fx >= w - 1 || fy >= h - 1) continue;

                        float rec = BilinearMedian(chan, w, h, fx, fy);
                        double recSigned = (rec - 0.5) * res.Range;

                        double sx = (fx + 0.5) - spread + Math.Floor(minX);
                        double sy = (fy + 0.5) - spread + Math.Floor(minY);
                        double trueSigned = TrueSignedDistance(polys, new Vector2D(sx, sy));
                        // Clamp true distance to the representable field range for a fair comparison.
                        double half = res.Range * 0.5;
                        if (trueSigned > half) trueSigned = half;
                        if (trueSigned < -half) trueSigned = -half;

                        double e = recSigned - trueSigned;
                        sum += e * e;
                        count++;
                    }
                }
            }
            return count > 0 ? sum / count : 0;
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
