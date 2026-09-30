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

                    int i = (row * w + x) * 3;
                    output[i + 0] = (float)(r.MinDistance.Distance / range + 0.5);
                    output[i + 1] = (float)(g.MinDistance.Distance / range + 0.5);
                    output[i + 2] = (float)(b.MinDistance.Distance / range + 0.5);
                }
            }

            if (cfg.ErrorCorrection)
                ErrorCorrect(output, w, h);

            return output;
        }

        /// <summary>
        /// msdfgen error correction: where the median of a pixel disagrees in sign with what
        /// bilinear interpolation to its neighbours would produce (an interpolation artifact),
        /// collapse that pixel's three channels to their median so the artifact disappears.
        /// Simplified port of msdfgen's legacy <c>msdfErrorCorrection</c>.
        /// </summary>
        private static void ErrorCorrect(float[] img, int w, int h)
        {
            // Threshold in normalised units (msdfgen default ~ 1.0 pixel of edge).
            const float threshold = 0.5f / 3f + 1e-3f;
            var clones = (float[])img.Clone();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = (y * w + x) * 3;
                    float m = Median(clones[idx], clones[idx + 1], clones[idx + 2]);

                    bool artifact = false;
                    // 4-neighbourhood check.
                    if (x > 0) artifact |= Disagree(clones, idx, (y * w + (x - 1)) * 3, m, threshold);
                    if (x + 1 < w) artifact |= Disagree(clones, idx, (y * w + (x + 1)) * 3, m, threshold);
                    if (y > 0) artifact |= Disagree(clones, idx, ((y - 1) * w + x) * 3, m, threshold);
                    if (y + 1 < h) artifact |= Disagree(clones, idx, ((y + 1) * w + x) * 3, m, threshold);

                    if (artifact)
                    {
                        img[idx] = m;
                        img[idx + 1] = m;
                        img[idx + 2] = m;
                    }
                }
            }
        }

        private static bool Disagree(float[] img, int a, int b, float medianA, float threshold)
        {
            float mb = Median(img[b], img[b + 1], img[b + 2]);
            // The interpolated median at the midpoint of channels should be monotone; a large
            // per-channel swing that flips relative to the medians is an artifact.
            for (int c = 0; c < 3; c++)
            {
                float da = img[a + c] - medianA;
                float db = img[b + c] - mb;
                if (Math.Sign(da) != Math.Sign(db) &&
                    Math.Abs(da) > threshold && Math.Abs(db) > threshold)
                    return true;
            }
            return false;
        }

        /// <summary>Median of three — the value an MSDF shader reconstructs per pixel.</summary>
        public static float Median(float a, float b, float c) =>
            Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

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
