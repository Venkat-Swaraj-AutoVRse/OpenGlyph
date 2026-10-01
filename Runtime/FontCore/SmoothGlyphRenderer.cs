using System;

namespace LightSide
{
    /// <summary>
    /// Grayscale (anti-aliased) coverage rasteriser for <see cref="UniTextRenderMode.Smooth"/> on
    /// regular (non-color) fonts (Phase 1c). Renders a glyph with FreeType's NORMAL render mode and
    /// copies the 8-bit coverage into a single-channel Alpha8 buffer — intermediate alpha values are
    /// preserved (that is what makes Smooth smooth). Shaped as an <see cref="SdfRenderedGlyph"/> with
    /// <c>channels = 1</c> and <c>spread = 0</c> so it packs through the existing Alpha8 atlas path.
    /// </summary>
    /// <remarks>
    /// Companion to <see cref="MonoGlyphRenderer"/> (1-bit, 0/255). The base engine previously had no
    /// bitmap raster path for Smooth/Mono on regular fonts — <c>RenderPreparedBatch</c> only produced
    /// SDF — so the channel-coherence guard dropped every glyph and Smooth fonts added zero glyphs.
    /// This renderer, wired for all fonts, fixes that. Uses only existing FT APIs (load + normal
    /// render + bitmap read); no native changes. EmojiFont overrides the whole batch pipeline, so it
    /// never reaches here.
    /// </remarks>
    internal static class SmoothGlyphRenderer
    {
        /// <summary>
        /// Renders a single glyph as an anti-aliased grayscale coverage bitmap at
        /// <paramref name="pixelSize"/> ppem into a pooled single-channel buffer.
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

            if (!FT.LoadGlyph(face, glyphIndex, FT.LOAD_DEFAULT | FT.LOAD_NO_BITMAP))
                return false;

            var metrics = FT.GetGlyphMetrics(face);
            result.metricWidth = metrics.width;
            result.metricHeight = metrics.height;
            result.metricBearingX = metrics.bearingX;
            result.metricBearingY = metrics.bearingY;
            result.metricAdvanceX26_6 = metrics.advanceX;

            if (!FT.RenderGlyph(face, FT.RENDER_MODE_NORMAL))
                return false;

            var bmp = FT.GetBitmapData(face);
            result.bitmapLeft = FT.GetBitmapLeft(face);
            result.bitmapTop = FT.GetBitmapTop(face);
            result.bmpWidth = bmp.width;
            result.bmpHeight = bmp.height;
            result.bmpPitch = bmp.width; // dst tightly packed single-channel

            // Whitespace / empty glyph: valid but no pixels.
            if (bmp.width <= 0 || bmp.height <= 0 || bmp.buffer == IntPtr.Zero)
            {
                result.isValid = true;
                return true;
            }

            int w = bmp.width;
            int h = bmp.height;
            var pixels = UniTextArrayPool<byte>.Rent(w * h);
            byte* src = (byte*)bmp.buffer;
            int pitch = bmp.pitch;

            if (bmp.pixelMode == FT.PIXEL_MODE_GRAY)
            {
                fixed (byte* dst = pixels)
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy(src + y * pitch, dst + y * w, w, w);
            }
            else if (bmp.pixelMode == FT.PIXEL_MODE_MONO)
            {
                // Some faces (e.g. bitmap-only strikes) hand back 1-bit even for a normal render;
                // expand to 0/255 so the buffer is still valid coverage.
                fixed (byte* dst = pixels)
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = src + y * pitch;
                        int dstRow = y * w;
                        for (int x = 0; x < w; x++)
                            dst[dstRow + x] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)255 : (byte)0;
                    }
            }
            else
            {
                UniTextArrayPool<byte>.Return(pixels);
                return false;
            }

            result.sdfPixels = pixels;
            result.isValid = true;
            return true;
        }
    }
}
