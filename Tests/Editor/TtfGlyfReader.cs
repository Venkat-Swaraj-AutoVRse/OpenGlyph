using System;
using System.IO;
using LightSide.Msdf;

namespace LightSide.Tests
{
    /// <summary>
    /// TEST-ONLY minimal TrueType parser: reads <c>cmap</c>/<c>loca</c>/<c>glyf</c> and decodes
    /// SIMPLE glyphs (no composites) into a <see cref="GlyphOutline"/> in font design units,
    /// scaled to a requested pixels-per-em. Exists so MSDF tests can obtain real glyph outlines
    /// while the native <c>ut_ft_get_outline_data</c> export is absent from the shipped binary.
    /// It is deliberately not production code — it handles only what the bundled Noto fonts need
    /// for the test glyphs (A, M, O, I, period).
    /// </summary>
    internal sealed class TtfGlyfReader
    {
        private readonly byte[] _data;
        private int _unitsPerEm;
        private int _numGlyphs;
        private bool _longLoca;
        private uint _glyfOffset, _locaOffset, _cmapOffset, _headOffset, _maxpOffset;

        public int UnitsPerEm => _unitsPerEm;
        public bool Composite { get; private set; }

        public TtfGlyfReader(byte[] ttf)
        {
            _data = ttf ?? throw new ArgumentNullException(nameof(ttf));
            ParseTables();
        }

        public static TtfGlyfReader FromFile(string path) => new TtfGlyfReader(File.ReadAllBytes(path));

        // ---- big-endian readers ----
        private ushort U16(int o) => (ushort)((_data[o] << 8) | _data[o + 1]);
        private short S16(int o) => (short)U16(o);
        private uint U32(int o) => (uint)((_data[o] << 24) | (_data[o + 1] << 16) | (_data[o + 2] << 8) | _data[o + 3]);

        private void ParseTables()
        {
            int numTables = U16(4);
            int rec = 12;
            for (int i = 0; i < numTables; i++)
            {
                string tag = System.Text.Encoding.ASCII.GetString(_data, rec, 4);
                uint off = U32(rec + 8);
                switch (tag)
                {
                    case "glyf": _glyfOffset = off; break;
                    case "loca": _locaOffset = off; break;
                    case "cmap": _cmapOffset = off; break;
                    case "head": _headOffset = off; break;
                    case "maxp": _maxpOffset = off; break;
                }
                rec += 16;
            }

            _unitsPerEm = U16((int)_headOffset + 18);
            _longLoca = S16((int)_headOffset + 50) != 0;
            _numGlyphs = U16((int)_maxpOffset + 4);
        }

        /// <summary>Maps a Unicode codepoint to a glyph id via a format-4 or format-12 cmap subtable.</summary>
        public int GetGlyphId(int codepoint)
        {
            int baseOff = (int)_cmapOffset;
            int numSub = U16(baseOff + 2);
            int best = -1, bestScore = -1;
            for (int i = 0; i < numSub; i++)
            {
                int rec = baseOff + 4 + i * 8;
                int platform = U16(rec);
                int encoding = U16(rec + 2);
                int sub = baseOff + (int)U32(rec + 4);
                int score = (platform == 3 && encoding == 10) ? 5
                          : (platform == 3 && encoding == 1) ? 4
                          : (platform == 0) ? 3 : 1;
                if (score > bestScore) { bestScore = score; best = sub; }
            }
            if (best < 0) return 0;

            int format = U16(best);
            if (format == 4) return CmapFormat4(best, codepoint);
            if (format == 12) return CmapFormat12(best, codepoint);
            return 0;
        }

        private int CmapFormat4(int sub, int cp)
        {
            if (cp > 0xFFFF) return 0;
            int segX2 = U16(sub + 6);
            int segCount = segX2 / 2;
            int endO = sub + 14;
            int startO = endO + segX2 + 2;
            int deltaO = startO + segX2;
            int rangeO = deltaO + segX2;
            for (int s = 0; s < segCount; s++)
            {
                int end = U16(endO + s * 2);
                if (cp <= end)
                {
                    int start = U16(startO + s * 2);
                    if (cp < start) return 0;
                    short delta = S16(deltaO + s * 2);
                    int rangeOffset = U16(rangeO + s * 2);
                    if (rangeOffset == 0)
                        return (cp + delta) & 0xFFFF;
                    int giO = rangeO + s * 2 + rangeOffset + (cp - start) * 2;
                    int gi = U16(giO);
                    return gi == 0 ? 0 : (gi + delta) & 0xFFFF;
                }
            }
            return 0;
        }

        private int CmapFormat12(int sub, int cp)
        {
            int nGroups = (int)U32(sub + 12);
            int g = sub + 16;
            for (int i = 0; i < nGroups; i++, g += 12)
            {
                uint startC = U32(g);
                uint endC = U32(g + 4);
                uint startGid = U32(g + 8);
                if ((uint)cp >= startC && (uint)cp <= endC)
                    return (int)(startGid + (cp - startC));
            }
            return 0;
        }

        private int LocaOffset(int gid) =>
            _longLoca ? (int)U32((int)_locaOffset + gid * 4)
                      : U16((int)_locaOffset + gid * 2) * 2;

        /// <summary>
        /// Reads a SIMPLE glyph's outline in font design units (y-up). Returns null for empty or
        /// composite glyphs. <paramref name="pixelsPerEm"/> scales coordinates so 1 unit == 1 px.
        /// </summary>
        public GlyphOutline ReadOutline(int gid, int pixelsPerEm)
        {
            Composite = false;
            if (gid < 0 || gid >= _numGlyphs) return null;

            int start = LocaOffset(gid);
            int end = LocaOffset(gid + 1);
            if (end <= start) return null; // empty glyph (e.g. space)

            int g = (int)_glyfOffset + start;
            int numContours = S16(g);
            if (numContours < 0) { Composite = true; return null; } // composite: unsupported in test reader

            double scale = _unitsPerEm > 0 ? (double)pixelsPerEm / _unitsPerEm : 1.0;

            int p = g + 10;
            var endPts = new int[numContours];
            for (int i = 0; i < numContours; i++) { endPts[i] = U16(p); p += 2; }
            int numPoints = endPts[numContours - 1] + 1;

            int instrLen = U16(p); p += 2 + instrLen;

            // Flags (with repeat).
            var flags = new byte[numPoints];
            for (int i = 0; i < numPoints;)
            {
                byte f = _data[p++];
                flags[i++] = f;
                if ((f & 0x08) != 0) // repeat
                {
                    int repeat = _data[p++];
                    for (int r = 0; r < repeat && i < numPoints; r++)
                        flags[i++] = f;
                }
            }

            // X coordinates.
            var xs = new int[numPoints];
            int x = 0;
            for (int i = 0; i < numPoints; i++)
            {
                byte f = flags[i];
                if ((f & 0x02) != 0) // x short
                {
                    int dx = _data[p++];
                    x += ((f & 0x10) != 0) ? dx : -dx;
                }
                else if ((f & 0x10) == 0)
                {
                    x += S16(p); p += 2;
                }
                xs[i] = x;
            }

            // Y coordinates.
            var ys = new int[numPoints];
            int y = 0;
            for (int i = 0; i < numPoints; i++)
            {
                byte f = flags[i];
                if ((f & 0x04) != 0) // y short
                {
                    int dy = _data[p++];
                    y += ((f & 0x20) != 0) ? dy : -dy;
                }
                else if ((f & 0x20) == 0)
                {
                    y += S16(p); p += 2;
                }
                ys[i] = y;
            }

            var outline = new GlyphOutline(numContours) { ReverseFill = false };
            int startPt = 0;
            for (int c = 0; c < numContours; c++)
            {
                int last = endPts[c];
                var contour = new OutlineContour(last - startPt + 1);
                for (int i = startPt; i <= last; i++)
                {
                    bool onCurve = (flags[i] & 0x01) != 0;
                    contour.Add(
                        new OutlinePoint(xs[i] * scale, ys[i] * scale),
                        onCurve ? OutlinePointTag.OnCurve : OutlinePointTag.QuadraticControl);
                }
                outline.Contours.Add(contour);
                startPt = last + 1;
            }

            return outline;
        }
    }
}
