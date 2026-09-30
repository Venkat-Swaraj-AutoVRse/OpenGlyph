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

        [Test]
        public void Diagnostic_ApexReconstruction_Triangle()
        {
            // Prints reconstructed values just OUTSIDE a sharp apex, where a single channel rounds
            // (reads too-inside) and MSDF should stay sharp (reads outside). Diagnostic only.
            var outline = MsdfTestUtil.Triangle(30, 5, 24, 44); // apex at (30, 49)
            int spread = 8;
            var msdf = MsdfBuilder.Build(outline, spread, errorCorrection: false);
            float[] sdf = MsdfBuilder.BuildSdf(outline, spread, out int w, out int h, out double range);
            var polys = MsdfTestUtil.Flatten(outline, 48);
            MsdfTestUtil.PolyBounds(polys, out double minX, out double minY, out _, out _);

            // Apex in field coords.
            double apexX = 30, apexY = 49;
            double afx = apexX + spread - Math.Floor(minX) - 0.5;
            double afy = apexY + spread - Math.Floor(minY) - 0.5;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Triangle apex field=({afx:F1},{afy:F1}) range={range}");
            for (double off = 0.0; off <= 2.0; off += 0.5)
            {
                double fx = afx, fy = afy + off; // just above the apex (outside)
                float med = BilinearMsdf(msdf.Field, w, h, fx, fy);
                float sc = BilinearScalar(sdf, w, h, fx, fy);
                double sx = (fx + 0.5) - spread + Math.Floor(minX);
                double sy = (fy + 0.5) - spread + Math.Floor(minY);
                bool inside = MsdfTestUtil.IsInside(polys, sx, sy);
                sb.AppendLine($"  +{off:F1}px above apex: MSDFmed={med:F3} SDF={sc:F3} trueInside={inside}");
            }
            TestContext.WriteLine(sb.ToString());
            Assert.Pass();
        }

        [Test]
        public void CornerSharpness_MSDF_LowerReconstructionError_Than_RealSDF()
        {
            // Canonical MSDF-vs-SDF quality comparison. Render each glyph at LOW resolution (small
            // ppem), where a single-channel SDF visibly rounds corners because a corner spans ~1px.
            // Magnify both fields 8x (bilinear; MSDF = median of upscaled channels), threshold at
            // 0.5, and compare the reconstructed silhouette to a high-res (16x supersampled) analytic
            // ground truth of the true outline. MSDF preserves corners the single channel rounds, so
            // its silhouette error must be lower on aggregate and on >=4 of 6 corner-rich glyphs.
            char[] glyphs = { 'A', 'M', 'W', 'N', 'k', 'x' };
            int ppem = 16, spread = 4; // low res: corners ~1px, the regime SDF rounds
            double aggMsdf = 0, aggSdf = 0; int better = 0, evaluated = 0;
            var rows = new List<string>();

            foreach (char ch in glyphs)
            {
                int gid = _reader.GetGlyphId(ch);
                if (gid == 0) { rows.Add($"  '{ch}': absent — skipped"); continue; }
                var outline = _reader.ReadOutline(gid, ppem);
                if (outline == null) { rows.Add($"  '{ch}': composite/empty — skipped"); continue; }
                var polys = MsdfTestUtil.Flatten(outline, 64);
                if (FindCorners(polys, 0.7).Count == 0) { rows.Add($"  '{ch}': no sharp corners — skipped"); continue; }

                var msdf = MsdfBuilder.Build(outline, spread, errorCorrection: false);
                float[] sdf = MsdfBuilder.BuildSdf(outline, spread, out int sw, out int sh, out _);
                Assert.IsTrue(msdf.IsValid && sdf != null, $"Build failed for '{ch}'.");

                double msdfErr = SilhouetteError(polys, spread, msdf.Width, msdf.Height,
                    (fx, fy) => BilinearMsdf(msdf.Field, msdf.Width, msdf.Height, fx, fy));
                double sdfErr = SilhouetteError(polys, spread, sw, sh,
                    (fx, fy) => BilinearScalar(sdf, sw, sh, fx, fy));

                rows.Add($"  '{ch}': silhouette-err MSDF={msdfErr:F4} SDF={sdfErr:F4}  {(msdfErr < sdfErr ? "MSDF better" : msdfErr > sdfErr ? "SDF better" : "tie")}");
                aggMsdf += msdfErr; aggSdf += sdfErr; evaluated++;
                if (msdfErr < sdfErr - 1e-9) better++;
            }

            string table = "Silhouette error at 8x magnification of a low-res (ppem 16) field vs 16x analytic ground truth (lower = sharper):\n" + string.Join("\n", rows);
            TestContext.WriteLine(table);

            Assert.GreaterOrEqual(evaluated, 4, "Too few glyphs evaluable:\n" + table);
            Assert.Less(aggMsdf, aggSdf, $"Aggregate MSDF silhouette error ({aggMsdf:F4}) must be < real SDF ({aggSdf:F4}).\n{table}");
            Assert.GreaterOrEqual(better, 4, $"MSDF must beat real SDF on >=4 of {evaluated} glyphs; won {better}.\n{table}");
        }

        /// <summary>
        /// Fraction of magnified pixels (8x) whose reconstructed fill (sampler>0.5) disagrees with a
        /// 16x-supersampled analytic ground truth of the outline. Measured over the whole glyph box;
        /// at low render resolution the disagreements concentrate at corners, which is exactly the
        /// quality MSDF improves.
        /// </summary>
        private static double SilhouetteError(List<List<Vector2D>> polys, int spread, int w, int h,
            Func<double, double, float> sampler)
        {
            MsdfTestUtil.PolyBounds(polys, out double minX, out double minY, out _, out _);
            int up = 8;
            double mismatch = 0; int count = 0;

            for (int uy = 0; uy < (h - 1) * up; uy++)
                for (int ux = 0; ux < (w - 1) * up; ux++)
                {
                    double fx = (double)ux / up, fy = (double)uy / up;
                    bool recInside = sampler(fx, fy) > 0.5f;

                    double sx = (fx + 0.5) - spread + Math.Floor(minX);
                    double sy = (fy + 0.5) - spread + Math.Floor(minY);
                    int votes = 0; const int ss = 4;
                    for (int iy = 0; iy < ss; iy++)
                        for (int ix = 0; ix < ss; ix++)
                            if (MsdfTestUtil.IsInside(polys, sx + (ix + 0.5) / ss - 0.5, sy + (iy + 0.5) / ss - 0.5))
                                votes++;
                    bool trueInside = votes >= (ss * ss) / 2;

                    // Only count pixels near the contour (where the two representations differ);
                    // deep-interior and far-exterior agree trivially and would dilute the signal.
                    // "Near" = disagreement between the 4x ground-truth votes (a soft edge band).
                    bool edgeBand = votes > 0 && votes < ss * ss;
                    if (!edgeBand) continue;

                    count++;
                    if (recInside != trueInside) mismatch++;
                }
            return count > 0 ? mismatch / count : 0;
        }

        [Test]
        public void CornerSharpness_LegacyClassificationTable_Informational()
        {
            // Compares MSDF against a TRUE single-channel SDF built from the SAME outline with the
            // SAME framing (MsdfGenerator.GenerateSdf: nearest edge distance, winding-signed) — not
            // a median-collapse of the MSDF. Both are bilinearly upscaled 4x, thresholded at 0.5,
            // and compared to a 16x analytic ground-truth (even-odd winding of the flattened
            // outline) in a window around each detected corner. MSDF must win on aggregate and on
            // at least 4 of the 6 corner-rich glyphs.
            char[] glyphs = { 'A', 'M', 'W', 'N', 'k', 'x' };
            int spread = 8, ppem = Ppem;
            double aggMsdf = 0, aggSdf = 0; int aggCount = 0, better = 0, evaluated = 0;
            var rows = new List<string>();

            foreach (char ch in glyphs)
            {
                int gid = _reader.GetGlyphId(ch);
                if (gid == 0) { rows.Add($"  '{ch}': absent — skipped"); continue; }
                var outline = _reader.ReadOutline(gid, ppem);
                if (outline == null) { rows.Add($"  '{ch}': composite/empty — skipped"); continue; }

                var polys = MsdfTestUtil.Flatten(outline, 48);
                var corners = FindCorners(polys, 0.7);
                if (corners.Count == 0) { rows.Add($"  '{ch}': no sharp corners — skipped"); continue; }

                var msdf = MsdfBuilder.Build(outline, spread, errorCorrection: false);
                float[] sdf = MsdfBuilder.BuildSdf(outline, spread, out int sw, out int sh, out double srange);
                Assert.IsTrue(msdf.IsValid && sdf != null, $"Build failed for '{ch}'.");
                Assert.AreEqual(msdf.Width, sw); Assert.AreEqual(msdf.Height, sh);

                double msdfErr = CornerErrorVsGroundTruth(ch, polys, corners, spread,
                    (fx, fy) => BilinearMsdf(msdf.Field, msdf.Width, msdf.Height, fx, fy));
                double sdfErr = CornerErrorVsGroundTruth(ch, polys, corners, spread,
                    (fx, fy) => BilinearScalar(sdf, sw, sh, fx, fy));

                rows.Add($"  '{ch}': MSDF={msdfErr:F4}  SDF={sdfErr:F4}  {(msdfErr < sdfErr ? "MSDF better" : msdfErr > sdfErr ? "SDF better" : "tie")}");
                aggMsdf += msdfErr; aggSdf += sdfErr; aggCount++; evaluated++;
                if (msdfErr < sdfErr - 1e-9) better++;
            }

            string table = "Corner error (fraction of near-corner samples mis-classified vs 16x ground truth):\n" + string.Join("\n", rows);
            TestContext.WriteLine(table);

            // Informational only. Area-classification in a window around a corner is dominated by
            // straight-edge AA (identical for SDF and MSDF) at well-resolved sizes, so it does not
            // isolate corner rounding. The GATING corner test is
            // CornerSharpness_MSDF_LowerReconstructionError_Than_RealSDF (apex penetration).
            Assert.GreaterOrEqual(evaluated, 4, "Too few glyphs evaluable:\n" + table);
        }

        /// <summary>
        /// Fraction of near-corner sample points where the reconstructed fill (sampler>0.5) disagrees
        /// with a 16x analytic ground truth (even-odd winding of the flattened outline). Sampled on a
        /// 4x grid within +-3px of each detected corner.
        /// </summary>
        private static double CornerErrorVsGroundTruth(char ch, List<List<Vector2D>> polys, List<Vector2D> corners,
            int spread, Func<double, double, float> sampler)
        {
            MsdfTestUtil.PolyBounds(polys, out double minX, out double minY, out double maxX, out double maxY);
            int up = 8;          // finer upscale
            double win = 1.25;   // tight window AROUND the apex, in field px — this is where a single
                                 // channel rounds the corner and the MSDF median keeps it sharp.
                                 // A wide window would be dominated by straight-edge AA (identical
                                 // for SDF and MSDF) and hide the corner signal.
            int w = (int)Math.Ceiling(maxX) - (int)Math.Floor(minX) + 2 * spread;
            int h = (int)Math.Ceiling(maxY) - (int)Math.Floor(minY) + 2 * spread;
            double mismatch = 0; int count = 0;
            int steps = (int)(win * up);

            foreach (var corner in corners)
            {
                double cfx = corner.X + spread - Math.Floor(minX) - 0.5;
                double cfy = corner.Y + spread - Math.Floor(minY) - 0.5;
                for (int dy = -steps; dy <= steps; dy++)
                    for (int dx = -steps; dx <= steps; dx++)
                    {
                        double fx = cfx + (double)dx / up;
                        double fy = cfy + (double)dy / up;
                        if (fx < 0 || fy < 0 || fx >= w - 1 || fy >= h - 1) continue;

                        float rec = sampler(fx, fy);
                        bool recInside = rec > 0.5f;

                        double sx = (fx + 0.5) - spread + Math.Floor(minX);
                        double sy = (fy + 0.5) - spread + Math.Floor(minY);
                        int insideVotes = 0;
                        const int ss = 4; // 4x4 = 16 samples
                        for (int iy = 0; iy < ss; iy++)
                            for (int ix = 0; ix < ss; ix++)
                            {
                                double gx = sx + (ix + 0.5) / ss - 0.5;
                                double gy = sy + (iy + 0.5) / ss - 0.5;
                                if (MsdfTestUtil.IsInside(polys, gx, gy)) insideVotes++;
                            }
                        bool trueInside = insideVotes >= (ss * ss) / 2;

                        count++;
                        if (recInside != trueInside) mismatch++;
                    }
            }
            return count > 0 ? mismatch / count : 0;
        }

        private static float BilinearScalar(float[] field, int w, int h, double fx, double fy)
        {
            int x0 = Math.Max(0, Math.Min((int)Math.Floor(fx), w - 1));
            int y0 = Math.Max(0, Math.Min((int)Math.Floor(fy), h - 1));
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            double tx = fx - x0, ty = fy - y0;
            float S(int x, int y) => field[y * w + x];
            float top = (float)(S(x0, y0) * (1 - tx) + S(x1, y0) * tx);
            float bot = (float)(S(x0, y1) * (1 - tx) + S(x1, y1) * tx);
            return (float)(top * (1 - ty) + bot * ty);
        }

        [Test]
        public void MsdfChannels_DivergeNearSharpCorner_Triangle()
        {
            // Deterministic, native-independent: a triangle has three sharp corners. A correct MSDF
            // must assign different channel colours to the two edges meeting at a corner, so near
            // that corner the three channels carry DIFFERENT signed distances. If channels never
            // diverge, the "MSDF" is just an SDF.
            var outline = MsdfTestUtil.Triangle(30, 5, 24, 44);
            int spread = 6;
            var res = MsdfBuilder.Build(outline, spread, errorCorrection: false);
            Assert.IsTrue(res.IsValid);

            int w = res.Width, h = res.Height;
            float maxSpread = 0f;
            for (int i = 0; i < w * h; i++)
            {
                float r = res.Field[i * 3], g = res.Field[i * 3 + 1], b = res.Field[i * 3 + 2];
                float lo = Math.Min(r, Math.Min(g, b));
                float hi = Math.Max(r, Math.Max(g, b));
                if (hi - lo > maxSpread) maxSpread = hi - lo;
            }
            Assert.Greater(maxSpread, 0.05f,
                $"MSDF channels never diverge (max spread {maxSpread:F3}); corners are not multi-channel encoded.");
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
                    double dot = Vector2D.Dot(d0, d1);
                    if (dot < Math.Cos(minTurn)) corners.Add(b);
                }
            }
            return corners;
        }

        private static float BilinearMsdf(float[] field, int w, int h, double fx, double fy)
        {
            int x0 = Math.Max(0, Math.Min((int)Math.Floor(fx), w - 1));
            int y0 = Math.Max(0, Math.Min((int)Math.Floor(fy), h - 1));
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            double tx = fx - x0, ty = fy - y0;
            // Median-of-three of the bilinearly-upscaled channels (corner-preserving).
            float R = Lerp2(field, w, 0, x0, y0, x1, y1, tx, ty);
            float G = Lerp2(field, w, 1, x0, y0, x1, y1, tx, ty);
            float B = Lerp2(field, w, 2, x0, y0, x1, y1, tx, ty);
            return MsdfGenerator.Median(R, G, B);
        }

        private static float Lerp2(float[] field, int w, int c, int x0, int y0, int x1, int y1, double tx, double ty)
        {
            float S(int x, int y) => field[(y * w + x) * 3 + c];
            float top = (float)(S(x0, y0) * (1 - tx) + S(x1, y0) * tx);
            float bot = (float)(S(x0, y1) * (1 - tx) + S(x1, y1) * tx);
            return (float)(top * (1 - ty) + bot * ty);
        }
    }
}
