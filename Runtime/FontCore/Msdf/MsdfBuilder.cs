using System;

namespace LightSide.Msdf
{
    /// <summary>Result of building an MSDF for one glyph.</summary>
    public struct MsdfGlyphResult
    {
        public bool IsValid;
        /// <summary>Bitmap width in pixels (glyph box + 2*spread).</summary>
        public int Width;
        /// <summary>Bitmap height in pixels (glyph box + 2*spread).</summary>
        public int Height;
        /// <summary>RGB24 bytes, row-major, TOP-DOWN (row 0 = top), ready for atlas copy.</summary>
        public byte[] Rgb;
        /// <summary>Raw float field (bottom-up) for testing/introspection. Channels ~[0,1].</summary>
        public float[] Field;
        /// <summary>Left bearing of the drawn bitmap relative to the pen, in pixels.</summary>
        public int BitmapLeft;
        /// <summary>Top bearing of the drawn bitmap relative to the baseline, in pixels.</summary>
        public int BitmapTop;
        /// <summary>The distance range in pixels used (pxRange).</summary>
        public double Range;
    }

    /// <summary>
    /// High-level MSDF construction for a single glyph: frames the outline into a padded
    /// bitmap, ensures a consistent fill sign (inside &gt; 0.5), colours edges, and runs the
    /// generator. Ported / adapted from msdfgen's example framing (MIT).
    /// </summary>
    public static class MsdfBuilder
    {
        /// <summary>
        /// Builds an MSDF for a glyph outline expressed in pixel units (FreeType 26.6 already
        /// converted to whole pixels via /64 by the caller, so 1 unit == 1 output pixel).
        /// </summary>
        /// <param name="outline">Glyph outline in pixel units, y-up.</param>
        /// <param name="spread">Padding in pixels on each side; pxRange = 2*spread.</param>
        /// <param name="angleThreshold">Corner threshold for edge colouring.</param>
        public static MsdfGlyphResult Build(GlyphOutline outline, int spread, double angleThreshold = EdgeColoring.DefaultAngleThreshold, bool errorCorrection = true)
        {
            var result = new MsdfGlyphResult { IsValid = false };
            if (outline == null || outline.IsEmpty || spread < 1)
                return result;

            Shape shape = Shape.FromOutline(outline);
            if (shape.EdgeCount == 0)
                return result;

            // Ensure positive-is-inside: sum contour windings; if the dominant orientation is
            // clockwise (TrueType), reverse the y-axis interpretation by negating distances via
            // a global sign. msdfgen expects counter-clockwise outer contours to yield positive
            // inside. We detect the outer winding by the sign of the total signed area.
            int windingSum = 0;
            foreach (var c in shape.Contours) windingSum += c.Winding();
            bool flipSign = windingSum < 0; // clockwise-dominant -> flip so inside is positive

            shape.Bounds(out double l, out double b, out double r, out double t);
            // Integer glyph box in pixels.
            int gx0 = (int)Math.Floor(l);
            int gy0 = (int)Math.Floor(b);
            int gx1 = (int)Math.Ceiling(r);
            int gy1 = (int)Math.Ceiling(t);
            int glyphW = Math.Max(1, gx1 - gx0);
            int glyphH = Math.Max(1, gy1 - gy0);

            int w = glyphW + 2 * spread;
            int h = glyphH + 2 * spread;

            EdgeColoring.ColorSimple(shape, angleThreshold);

            var cfg = new MsdfConfig
            {
                Width = w,
                Height = h,
                Range = 2.0 * spread,
                ScaleX = 1.0,
                ScaleY = 1.0,
                // Translate so the glyph box's lower-left sits at (spread, spread).
                TranslateX = spread - gx0,
                TranslateY = spread - gy0,
                ErrorCorrection = errorCorrection,
            };

            float[] field = MsdfGenerator.Generate(shape, in cfg);

            if (flipSign)
            {
                for (int i = 0; i < field.Length; i++)
                    field[i] = 1f - field[i];
            }

            // Encode to RGB24, converting bottom-up field to top-down bitmap rows.
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                int srcRow = (h - 1 - y) * w * 3;
                int dstRow = y * w * 3;
                for (int x = 0; x < w * 3; x++)
                {
                    int v = (int)(field[srcRow + x] * 255f + 0.5f);
                    if (v < 0) v = 0; else if (v > 255) v = 255;
                    rgb[dstRow + x] = (byte)v;
                }
            }

            result.IsValid = true;
            result.Width = w;
            result.Height = h;
            result.Rgb = rgb;
            result.Field = field;
            result.Range = cfg.Range;
            result.BitmapLeft = gx0 - spread;
            result.BitmapTop = gy1 + spread; // top edge, baseline-relative, y-up
            return result;
        }

        /// <summary>
        /// Samples the reconstructed signed value (median-of-three, range-mapped back to pixels)
        /// at a bottom-up pixel in a field produced by <see cref="Build"/> (before y-flip/encode).
        /// Positive = inside. Used by parity tests.
        /// </summary>
        public static float SampleMedianSigned(float[] field, int w, int h, int x, int y, double range)
        {
            int i = (y * w + x) * 3;
            float med = MsdfGenerator.Median(field[i], field[i + 1], field[i + 2]);
            return (float)((med - 0.5) * range);
        }
    }
}
