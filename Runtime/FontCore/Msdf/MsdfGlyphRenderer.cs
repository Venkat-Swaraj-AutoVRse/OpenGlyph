using System;

namespace LightSide.Msdf
{
    /// <summary>
    /// Renders a single glyph as a multi-channel signed distance field, packaged into the same
    /// <c>SdfRenderedGlyph</c> shape the SDF pipeline uses (with <c>channels = 3</c>). Metrics are
    /// read from FreeType so layout matches SDF exactly; only the bitmap payload differs.
    /// </summary>
    /// <remarks>
    /// Requires the native outline export (via <see cref="FreeTypeOutlineSource"/>). When the
    /// outline is unavailable the caller must fall back to SDF.
    /// </remarks>
    internal static class MsdfGlyphRenderer
    {
        /// <summary>
        /// Attempts MSDF rendering. Returns false (with <paramref name="outlineUnavailable"/> true)
        /// when the native outline export is missing, signalling the caller to fall back to SDF.
        /// </summary>
        public static bool TryRender(
            IntPtr face, uint glyphIndex, int pixelSize, int spread,
            IGlyphOutlineSource outlineSource,
            out SdfRenderedGlyph result, out bool outlineUnavailable)
        {
            result = default;
            result.glyphIndex = glyphIndex;
            outlineUnavailable = false;

            if (face == IntPtr.Zero || outlineSource == null || !outlineSource.IsAvailable)
            {
                outlineUnavailable = true;
                return false;
            }

            // Metrics (26.6) from FreeType — load the glyph unhinted, no bitmap.
            FT.SetPixelSize(face, pixelSize);
            if (!FT.LoadGlyph(face, glyphIndex, FT.LOAD_DEFAULT | FT.LOAD_NO_HINTING))
                return false;

            var m = FT.GetGlyphMetrics(face);
            result.metricWidth = m.width;
            result.metricHeight = m.height;
            result.metricBearingX = m.bearingX;
            result.metricBearingY = m.bearingY;
            result.metricAdvanceX26_6 = m.advanceX;

            GlyphOutline outline = outlineSource.GetOutline(glyphIndex, pixelSize);
            if (outline == null)
            {
                if (!outlineSource.IsAvailable)
                {
                    outlineUnavailable = true;
                    return false;
                }
                // Empty glyph (whitespace): metrics-only, no bitmap. Valid.
                result.isValid = true;
                return true;
            }

            MsdfGlyphResult msdf = MsdfBuilder.Build(outline, spread);
            if (!msdf.IsValid)
            {
                result.isValid = true; // treat as empty rather than failing the whole batch
                return true;
            }

            result.bmpWidth = msdf.Width;
            result.bmpHeight = msdf.Height;
            result.bmpPitch = msdf.Width * 3;
            result.bitmapLeft = msdf.BitmapLeft;
            result.bitmapTop = msdf.BitmapTop;

            int byteCount = msdf.Width * msdf.Height * 3;
            var pixels = UniTextArrayPool<byte>.Rent(byteCount);
            Buffer.BlockCopy(msdf.Rgb, 0, pixels, 0, byteCount);

            result.sdfPixels = pixels;
            result.channels = 3;
            result.isValid = true;
            return true;
        }
    }
}
