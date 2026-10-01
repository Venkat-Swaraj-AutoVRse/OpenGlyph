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
    /// Ported from msdfgen's core <c>generateMSDF</c> (<c>core/msdfgen.cpp</c>) + the standalone
    /// <c>MSDFErrorCorrection</c> class (<c>core/MSDFErrorCorrection.cpp</c>/.h, msdfgen &gt;= 1.9,
    /// Viktor Chlumsky, MIT). See Third-Party Notices.txt. No original source is bundled; the
    /// algorithms are re-expressed against OpenGlyph's field + <see cref="Shape"/> model.
    /// </summary>
    /// <remarks>
    /// Output is a float[width*height*3] in row-major, bottom-up (y=0 is the bottom row),
    /// each channel a signed distance in the range [-0.5..+0.5] where 0.5 = fully inside at
    /// the field's zero crossing after the range mapping. Values are NOT yet clamped to
    /// [0,1] for a texture; call <see cref="EncodeRgb24"/> for that.
    /// </remarks>
    public static class MsdfGenerator
    {
        public static float[] Generate(Shape shape, in MsdfConfig cfg)
        {
            int w = cfg.Width, h = cfg.Height;
            var output = new float[w * h * 3];
            if (w <= 0 || h <= 0) return output;

            double range = cfg.Range <= 0 ? 1 : cfg.Range;

            // Faithful msdfgen generateMSDF: OverlappingContourCombiner<MultiDistanceSelector> with
            // overlapSupport (core/msdfgen.cpp generateMSDF, core/contour-combiners.cpp,
            // core/edge-selectors.cpp). The earlier flat global per-channel nearest-edge min did not
            // resolve per-channel perpendicular distance with edge-domain gating nor combine
            // overlapping contours by winding, which caused the measured RAW |Δ| at junctions and
            // overlaps (W/g/&/@). One combiner is reused across all texels (msdfgen keeps the finder
            // for the whole bitmap); it is reset per sample point.
            var combiner = new MsdfDistance.OverlappingContourCombiner(shape);

            for (int y = 0; y < h; y++)
            {
                // Bottom-up: row 0 is the bottom of the glyph.
                int row = y;
                for (int x = 0; x < w; x++)
                {
                    // Pixel centre in shape space == msdfgen transformation.unproject(x+.5, y+.5).
                    double px = (x + 0.5) / cfg.ScaleX - cfg.TranslateX;
                    double py = (y + 0.5) / cfg.ScaleY - cfg.TranslateY;
                    Vector2D p = new Vector2D(px, py);

                    var d = MsdfDistance.ShapeDistance(combiner, shape, p);

                    int i = (row * w + x) * 3;
                    output[i + 0] = (float)(d.r / range + 0.5);
                    output[i + 1] = (float)(d.g / range + 0.5);
                    output[i + 2] = (float)(d.b / range + 0.5);
                }
            }

            if (cfg.ErrorCorrection)
                MsdfErrorCorrection.Correct(output, w, h, shape, in cfg);

            return output;
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

                    // ROOT FIX (round 6). The single-channel SDF must be the TRUE signed distance:
                    // the shortest distance to the outline, signed by the shape's fill. The nearest
                    // edge's OWN directed sign (min.Distance's sign) is a per-edge pseudo-sign and it
                    // DISAGREES with the fill wherever the nearest edge belongs to a contour whose
                    // local orientation does not match the global non-zero winding — e.g. at the '@'
                    // inner-wall column, where it inverted a 13-pixel foreground band to -range/2 and
                    // drew the "SDF notch" the reviewer flagged at that column. msdfgen's generateSDF
                    // signs the true distance via its scanline/overlap fill, NOT the nearest edge; the
                    // faithful equivalent against our model is Shape.Contains (non-zero winding),
                    // which is already the authoritative inside test used everywhere else. Magnitude
                    // stays the nearest-edge distance; only the SIGN comes from the fill.
                    double mag = Math.Abs(min.Distance);
                    double signed = shape.Contains(p) ? mag : -mag;
                    output[y * w + x] = (float)(signed / range + 0.5);
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
