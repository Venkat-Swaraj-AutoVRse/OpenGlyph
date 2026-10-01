using System;

namespace LightSide
{
    /// <summary>
    /// Managed, allocation-light reader for the font-matching fields of the OpenType <c>OS/2</c> and
    /// <c>head</c> tables, parsed directly from the TTF/OTF bytes the font asset already holds — no
    /// native export (round-2 decision). Returns <see cref="FaceStyle"/> for a face:
    /// weight ← <c>OS/2.usWeightClass</c>, width ← <c>OS/2.usWidthClass</c>,
    /// italic ← <c>OS/2.fsSelection</c> ITALIC bit (fallback <c>head.macStyle</c> italic bit),
    /// oblique ← <c>OS/2.fsSelection</c> OBLIQUE bit. When <c>OS/2</c> is absent the caller should
    /// fall back to <see cref="FaceStyle.FromStyleName"/>.
    /// </summary>
    public static class OpenTypeStyleReader
    {
        // usWidthClass (1..9) -> percentage of normal, per the OpenType spec.
        private static readonly float[] WidthClassPct = { 100, 50, 62.5f, 75, 87.5f, 100, 112.5f, 125, 150, 200 };

        /// <summary>
        /// Parses <paramref name="fontData"/> and fills <paramref name="style"/>. Returns false when
        /// the bytes are not a parseable sfnt or carry no <c>OS/2</c> table (caller falls back).
        /// </summary>
        public static bool TryRead(byte[] fontData, out FaceStyle style)
        {
            style = FaceStyle.Regular;
            if (fontData == null || fontData.Length < 12) return false;

            try
            {
                uint sfnt = U32(fontData, 0);
                // 0x00010000 (TrueType), 'OTTO' (CFF), 'true', 'typ1'. TTC not handled here.
                bool ttc = sfnt == 0x74746366; // 'ttcf'
                int tableDirOffset = 0;
                if (ttc)
                {
                    // ttcf: numFonts at 8, first offset table pointer at 12.
                    if (fontData.Length < 16) return false;
                    tableDirOffset = (int)U32(fontData, 12);
                }

                if (tableDirOffset + 12 > fontData.Length) return false;
                ushort numTables = U16(fontData, tableDirOffset + 4);
                int rec = tableDirOffset + 12;

                int os2 = -1, head = -1;
                for (int i = 0; i < numTables; i++)
                {
                    int off = rec + i * 16;
                    if (off + 16 > fontData.Length) break;
                    uint tag = U32(fontData, off);
                    int tableOff = (int)U32(fontData, off + 8);
                    if (tag == 0x4F532F32) os2 = tableOff;   // 'OS/2'
                    else if (tag == 0x68656164) head = tableOff; // 'head'
                }

                bool macItalic = false;
                if (head >= 0 && head + 46 <= fontData.Length)
                {
                    ushort macStyle = U16(fontData, head + 44); // head.macStyle
                    macItalic = (macStyle & 0x0002) != 0;       // bit 1 = Italic
                }

                if (os2 < 0 || os2 + 64 > fontData.Length)
                {
                    // No OS/2 — only head.macStyle italic is knowable; let caller use name fallback,
                    // but still report italic if head said so.
                    if (macItalic) { style.style = StyleAxis.Italic; return false; }
                    return false;
                }

                ushort usWeightClass = U16(fontData, os2 + 4);  // OS/2.usWeightClass
                ushort usWidthClass = U16(fontData, os2 + 6);   // OS/2.usWidthClass
                ushort fsSelection = U16(fontData, os2 + 62);   // OS/2.fsSelection

                style.weight = usWeightClass >= 1 && usWeightClass <= 1000 ? usWeightClass : FontStyleSpec.NormalWeight;
                style.width = (usWidthClass >= 1 && usWidthClass <= 9) ? WidthClassPct[usWidthClass] : FontStyleSpec.NormalWidth;

                bool italic = (fsSelection & 0x0001) != 0 || macItalic; // fsSelection bit 0 = ITALIC
                bool oblique = (fsSelection & 0x0200) != 0;             // fsSelection bit 9 = OBLIQUE
                style.style = oblique ? StyleAxis.Oblique : (italic ? StyleAxis.Italic : StyleAxis.Normal);
                style.slant = oblique ? -12f : 0f;

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Authoritative face-style read: OS/2 + head if present, else the style-name heuristic.
        /// </summary>
        public static FaceStyle Derive(byte[] fontData, string styleName)
        {
            if (TryRead(fontData, out var fromTables))
                return fromTables;
            return FaceStyle.FromStyleName(styleName);
        }

        private static ushort U16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
        private static uint U32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
    }
}
