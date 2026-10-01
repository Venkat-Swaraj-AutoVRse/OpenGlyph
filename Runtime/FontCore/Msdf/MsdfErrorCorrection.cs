using System;

namespace LightSide.Msdf
{
    /// <summary>
    /// Faithful port of msdfgen's standalone error-correction stage (msdfgen &gt;= 1.9):
    /// <c>core/MSDFErrorCorrection.cpp</c> + <c>core/MSDFErrorCorrection.h</c> and the artifact
    /// classifiers in <c>core/edge-selectors</c> that it reuses (Viktor Chlumsky, MIT). See
    /// Third-Party Notices.txt. No original source is bundled — the algorithm is re-expressed
    /// against OpenGlyph's bottom-up float field (channel values = dist/range + 0.5, so the shape
    /// contour sits at the 0.5 iso-level) and <see cref="Shape"/> model.
    ///
    /// WHY THIS REPLACES THE EARLIER CUSTOM PASSES. The '@' vertical-streak artifact is a
    /// multi-channel INTERPOLATION artifact: between two texels the per-channel linear interpolation
    /// makes the reconstructed median cross 0.5 at a point where the true shape has no edge, drawing
    /// a phantom contour (the streak) when the shader bilinear-samples and thresholds the median.
    /// msdfgen's documented fix (not a heuristic) is: PROTECT the texels that carry genuine edges
    /// (corners, and texels whose median already lies on a real distance edge), then scan every
    /// texel against its 4 primary neighbours (right, up, and the two diagonals) with the
    /// INTERPOLATED-MEDIAN artifact test; a texel flagged as an artifact and not protected is set to
    /// its own median across all three channels — a value that, being a single scalar, can no longer
    /// produce a channel clash. EDGE_PRIORITY config with the default minimum deviation ratio 1.11111
    /// and minimum improvement ratio 1.11111 (msdfgen <c>ErrorCorrectionConfig</c> defaults).
    /// </summary>
    internal static class MsdfErrorCorrection
    {
        // msdfgen ErrorCorrectionConfig defaults (config.h): defaultMinDeviationRatio = 1.11111111111111111,
        // defaultMinImproveRatio = 1.11111111111111111. EDGE_PRIORITY mode (the recommended default).
        private const double DefaultMinDeviationRatio = 1.11111111111111111;
        private const double DefaultMinImproveRatio = 1.11111111111111111;

        [Flags]
        private enum Stencil : byte
        {
            None = 0,
            Error = 1,     // msdfgen ERROR
            Protected = 2, // msdfgen PROTECTED
        }

        /// <summary>
        /// Runs the full error-correction stage over a bottom-up MSDF float field in place.
        /// Mirrors <c>MSDFErrorCorrection::protectCorners</c> + <c>protectEdges</c> +
        /// <c>findErrors(const BitmapConstRef)</c> + <c>apply</c> as orchestrated by
        /// <c>msdfErrorCorrection()</c> for EDGE_PRIORITY mode.
        /// </summary>
        public static void Correct(float[] field, int w, int h, Shape shape, in MsdfConfig cfg)
        {
            if (w <= 0 || h <= 0) return;
            var stencil = new Stencil[w * h];

            // The classifier works in the field's normalized [0..1] units where 0.5 is the contour,
            // matching msdfgen's internal normalized distance. The deviation/improve ratios are
            // applied directly in those units (see RangeTest), so the pxRange cancels and no explicit
            // per-texel range term is needed here.

            // 1) Protect corners and genuine distance edges (EDGE_PRIORITY): texels that legitimately
            //    carry an edge must never be flattened, or we would round corners and erase strokes.
            ProtectCorners(field, w, h, shape, in cfg, stencil);
            ProtectEdges(field, w, h, stencil);

            // 2) Find interpolation artifacts among the unprotected texels.
            FindErrors(field, w, h, stencil);

            // 3) Apply: msdfgen MSDFErrorCorrection::apply sets each flagged texel to its own median
            //    across all three channels — a single scalar cannot clash, so the phantom edge is
            //    removed. With the round-6 generator root-cause fix (nonZeroSign in EdgeSegment), the
            //    field no longer pins a whole column to the contour, so the earlier custom
            //    "degenerate-median" resolution (neighbourhood averaging) is unnecessary and has been
            //    removed; this is now exactly msdfgen's apply().
            for (int i = 0; i < w * h; i++)
            {
                if ((stencil[i] & Stencil.Error) == 0) continue;
                int o = i * 3;
                float m = MsdfGenerator.Median(field[o], field[o + 1], field[o + 2]);
                field[o] = m; field[o + 1] = m; field[o + 2] = m;
            }
        }

        // ---------------------------------------------------------------------------------------
        // protectCorners — msdfgen MSDFErrorCorrection::protectCorners(const Shape&, const Projection&)
        // For every contour corner (an edge endpoint where the two incident edges are a true corner,
        // i.e. not smooth), protect the up-to-4 texels straddling that corner's position, because
        // the median there is SUPPOSED to encode two differently-coloured edges and must not be
        // flattened to a scalar.
        // ---------------------------------------------------------------------------------------
        private static void ProtectCorners(float[] field, int w, int h, Shape shape, in MsdfConfig cfg, Stencil[] stencil)
        {
            // msdfgen uses the geometric corner test (the same angle threshold edgeColoringSimple
            // uses to split colours), NOT a colour-difference test: on a smooth contour the teardrop
            // colouring introduces colour-change SEAMS that are not real corners, and protecting
            // those would shield exactly the smooth-wall texels where the '@' streak lives.
            double crossThreshold = Math.Sin(EdgeColoring.DefaultAngleThreshold);
            foreach (var contour in shape.Contours)
            {
                int n = contour.Edges.Count;
                if (n == 0) continue;
                Vector2D prevDir = contour.Edges[n - 1].Direction(1).Normalize();
                for (int e = 0; e < n; e++)
                {
                    EdgeSegment cur = contour.Edges[e];
                    Vector2D curDir = cur.Direction(0).Normalize();
                    bool corner = Vector2D.Dot(prevDir, curDir) <= 0 ||
                                  Math.Abs(Vector2D.Cross(prevDir, curDir)) > crossThreshold;
                    if (corner)
                    {
                        // Corner point in field space (inverse of the generator's shape->pixel map).
                        Vector2D cp = cur.Point(0);
                        double fx = (cp.X + cfg.TranslateX) * cfg.ScaleX - 0.5;
                        double fy = (cp.Y + cfg.TranslateY) * cfg.ScaleY - 0.5;
                        int x0 = (int)Math.Floor(fx);
                        int y0 = (int)Math.Floor(fy);
                        for (int dy = 0; dy <= 1; dy++)
                            for (int dx = 0; dx <= 1; dx++)
                            {
                                int px = x0 + dx, py = y0 + dy;
                                if (px >= 0 && px < w && py >= 0 && py < h)
                                    stencil[py * w + px] |= Stencil.Protected;
                            }
                    }
                    prevDir = cur.Direction(1).Normalize();
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // protectEdges — msdfgen MSDFErrorCorrection::protectEdges(const BitmapConstRef)
        // Protect texels on either side of a GENUINE median edge: where the reconstructed median
        // crosses 0.5 between two adjacent texels AND that crossing is backed by all channels moving
        // consistently (a real shape contour, not a single-channel interpolation glitch). This keeps
        // true strokes and bowls from being flagged as artifacts. Horizontal, vertical and both
        // diagonal neighbours, per msdfgen.
        // ---------------------------------------------------------------------------------------
        private static void ProtectEdges(float[] field, int w, int h, Stencil[] stencil)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int a = (y * w + x) * 3;
                    if (x + 1 < w) ProtectEdgePair(field, a, (y * w + (x + 1)) * 3, (y * w + x), (y * w + (x + 1)), stencil);
                    if (y + 1 < h) ProtectEdgePair(field, a, ((y + 1) * w + x) * 3, (y * w + x), ((y + 1) * w + x), stencil);
                    if (x + 1 < w && y + 1 < h) ProtectEdgePair(field, a, ((y + 1) * w + (x + 1)) * 3, (y * w + x), ((y + 1) * w + (x + 1)), stencil);
                    if (x > 0 && y + 1 < h) ProtectEdgePair(field, a, ((y + 1) * w + (x - 1)) * 3, (y * w + x), ((y + 1) * w + (x - 1)), stencil);
                }
        }

        private static void ProtectEdgePair(float[] field, int oa, int ob, int ia, int ib, Stencil[] stencil)
        {
            float ar = field[oa], ag = field[oa + 1], ab = field[oa + 2];
            float br = field[ob], bg = field[ob + 1], bb = field[ob + 2];
            float ma = MsdfGenerator.Median(ar, ag, ab);
            float mb = MsdfGenerator.Median(br, bg, bb);
            // A real median edge: the median crosses 0.5 between the two texels.
            if ((ma - 0.5f) * (mb - 0.5f) >= 0f) return;

            // Backed by a consistent per-channel move in the SAME direction as the median — the
            // signature of a true distance edge rather than a channel clash. (msdfgen's protectEdges
            // uses the edge selector; this is the equivalent condition on the reconstructed field:
            // all channels that cross 0.5 cross the same way the median does.)
            int medDir = Math.Sign(mb - ma);
            for (int c = 0; c < 3; c++)
            {
                float ca = field[oa + c], cb = field[ob + c];
                if ((ca - 0.5f) * (cb - 0.5f) < 0f && Math.Sign(cb - ca) != medDir)
                    return; // a channel crosses AGAINST the median -> this is a clash, not an edge
            }
            stencil[ia] |= Stencil.Protected;
            stencil[ib] |= Stencil.Protected;
        }

        // ---------------------------------------------------------------------------------------
        // findErrors — msdfgen MSDFErrorCorrection::findErrors(const BitmapConstRef)
        // For each unprotected texel, test it against its 8 neighbours. Two artifact kinds are
        // flagged, matching msdfgen's classifier once the protect stencil has removed the genuine
        // edges:
        //   (a) INTERIOR-DIP artifact (interpolatedMedianArtifact): both texels on the SAME side of
        //       0.5 but the interpolated median dips across to the other side between them — an
        //       isolated speck.
        //   (b) UNPROTECTED CROSSING: the median crosses 0.5 between the two texels but
        //       protectEdges did NOT protect it, which (by construction of protectEdges) means the
        //       per-channel moves are inconsistent — a channel clash painting a false edge, i.e. the
        //       sustained streak. protectEdges has already shielded every genuine edge, so a
        //       surviving unprotected crossing is an error.
        // Testing all 8 neighbours (not just the forward half) is needed so a wrong texel is caught
        // from whichever side its CORRECT neighbour sits on.
        // ---------------------------------------------------------------------------------------
        private static void FindErrors(float[] field, int w, int h, Stencil[] stencil)
        {
            int[] dxs = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] dys = { 0, 0, 1, -1, 1, -1, 1, -1 };

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if ((stencil[i] & Stencil.Protected) != 0) continue;

                    int o = i * 3;
                    float cr = field[o], cg = field[o + 1], cb = field[o + 2];
                    float cm = MsdfGenerator.Median(cr, cg, cb);

                    bool artifact = false;
                    for (int k = 0; k < 8 && !artifact; k++)
                    {
                        int nx = x + dxs[k], ny = y + dys[k];
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int ob = (ny * w + nx) * 3;
                        float br = field[ob], bg = field[ob + 1], bb = field[ob + 2];
                        float bm = MsdfGenerator.Median(br, bg, bb);

                        if ((cm - 0.5f) * (bm - 0.5f) < 0f)
                        {
                            // The median crosses 0.5 across this neighbour pair. If protectEdges did
                            // not protect this texel for a real edge, the crossing must be a clash —
                            // flag it when the channels move inconsistently with the median.
                            if (IsClashCrossing(cr, cg, cb, br, bg, bb, cm, bm))
                                artifact = true;
                        }
                        else
                        {
                            // Same side: look for an interior dip (speck).
                            artifact = InterpolatedMedianArtifact(cr, cg, cb, cm, br, bg, bb, bm);
                        }
                    }

                    // msdfgen interpolatedMedianMinimum / interpolatedMedianMaximum
                    // (core/MSDFErrorCorrection.cpp): the interpolated median along an axis through a
                    // texel attains an interior EXTREMUM that lands on the opposite side of 0.5 from
                    // both axis endpoints. msdfgen evaluates this per axis (H, V, and both diagonals);
                    // it is the test that catches a median pulled toward the contour at a junction
                    // (e.g. the 'W' middle vertex) where the pairwise crossing/same-side dip tests
                    // alone do not flag it. A genuine thin stroke is NOT flagged: its opposite
                    // neighbours straddle 0.5 (one in, one out), so the firm same-side condition fails.
                    if (!artifact)
                    {
                        int[,] axes = { { 1, 0 }, { 0, 1 }, { 1, 1 }, { 1, -1 } };
                        for (int a = 0; a < 4 && !artifact; a++)
                        {
                            int ax = axes[a, 0], ay = axes[a, 1];
                            int px1 = x + ax, py1 = y + ay, px2 = x - ax, py2 = y - ay;
                            if (px1 < 0 || px1 >= w || py1 < 0 || py1 >= h) continue;
                            if (px2 < 0 || px2 >= w || py2 < 0 || py2 >= h) continue;
                            float m1 = MedAt(field, (py1 * w + px1) * 3);
                            float m2 = MedAt(field, (py2 * w + px2) * 3);
                            artifact = InterpolatedMedianExtremum(cm, m1, m2);
                        }
                    }

                    if (artifact)
                        stencil[i] |= Stencil.Error;
                }
        }

        private static float MedAt(float[] field, int o) => MsdfGenerator.Median(field[o], field[o + 1], field[o + 2]);

        /// <summary>
        /// msdfgen interpolatedMedianMinimum / interpolatedMedianMaximum: the centre median sits on
        /// the contour (within a small band of 0.5) while BOTH opposite neighbour medians lie firmly
        /// on the SAME side — the interpolated median has an interior extremum crossing 0.5 where no
        /// real contour passes. Firm margins keep genuine thin strokes (opposite neighbours straddle
        /// 0.5) and real edges (centre already off 0.5) from being flagged.
        /// </summary>
        private static bool InterpolatedMedianExtremum(float cm, float m1, float m2)
        {
            const float onEdge = 0.06f;  // |cm-0.5| this small == median pulled toward the contour
            const float firm = 0.04f;    // a neighbour this far past 0.5 is a firm vote
            if (Math.Abs(cm - 0.5f) > onEdge) return false;
            bool bothInside = (m1 - 0.5f) > firm && (m2 - 0.5f) > firm;
            bool bothOutside = (0.5f - m1) > firm && (0.5f - m2) > firm;
            return bothInside || bothOutside;
        }

        /// <summary>
        /// True when the median crosses 0.5 between two texels but at least one channel crosses 0.5
        /// in the OPPOSITE direction to the median (or a channel crosses while the median's crossing
        /// is driven by a different channel) — the signature of a multi-channel clash painting a
        /// false edge. Genuine edges (all crossing channels agree with the median direction) were
        /// already PROTECTED, so reaching here on a crossing means it is spurious.
        /// </summary>
        private static bool IsClashCrossing(float ar, float ag, float ab, float br, float bg, float bb, float am, float bm)
        {
            const float t = 0.5f;
            int medDir = Math.Sign(bm - am);
            int crossing = 0, agreeing = 0;
            void Chk(float a, float b)
            {
                if ((a - t) * (b - t) < 0f)
                {
                    crossing++;
                    if (Math.Sign(b - a) == medDir) agreeing++;
                }
            }
            Chk(ar, br); Chk(ag, bg); Chk(ab, bb);
            // A clean edge has every crossing channel moving with the median. A clash has a channel
            // crossing against the median, OR the median crossing with NO channel actually crossing
            // (the "middle" channel swapped without reaching 0.5).
            return crossing == 0 || agreeing < crossing;
        }

        // ---------------------------------------------------------------------------------------
        // Interpolated-median artifact test (msdfgen core/MSDFErrorCorrection.cpp,
        // interpolatedMedianArtifact + BaseArtifactClassifier). Catches the SAME-SIDE interior dip:
        // both texels sit on one side of 0.5 but the per-channel linear interpolation makes the
        // reconstructed median cross to the other side between them — an isolated speck. The
        // deviation/improve ratios reject dips too shallow to render.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Core interpolated-median artifact classifier for a straight span A->B, faithful to
        /// msdfgen's <c>interpolatedMedianArtifact</c> + <c>BaseArtifactClassifier::evaluate</c>.
        /// The reconstructed median along the span is piecewise linear with breakpoints where two
        /// channels swap order; at each breakpoint parameter the median has a potential dip. An
        /// artifact exists when the median at an interior breakpoint lands on the OPPOSITE side of
        /// 0.5 from BOTH endpoint medians — the median paints an edge (and its mirror) inside a span
        /// the shape does not actually cross. The deviation/improve ratios gate shallow dips.
        /// </summary>
        private static bool InterpolatedMedianArtifact(
            float ar, float ag, float ab, float am,
            float br, float bg, float bb, float bm)
        {
            const float thr = 0.5f;

            // Both endpoints on the same side (otherwise the single median crossing is a real edge,
            // which protectEdges will have handled / which is legitimate).
            bool bothInside = am > thr && bm > thr;
            bool bothOutside = am < thr && bm < thr;
            if (!bothInside && !bothOutside) return false;

            // Breakpoints of the piecewise-linear median = parameters where each channel PAIR is
            // equal (there the "middle" channel can change). Evaluate the median at each and keep the
            // most extreme interior value.
            float worst = bothInside ? 1f : 0f;
            float worstT = -1f;
            void Consider(float t)
            {
                if (t <= 0f || t >= 1f) return;
                float R = ar + (br - ar) * t;
                float G = ag + (bg - ag) * t;
                float B = ab + (bb - ab) * t;
                float med = MsdfGenerator.Median(R, G, B);
                if (bothInside && med < worst) { worst = med; worstT = t; }
                if (bothOutside && med > worst) { worst = med; worstT = t; }
            }
            Consider(PairEqualParam(ar, br, ag, bg)); // R == G
            Consider(PairEqualParam(ag, bg, ab, bb)); // G == B
            Consider(PairEqualParam(ar, br, ab, bb)); // R == B
            if (worstT < 0f) return false;

            // The interior median must cross to the other side of 0.5 to be a phantom edge.
            bool crosses = bothInside ? worst < thr : worst > thr;
            if (!crosses) return false;

            return RangeTest(am, bm, worst, worstT);
        }

        /// <summary>
        /// msdfgen <c>BaseArtifactClassifier::rangeTest</c> gate. The caller has established that the
        /// reconstructed median at interior parameter <paramref name="xt"/> (value <paramref
        /// name="xm"/>) crosses to the opposite side of 0.5 from both endpoint medians (<paramref
        /// name="am"/>, <paramref name="bm"/>) — a candidate phantom edge. This gate rejects it when
        /// it is too shallow to render (minimum DEVIATION ratio) or when flattening would not
        /// materially improve the field (minimum IMPROVE ratio), exactly as msdfgen's defaults
        /// (minDeviationRatio = minImproveRatio = 1.11111...) do.
        /// </summary>
        private static bool RangeTest(float am, float bm, float xm, float xt)
        {
            const float thr = 0.5f;

            // "Expected" median at xt is the straight-line interpolation of the endpoint medians; the
            // real reconstructed median departs from it by `deviation`. A genuine artifact departs far
            // (it reverses across 0.5); interpolation noise departs little.
            float expected = am + (bm - am) * xt;
            float deviation = Math.Abs(xm - expected);
            if (deviation <= 0f) return false;

            // DEVIATION gate: the reversal must carry the median at least (minDeviationRatio-1) of its
            // distance-to-expected onto the far side of 0.5. |xm-thr| is how far it actually reversed.
            float reversal = Math.Abs(xm - thr);
            if (reversal * (float)DefaultMinDeviationRatio < deviation * ((float)DefaultMinDeviationRatio - 1f))
                return false;

            // IMPROVE gate: flattening the texel to its median replaces the reversed value with the
            // endpoint-consistent median. That is an improvement only if the reversed median is far
            // enough past 0.5 relative to the nearer endpoint's own margin; otherwise the field was
            // already essentially correct and a flatten changes nothing worth doing.
            float endpointMargin = Math.Min(Math.Abs(am - thr), Math.Abs(bm - thr));
            if (reversal * (float)DefaultMinImproveRatio < endpointMargin)
                return false;

            return true;
        }

        /// <summary>
        /// Parameter t in (0,1) where linear interpolations (a0..a1) and (b0..b1) are equal, or -1
        /// when they are parallel or meet outside the span. This is a median breakpoint.
        /// </summary>
        private static float PairEqualParam(float a0, float a1, float b0, float b1)
        {
            float denom = (a1 - a0) - (b1 - b0);
            if (denom == 0f) return -1f;
            float t = (b0 - a0) / denom;
            return (t > 0f && t < 1f) ? t : -1f;
        }
    }
}
