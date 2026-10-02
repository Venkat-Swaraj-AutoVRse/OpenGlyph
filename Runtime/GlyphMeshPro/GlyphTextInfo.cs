// OpenGlyph GlyphMeshPro — TMP_TextInfo-parity container.
//
// Clean-room mirror of the TextMeshPro textInfo surface that Round 1 needs:
// characterCount, lineCount, and per-character / per-line info arrays. Populated
// from OpenGlyph's engine results (PositionedGlyph span + line metrics).
// Field names mirror TMP so migration is a rename. See the parity doc.

using UnityEngine;

namespace OpenGlyph
{
    /// <summary>Per-character layout info. Mirrors the subset of TMP_CharacterInfo
    /// used for layout parity assertions and basic queries.</summary>
    public struct GlyphCharacterInfo
    {
        /// <summary>The source character.</summary>
        public char character;
        /// <summary>Index of the line this character belongs to.</summary>
        public int lineNumber;
        /// <summary>Whether this character is currently rendered (respecting maxVisible*).</summary>
        public bool isVisible;
        /// <summary>Baseline-origin x of the character, in local text space.</summary>
        public float origin;
        /// <summary>Horizontal advance applied after this character.</summary>
        public float xAdvance;
        /// <summary>Top-left corner of the character quad (local space).</summary>
        public Vector2 topLeft;
        /// <summary>Bottom-right corner of the character quad (local space).</summary>
        public Vector2 bottomRight;
    }

    /// <summary>Per-line layout info. Mirrors the subset of TMP_LineInfo used for
    /// layout parity (first/last character index, line extents, start x).</summary>
    public struct GlyphLineInfo
    {
        /// <summary>Index of the first character on the line.</summary>
        public int firstCharacterIndex;
        /// <summary>Index of the last character on the line.</summary>
        public int lastCharacterIndex;
        /// <summary>Number of characters on the line.</summary>
        public int characterCount;
        /// <summary>Line width (sum of advances).</summary>
        public float lineWidth;
        /// <summary>Local-space x where the line's first glyph starts (after alignment).</summary>
        public float startX;
        /// <summary>Baseline y of the line (local space).</summary>
        public float baseline;
        /// <summary>Line height used for this line.</summary>
        public float lineHeight;
    }

    /// <summary>
    /// Layout result container. Mirrors the subset of TextMeshPro's
    /// <c>TMP_TextInfo</c> that Round 1 exposes. Arrays may be larger than the
    /// live count — read <see cref="characterCount"/> / <see cref="lineCount"/>.
    /// </summary>
    public class GlyphTextInfo
    {
        /// <summary>Number of laid-out characters.</summary>
        public int characterCount;
        /// <summary>Number of laid-out lines.</summary>
        public int lineCount;
        /// <summary>Per-character info (length may exceed characterCount).</summary>
        public GlyphCharacterInfo[] characterInfo = System.Array.Empty<GlyphCharacterInfo>();
        /// <summary>Per-line info (length may exceed lineCount).</summary>
        public GlyphLineInfo[] lineInfo = System.Array.Empty<GlyphLineInfo>();

        /// <summary>Resets live counts without discarding backing arrays.</summary>
        public void Clear()
        {
            characterCount = 0;
            lineCount = 0;
        }

        internal void EnsureCharacterCapacity(int n)
        {
            if (characterInfo.Length < n)
                System.Array.Resize(ref characterInfo, Mathf.NextPowerOfTwo(Mathf.Max(n, 8)));
        }

        internal void EnsureLineCapacity(int n)
        {
            if (lineInfo.Length < n)
                System.Array.Resize(ref lineInfo, Mathf.NextPowerOfTwo(Mathf.Max(n, 4)));
        }
    }
}
