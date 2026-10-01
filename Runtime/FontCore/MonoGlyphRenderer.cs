using System;

namespace LightSide
{
    /// <summary>
    /// Pixel-perfect Mono glyph rasteriser (Phase 1c). Renders a glyph as a 1-bit monochrome bitmap
    /// (FreeType <c>FT_RENDER_MODE_MONO</c>) and expands it to a single-channel Alpha8 coverage buffer
    /// whose texels are ONLY 0 or 255 — no anti-aliasing, no distance field, no spread. The output is
    /// shaped as an <see cref="SdfRenderedGlyph"/> with <c>channels = 1</c> and <c>spread = 0</c> so it
    /// packs through the existing Alpha8 atlas path unchanged.
    /// </summary>
    /// <remarks>
    /// This is the render half of pixel-perfect mode; the atlas is point-filtered with no mipmaps
    /// (see <c>UniTextFont.CreateNewAtlasTexture</c>) and quads are snapped to the device pixel grid
    /// (see <c>UniTextMeshGenerator</c>). Because it uses only existing FT APIs (load + mono render +
    /// bitmap read) it needs no native changes and degrades to a normal failure (returns false) when
    /// a glyph cannot be rendered.
    /// </remarks>
    internal static class MonoGlyphRenderer
    {
        /// <summary>
        /// Renders a single glyph as a 1-bit mono bitmap at <paramref name="pixelSize"/> ppem and
        /// copies it into a pooled single-channel buffer of pure 0/255 values.
        /// </summary>
        public static unsafe bool TryRender(IntPtr face, uint glyphIndex, int pixelSize,
            out SdfRenderedGlyph result)
        {
            result = default;
            result.glyphIndex = glyphIndex;
            result.channels = 1;

            if (face == IntPtr.Zero)
                return false;

            if (!FT.SetPixelSize(face, pixelSize))
                return false;

            // Load without a bitmap strike and without hinting mangling the grid, then render to a
            // 1-bit mono target. LOAD_TARGET_MONO is implied by rendering with RENDER_MODE_MONO.
            if (!FT.LoadGlyph(face, glyphIndex, FT.LOAD_DEFAULT | FT.LOAD_NO_BITMAP))
                return false;

            var metrics = FT.GetGlyphMetrics(face);
            result.metricWidth = metrics.width;
            result.metricHeight = metrics.height;
            result.metricBearingX = metrics.bearingX;
            result.metricBearingY = metrics.bearingY;
            result.metricAdvanceX26_6 = metrics.advanceX;

            if (!FT.RenderGlyph(face, FT.RENDER_MODE_MONO))
                return false;

            var bmp = FT.GetBitmapData(face);
            result.bitmapLeft = FT.GetBitmapLeft(face);
            result.bitmapTop = FT.GetBitmapTop(face);
            result.bmpWidth = bmp.width;
            result.bmpHeight = bmp.height;
            result.bmpPitch = bmp.width; // dst is tightly packed single-channel

            // Whitespace / empty glyph: valid but no pixels (matches SdfGlyphRenderer contract).
            if (bmp.width <= 0 || bmp.height <= 0 || bmp.buffer == IntPtr.Zero)
            {
                result.isValid = true;
                return true;
            }

            if (bmp.pixelMode != FT.PIXEL_MODE_MONO)
            {
                // The mono render did not yield a 1-bit bitmap (unexpected). Refuse rather than emit
                // anti-aliased texels that would violate the 0/255 guarantee.
                return false;
            }

            int w = bmp.width;
            int h = bmp.height;
            int pixelCount = w * h;
            var pixels = UniTextArrayPool<byte>.Rent(pixelCount);

            byte* src = (byte*)bmp.buffer;
            int pitch = bmp.pitch; // 1-bit rows, packed MSB-first, pitch in bytes

            fixed (byte* dst = pixels)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = src + y * pitch;
                    int dstRow = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        bool set = (row[x >> 3] & (0x80 >> (x & 7))) != 0;
                        dst[dstRow + x] = set ? (byte)255 : (byte)0;
                    }
                }
            }

            result.sdfPixels = pixels;
            result.isValid = true;
            return true;
        }
    }
}
