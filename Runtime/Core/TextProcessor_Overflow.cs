using System;

namespace LightSide
{
    public sealed partial class TextProcessor
    {
        // The ellipsis is shaped as a single U+2026 glyph, or as three '.' glyphs when no font in the stack
        // has U+2026. Its glyphs live in scratch slots just PAST shapedGlyphs.count (never counted), so the
        // first-pass data is never altered.
        internal const int EllipsisSlotCount = 3;
        private const float OverflowEpsilon = 0.01f;
        private const int EllipsisCodepoint = 0x2026;

        private static readonly int[] EllipsisCodepoints = { EllipsisCodepoint };
        private static readonly int[] DotsCodepoints = { '.', '.', '.' };

        private bool ellipsisResolved;
        private int ellipsisFontId;
        private int ellipsisGlyphCount;
        private float ellipsisAdvance; // shaping units
        private ShapedGlyph[] ellipsisGlyphs;

        // Saved state of the last kept line while the ellipsis is spliced in (restored by EndEllipsisOnLine).
        private bool ellipsisActive;
        private int ellipsisLineIndex;
        private int ellipsisRunStart;
        private int ellipsisRunCount;
        private TextLine ellipsisSavedLine;

        /// <summary>
        /// Registers the ellipsis glyphs (U+2026 and the '.' fallback) as virtual codepoints so the atlas
        /// rasterizer prepares them even though no text codepoint produces them. Idempotent.
        /// </summary>
        internal void RegisterOverflowEllipsisGlyphs()
        {
            if (!hasValidFirstPassData) return;

            // Check each codepoint separately: another feature (the <ellipsis> tag) may already have
            // registered U+2026 without the '.' fallback.
            var hasEllipsis = false;
            var hasDot = false;
            var vc = buf.virtualCodepoints;
            for (var i = 0; i < vc.count; i++)
            {
                if (vc.data[i] == EllipsisCodepoint) hasEllipsis = true;
                else if (vc.data[i] == '.') hasDot = true;
            }
            if (hasEllipsis && hasDot) return;

            if (!hasEllipsis) buf.virtualCodepoints.Add(EllipsisCodepoint);
            if (!hasDot) buf.virtualCodepoints.Add('.');
            hasValidGlyphsInAtlas = false;
        }

        /// <summary>
        /// Finds how many leading lines fit in <see cref="TextProcessSettings.MaxHeight"/> (at least one) and the
        /// layout height of exactly those lines. Uses the per-line advances computed for the full text, which
        /// are identical for every line but the last kept one, so no state is recomputed or mutated.
        /// </summary>
        private int FitVisibleLineCount(in TextProcessSettings settings, out float layoutHeight, out float lastLineHeight)
        {
            var lineCount = buf.lines.count;
            layoutHeight = cachedRawHeight;
            lastLineHeight = cachedEffectiveLastLineHeight;

            var maxHeight = settings.MaxHeight;
            if (lineCount <= 1 || !(maxHeight > 0f) || maxHeight >= TextProcessSettings.FloatMax)
                return lineCount;

            var advances = buf.perLineAdvances.data;
            var capHeight = fontProvider?.GetCapHeight(settings.fontSize) ?? 0f;
            var baseHeight = cachedMainAscender - cachedMainDescender;
            var visible = 1;
            var accumulated = 0f; // sum of advances of the lines BEFORE the candidate last line

            for (var k = 1; k <= lineCount; k++)
            {
                var raw = baseHeight + accumulated;
                var lastH = k == lineCount
                    ? cachedEffectiveLastLineHeight
                    : EffectiveLineHeight(k - 1, settings.LineSpacing);
                var trim = TextLayout.ComputeTrimAmount(cachedMainAscender, cachedMainDescender, capHeight,
                    settings.OverEdge, settings.UnderEdge, settings.LeadingDistribution,
                    cachedEffectiveFirstLineHeight, lastH);

                if (k > 1 && raw - trim > maxHeight + OverflowEpsilon) break;

                visible = k;
                layoutHeight = raw; // Layout subtracts the trim itself
                lastLineHeight = lastH;
                if (k < lineCount) accumulated += advances[k - 1];
            }

            return visible;
        }

        private float EffectiveLineHeight(int lineIndex, float lineSpacing)
        {
            var h = cachedMainLineHeight + lineSpacing;
            if (OnCalculateLineHeight != null)
            {
                ref readonly var line = ref buf.lines.data[lineIndex];
                OnCalculateLineHeight.Invoke(lineIndex, line.range.start, line.range.End, ref h);
            }
            return h;
        }

        private void EnsureEllipsisGlyphs()
        {
            if (ellipsisResolved) return;
            ellipsisResolved = true;
            ellipsisGlyphCount = 0;
            ellipsisAdvance = 0f;
            if (fontProvider == null) return;

            ellipsisGlyphs ??= new ShapedGlyph[EllipsisSlotCount];

            var fontId = fontProvider.FindFontForCodepoint(EllipsisCodepoint);
            var result = Shaper.Shape(EllipsisCodepoints, 0, 1, fontProvider, fontId, UnicodeScript.Common,
                TextDirection.LeftToRight);
            if (result.Glyphs.Length != 1 || result.Glyphs[0].glyphId == 0)
            {
                fontId = fontProvider.FindFontForCodepoint('.');
                result = Shaper.Shape(DotsCodepoints, 0, 3, fontProvider, fontId, UnicodeScript.Common,
                    TextDirection.LeftToRight);
                if (result.Glyphs.Length == 0 || result.Glyphs.Length > EllipsisSlotCount) return;
            }

            ellipsisFontId = fontId;
            ellipsisGlyphCount = result.Glyphs.Length;
            for (var i = 0; i < ellipsisGlyphCount; i++)
                ellipsisGlyphs[i] = result.Glyphs[i];
            ellipsisAdvance = result.TotalAdvance;
        }

        private static bool IsOverflowWhitespace(int cp) =>
            cp == ' ' || cp == '\t' || cp == '\n' || cp == '\r' || cp == 0xA0 || cp == 0x85 || cp == 0x2028 ||
            cp == 0x2029 || cp == 0x3000 || (cp >= 0x2000 && cp <= 0x200B);

        /// <summary>
        /// Replaces the logical tail of line <paramref name="lineIndex"/> with an ellipsis for the duration of
        /// one layout call: whole grapheme clusters are removed from the logical end (the array end of an LTR
        /// run, the array start of an RTL run) until the remaining line plus the ellipsis fits the width, then
        /// an ellipsis run is added at the paragraph's trailing visual edge. The line, its runs and the ellipsis
        /// glyph slots are patched in place; <see cref="EndEllipsisOnLine"/> restores them.
        /// </summary>
        private bool BeginEllipsisOnLine(int lineIndex, in TextProcessSettings settings, ref int runsLength,
            ref int glyphsLength)
        {
            EnsureEllipsisGlyphs();
            if (ellipsisGlyphCount == 0 || lineIndex < 0 || lineIndex >= buf.lines.count) return false;

            var glyphScale = buf.GetGlyphScale(settings.fontSize);
            if (glyphScale <= 0f) glyphScale = 1f;

            var lines = buf.lines.data;
            var line = lines[lineIndex];
            var rs = line.runStart;
            var rc = line.runCount;

            buf.orderedRuns.EnsureCapacity(rs + rc + 1);
            buf.overflowRuns.EnsureCapacity(rc + 1);
            var runs = buf.orderedRuns.data;
            Array.Copy(runs, rs, buf.overflowRuns.data, 0, rc + 1);
            ellipsisSavedLine = line;
            ellipsisLineIndex = lineIndex;
            ellipsisRunStart = rs;
            ellipsisRunCount = rc;
            ellipsisActive = true;

            var glyphs = buf.shapedGlyphs.data;
            var codepoints = buf.codepoints.data;
            var cpCount = buf.codepoints.count;
            var breaks = buf.graphemeBreaks.data;
            var breakCount = buf.graphemeBreaks.count;

            var maxWidth = settings.MaxWidth;
            var limited = maxWidth > 0f && maxWidth < TextProcessSettings.FloatMax;
            var available = limited ? maxWidth / glyphScale - line.startMargin - ellipsisAdvance : float.MaxValue;

            var lineWidth = 0f;
            for (var i = 0; i < rc; i++) lineWidth += runs[rs + i].width;

            var cut = -1;
            var keptCluster = -1; // logical-last KEPT glyph: the ellipsis inherits its attributes
            while (true)
            {
                // Logical-last remaining glyph of the line: highest cluster at a run's logical end.
                var bestRun = -1;
                var bestCluster = -1;
                for (var i = 0; i < rc; i++)
                {
                    ref readonly var run = ref runs[rs + i];
                    if (run.glyphCount <= 0) continue;
                    var g = (run.bidiLevel & 1) == 1 ? run.glyphStart : run.glyphStart + run.glyphCount - 1;
                    var c = glyphs[g].cluster;
                    if (c > bestCluster) { bestCluster = c; bestRun = i; }
                }
                if (bestRun < 0) { keptCluster = -1; break; }
                keptCluster = bestCluster;

                var isSpace = (uint)bestCluster < (uint)cpCount && IsOverflowWhitespace(codepoints[bestCluster]);
                if (!isSpace && lineWidth <= available + OverflowEpsilon) break;

                // Never split a grapheme cluster: remove the whole cluster the last glyph belongs to.
                var gs = bestCluster;
                while (gs > 0 && gs < breakCount && !breaks[gs]) gs--;
                var ge = bestCluster + 1;
                while (ge < cpCount && ge < breakCount && !breaks[ge]) ge++;

                ref var target = ref runs[rs + bestRun];
                var rtl = (target.bidiLevel & 1) == 1;
                while (target.glyphCount > 0)
                {
                    var g = rtl ? target.glyphStart : target.glyphStart + target.glyphCount - 1;
                    var c = glyphs[g].cluster;
                    if (c < gs || c >= ge) break;
                    var adv = glyphs[g].advanceX;
                    target.width -= adv;
                    lineWidth -= adv;
                    target.glyphCount--;
                    if (rtl) target.glyphStart++;
                }
                cut = gs;
            }

            // Attribute cluster (link/colour/gradient/underline): the last kept glyph; when nothing is kept,
            // the first removed grapheme (or the line start for an empty line).
            if (keptCluster >= 0) cut = keptCluster;
            else if (cut < 0) cut = line.range.start;
            if (cut >= cpCount) cut = cpCount - 1;
            if (cut < 0) cut = 0;

            // Ellipsis glyph slots just past the counted glyphs.
            var slotBase = buf.shapedGlyphs.count;
            buf.shapedGlyphs.EnsureCapacity(slotBase + EllipsisSlotCount);
            buf.EnsureGlyphCacheCapacity(slotBase + EllipsisSlotCount);
            glyphs = buf.shapedGlyphs.data;
            for (var j = 0; j < ellipsisGlyphCount; j++)
            {
                var sg = ellipsisGlyphs[j];
                sg.cluster = cut;
                glyphs[slotBase + j] = sg;
                buf.glyphDataCache.data[slotBase + j].isValid = false;
            }

            var paragraphRtl = (line.paragraphBaseLevel & 1) == 1;
            var ellipsisRun = new ShapedRun
            {
                range = new TextRange(cut, 0),
                glyphStart = slotBase,
                glyphCount = ellipsisGlyphCount,
                width = ellipsisAdvance,
                direction = paragraphRtl ? TextDirection.RightToLeft : TextDirection.LeftToRight,
                bidiLevel = line.paragraphBaseLevel,
                fontId = ellipsisFontId,
                variationKey = VariationKey.None,
                styleSpec = FontStyleSpec.Normal,
                realBold = false,
                realItalic = false
            };

            if (paragraphRtl)
            {
                Array.Copy(runs, rs, runs, rs + 1, rc);
                runs[rs] = ellipsisRun;
            }
            else
            {
                runs[rs + rc] = ellipsisRun;
            }

            ref var patched = ref lines[lineIndex];
            patched.runCount = rc + 1;
            patched.width = lineWidth + ellipsisAdvance;

            runsLength = Math.Max(runsLength, rs + rc + 1);
            glyphsLength = slotBase + ellipsisGlyphCount;
            return true;
        }

        private void EndEllipsisOnLine()
        {
            if (!ellipsisActive) return;
            ellipsisActive = false;
            Array.Copy(buf.overflowRuns.data, 0, buf.orderedRuns.data, ellipsisRunStart, ellipsisRunCount + 1);
            buf.lines.data[ellipsisLineIndex] = ellipsisSavedLine;
        }
    }
}
