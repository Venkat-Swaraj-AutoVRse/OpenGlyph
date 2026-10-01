using System;

namespace LightSide.Msdf
{
    /// <summary>
    /// Faithful port of msdfgen v1.12 (commit 85e8b3d, MIT, Viktor Chlumsky) standalone
    /// error-correction stage: <c>core/MSDFErrorCorrection.cpp</c> + <c>core/MSDFErrorCorrection.h</c>.
    /// See Third-Party Notices.txt. No upstream source is bundled — the algorithm is re-expressed in
    /// C# against OpenGlyph's bottom-up float field (channel value = dist/range + 0.5, 0.5 == contour,
    /// which is byte-for-byte msdfgen's <c>DistanceMapping(Range)(d) = d/range + 0.5</c>) and
    /// <see cref="Shape"/> model.
    ///
    /// SCOPE. This ports msdfgen's SDF-only classifier path — <c>protectCorners(shape)</c> +
    /// <c>protectEdges(sdf)</c> + <c>findErrors(sdf)</c> + <c>apply(sdf)</c> — orchestrated for the
    /// EDGE_PRIORITY mode with distance-check mode <c>DO_NOT_CHECK_DISTANCE</c>. The optional
    /// <c>CHECK_DISTANCE_AT_EDGE</c> refinement (msdfgen's <c>ShapeDistanceChecker</c>, which
    /// re-evaluates suspected artifacts against the exact shape distance via a full
    /// <c>ShapeDistanceFinder</c>) is deliberately NOT ported here; it is a performance/quality
    /// refinement layered on top of this base classifier and requires the whole perpendicular
    /// distance-selector machinery. The corrected-field parity test therefore compares against
    /// msdfgen's own <c>DO_NOT_CHECK_DISTANCE</c> output (see NativeSource~/tests/msdfref).
    ///
    /// Everything the previous OpenGlyph version invented and msdfgen does not have —
    /// <c>InterpolatedMedianExtremum</c> (with the <c>onEdge</c>/<c>firm</c> constants),
    /// <c>IsClashCrossing</c>, the "consistent per-channel move" edge test, 8-neighbour same-side
    /// dip scanning — is DELETED. What remains is a direct translation of upstream's functions, each
    /// annotated with the upstream function it mirrors.
    /// </summary>
    internal static class MsdfErrorCorrection
    {
        // msdfgen ErrorCorrectionConfig defaults (core/MSDFErrorCorrection.cpp):
        //   defaultMinDeviationRatio = 1.11111111111111111
        //   defaultMinImproveRatio   = 1.11111111111111111
        private const double DefaultMinDeviationRatio = 1.11111111111111111;
        // minImproveRatio only affects the distance-check path (not ported); kept for fidelity/doc.

        // msdfgen #defines (core/MSDFErrorCorrection.cpp).
        private const double ArtifactTEpsilon = 0.01;              // ARTIFACT_T_EPSILON
        private const double ProtectionRadiusTolerance = 1.001;    // PROTECTION_RADIUS_TOLERANCE
        private const int ClassifierFlagCandidate = 0x01;          // CLASSIFIER_FLAG_CANDIDATE
        private const int ClassifierFlagArtifact = 0x02;           // CLASSIFIER_FLAG_ARTIFACT

        [Flags]
        private enum Stencil : byte
        {
            None = 0,
            Error = 1,     // msdfgen MSDFErrorCorrection::ERROR
            Protected = 2, // msdfgen MSDFErrorCorrection::PROTECTED
        }

        // --- msdfgen median / mix (arithmetics.hpp) -----------------------------------------------
        private static float Median(float a, float b, float c) =>
            Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        private static float Mix(float a, float b, double t) => (float)(a * (1 - t) + b * t);

        /// <summary>
        /// Runs the full error-correction stage over a bottom-up MSDF float field in place.
        /// Mirrors <c>msdfErrorCorrection()</c> (core/msdf-error-correction.cpp) for EDGE_PRIORITY
        /// with DO_NOT_CHECK_DISTANCE: protectCorners(shape) → protectEdges(sdf) → findErrors(sdf) →
        /// apply(sdf).
        /// </summary>
        public static void Correct(float[] field, int w, int h, Shape shape, in MsdfConfig cfg)
        {
            if (w <= 0 || h <= 0) return;
            var stencil = new Stencil[w * h];

            // msdfgen's SDFTransformation: our projection is scale=(ScaleX,ScaleY),
            // translate=(TranslateX,TranslateY); the distance mapping is value = d/range + 0.5, i.e.
            // DistanceMapping(Range(range)) with scale_dm = 1/range. unprojectVector(v) = v/scale.
            double range = cfg.Range <= 0 ? 1 : cfg.Range;
            double invRange = 1.0 / range;              // distanceMapping(Delta(1))
            double sx = cfg.ScaleX == 0 ? 1 : cfg.ScaleX;
            double sy = cfg.ScaleY == 0 ? 1 : cfg.ScaleY;

            ProtectCorners(field, w, h, shape, sx, sy, cfg.TranslateX, cfg.TranslateY, stencil);
            ProtectEdges(field, w, h, invRange, sx, sy, stencil);
            FindErrors(field, w, h, invRange, sx, sy, stencil);

            // apply(): every ERROR texel collapses to its own median (a single scalar cannot clash).
            for (int i = 0; i < w * h; i++)
            {
                if ((stencil[i] & Stencil.Error) == 0) continue;
                int o = i * 3;
                float m = Median(field[o], field[o + 1], field[o + 2]);
                field[o] = m; field[o + 1] = m; field[o + 2] = m;
            }
        }

        // ==========================================================================================
        // protectCorners — msdfgen MSDFErrorCorrection::protectCorners(const Shape&)
        // A corner is where the COLOUR changes between consecutive edges: commonColor =
        // prevEdge.color & edge.color; it is a corner iff commonColor is not a single bit
        // (!(c & (c-1)) is TRUE for 0 or a single set bit → NOT a corner; so a corner is when that is
        // FALSE, i.e. commonColor has 0 or ≥2 bits). msdfgen marks the 4 texels enveloping the
        // corner point (floor(p-.5) .. +1) as PROTECTED. Our field is bottom-up with no inverseYAxis,
        // so we project directly (no height-flip).
        // ==========================================================================================
        private static void ProtectCorners(float[] field, int w, int h, Shape shape,
            double sx, double sy, double tx, double ty, Stencil[] stencil)
        {
            foreach (var contour in shape.Contours)
            {
                int n = contour.Edges.Count;
                if (n == 0) continue;
                EdgeSegment prevEdge = contour.Edges[n - 1];
                for (int e = 0; e < n; e++)
                {
                    EdgeSegment edge = contour.Edges[e];
                    int commonColor = (int)prevEdge.Color & (int)edge.Color;
                    // Corner iff commonColor is NOT a single bit (msdfgen: !(commonColor&(commonColor-1))
                    // identifies 0-or-single-bit; the branch body runs when THAT is true, protecting
                    // the corner). i.e. protect when commonColor has 0 or exactly 1 bit set.
                    if ((commonColor & (commonColor - 1)) == 0)
                    {
                        // project((*edge).point(0)) = scale*(p + translate).
                        Vector2D p0 = edge.Point(0);
                        double px = sx * (p0.X + tx);
                        double py = sy * (p0.Y + ty);
                        int l = (int)Math.Floor(px - 0.5);
                        int b = (int)Math.Floor(py - 0.5);
                        int r = l + 1;
                        int t = b + 1;
                        if (l < w && b < h && r >= 0 && t >= 0)
                        {
                            if (l >= 0 && b >= 0) stencil[b * w + l] |= Stencil.Protected;
                            if (r < w && b >= 0) stencil[b * w + r] |= Stencil.Protected;
                            if (l >= 0 && t < h) stencil[t * w + l] |= Stencil.Protected;
                            if (r < w && t < h) stencil[t * w + r] |= Stencil.Protected;
                        }
                    }
                    prevEdge = edge;
                }
            }
        }

        // ==========================================================================================
        // protectEdges — msdfgen MSDFErrorCorrection::protectEdges(const BitmapConstRef<float,N>)
        // For every H / V / diagonal texel pair whose medians are both near the 0.5 contour
        // (|lm-.5|+|rm-.5| < radius), find which channels carry a real edge between them
        // (edgeBetweenTexels) and PROTECT each texel's non-median channel that participates
        // (protectExtremeChannels). radius = PROTECTION_RADIUS_TOLERANCE * |unprojectVector(Delta(1)·axis)|.
        // ==========================================================================================
        private static void ProtectEdges(float[] field, int w, int h, double invRange, double sx, double sy, Stencil[] stencil)
        {
            // Horizontal pairs. unprojectVector(Vector2(invRange,0)) = (invRange/sx, 0); length = invRange/sx.
            float radiusH = (float)(ProtectionRadiusTolerance * (invRange / sx));
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w - 1; x++)
                {
                    int ia = y * w + x, ib = y * w + (x + 1);
                    int oa = ia * 3, ob = ib * 3;
                    float lm = Median(field[oa], field[oa + 1], field[oa + 2]);
                    float rm = Median(field[ob], field[ob + 1], field[ob + 2]);
                    if (Math.Abs(lm - 0.5f) + Math.Abs(rm - 0.5f) < radiusH)
                    {
                        int mask = EdgeBetweenTexels(field, oa, ob);
                        ProtectExtremeChannels(stencil, ia, field, oa, lm, mask);
                        ProtectExtremeChannels(stencil, ib, field, ob, rm, mask);
                    }
                }

            // Vertical pairs. length = invRange/sy.
            float radiusV = (float)(ProtectionRadiusTolerance * (invRange / sy));
            for (int y = 0; y < h - 1; y++)
                for (int x = 0; x < w; x++)
                {
                    int ia = y * w + x, ib = (y + 1) * w + x;
                    int oa = ia * 3, ob = ib * 3;
                    float bm = Median(field[oa], field[oa + 1], field[oa + 2]);
                    float tm = Median(field[ob], field[ob + 1], field[ob + 2]);
                    if (Math.Abs(bm - 0.5f) + Math.Abs(tm - 0.5f) < radiusV)
                    {
                        int mask = EdgeBetweenTexels(field, oa, ob);
                        ProtectExtremeChannels(stencil, ia, field, oa, bm, mask);
                        ProtectExtremeChannels(stencil, ib, field, ob, tm, mask);
                    }
                }

            // Diagonal pairs. unprojectVector(Vector2(invRange,invRange)) = (invRange/sx, invRange/sy);
            // length = invRange*sqrt(1/sx^2 + 1/sy^2).
            float radiusD = (float)(ProtectionRadiusTolerance * invRange * Math.Sqrt(1.0 / (sx * sx) + 1.0 / (sy * sy)));
            for (int y = 0; y < h - 1; y++)
                for (int x = 0; x < w - 1; x++)
                {
                    int iLB = y * w + x, iRB = y * w + (x + 1), iLT = (y + 1) * w + x, iRT = (y + 1) * w + (x + 1);
                    int oLB = iLB * 3, oRB = iRB * 3, oLT = iLT * 3, oRT = iRT * 3;
                    float mlb = Median(field[oLB], field[oLB + 1], field[oLB + 2]);
                    float mrb = Median(field[oRB], field[oRB + 1], field[oRB + 2]);
                    float mlt = Median(field[oLT], field[oLT + 1], field[oLT + 2]);
                    float mrt = Median(field[oRT], field[oRT + 1], field[oRT + 2]);
                    if (Math.Abs(mlb - 0.5f) + Math.Abs(mrt - 0.5f) < radiusD)
                    {
                        int mask = EdgeBetweenTexels(field, oLB, oRT);
                        ProtectExtremeChannels(stencil, iLB, field, oLB, mlb, mask);
                        ProtectExtremeChannels(stencil, iRT, field, oRT, mrt, mask);
                    }
                    if (Math.Abs(mrb - 0.5f) + Math.Abs(mlt - 0.5f) < radiusD)
                    {
                        int mask = EdgeBetweenTexels(field, oRB, oLT);
                        ProtectExtremeChannels(stencil, iRB, field, oRB, mrb, mask);
                        ProtectExtremeChannels(stencil, iLT, field, oLT, mlt, mask);
                    }
                }
        }

        /// <summary>msdfgen edgeBetweenTexelsChannel: is there an edge in this channel between a and b?</summary>
        private static bool EdgeBetweenTexelsChannel(float[] f, int oa, int ob, int channel)
        {
            float a0 = f[oa + channel], b0 = f[ob + channel];
            double denom = a0 - b0;
            if (denom == 0) return false;
            double t = (a0 - 0.5) / denom;
            if (t > 0 && t < 1)
            {
                float cR = Mix(f[oa], f[ob], t);
                float cG = Mix(f[oa + 1], f[ob + 1], t);
                float cB = Mix(f[oa + 2], f[ob + 2], t);
                float m = Median(cR, cG, cB);
                float cc = channel == 0 ? cR : (channel == 1 ? cG : cB);
                return m == cc;
            }
            return false;
        }

        /// <summary>msdfgen edgeBetweenTexels: bit mask (RED=1|GREEN=2|BLUE=4) of edge-carrying channels.</summary>
        private static int EdgeBetweenTexels(float[] f, int oa, int ob)
        {
            return (EdgeBetweenTexelsChannel(f, oa, ob, 0) ? 1 : 0)
                 + (EdgeBetweenTexelsChannel(f, oa, ob, 1) ? 2 : 0)
                 + (EdgeBetweenTexelsChannel(f, oa, ob, 2) ? 4 : 0);
        }

        /// <summary>msdfgen protectExtremeChannels: protect the texel if a non-median channel is in the mask.</summary>
        private static void ProtectExtremeChannels(Stencil[] stencil, int texelIndex, float[] f, int o, float m, int mask)
        {
            if (((mask & 1) != 0 && f[o] != m) ||
                ((mask & 2) != 0 && f[o + 1] != m) ||
                ((mask & 4) != 0 && f[o + 2] != m))
                stencil[texelIndex] |= Stencil.Protected;
        }

        // ==========================================================================================
        // findErrors — msdfgen MSDFErrorCorrection::findErrors(const BitmapConstRef<float,N>)
        // For each texel, flag ERROR if an artifact occurs when interpolated with ANY of its 8
        // neighbours: 4 orthogonal via hasLinearArtifact, 4 diagonal via hasDiagonalArtifact, each
        // with a BaseArtifactClassifier(span, protectedFlag).
        // ==========================================================================================
        private static void FindErrors(float[] field, int w, int h, double invRange, double sx, double sy, Stencil[] stencil)
        {
            double hSpan = DefaultMinDeviationRatio * (invRange / sx);
            double vSpan = DefaultMinDeviationRatio * (invRange / sy);
            double dSpan = DefaultMinDeviationRatio * invRange * Math.Sqrt(1.0 / (sx * sx) + 1.0 / (sy * sy));

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, o = i * 3;
                    float cm = Median(field[o], field[o + 1], field[o + 2]);
                    bool prot = (stencil[i] & Stencil.Protected) != 0;

                    int oL = x > 0 ? (y * w + (x - 1)) * 3 : -1;
                    int oB = y > 0 ? ((y - 1) * w + x) * 3 : -1;
                    int oR = x < w - 1 ? (y * w + (x + 1)) * 3 : -1;
                    int oT = y < h - 1 ? ((y + 1) * w + x) * 3 : -1;

                    bool err =
                        (oL >= 0 && HasLinearArtifact(hSpan, prot, cm, field, o, oL)) ||
                        (oB >= 0 && HasLinearArtifact(vSpan, prot, cm, field, o, oB)) ||
                        (oR >= 0 && HasLinearArtifact(hSpan, prot, cm, field, o, oR)) ||
                        (oT >= 0 && HasLinearArtifact(vSpan, prot, cm, field, o, oT)) ||
                        (x > 0 && y > 0 && HasDiagonalArtifact(dSpan, prot, cm, field, o, oL, oB, ((y - 1) * w + (x - 1)) * 3)) ||
                        (x < w - 1 && y > 0 && HasDiagonalArtifact(dSpan, prot, cm, field, o, oR, oB, ((y - 1) * w + (x + 1)) * 3)) ||
                        (x > 0 && y < h - 1 && HasDiagonalArtifact(dSpan, prot, cm, field, o, oL, oT, ((y + 1) * w + (x - 1)) * 3)) ||
                        (x < w - 1 && y < h - 1 && HasDiagonalArtifact(dSpan, prot, cm, field, o, oR, oT, ((y + 1) * w + (x + 1)) * 3));

                    if (err) stencil[i] |= Stencil.Error;
                }
        }

        // --- BaseArtifactClassifier (msdfgen) -----------------------------------------------------
        // rangeTest returns CLASSIFIER_FLAG_CANDIDATE[|ARTIFACT]. For the SDF-only path, evaluate()
        // is (flags & ARTIFACT) != 0, so an artifact is simply: isArtifact(...) in the inlined form.
        private static int RangeTest(double span, bool prot, double at, double bt, double xt, float am, float bm, float xm)
        {
            // For protected texels, only inversion artifacts count; else it suffices that the
            // interpolated median is outside its boundaries.
            if ((am > 0.5f && bm > 0.5f && xm <= 0.5f) ||
                (am < 0.5f && bm < 0.5f && xm >= 0.5f) ||
                (!prot && Median(am, bm, xm) != xm))
            {
                double axSpan = (xt - at) * span, bxSpan = (bt - xt) * span;
                if (!(xm >= am - axSpan && xm <= am + axSpan && xm >= bm - bxSpan && xm <= bm + bxSpan))
                    return ClassifierFlagCandidate | ClassifierFlagArtifact;
                return ClassifierFlagCandidate;
            }
            return 0;
        }

        /// <summary>msdfgen interpolatedMedian (linear): median of mix(a,b,t) across channels.</summary>
        private static float InterpolatedMedianLinear(float[] f, int oa, int ob, double t) =>
            Median(Mix(f[oa], f[ob], t), Mix(f[oa + 1], f[ob + 1], t), Mix(f[oa + 2], f[ob + 2], t));

        /// <summary>msdfgen interpolatedMedian (bilinear quadratic form): median of t*(t*q+l)+a.</summary>
        private static float InterpolatedMedianQuad(float[] a, float[] l, float[] q, double t) =>
            Median(
                (float)(t * (t * q[0] + l[0]) + a[0]),
                (float)(t * (t * q[1] + l[1]) + a[1]),
                (float)(t * (t * q[2] + l[2]) + a[2]));

        /// <summary>msdfgen hasLinearArtifactInner.</summary>
        private static bool HasLinearArtifactInner(double span, bool prot, float am, float bm, float[] f, int oa, int ob, float dA, float dB)
        {
            double t = (double)dA / (dA - dB);
            if (t > ArtifactTEpsilon && t < 1 - ArtifactTEpsilon)
            {
                float xm = InterpolatedMedianLinear(f, oa, ob, t);
                int flags = RangeTest(span, prot, 0, 1, t, am, bm, xm);
                return (flags & ClassifierFlagArtifact) != 0;
            }
            return false;
        }

        /// <summary>msdfgen hasLinearArtifact (orthogonal pair a,b; a is the current texel 'o').</summary>
        private static bool HasLinearArtifact(double span, bool prot, float am, float[] f, int oa, int ob)
        {
            float bm = Median(f[ob], f[ob + 1], f[ob + 2]);
            // Only report for the texel further from the edge (minimises side effects).
            if (Math.Abs(am - 0.5f) >= Math.Abs(bm - 0.5f))
            {
                return
                    HasLinearArtifactInner(span, prot, am, bm, f, oa, ob, f[oa + 1] - f[oa], f[ob + 1] - f[ob]) || // R==G
                    HasLinearArtifactInner(span, prot, am, bm, f, oa, ob, f[oa + 2] - f[oa + 1], f[ob + 2] - f[ob + 1]) || // G==B
                    HasLinearArtifactInner(span, prot, am, bm, f, oa, ob, f[oa] - f[oa + 2], f[ob] - f[ob + 2]); // B==R
            }
            return false;
        }

        /// <summary>msdfgen hasDiagonalArtifactInner.</summary>
        private static bool HasDiagonalArtifactInner(double span, bool prot, float am, float dm,
            float[] a, float[] l, float[] q, float dA, float dBC, float dD, double tEx0, double tEx1,
            float[] f, int oa) // oa unused beyond a[]; a/l/q are precomputed arrays
        {
            double[] roots = new double[2];
            int solutions = EquationSolver.SolveQuadratic(roots, dD - dBC + dA, dBC - dA - dA, dA);
            for (int si = 0; si < solutions; si++)
            {
                double t = roots[si];
                if (t > ArtifactTEpsilon && t < 1 - ArtifactTEpsilon)
                {
                    float xm = InterpolatedMedianQuad(a, l, q, t);
                    int rangeFlags = RangeTest(span, prot, 0, 1, t, am, dm, xm);
                    // Additional checks against interpolated medians at the local extremes tEx0,tEx1.
                    rangeFlags |= ExtremeCheck(span, prot, a, l, q, am, dm, xm, t, tEx0);
                    rangeFlags |= ExtremeCheck(span, prot, a, l, q, am, dm, xm, t, tEx1);
                    if ((rangeFlags & ClassifierFlagArtifact) != 0) return true;
                }
            }
            return false;
        }

        private static int ExtremeCheck(double span, bool prot, float[] a, float[] l, float[] q,
            float am, float dm, float xm, double t, double tEx)
        {
            if (tEx > 0 && tEx < 1)
            {
                double t0 = 0, t1 = 1;
                float e0 = am, e1 = dm;
                // tEnd[tEx>t] = tEx; em[tEx>t] = interpolatedMedian(a,l,q,tEx)
                if (tEx > t) { t1 = tEx; e1 = InterpolatedMedianQuad(a, l, q, tEx); }
                else { t0 = tEx; e0 = InterpolatedMedianQuad(a, l, q, tEx); }
                return RangeTest(span, prot, t0, t1, t, e0, e1, xm);
            }
            return 0;
        }

        /// <summary>msdfgen hasDiagonalArtifact (texels a,d diagonal; b,c the other diagonal).</summary>
        private static bool HasDiagonalArtifact(double span, bool prot, float am, float[] f, int oa, int ob, int oc, int od)
        {
            float dm = Median(f[od], f[od + 1], f[od + 2]);
            if (Math.Abs(am - 0.5f) >= Math.Abs(dm - 0.5f))
            {
                // abc = a - b - c (per channel).
                float[] a = { f[oa], f[oa + 1], f[oa + 2] };
                float[] abc =
                {
                    f[oa]     - f[ob]     - f[oc],
                    f[oa + 1] - f[ob + 1] - f[oc + 1],
                    f[oa + 2] - f[ob + 2] - f[oc + 2],
                };
                float[] l = { -a[0] - abc[0], -a[1] - abc[1], -a[2] - abc[2] };
                float[] q = { f[od] + abc[0], f[od + 1] + abc[1], f[od + 2] + abc[2] };
                double[] tEx =
                {
                    -0.5 * l[0] / q[0],
                    -0.5 * l[1] / q[1],
                    -0.5 * l[2] / q[2],
                };
                // dBC terms: (b[ch1]-b[ch0]) + (c[ch1]-c[ch0]).
                float bR = f[ob], bG = f[ob + 1], bB = f[ob + 2];
                float cR = f[oc], cG = f[oc + 1], cB = f[oc + 2];
                float dR = f[od], dG = f[od + 1], dB = f[od + 2];
                return
                    HasDiagonalArtifactInner(span, prot, am, dm, a, l, q, a[1] - a[0], (bG - bR) + (cG - cR), dG - dR, tEx[0], tEx[1], f, oa) ||
                    HasDiagonalArtifactInner(span, prot, am, dm, a, l, q, a[2] - a[1], (bB - bG) + (cB - cG), dB - dG, tEx[1], tEx[2], f, oa) ||
                    HasDiagonalArtifactInner(span, prot, am, dm, a, l, q, a[0] - a[2], (bR - bB) + (cR - cB), dR - dB, tEx[2], tEx[0], f, oa);
            }
            return false;
        }
    }
}
