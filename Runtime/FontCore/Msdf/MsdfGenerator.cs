using System;

namespace LightSide.Msdf
{
    /// <summary>Parameters controlling MSDF field generation.</summary>
    public struct MsdfConfig
    {
        /// <summary>Output width in pixels (including padding).</summary>
        public int Width;
        /// <summary>Output height in pixels (including padding).</summary>
        public int Height;
        /// <summary>Distance range in output pixels (a.k.a. pxRange). Typically 2*spread.</summary>
        public double Range;
        /// <summary>Scale applied to shape coordinates (output px per shape unit).</summary>
        public double ScaleX;
        public double ScaleY;
        /// <summary>Translation applied to shape coordinates (in shape units) before scaling.</summary>
        public double TranslateX;
        public double TranslateY;
        /// <summary>Enable msdfgen-style error correction of interpolation artifacts.</summary>
        public bool ErrorCorrection;
        /// <summary>
        /// Enable the second-stage mid-region median-collapse repair ("Lift"). Kept as a separate
        /// switch so the edge-colouring fix can be evaluated on its own; when faithful edge-colouring
        /// alone clears the artifact gate this stays off.
        /// </summary>
        public bool LiftCollapse;
    }

    /// <summary>
    /// Generates a multi-channel signed distance field from a coloured <see cref="Shape"/>.
    /// Ported from msdfgen's core <c>generateMSDF</c> + <c>msdfErrorCorrection</c>
    /// (Viktor Chlumsky, MIT). See Third-Party Notices.txt.
    /// </summary>
    /// <remarks>
    /// Output is a float[width*height*3] in row-major, bottom-up (y=0 is the bottom row),
    /// each channel a signed distance in the range [-0.5..+0.5] where 0.5 = fully inside at
    /// the field's zero crossing after the range mapping. Values are NOT yet clamped to
    /// [0,1] for a texture; call <see cref="EncodeRgb24"/> for that.
    /// </remarks>
    public static class MsdfGenerator
    {
        private struct EdgePoint
        {
            public SignedDistance MinDistance;
            public EdgeSegment NearEdge;
            public double NearParam;
        }

        public static float[] Generate(Shape shape, in MsdfConfig cfg)
        {
            int w = cfg.Width, h = cfg.Height;
            var output = new float[w * h * 3];
            if (w <= 0 || h <= 0) return output;

            double range = cfg.Range <= 0 ? 1 : cfg.Range;

            for (int y = 0; y < h; y++)
            {
                // Bottom-up: row 0 is the bottom of the glyph.
                int row = y;
                for (int x = 0; x < w; x++)
                {
                    // Pixel centre in shape space.
                    double px = (x + 0.5) / cfg.ScaleX - cfg.TranslateX;
                    double py = (y + 0.5) / cfg.ScaleY - cfg.TranslateY;
                    Vector2D p = new Vector2D(px, py);

                    EdgePoint r = new EdgePoint { MinDistance = SignedDistance.Infinite };
                    EdgePoint g = new EdgePoint { MinDistance = SignedDistance.Infinite };
                    EdgePoint b = new EdgePoint { MinDistance = SignedDistance.Infinite };

                    foreach (var contour in shape.Contours)
                    {
                        foreach (var edge in contour.Edges)
                        {
                            SignedDistance distance = edge.MinSignedDistance(p, out double t);
                            if ((edge.Color & EdgeColor.Red) != 0 && distance < r.MinDistance)
                            {
                                r.MinDistance = distance; r.NearEdge = edge; r.NearParam = t;
                            }
                            if ((edge.Color & EdgeColor.Green) != 0 && distance < g.MinDistance)
                            {
                                g.MinDistance = distance; g.NearEdge = edge; g.NearParam = t;
                            }
                            if ((edge.Color & EdgeColor.Blue) != 0 && distance < b.MinDistance)
                            {
                                b.MinDistance = distance; b.NearEdge = edge; b.NearParam = t;
                            }
                        }
                    }

                    if (r.NearEdge != null)
                        r.NearEdge.DistanceToPerpendicularDistance(ref r.MinDistance, p, r.NearParam);
                    if (g.NearEdge != null)
                        g.NearEdge.DistanceToPerpendicularDistance(ref g.MinDistance, p, g.NearParam);
                    if (b.NearEdge != null)
                        b.NearEdge.DistanceToPerpendicularDistance(ref b.MinDistance, p, b.NearParam);

                    // Preserve each channel's OWN edge-relative sign (this per-channel independence is
                    // what makes the median sharp at corners). A single global orientation sign is
                    // applied afterwards, below, so overlapping/mis-oriented contours decode correctly
                    // without flattening the three channels to one sign.
                    int i = (row * w + x) * 3;
                    output[i + 0] = (float)(r.MinDistance.Distance / range + 0.5);
                    output[i + 1] = (float)(g.MinDistance.Distance / range + 0.5);
                    output[i + 2] = (float)(b.MinDistance.Distance / range + 0.5);
                }
            }

            // Contour orientation is resolved up-front by Shape.OrientContours() (called by
            // MsdfBuilder before generation), so each edge's directed-distance sign already means
            // "inside". No global field flip is applied here.
            if (cfg.ErrorCorrection)
                ErrorCorrect(output, w, h, cfg.LiftCollapse);

            return output;
        }

        /// <summary>
        /// msdfgen error correction: removes multi-channel "clash" artifacts — the small specks and
        /// spurs that appear where two differently-coloured edges meet (corners, junctions, thin
        /// stems). Between a pixel and each neighbour, a channel whose value crosses the 0.5 contour
        /// while the reconstructed MEDIAN does NOT (or vice-versa) would make the shader draw a false
        /// edge there; such a pixel is collapsed to its own median, which cannot clash. Port of the
        /// crossing test in msdfgen's <c>msdfErrorCorrection</c> (Viktor Chlumsky, MIT).
        /// </summary>
        private static void ErrorCorrect(float[] img, int w, int h, bool liftCollapse)
        {
            var clones = (float[])img.Clone();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = (y * w + x) * 3;

                    bool artifact = false;
                    if (x > 0) artifact |= Clashes(clones, idx, (y * w + (x - 1)) * 3);
                    if (!artifact && x + 1 < w) artifact |= Clashes(clones, idx, (y * w + (x + 1)) * 3);
                    if (!artifact && y > 0) artifact |= Clashes(clones, idx, ((y - 1) * w + x) * 3);
                    if (!artifact && y + 1 < h) artifact |= Clashes(clones, idx, ((y + 1) * w + x) * 3);
                    // Diagonals catch corner specks that the 4-neighbourhood misses.
                    if (!artifact && x > 0 && y > 0) artifact |= Clashes(clones, idx, ((y - 1) * w + (x - 1)) * 3);
                    if (!artifact && x + 1 < w && y > 0) artifact |= Clashes(clones, idx, ((y - 1) * w + (x + 1)) * 3);
                    if (!artifact && x > 0 && y + 1 < h) artifact |= Clashes(clones, idx, ((y + 1) * w + (x - 1)) * 3);
                    if (!artifact && x + 1 < w && y + 1 < h) artifact |= Clashes(clones, idx, ((y + 1) * w + (x + 1)) * 3);

                    if (artifact)
                    {
                        float m = Median(clones[idx], clones[idx + 1], clones[idx + 2]);
                        img[idx] = m; img[idx + 1] = m; img[idx + 2] = m;
                    }
                }
            }

            // Second stage — PINNED-PAIR collapse repair. A long two-channel spline running tangent
            // to a sample column pins ITS TWO channels to ~0.5 down an interior line (CYAN pins G,B;
            // the far wall supplies only the third channel). The median then reads 0.5 — a false edge
            // in open field — even though the UNPINNED third channel, and every genuine neighbour,
            // agree firmly on one side. Edge-colouring (long-spline whitening) removes most of these;
            // this pass cleans the residue the colouring cannot reach (short tangent CYAN runs on
            // '@''s inner ring). A texel is repaired only when its median hugs 0.5 AND two of its
            // channels are a near-equal pinned pair AND its 8-neighbourhood votes firmly for ONE side
            // with no dissent — a condition a real edge (whose neighbourhood straddles 0.5) can never
            // meet, so corners, stems and 1px features are untouched. The repair sets the median to
            // the firmly-agreed side, i.e. lets the honest unpinned channel win. Iterated twice so a
            // 2-texel band heals inward. Equivalent in effect to msdfgen's distance error-correction
            // for the pinned-pair case, expressed on the reconstructed field.
            const float onEdge = 0.10f;   // |median-0.5| below this == pinned to the contour
            const float firm = 0.06f;     // a neighbour this far past 0.5 is a firm vote
            const float pairEps = 0.08f;  // two channels this close AND both near 0.5 == a pinned pair
            for (int pass = 0; pass < 2 && liftCollapse; pass++)
            {
                var src = (float[])img.Clone();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int idx = (y * w + x) * 3;
                        float cr = src[idx], cg = src[idx + 1], cb = src[idx + 2];
                        float m = Median(cr, cg, cb);
                        if (Math.Abs(m - 0.5f) > onEdge) continue; // only edge-pinned texels

                        // Require an actual pinned PAIR: two channels near each other AND near 0.5.
                        if (!PinnedPair(cr, cg, cb, pairEps)) continue;

                        int inside = 0, outside = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                                int ni = (ny * w + nx) * 3;
                                float nm = Median(src[ni], src[ni + 1], src[ni + 2]) - 0.5f;
                                if (nm > firm) inside++;
                                else if (nm < -firm) outside++;
                            }

                        // Lift only on an unambiguous consensus with no dissent.
                        if (inside >= 3 && outside == 0) { float keep = Math.Max(cr, Math.Max(cg, cb)); img[idx] = img[idx + 1] = img[idx + 2] = keep; }
                        else if (outside >= 3 && inside == 0) { float keep = Math.Min(cr, Math.Min(cg, cb)); img[idx] = img[idx + 1] = img[idx + 2] = keep; }
                    }
            }
        }

        /// <summary>
        /// True when two of the three channels form a "pinned pair": they are within <paramref
        /// name="eps"/> of each other AND both within <paramref name="eps"/> of the 0.5 contour. That
        /// is the signature of a single tangent edge driving two channels to the contour value while
        /// the third channel (the honest one) is left to carry the real side.
        /// </summary>
        private static bool PinnedPair(float r, float g, float b, float eps)
        {
            return NearPair(r, g, eps) || NearPair(g, b, eps) || NearPair(r, b, eps);
        }

        private static bool NearPair(float a, float b, float eps) =>
            Math.Abs(a - b) <= eps && Math.Abs(a - 0.5f) <= eps && Math.Abs(b - 0.5f) <= eps;

        /// <summary>
        /// True when, between pixels <paramref name="a"/> and <paramref name="b"/>, the number of
        /// per-channel 0.5-crossings disagrees with whether the MEDIAN crosses 0.5 — i.e. at least
        /// one channel draws an edge the median does not, or the median draws an edge no single
        /// channel supports. That mismatch is exactly a renderable clash artifact.
        /// </summary>
        private static bool Clashes(float[] img, int a, int b)
        {
            const float t = 0.5f;
            float ma = Median(img[a], img[a + 1], img[a + 2]);
            float mb = Median(img[b], img[b + 1], img[b + 2]);
            bool medianCrosses = (ma - t) * (mb - t) < 0f;

            int channelCrossings = 0;
            for (int c = 0; c < 3; c++)
                if ((img[a + c] - t) * (img[b + c] - t) < 0f)
                    channelCrossings++;

            // No true edge here (median flat) but a channel still flips -> spurious contour.
            if (!medianCrosses && channelCrossings > 0) return true;
            // Median flips but is driven by only ONE channel while another flips the OTHER way,
            // producing a double/false edge (the classic corner clash).
            if (medianCrosses && channelCrossings >= 2)
            {
                // Count how many channels cross in the SAME direction as the median.
                int sameDir = 0;
                float medDir = Math.Sign(mb - ma);
                for (int c = 0; c < 3; c++)
                    if ((img[a + c] - t) * (img[b + c] - t) < 0f && Math.Sign(img[b + c] - img[a + c]) == medDir)
                        sameDir++;
                if (sameDir < channelCrossings) return true; // a channel crosses against the median
            }
            return false;
        }

        /// <summary>Median of three — the value an MSDF shader reconstructs per pixel.</summary>
        public static float Median(float a, float b, float c) =>
            Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

        /// <summary>
        /// Generates a TRUE single-channel signed distance field from a shape: at each pixel the
        /// nearest edge distance across ALL edges (ignoring colour), signed by the shape's fill.
        /// This is the honest SDF baseline for comparing corner reconstruction against MSDF — it is
        /// exactly what a one-channel atlas can represent, and it rounds corners because a single
        /// scalar cannot encode two independent edges meeting at a point. Output layout matches
        /// <see cref="Generate"/> (row-major, bottom-up, value = dist/range + 0.5).
        /// </summary>
        public static float[] GenerateSdf(Shape shape, in MsdfConfig cfg)
        {
            int w = cfg.Width, h = cfg.Height;
            var output = new float[w * h];
            if (w <= 0 || h <= 0) return output;
            double range = cfg.Range <= 0 ? 1 : cfg.Range;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double px = (x + 0.5) / cfg.ScaleX - cfg.TranslateX;
                    double py = (y + 0.5) / cfg.ScaleY - cfg.TranslateY;
                    var p = new Vector2D(px, py);

                    SignedDistance min = SignedDistance.Infinite;
                    foreach (var contour in shape.Contours)
                        foreach (var edge in contour.Edges)
                        {
                            SignedDistance d = edge.MinSignedDistance(p, out _);
                            if (d < min) min = d;
                        }

                    output[y * w + x] = (float)(min.Distance / range + 0.5);
                }
            }

            // Orientation already resolved by Shape.OrientContours() before generation.
            return output;
        }

        /// <summary>
        /// Encodes a float MSDF field (channels ~[0,1] after range mapping) into an 8-bit
        /// RGB24 byte buffer (3 bytes/pixel), clamped to [0,255]. Layout matches the float
        /// buffer (row-major, bottom-up).
        /// </summary>
        public static byte[] EncodeRgb24(float[] field, int w, int h)
        {
            var bytes = new byte[w * h * 3];
            for (int i = 0; i < bytes.Length; i++)
            {
                int v = (int)(field[i] * 255f + 0.5f);
                if (v < 0) v = 0; else if (v > 255) v = 255;
                bytes[i] = (byte)v;
            }
            return bytes;
        }
    }
}
