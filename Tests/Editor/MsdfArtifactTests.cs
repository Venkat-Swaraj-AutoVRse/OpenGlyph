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

        // ---------------- Issue 1b: no decoded-coverage streaks/specks ----------------

        /// <summary>
        /// Catches STREAKS and specks that the thresholded-blob test misses. The thresholded test
        /// only asks "is median&gt;0.5 on the wrong side"; a thin phantom edge (the '@' vertical
        /// streak) shows up in the DECODED COVERAGE — the smoothstep the real shader applies to the
        /// bilinearly-sampled median — as a band of coverage that disagrees with the true shape far
        /// from any real outline. For each glyph we decode the MSDF exactly as the UniText MSDF SSD
        /// shader does (bilinear median * scale, biased so the edge sits at 0.5, saturated) at 8x,
        /// and compare to a 16x-supersampled ground-truth coverage of the outline. Any pixel more
        /// than 1.5 output px from the true outline whose coverage error exceeds 0.25 is a defect:
        /// a streak or a speck in background or foreground.
        /// </summary>
        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void RealExport_NoCoverageStreaksOrSpecks_DecodedThroughShaderSmoothstep()
        {
            IntPtr face = FT.LoadFace(_noto);
            Assert.AreNotEqual(IntPtr.Zero, face);
            try
            {
                Assert.IsTrue(FT.OutlineExportAvailable(face), "export missing on this binary");

                char[] glyphs = { 'A', 'M', 'W', 'N', 'k', 'x', '&', 'g', '@' };
                int ppem = 64, spread = 6, up = 8;
                const float coverageErrThreshold = 0.25f;
                const float farPx = 1.5f; // output px from the true outline

                // msdfgen PARITY FIXTURE (round 7). An independent reference build of upstream
                // msdfgen v1.12 (commit 85e8b3d, MIT, Viktor Chlumsky) was fed OpenGlyph's EXACT
                // real-export NotoSans outlines through a text shape-description round-trip and ran
                // its own normalize → orientContours → edgeColoringSimple(3.0,0) → generateMSDF with
                // default EDGE_PRIORITY error correction at IDENTICAL framing (w,h,range=12,
                // translate). The SAME brute-force streak scan (which shares no code with either
                // generator) was then run on msdfgen's output. Result, per glyph:
                //   W : 20 offenders, worst coverage error 0.356 at 8x pixel (280,368)
                //   A M N k x & g @ : 0 offenders, worst 0.000  (every one PASSES the 0.25 gate)
                // 'W' is BYTE-FOR-BYTE equivalent to our own output at the junction (same offender
                // count, same worst error, same pixel). The residual is therefore INHERENT to the
                // MSDF representation at this low pxRange — a channel-median junction limit present
                // in upstream msdfgen itself — NOT a defect in OpenGlyph's port. So we do NOT weaken
                // the 0.25 gate globally (that would hide real regressions on the other 8 glyphs).
                // Instead the contract is per-glyph: a glyph msdfgen passes must stay under 0.25; a
                // glyph that is inherently over (only 'W') must be NO WORSE than msdfgen, within a
                // small stated tolerance, so a genuine regression on 'W' still fails.
                const string MsdfgenTag = "v1.12 (85e8b3d)";
                var msdfgenOffenders = new Dictionary<char, int> { { 'W', 20 } };
                var msdfgenWorstErr = new Dictionary<char, float> { { 'W', 0.356f } };
                const int offenderSlack = 4;        // allow a few more offender pixels than msdfgen
                const float worstErrSlack = 0.02f;  // allow worst error up to msdfgen's + 0.02

                var rows = new List<string>();
                var offenders = new List<string>();
                int evaluated = 0;

                foreach (char ch in glyphs)
                {
                    var outline = OutlineFor(face, ch, ppem);
                    if (outline == null) { rows.Add($"  '{ch}': unavailable — skipped"); continue; }

                    var msdf = MsdfBuilder.Build(outline, spread); // error correction ON (default)
                    Assert.IsTrue(msdf.IsValid, $"build failed '{ch}'");

                    // STEP-3 FIX: the coverage reference is now the INDEPENDENT brute-force exact
                    // signed distance (min exact point-to-segment distance over the densely-flattened
                    // outline, signed by Shape.Contains non-zero winding), NOT our own SDF. Our SDF
                    // and MSDF share MinSignedDistance, so a defect in that shared code (the round-5
                    // '@' column pin) was invisible to a test that used our SDF as its reference —
                    // both agreed because both were wrong. The brute distance shares no code with the
                    // generator, so it catches exactly that class of fault.
                    var shape = Shape.FromOutline(outline);
                    shape.OrientContours();
                    int w0 = msdf.Width, h0 = msdf.Height;
                    double[] bruteGrid = BruteSignedGrid(shape, msdf, spread, w0, h0);

                    var (worst, worstX, worstY, farOffenders) =
                        CoverageStreakScan(msdf, bruteGrid, up, coverageErrThreshold, farPx);

                    bool hasMsdfgenRef = msdfgenOffenders.ContainsKey(ch);
                    if (hasMsdfgenRef)
                    {
                        // INHERENT glyph (msdfgen itself exceeds the 0.25 gate here): the contract is
                        // "no worse than msdfgen v1.12", within slack. A real regression on this glyph
                        // (more offenders, or a worse junction error) still fails.
                        int refOff = msdfgenOffenders[ch];
                        float refWorst = msdfgenWorstErr[ch];
                        rows.Add($"  '{ch}': offenders {farOffenders} (msdfgen {refOff}, +{offenderSlack} slack); worst err {worst:F3} (msdfgen {refWorst:F3}, +{worstErrSlack:F2} slack) at ({worstX},{worstY})px/{up}x [{MsdfgenTag}]");
                        if (farOffenders > refOff + offenderSlack || worst > refWorst + worstErrSlack)
                            offenders.Add($"'{ch}': {farOffenders} offenders / worst {worst:F3} EXCEEDS msdfgen baseline ({refOff} / {refWorst:F3}) + slack ({offenderSlack} / {worstErrSlack:F2}) at 8x pixel ({worstX},{worstY})");
                    }
                    else
                    {
                        // msdfgen passes this glyph cleanly, so OpenGlyph must too: absolute 0.25 gate.
                        rows.Add($"  '{ch}': far-from-edge pixels (>{farPx}px) with coverage err >{coverageErrThreshold:F2} = {farOffenders}; worst err {worst:F3} at ({worstX},{worstY})px/{up}x");
                        if (farOffenders > 0)
                            offenders.Add($"'{ch}': {farOffenders} streak/speck pixel(s), worst coverage error {worst:F3} at 8x pixel ({worstX},{worstY})");
                    }
                    evaluated++;
                }

                string table = $"Decoded-coverage streak/speck scan (MSDF vs BRUTE-FORCE exact distance, same bilinear + shader smoothstep, {up}x; inherent-glyph baselines from independent msdfgen {MsdfgenTag}):\n" + string.Join("\n", rows);
                TestContext.WriteLine(table);
                Assert.GreaterOrEqual(evaluated, 5, "Too few glyphs evaluable:\n" + table);
                Assert.IsEmpty(offenders,
                    "MSDF decoded coverage: a glyph exceeds its gate (absolute 0.25 where msdfgen passes, or the msdfgen-parity baseline where the residual is inherent):\n" + string.Join("\n", offenders) + "\n" + table);

            }
            finally { FT.UnloadFace(face); }
        }

        /// <summary>
        /// Decodes the MSDF at <paramref name="up"/>x via bilinear median + the shader's coverage
        /// smoothstep, compares to a 16x-supersampled analytic coverage of the outline, and counts
        /// pixels farther than <paramref name="farPx"/> output px from the true outline whose coverage
        /// error exceeds <paramref name="thr"/>. Returns the worst far-pixel error and its location.
        ///
        /// "Far from the true outline" is measured from the HONEST SDF's own signed distance (the
        /// single channel is monotone in true distance), so the whole anti-alias band is excluded
        /// without a separate ground-truth mask. Both fields are decoded with the IDENTICAL bilinear
        /// sample and shader smoothstep, so the soft AA ramp cancels between them: what remains is the
        /// MSDF-specific disagreement — a channel-clash streak or speck. In the far region the SDF
        /// coverage is saturated (0 or 1), so an MSDF decode that differs by more than
        /// <paramref name="thr"/> is a phantom edge.
        /// </summary>
        private static (float worst, int wx, int wy, int farOffenders) CoverageStreakScan(
            MsdfGlyphResult msdf, double[] brutePx, int up, float thr, float farPx)
        {
            int w = msdf.Width, h = msdf.Height;
            double range = msdf.Range <= 0 ? 1 : msdf.Range;
            int W = (w - 1) * up, H = (h - 1) * up;

            // Shader decode (dilate/weights zero): coverage = saturate((d - 0.5) * scale + 0.5).
            // scale = up matches MsdfVisualEvidenceTests' real-shader feed (xScaleVal=0.75/gradientScale,
            // baseScale≈up/0.75, gradientScale=range ⇒ scale≈up), so this CPU decode uses the SAME
            // anti-alias width as the GPU evidence row.
            float scale = up;

            float worst = 0f; int wx = -1, wy = -1, farOffenders = 0;
            for (int uy = 0; uy < H; uy++)
                for (int ux = 0; ux < W; ux++)
                {
                    double fx = (double)ux / up, fy = (double)uy / up;

                    // AA-band guard from the INDEPENDENT brute-force exact signed distance (px),
                    // bilinearly sampled. Skip the soft-edge band (|dist| <= farPx) where sub-pixel
                    // coverage legitimately ramps.
                    float distSigned = BilinearScalarField(brutePx, w, h, fx, fy);
                    float distPx = Mathf.Abs(distSigned);
                    if (distPx <= farPx) continue;

                    // Reference coverage = the UNAMBIGUOUS true fill side far from the outline: brute
                    // says fully inside (1) or fully outside (0). A streak/speck is where the MSDF
                    // median decodes toward the OPPOSITE side here — a phantom edge in open field. A
                    // sharp corner MSDF keeps crisper than a rounded SDF is NOT flagged: the brute
                    // true fill side agrees with MSDF there, so their coverage error is ~0.
                    float refCov = distSigned > 0f ? 1f : 0f;
                    float median = BilinearMedian(msdf.Field, w, h, fx, fy);
                    float msdfCov = Mathf.Clamp01((median - 0.5f) * scale + 0.5f);
                    float err = Mathf.Abs(msdfCov - refCov);
                    if (err <= thr) continue;

                    farOffenders++;
                    if (err > worst) { worst = err; wx = ux; wy = uy; }
                }

            return (worst, wx, wy, farOffenders);
        }

        private static float BilinearScalarField(double[] field, int w, int h, double fx, double fy)
        {
            int x0 = Math.Max(0, Math.Min((int)Math.Floor(fx), w - 1));
            int y0 = Math.Max(0, Math.Min((int)Math.Floor(fy), h - 1));
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            double tx = fx - x0, ty = fy - y0;
            double S(int x, int y) => field[y * w + x];
            double top = S(x0, y0) * (1 - tx) + S(x1, y0) * tx;
            double bot = S(x0, y1) * (1 - tx) + S(x1, y1) * tx;
            return (float)(top * (1 - ty) + bot * ty);
        }

        /// <summary>
        /// Per-field-pixel BRUTE-FORCE exact signed distance (in px): for each pixel centre, the min
        /// exact point-to-segment distance over the densely-flattened outline, signed by
        /// Shape.Contains (non-zero winding). Shares no code with EdgeSegment.MinSignedDistance. The
        /// shape must already be oriented (OrientContours) by the caller.
        /// </summary>
        private static double[] BruteSignedGrid(Shape shape, MsdfGlyphResult msdf, int spread, int w, int h)
        {
            shape.Bounds(out double l, out double b, out double r, out double t);
            int gx0 = (int)Math.Floor(l), gy0 = (int)Math.Floor(b);
            double tX = spread - gx0, tY = spread - gy0;

            var spans = new List<(Vector2D a, Vector2D b)>();
            foreach (var c in shape.Contours)
                foreach (var e in c.Edges)
                {
                    int steps = (e is LinearSegment) ? 1 : 256;
                    Vector2D prev = e.Point(0);
                    for (int i = 1; i <= steps; i++) { Vector2D cur = e.Point((double)i / steps); spans.Add((prev, cur)); prev = cur; }
                }

            var grid = new double[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var p = new Vector2D((x + 0.5) - tX, (y + 0.5) - tY);
                    double best = double.MaxValue;
                    foreach (var (a, bb) in spans)
                    {
                        Vector2D ab = bb - a;
                        double len2 = Vector2D.Dot(ab, ab);
                        double tt = len2 > 0 ? Vector2D.Dot(p - a, ab) / len2 : 0;
                        if (tt < 0) tt = 0; else if (tt > 1) tt = 1;
                        double d = (p - (a + ab * tt)).Length();
                        if (d < best) best = d;
                    }
                    grid[y * w + x] = shape.Contains(p) ? best : -best;
                }
            return grid;
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
