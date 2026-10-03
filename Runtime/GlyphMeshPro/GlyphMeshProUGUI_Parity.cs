// OpenGlyph GlyphMeshProUGUI — Round 3: the TMP properties that used to be stored-only now drive the
// engine (spacing, margins, rich-text off, vertex gradient, maxVisible*, Page / Linked overflow).
//
// How each one reaches the engine:
//   characterSpacing / wordSpacing / lineSpacing / paragraphSpacing -> TextProcessor TMP-parity inputs
//     (em/100 of the font size, TMP units), pushed in ConfigureTextProcessor before every rebuild.
//   margin            -> UniText.LayoutMargins (insets the layout + mesh rect, pads preferred size).
//   richText = false  -> the raw run is wrapped in a literal <og-raw> span (NoParseParseRule).
//   colorGradient     -> per-glyph OnGlyph colour pass (runs after <color>, multiplies like TMP).
//   maxVisible*       -> per-cluster ClusterHidden mask on the mesh generator: hidden glyphs emit no
//                        geometry, and a change only regenerates the mesh (no reshape, no relayout).
//   Page              -> TextProcessor.PageToDisplay (only the page's lines are laid out).
//   Linked            -> Truncate here; the overflowing text goes to linkedTextComponent with its
//                        firstVisibleCharacter set to the first character that did not fit.
//
// Clean-room: TMP semantics only (units/visibility rules read from the TMP docs/source behaviour);
// no TMP code copied. See Documentation/GlyphMeshPro-Parity.md.

using System;
using UnityEngine;
using LightSide;

namespace OpenGlyph
{
    public partial class GlyphMeshProUGUI
    {
        [SerializeField] private int m_pageToDisplay = 1;
        [SerializeField] private GlyphMeshProUGUI m_linkedTextComponent;
        [SerializeField] private int m_firstVisibleCharacter;

        private byte[] m_hiddenMask = Array.Empty<byte>();
        private Action m_gradientHandler;

        // =====================================================================
        // Page / Linked overflow
        // =====================================================================

        /// <summary>Mirrors <c>TMP_Text.pageToDisplay</c> (1-based). Used when <see cref="overflowMode"/> is
        /// <see cref="TextOverflowModes.Page"/>: the lines are grouped into pages that fit the text area and
        /// only this page is shown. Out-of-range values clamp to the first/last page.</summary>
        public int pageToDisplay
        {
            get => m_pageToDisplay;
            set
            {
                if (m_pageToDisplay == value) return;
                m_pageToDisplay = value;
                PushProcessorInputs();
                SetDirty(DirtyFlags.Alignment); // positions only: the lines are unchanged
            }
        }

        /// <summary>Mirrors <c>TMP_Text.linkedTextComponent</c>. With <see cref="overflowMode"/> =
        /// <see cref="TextOverflowModes.Linked"/> the text that does not fit is shown by this component
        /// (which may itself be Linked, forming a chain).</summary>
        public GlyphMeshProUGUI linkedTextComponent
        {
            get => m_linkedTextComponent;
            set
            {
                if (m_linkedTextComponent == value) return;
                if (m_linkedTextComponent != null && m_linkedTextComponent != this) m_linkedTextComponent.ClearLinked();
                m_linkedTextComponent = value == this ? null : value;
                SetDirty(DirtyFlags.Layout);
            }
        }

        /// <summary>Mirrors <c>TMP_Text.firstVisibleCharacter</c>: characters before this index are not laid
        /// out. Set by the source component of a linked chain.</summary>
        public int firstVisibleCharacter
        {
            get => m_firstVisibleCharacter;
            set
            {
                value = Mathf.Max(0, value);
                if (m_firstVisibleCharacter == value) return;
                m_firstVisibleCharacter = value;
                PushProcessorInputs();
                SetDirty(DirtyFlags.Layout);
            }
        }

        /// <summary>Index of the first character that did not fit (Truncate/Ellipsis/Linked/Page), or -1.</summary>
        public int firstOverflowCharacterIndex => TextProcessor != null ? TextProcessor.FirstOverflowCodepoint : -1;

        /// <summary>Mirrors <c>TMP_Text.isTextOverflowing</c>.</summary>
        public bool isTextOverflowing => firstOverflowCharacterIndex >= 0;

        private void ClearLinked()
        {
            if (!string.IsNullOrEmpty(m_rawText)) text = string.Empty;
            firstVisibleCharacter = 0;
        }

        // =====================================================================
        // Engine hooks
        // =====================================================================

        protected override Vector4 LayoutMargins => m_margin;

        protected override void ConfigureTextProcessor(TextProcessor processor)
        {
            processor.CharacterSpacingEm = m_characterSpacing;
            processor.WordSpacingEm = m_wordSpacing;
            processor.LineSpacingEm = m_lineSpacing;
            processor.ParagraphSpacingEm = m_paragraphSpacing;
            processor.FirstVisibleCodepoint = Mathf.Max(0, m_firstVisibleCharacter);
            processor.PageToDisplay = m_overflowMode == TextOverflowModes.Page ? Mathf.Max(1, m_pageToDisplay) : 0;
        }

        /// <summary>Pushes the TMP layout inputs onto the live processor now (the next rebuild re-pushes).</summary>
        private void PushProcessorInputs()
        {
            if (TextProcessor != null) ConfigureTextProcessor(TextProcessor);
        }

        protected override void OnBeforeGenerateMeshData(UniTextMeshGenerator generator)
        {
            generator.ClusterHidden = ComputeHiddenMask() ? m_hiddenMask : null;

            // Vertex gradient: (re)subscribe LAST so it runs after <color> and multiplies the final colour.
            m_gradientHandler ??= OnGradientGlyph;
            generator.OnGlyph -= m_gradientHandler;
            if (m_enableVertexGradient) generator.OnGlyph += m_gradientHandler;
        }

        protected override void OnAfterMeshApplied()
        {
            if (m_overflowMode != TextOverflowModes.Linked) return;
            var linked = m_linkedTextComponent;
            if (linked == null || linked == this) return;

            var overflow = TextProcessor != null ? TextProcessor.FirstOverflowCodepoint : -1;
            if (overflow < 0)
            {
                // Everything fits: the chain shows nothing (TMP clears the linked components).
                if (!string.IsNullOrEmpty(linked.m_rawText)) linked.text = string.Empty;
                return;
            }
            if (linked.m_rawText != m_rawText) linked.text = m_rawText;
            linked.firstVisibleCharacter = overflow;
        }

        // =====================================================================
        // maxVisibleCharacters / Words / Lines (TMP visibility rules)
        // =====================================================================

        private bool HasVisibilityLimit =>
            m_maxVisibleCharacters != int.MaxValue || m_maxVisibleWords != int.MaxValue || m_maxVisibleLines != int.MaxValue;

        /// <summary>
        /// Fills <see cref="m_hiddenMask"/> per codepoint with TMP's rule: character i is visible when
        /// <c>i &lt; maxVisibleCharacters</c>, the number of words completed before it is
        /// <c>&lt; maxVisibleWords</c>, and its line index is <c>&lt; maxVisibleLines</c>. Returns false (no
        /// mask needed) when no limit is set. Pure data work: safe on a worker thread.
        /// </summary>
        private bool ComputeHiddenMask()
        {
            if (!HasVisibilityLimit) return false;
            var buf = Buffers;
            if (buf == null) return false;
            var cpCount = buf.codepoints.count;
            if (cpCount == 0) return false;
            if (m_hiddenMask.Length < cpCount) m_hiddenMask = new byte[Mathf.NextPowerOfTwo(cpCount)];
            var mask = m_hiddenMask;
            var cps = buf.codepoints.data;

            // Line index per codepoint (codepoints in no laid-out line keep a huge index).
            var lines = buf.lines.data;
            var lineCount = buf.lines.count;
            var lineIdx = 0;
            var lineEnd = lineCount > 0 ? lines[0].range.End : int.MaxValue;
            var lineStart = lineCount > 0 ? lines[0].range.start : int.MaxValue;

            var wordCount = 0;
            var inWord = false;
            for (var i = 0; i < cpCount; i++)
            {
                while (lineIdx < lineCount && i >= lineEnd)
                {
                    lineIdx++;
                    if (lineIdx < lineCount) { lineStart = lines[lineIdx].range.start; lineEnd = lines[lineIdx].range.End; }
                }
                var line = lineIdx < lineCount && i >= lineStart ? lineIdx : int.MaxValue;

                var visible = i < m_maxVisibleCharacters && wordCount < m_maxVisibleWords && line < m_maxVisibleLines;
                mask[i] = visible ? (byte)0 : (byte)1;

                // TMP word tracking (after the visibility test, as in TMP).
                var cp = cps[i];
                var c = cp <= 0xFFFF ? (char)cp : '\0';
                var wordChar = (cp <= 0xFFFF && char.IsLetterOrDigit(c)) || cp == 0x2D || cp == 0xAD || cp == 0x2010 || cp == 0x2011;
                if (wordChar)
                {
                    inWord = true;
                    if (i == cpCount - 1) wordCount++;
                }
                else if (inWord || (i == 0 && (!char.IsPunctuation(c) || char.IsWhiteSpace(c) || cp == 0x200B || i == cpCount - 1)))
                {
                    var apostropheInWord = i > 0 && i < cpCount - 1 && (cp == '\'' || cp == 0x2019)
                                           && IsLetterOrDigitCp(cps[i - 1]) && IsLetterOrDigitCp(cps[i + 1]);
                    if (!apostropheInWord) { inWord = false; wordCount++; }
                }
            }
            for (var i = cpCount; i < mask.Length; i++) mask[i] = 1;
            return true;
        }

        private static bool IsLetterOrDigitCp(int cp) => cp <= 0xFFFF && char.IsLetterOrDigit((char)cp);

        /// <summary>TMP word count over the clean text (same rules as the visibility pass).</summary>
        private static int CountWords(UniTextBuffers buf)
        {
            var n = buf.codepoints.count;
            var cps = buf.codepoints.data;
            var words = 0;
            var inWord = false;
            for (var i = 0; i < n; i++)
            {
                var cp = cps[i];
                var c = cp <= 0xFFFF ? (char)cp : '\0';
                var wordChar = (cp <= 0xFFFF && char.IsLetterOrDigit(c)) || cp == 0x2D || cp == 0xAD || cp == 0x2010 || cp == 0x2011;
                if (wordChar) { inWord = true; if (i == n - 1) words++; }
                else if (inWord) { inWord = false; words++; }
            }
            return words;
        }

        // =====================================================================
        // Vertex gradient (TMP VertexGradient: per character quad, multiplied with the vertex colour)
        // =====================================================================

        private void OnGradientGlyph()
        {
            var gen = UniTextMeshGenerator.Current;
            if (gen == null || gen.font == null || gen.font.IsColor) return;
            var b = gen.vertexCount - 4;
            if (b < 0) return;
            var cols = gen.Colors;
            var g = m_colorGradient;
            // Generator vertex order: 0 = bottom-left, 1 = top-left, 2 = top-right, 3 = bottom-right.
            cols[b] = Mul(cols[b], g.bottomLeft);
            cols[b + 1] = Mul(cols[b + 1], g.topLeft);
            cols[b + 2] = Mul(cols[b + 2], g.topRight);
            cols[b + 3] = Mul(cols[b + 3], g.bottomRight);
        }

        private static Color32 Mul(Color32 a, Color b) => new Color32(
            (byte)Mathf.Clamp(Mathf.RoundToInt(a.r * b.r), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(a.g * b.g), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(a.b * b.b), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(a.a * b.a), 0, 255));
    }
}
