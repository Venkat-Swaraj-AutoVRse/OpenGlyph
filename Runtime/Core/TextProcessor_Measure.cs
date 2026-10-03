using System;

namespace LightSide
{
    // Content measurement (min-/max-content width, height for a width) that never touches the
    // processor's cached lines, positioned glyphs or line heights: line breaking for a query width runs
    // into scratch buffers owned by this file.
    public sealed partial class TextProcessor
    {
        private TextLine[] measureLines = new TextLine[8];
        private ShapedRun[] measureRuns = new ShapedRun[8];
        private float[] measureAdvances = new float[8];

        /// <summary>
        /// The narrowest width the text can be wrapped to without breaking inside a word, at
        /// <paramref name="fontSize"/>: the widest segment between two line-break opportunities, measured
        /// the way the line breaker measures it (a breaking space at the end of the segment counts, unless
        /// trailing spaces hang), plus that line's start margin. Without word wrap this is the
        /// <see cref="GetPreferredWidth"/> (lines only break at hard breaks). Rounded up to whole pixels.
        /// Requires a valid first pass; returns 0 otherwise.
        /// </summary>
        public float GetMinContentWidth(float fontSize, bool wordWrap)
        {
            if (!hasValidFirstPassData) return 0f;
            if (!wordWrap) return GetPreferredWidth(fontSize);

            var cpCount = buf.codepoints.count;
            var widths = buf.cpWidths.data;
            var breaks = buf.breakOpportunities.data;
            var margins = buf.startMargins.data;
            var cps = buf.codepoints.data;
            var hang = firstPassTmpJustify;

            var max = 0f;
            var seg = 0f;
            var trailing = 0f;
            var segStart = 0;
            for (var cp = 0; cp < cpCount; cp++)
            {
                var w = widths[cp];
                seg += w;
                if (hang && IsHangingSpaceCp(cps[cp])) trailing += w;
                else if (w != 0f) trailing = 0f;

                var bt = cp + 1 < breaks.Length ? breaks[cp + 1] : LineBreakType.Mandatory;
                if (bt == LineBreakType.Optional || bt == LineBreakType.Mandatory || cp == cpCount - 1)
                {
                    var margin = segStart < margins.Length ? margins[segStart] : 0f;
                    var width = seg - trailing + margin;
                    if (width > max) max = width;
                    seg = 0f;
                    trailing = 0f;
                    segStart = cp + 1;
                }
            }

            return (float)Math.Ceiling(max * buf.GetGlyphScale(fontSize));
        }

        /// <summary>
        /// Height of the text laid out at <paramref name="width"/> (the text-area width) and
        /// <paramref name="fontSize"/>, with the same line breaking, line heights and edge trimming as the
        /// real layout. Does not change the processor's cached lines or glyph positions.
        /// </summary>
        public float MeasureHeightForWidth(float width, float fontSize, bool wordWrap, float lineSpacing = 0f,
            TextOverEdge overEdge = TextOverEdge.Ascent, TextUnderEdge underEdge = TextUnderEdge.Descent,
            LeadingDistribution leadingDistribution = LeadingDistribution.HalfLeading)
        {
            if (!hasValidFirstPassData) return 0f;
            var glyphScale = buf.GetGlyphScale(fontSize);
            if (glyphScale <= 0f) return 0f;
            if (!(width >= 0f)) width = 0f;

            var maxWidth = wordWrap ? width / glyphScale : TextProcessSettings.FloatMax;
            var cpCount = buf.codepoints.count;
            var margins = buf.startMargins.data.AsSpan(0, cpCount);
            ReadOnlySpan<float> jumps = default;
            if (indentSpans.Count > 0)
                BuildIndentMargins(cpCount, glyphScale, width / glyphScale, out margins, out jumps);

            var hAlign = firstPassHAlign;
            var widthTolerance =
                (firstPassTmpJustify && (hAlign == HorizontalAlignment.Justified || hAlign == HorizontalAlignment.Flush))
                    ? 1.05f : 1f;

            var lineCount = 0;
            var runCount = 0;
            LineBreaker.BreakLines(
                buf.codepoints.Span,
                buf.shapedRuns.Span,
                buf.shapedGlyphs.Span,
                buf.cpWidths.Span,
                buf.breakOpportunities.Span,
                maxWidth,
                buf.bidiParagraphs.Span,
                ref measureLines, ref lineCount,
                ref measureRuns, ref runCount,
                margins,
                widthTolerance,
                FirstVisibleCodepoint,
                jumps,
                firstPassTmpJustify);

            if (lineCount == 0) return 0f;
            if (measureAdvances.Length < lineCount) measureAdvances = new float[Math.Max(lineCount, measureAdvances.Length * 2)];

            var raw = ComputeLineAdvances(measureLines, lineCount, measureRuns, fontSize, lineSpacing,
                leadingDistribution, measureAdvances, out var firstH, out var lastH,
                out var asc, out var desc, out _);
            var capHeight = fontProvider?.GetCapHeight(fontSize) ?? 0f;
            var trim = TextLayout.ComputeTrimAmount(asc, desc, capHeight, overEdge, underEdge,
                leadingDistribution, firstH, lastH);
            return raw - trim;
        }

        /// <summary>
        /// Auto Size without word wrap: the size at which the widest hard line fills
        /// <paramref name="width"/>, clamped to [min, max] and snapped down to <paramref name="step"/>.
        /// Pure (no line breaking).
        /// </summary>
        public float GetWidthLimitedFontSize(float minSize, float maxSize, float width, float step)
        {
            if (!hasValidFirstPassData || buf.shapingFontSize <= 0f) return minSize;
            var line = GetMaxLineWidth();
            if (line <= 0f) return maxSize;
            var size = Math.Clamp(width / line * buf.shapingFontSize, minSize, maxSize);
            return step > 0f ? SnapFontSizeDown(size, minSize, maxSize, step) : size;
        }

        private static bool IsHangingSpaceCp(int cp) =>
            cp == ' ' || cp == 0x3000 || (cp >= 0x2000 && cp <= 0x2006) || (cp >= 0x2008 && cp <= 0x200A) || cp == 0x205F;
    }
}
