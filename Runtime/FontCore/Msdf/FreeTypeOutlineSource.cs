using System;

namespace LightSide.Msdf
{
    /// <summary>
    /// <see cref="IGlyphOutlineSource"/> backed by FreeType via the native
    /// <c>ut_ft_get_outline_data</c> export. FreeType handles glyf, CFF/CFF2 and variable fonts,
    /// which a managed glyf-only parser could not. When the loaded native binary predates the
    /// export, <see cref="IsAvailable"/> is false and the Msdf render mode falls back to SDF.
    /// </summary>
    /// <remarks>
    /// The caller supplies a loaded FreeType face pointer (from <c>FT.LoadFace</c>). Outline
    /// coordinates are returned in 26.6 fixed-point pixel units at the requested size and are
    /// converted to whole-pixel doubles (y-up) here, so 1 outline unit == 1 output pixel — which
    /// is what <see cref="MsdfBuilder"/> expects.
    /// </remarks>
    public sealed class FreeTypeOutlineSource : IGlyphOutlineSource
    {
        private readonly IntPtr _face;
        private readonly Func<uint, int, bool> _loadGlyphScaled;
        private bool _exportMissing;

        // Reusable buffers (grown as needed). Not thread-safe: one source per worker thread.
        private int[] _xy = new int[512];
        private byte[] _tags = new byte[256];
        private short[] _ends = new short[32];

        /// <param name="face">Loaded FreeType face pointer.</param>
        /// <param name="loadGlyphScaled">
        /// Callback that sets the pixel size and loads the glyph slot with an unhinted, unscaled-off
        /// (i.e. scaled) outline. Returns true on success. Typically wraps
        /// <c>FT.SetPixelSize + FT.LoadGlyph(face, gi, LOAD_NO_HINTING)</c>.
        /// </param>
        public FreeTypeOutlineSource(IntPtr face, Func<uint, int, bool> loadGlyphScaled)
        {
            _face = face;
            _loadGlyphScaled = loadGlyphScaled ?? throw new ArgumentNullException(nameof(loadGlyphScaled));
        }

        public bool IsAvailable => !_exportMissing && _face != IntPtr.Zero;

        public GlyphOutline GetOutline(uint glyphIndex, int pixelsPerEm)
        {
            if (_exportMissing || _face == IntPtr.Zero)
                return null;

            if (!_loadGlyphScaled(glyphIndex, pixelsPerEm))
                return null;

            // First probe for sizes; grow buffers if the export reports needing more.
            if (!MsdfNative.TryGetOutlineData(_face, _xy, _tags, _ends,
                    out int numPoints, out int numContours, out int flags))
            {
                if (!MsdfNative.IsExportAvailable)
                {
                    _exportMissing = true;
                    return null;
                }

                // Buffers too small: grow to the reported counts and retry once.
                if (numPoints > _tags.Length || numContours > _ends.Length)
                {
                    if (numPoints > _tags.Length)
                    {
                        _tags = new byte[NextPow2(numPoints)];
                        _xy = new int[_tags.Length * 2];
                    }
                    if (numContours > _ends.Length)
                        _ends = new short[NextPow2(numContours)];

                    if (!MsdfNative.TryGetOutlineData(_face, _xy, _tags, _ends,
                            out numPoints, out numContours, out flags))
                        return null;
                }
                else
                {
                    return null;
                }
            }

            if (numPoints <= 0 || numContours <= 0)
                return null; // empty glyph (whitespace)

            var outline = new GlyphOutline(numContours)
            {
                ReverseFill = (flags & MsdfNative.FT_OUTLINE_REVERSE_FILL) != 0,
            };

            int start = 0;
            for (int c = 0; c < numContours; c++)
            {
                int end = _ends[c]; // inclusive last point index of this contour
                int count = end - start + 1;
                if (count <= 0) { start = end + 1; continue; }

                var contour = new OutlineContour(count);
                for (int i = start; i <= end; i++)
                {
                    // 26.6 fixed-point -> whole pixels, y-up already in FreeType outline space.
                    double x = _xy[i * 2] / 64.0;
                    double y = _xy[i * 2 + 1] / 64.0;
                    contour.Add(new OutlinePoint(x, y), DecodeTag(_tags[i]));
                }
                outline.Contours.Add(contour);
                start = end + 1;
            }

            return outline;
        }

        /// <summary>
        /// Maps a FreeType FT_CURVE_TAG byte to our <see cref="OutlinePointTag"/>.
        /// FT tags: bit0 = on-curve; bit1 = (for off-curve) third-order (cubic) when set,
        /// second-order (conic/quadratic) when clear.
        /// </summary>
        private static OutlinePointTag DecodeTag(byte tag)
        {
            bool onCurve = (tag & 0x1) != 0;
            if (onCurve) return OutlinePointTag.OnCurve;
            bool cubic = (tag & 0x2) != 0;
            return cubic ? OutlinePointTag.CubicControl : OutlinePointTag.QuadraticControl;
        }

        private static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }
    }
}
