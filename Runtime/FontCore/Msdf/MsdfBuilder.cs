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

            // ROOT fix for inside-out glyphs: make contour orientation consistent with the non-zero
            // winding fill BEFORE edge-colouring and generation, so the generator's directed-edge
            // sign already means "inside". No post-hoc global field flip is needed (that could only
            // vote on a majority and could not fix glyphs whose polarity is wrong only in overlap
            // regions, e.g. '&', 'g', '@').
            shape.OrientContours();

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

            // Faithful msdfgen edge-colouring: smooth/short contours split into >=3 coloured parts
            // (teardrop treatment), corners anchor the per-spline colour switches. The '@' inner-wall
            // and corner-clash artifacts are cleaned afterwards by msdfgen's own MSDFErrorCorrection
            // on the generated field (MsdfErrorCorrection.cs), not by an edge-colouring heuristic.
            EdgeColoring.ColorSimple(shape, angleThreshold, 0);

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

            // Encode to RGB24. The atlas pack (CopySdfBitmapToAtlas) and the mesh UVs use ONE
            // convention shared with the SDF path, whose FreeType SDF buffer is copied row-straight.
            // The generator fills `field` bottom-up (row 0 = bottom); emitting it row-straight here
            // matches the SDF renderer's orientation so MSDF glyphs are upright in the atlas (verified
            // by MsdfOrientationTests). (A previous (h-1-y) flip here inverted MSDF relative to SDF.)
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                int srcRow = y * w * 3;
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

        /// <summary>
        /// Builds a TRUE single-channel SDF for the same outline with identical framing to
        /// <see cref="Build"/> (same width/height/spread/range and sign convention), returning the
        /// bottom-up scalar field (value = dist/range + 0.5). This is the honest SDF baseline for
        /// corner-sharpness comparisons — same generator distance math, one channel instead of three.
        /// </summary>
        public static float[] BuildSdf(GlyphOutline outline, int spread, out int width, out int height, out double range)
        {
            width = height = 0; range = 2.0 * spread;
            if (outline == null || outline.IsEmpty || spread < 1) return null;
            Shape shape = Shape.FromOutline(outline);
            if (shape.EdgeCount == 0) return null;

            shape.OrientContours();

            shape.Bounds(out double l, out double b, out double r, out double t);
            int gx0 = (int)Math.Floor(l), gy0 = (int)Math.Floor(b);
            int gx1 = (int)Math.Ceiling(r), gy1 = (int)Math.Ceiling(t);
            int glyphW = Math.Max(1, gx1 - gx0), glyphH = Math.Max(1, gy1 - gy0);
            int w = glyphW + 2 * spread, h = glyphH + 2 * spread;

            var cfg = new MsdfConfig
            {
                Width = w, Height = h, Range = 2.0 * spread,
                ScaleX = 1.0, ScaleY = 1.0,
                TranslateX = spread - gx0, TranslateY = spread - gy0,
                ErrorCorrection = false,
            };
            float[] field = MsdfGenerator.GenerateSdf(shape, in cfg);

            width = w; height = h; range = cfg.Range;
            return field;
        }

        /// <summary>Signed value (in pixels) of a single-channel field at a bottom-up pixel. Positive = inside.</summary>
        public static float SampleSdfSigned(float[] field, int w, int x, int y, double range) =>
            (float)((field[y * w + x] - 0.5) * range);
    }
}
