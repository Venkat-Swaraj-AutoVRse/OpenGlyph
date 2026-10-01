using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using LightSide;
using LightSide.Msdf;

namespace LightSide.Tests
{
    /// <summary>
    /// ROOT-CAUSE gate for the "vertical line / notch" family of MSDF/SDF artifacts (phase-1a round
    /// 6). The reviewer's complaint — a full-height column through the '@' cell, present in EMPTY
    /// BACKGROUND, with the SDF row showing notches at the SAME column — can only come from code
    /// SHARED by our single-channel SDF and multi-channel MSDF: the per-pixel signed distance and its
    /// sign, NOT edge colouring (a colouring seam cannot paint a line in the background). So we test
    /// the shared field DIRECTLY against an INDEPENDENT brute-force exact signed distance:
    ///
    ///   reference(p) = (shape.Contains(p) ? +1 : -1) * min over every outline segment of the exact
    ///                  point-to-segment distance, with each curve densely flattened.
    ///
    /// This reference shares NO code with <see cref="EdgeSegment.MinSignedDistance"/> (the method
    /// under test): it is a straight min-of-point-to-segment plus a non-zero-winding ray cast. We
    /// assert the generator's single-channel SDF matches it within 0.5 px of field range at EVERY
    /// pixel in the cell (background included), and that the MSDF median — reconstructed the way the
    /// shader does, by BILINEARLY interpolating the RGB field and taking the median — never flips to
    /// the wrong side of the contour away from the true edge (the phantom column/notch).
    ///
    /// Real FreeType outlines on Windows editor x64 (the binary that ships the export); the glyph set
    /// is the reviewer's: A M W N k x &amp; g @.
    /// </summary>
    [TestFixture]
    public class MsdfExactDistanceTests
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

        // ---- independent brute-force exact signed distance (shares no code with MinSignedDistance) ----

        private static List<(Vector2D a, Vector2D b)> DenseSpans(Shape shape, int perCurve = 256)
        {
            var spans = new List<(Vector2D, Vector2D)>();
            foreach (var c in shape.Contours)
                foreach (var e in c.Edges)
                {
                    int steps = (e is LinearSegment) ? 1 : perCurve;
                    Vector2D prev = e.Point(0);
                    for (int i = 1; i <= steps; i++)
                    {
                        Vector2D cur = e.Point((double)i / steps);
                        spans.Add((prev, cur));
                        prev = cur;
                    }
                }
            return spans;
        }

        private static double PointSegDist(Vector2D p, Vector2D a, Vector2D b)
        {
            Vector2D ab = b - a;
            double len2 = Vector2D.Dot(ab, ab);
            double t = len2 > 0 ? Vector2D.Dot(p - a, ab) / len2 : 0;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return (p - (a + ab * t)).Length();
        }

        private static double BruteSignedDistance(List<(Vector2D a, Vector2D b)> spans, Shape shape, Vector2D p)
        {
            double best = double.MaxValue;
            foreach (var (a, b) in spans)
            {
                double d = PointSegDist(p, a, b);
                if (d < best) best = d;
            }
            return shape.Contains(p) ? best : -best;
        }

        /// <summary>
        /// The generator's single-channel SDF must equal the brute-force exact signed distance within
        /// 0.5 px of field range at EVERY pixel of the cell, background included. A sign disagreement
        /// anywhere off the ~1px edge band is a shared-code defect (the vertical-line root cause).
        /// </summary>
        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void SdfField_MatchesBruteForceExactSignedDistance_EveryPixel()
        {
            IntPtr face = FT.LoadFace(_noto);
            Assert.AreNotEqual(IntPtr.Zero, face);
            try
            {
                Assert.IsTrue(FT.OutlineExportAvailable(face), "export missing on this binary");
                char[] glyphs = { 'A', 'M', 'W', 'N', 'k', 'x', '&', 'g', '@' };
                int ppem = 64, spread = 8;
                const double tolPx = 0.5;

                var rows = new List<string>();
                var offenders = new List<string>();
                int evaluated = 0;

                foreach (char ch in glyphs)
                {
                    var outline = OutlineFor(face, ch, ppem);
                    if (outline == null) { rows.Add($"  '{ch}': unavailable — skipped"); continue; }

                    float[] sdf = MsdfBuilder.BuildSdf(outline, spread, out int w, out int h, out double range);
                    Assert.IsNotNull(sdf, $"BuildSdf null for '{ch}'");

                    var shape = Shape.FromOutline(outline);
                    shape.OrientContours();
                    shape.Bounds(out double l, out double b, out double r, out double t);
                    int gx0 = (int)Math.Floor(l), gy0 = (int)Math.Floor(b);
                    double tX = spread - gx0, tY = spread - gy0;
                    var spans = DenseSpans(shape);
                    double half = range / 2.0;

                    double maxErr = 0, maxErrBg = 0; int worstX = -1, worstY = -1;
                    int signFlips = 0, signFlipsBg = 0; int worstCol = -1; var colFlip = new int[w];

                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            double px = (x + 0.5) - tX, py = (y + 0.5) - tY;
                            double brute = BruteSignedDistance(spans, shape, new Vector2D(px, py));
                            double gen = (sdf[y * w + x] - 0.5) * range;
                            double bc = Math.Max(-half, Math.Min(half, brute));
                            double gc = Math.Max(-half, Math.Min(half, gen));
                            double err = Math.Abs(bc - gc);
                            if (err > maxErr) { maxErr = err; worstX = x; worstY = y; }

                            bool trueInside = brute > 0;
                            // Count sign flips strictly OUTSIDE the ~1px true-edge band.
                            if (Math.Abs(brute) > 1.0 && (gen > 0) != trueInside)
                            {
                                signFlips++; colFlip[x]++;
                                if (!trueInside) { signFlipsBg++; if (err > maxErrBg) maxErrBg = err; }
                            }
                        }
                    for (int x = 0; x < w; x++) if (worstCol < 0 || colFlip[x] > colFlip[worstCol]) worstCol = x;

                    rows.Add($"  '{ch}': maxErr={maxErr:F3}px off-edge sign-flips={signFlips} (bg {signFlipsBg}) worstCol x={worstCol}×{(worstCol >= 0 ? colFlip[worstCol] : 0)}");
                    if (maxErr > tolPx || signFlips > 0)
                        offenders.Add($"'{ch}': maxErr {maxErr:F3}px at ({worstX},{worstY}), {signFlips} off-edge sign-flips ({signFlipsBg} in background), worst column x={worstCol}");
                    evaluated++;
                }

                string table = "Generator SDF vs brute-force exact signed distance (|err|<=0.5px, no off-edge sign flip):\n" + string.Join("\n", rows);
                TestContext.WriteLine(table);
                Assert.GreaterOrEqual(evaluated, 5, "Too few glyphs evaluable:\n" + table);
                Assert.IsEmpty(offenders,
                    "Generator SDF diverges from exact signed distance (shared-code artifact):\n" + string.Join("\n", offenders) + "\n" + table);
            }
            finally { FT.UnloadFace(face); }
        }

        /// <summary>
        /// The MSDF median, reconstructed the way the shader samples it — BILINEAR interpolation of
        /// the RGB field, then median of the interpolated channels — must never flip to the wrong
        /// side of the contour more than 1 px away from the true outline. A phantom column (the '@'
        /// line) is exactly a run of such flips sharing one x. Reference side is the brute-force
        /// signed distance. Error correction ON (the shipped path).
        /// </summary>
        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void MsdfMedian_BilinearReconstruction_NoPhantomColumn()
        {
            IntPtr face = FT.LoadFace(_noto);
            Assert.AreNotEqual(IntPtr.Zero, face);
            try
            {
                Assert.IsTrue(FT.OutlineExportAvailable(face), "export missing on this binary");
                char[] glyphs = { 'A', 'M', 'W', 'N', 'k', 'x', '&', 'g', '@' };
                int ppem = 64, spread = 8, sub = 8;
                const double edgeBand = 1.0;

                var rows = new List<string>();
                var offenders = new List<string>();
                int evaluated = 0;

                foreach (char ch in glyphs)
                {
                    var outline = OutlineFor(face, ch, ppem);
                    if (outline == null) { rows.Add($"  '{ch}': unavailable — skipped"); continue; }

                    var msdf = MsdfBuilder.Build(outline, spread); // EC ON (shipped)
                    Assert.IsTrue(msdf.IsValid, $"build failed '{ch}'");
                    int w = msdf.Width, h = msdf.Height; double range = msdf.Range;

                    var shape = Shape.FromOutline(outline);
                    shape.OrientContours();
                    shape.Bounds(out double l, out double b, out double r, out double t);
                    int gx0 = (int)Math.Floor(l), gy0 = (int)Math.Floor(b);
                    double tX = spread - gx0, tY = spread - gy0;
                    var spans = DenseSpans(shape);

                    int phantom = 0, scanned = 0; var colPh = new int[w];
                    for (int y = 0; y + 1 < h; y++)
                        for (int x = 0; x + 1 < w; x++)
                            for (int syi = 0; syi < sub; syi++)
                                for (int sxi = 0; sxi < sub; sxi++)
                                {
                                    double fx = x + (sxi + 0.5) / sub, fy = y + (syi + 0.5) / sub;
                                    double px = (fx + 0.5) - tX, py = (fy + 0.5) - tY;
                                    double brute = BruteSignedDistance(spans, shape, new Vector2D(px, py));
                                    if (Math.Abs(brute) <= edgeBand) continue;
                                    scanned++;
                                    float med = BilinearMedianField(msdf.Field, w, h, fx, fy);
                                    if ((med > 0.5f) != (brute > 0)) { phantom++; colPh[x]++; }
                                }
                    int worstCol = -1; for (int x = 0; x < w; x++) if (worstCol < 0 || colPh[x] > colPh[worstCol]) worstCol = x;
                    int worstColCount = worstCol >= 0 ? colPh[worstCol] : 0;

                    rows.Add($"  '{ch}': off-edge bilinear phantoms={phantom}/{scanned}; worst column x={worstCol} count={worstColCount}");
                    // A phantom column is a sustained run; flag when any column accumulates many flips
                    // (a line) OR total phantoms are non-trivial.
                    if (worstColCount > 2 * sub || phantom > 8)
                        offenders.Add($"'{ch}': {phantom} off-edge phantom sub-samples, worst column x={worstCol} with {worstColCount}");
                    evaluated++;
                }

                string table = "MSDF median (bilinear reconstruction) phantom scan vs exact distance:\n" + string.Join("\n", rows);
                TestContext.WriteLine(table);
                Assert.GreaterOrEqual(evaluated, 5, "Too few glyphs evaluable:\n" + table);
                Assert.IsEmpty(offenders,
                    "MSDF median reconstructs a phantom column/line away from the true edge:\n" + string.Join("\n", offenders) + "\n" + table);
            }
            finally { FT.UnloadFace(face); }
        }

        private static float BilinearMedianField(float[] field, int w, int h, double fx, double fy)
        {
            int x0 = Math.Max(0, Math.Min((int)Math.Floor(fx), w - 1));
            int y0 = Math.Max(0, Math.Min((int)Math.Floor(fy), h - 1));
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            double tx = fx - x0, ty = fy - y0;
            float Ch(int x, int y, int c) => field[(y * w + x) * 3 + c];
            float Lerp2(int c)
            {
                double top = Ch(x0, y0, c) * (1 - tx) + Ch(x1, y0, c) * tx;
                double bot = Ch(x0, y1, c) * (1 - tx) + Ch(x1, y1, c) * tx;
                return (float)(top * (1 - ty) + bot * ty);
            }
            return MsdfGenerator.Median(Lerp2(0), Lerp2(1), Lerp2(2));
        }
    }
}
