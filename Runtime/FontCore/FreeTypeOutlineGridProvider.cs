using System;
using System.Collections.Generic;

namespace LightSide
{
    /// <summary>
    /// <see cref="IPixelGridOutlineProvider"/> backed by the FreeType native outline export
    /// (<c>ut_ft_get_outline_data</c>, wrapped by <see cref="FT.TryGetOutlineData"/>). Loads glyphs
    /// with <see cref="FT.LOAD_NO_SCALE"/> so the returned point coordinates are raw font design
    /// units on the same scale as <see cref="UnitsPerEm"/>.
    /// </summary>
    /// <remarks>
    /// Degrades gracefully: if the installed native binary predates the outline export,
    /// <see cref="FT.TryGetOutlineData"/> returns false and this provider yields no coordinates, so
    /// <see cref="PixelFontDetection.Analyze"/> reports <c>outlineDataAvailable=false</c> rather than
    /// misclassifying the font. Never renders — it only reads contour points, so it is cheap and has
    /// no effect on any atlas or render mode.
    /// </remarks>
    internal sealed class FreeTypeOutlineGridProvider : IPixelGridOutlineProvider
    {
        private readonly IntPtr _face;
        private readonly int _unitsPerEm;

        public FreeTypeOutlineGridProvider(IntPtr face, int unitsPerEm)
        {
            _face = face;
            _unitsPerEm = unitsPerEm;
        }

        public int UnitsPerEm => _unitsPerEm;

        public bool TryCollectOutlineCoords(List<int> coords, int maxGlyphs)
        {
            if (_face == IntPtr.Zero || coords == null) return false;

            // Sample glyph ids spread across a representative ASCII-ish range plus a few beyond, so a
            // font with a handful of off-grid decorative glyphs cannot dominate the statistic.
            int sampled = 0;
            int[] xy = null;
            byte[] tags = null;
            short[] ends = null;

            // Probe common visible codepoints first (letters/digits) via char index; fall back to
            // raw glyph ids if cmap lookups miss.
            foreach (uint gid in EnumerateSampleGlyphs(maxGlyphs))
            {
                if (sampled >= maxGlyphs) break;

                // LOAD_NO_SCALE | LOAD_NO_BITMAP: untransformed design-unit outline, ignore any strike.
                if (FT.LoadGlyphWithError(_face, gid, FT.LOAD_NO_SCALE | FT.LOAD_NO_BITMAP) != 0)
                    continue;

                if (!FT.GetOutlineInfo(_face, out int nContours, out int nPoints) || nPoints <= 0)
                    continue;

                if (xy == null || xy.Length < nPoints * 2)
                {
                    xy = new int[Math.Max(64, nPoints * 2)];
                    tags = new byte[Math.Max(32, nPoints)];
                    ends = new short[Math.Max(8, Math.Max(1, nContours))];
                }
                else if (ends.Length < nContours)
                {
                    ends = new short[nContours];
                }

                if (!FT.TryGetOutlineData(_face, xy, tags, ends,
                        out int outPoints, out int outContours, out int _))
                {
                    // Export missing or capacity mismatch — if the export itself is absent we should
                    // bail entirely so the caller reports outline data unavailable.
                    if (coords.Count == 0)
                        return false;
                    continue;
                }

                for (int i = 0; i < outPoints; i++)
                {
                    coords.Add(xy[2 * i]);
                    coords.Add(xy[2 * i + 1]);
                }
                sampled++;
            }

            return coords.Count > 0;
        }

        private IEnumerable<uint> EnumerateSampleGlyphs(int maxGlyphs)
        {
            var seen = new HashSet<uint>();
            // Visible letters and digits are the most reliably on-grid glyphs.
            const string probe = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            foreach (char c in probe)
            {
                uint gid = FT.GetCharIndex(_face, c);
                if (gid != 0 && seen.Add(gid))
                    yield return gid;
            }
            // Fallback: raw glyph ids in case the cmap is unusual.
            for (uint g = 1; g <= (uint)maxGlyphs && seen.Count < maxGlyphs; g++)
                if (seen.Add(g))
                    yield return g;
        }
    }
}
