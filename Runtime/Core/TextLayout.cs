using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Configuration for text layout and positioning.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="TextLayout"/> to control how text is positioned within the available bounds.
    /// Includes settings for maximum dimensions, spacing, and alignment.
    /// </remarks>
    public struct LayoutSettings
    {
        /// <summary>Maximum width for text layout. Use <see cref="TextProcessSettings.FloatMax"/> for unlimited.</summary>
        public float maxWidth;

        /// <summary>Maximum height for text layout. Use <see cref="TextProcessSettings.FloatMax"/> for unlimited.</summary>
        public float maxHeight;

        /// <summary>Additional spacing between lines (can be negative).</summary>
        public float lineSpacing;

        /// <summary>Fallback line height when font metrics are unavailable.</summary>
        public float defaultLineHeight;

        /// <summary>Horizontal text alignment within the layout bounds.</summary>
        public HorizontalAlignment horizontalAlignment;

        /// <summary>Vertical text alignment within the layout bounds.</summary>
        public VerticalAlignment verticalAlignment;

        /// <summary>Top edge metric for text box trimming.</summary>
        public TextOverEdge overEdge;

        /// <summary>Bottom edge metric for text box trimming.</summary>
        public TextUnderEdge underEdge;

        /// <summary>How extra leading from line-height is distributed relative to the content area.</summary>
        public LeadingDistribution leadingDistribution;

        /// <summary>
        /// Opt-in TextMeshPro-compatible justification. When FALSE (the default, and the behaviour
        /// plain <c>UniText</c> keeps), Justified/Flush wrap at the exact box width and spread slack
        /// across inter-word whitespace only — the pre-Round-2.1 behaviour. When TRUE (set only by the
        /// TMP-parity components <c>GlyphMeshProUGUI</c>/<c>GlyphMeshPro</c>), the line-breaker allows a
        /// 5% overrun before wrapping and the layout splits slack between word AND character spacing
        /// (TMP's wordWrappingRatios), so GlyphMeshPro matches TMP without changing any existing plain
        /// UniText layout.
        /// </summary>
        public bool tmpJustification;

        /// <summary>
        /// Opt-in PHYSICAL horizontal alignment (TextMeshPro semantics). When FALSE (the default, and
        /// the behaviour plain <c>UniText</c> keeps), <see cref="HorizontalAlignment.Left"/> and
        /// <see cref="HorizontalAlignment.Right"/> are paragraph-relative START / END edges: an RTL
        /// paragraph aligned Left renders flush RIGHT. When TRUE (set only by the TMP-parity components
        /// <c>GlyphMeshProUGUI</c>/<c>GlyphMeshPro</c>), Left/Right are the physical left/right edges of
        /// the rect for every paragraph, whatever its direction (Left/Right are swapped per RTL
        /// paragraph). Center/Justified/Flush are unaffected.
        /// </summary>
        public bool physicalAlignment;

        /// <summary>
        /// Gets the default layout settings with unlimited dimensions and top-left alignment.
        /// </summary>
        public static LayoutSettings Default => new()
        {
            maxWidth = TextProcessSettings.FloatMax,
            maxHeight = TextProcessSettings.FloatMax,
            lineSpacing = 0,
            defaultLineHeight = 20,
            horizontalAlignment = HorizontalAlignment.Left,
            verticalAlignment = VerticalAlignment.Top,
            overEdge = TextOverEdge.Ascent,
            underEdge = TextUnderEdge.Descent,
            leadingDistribution = LeadingDistribution.HalfLeading,
            tmpJustification = false,
            physicalAlignment = false
        };
    }


    /// <summary>
    /// Positions glyphs within the layout bounds based on line breaking results and alignment settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the final positioning stage of the text processing pipeline. It takes the output
    /// from <see cref="LineBreaker"/> (lines and runs) and produces <see cref="PositionedGlyph"/>
    /// data with final X/Y coordinates.
    /// </para>
    /// <para>
    /// Handles:
    /// <list type="bullet">
    /// <item>Horizontal alignment (left, center, right) with RTL awareness</item>
    /// <item>Vertical alignment (top, middle, bottom)</item>
    /// <item>Line spacing and margins</item>
    /// <item>Glyph scaling based on font size</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <seealso cref="TextProcessor"/>
    /// <seealso cref="LayoutSettings"/>
    public sealed class TextLayout
    {
        /// <summary>
        /// Delegate for modifying line height during layout.
        /// </summary>
        /// <param name="lineIndex">Zero-based index of the current line.</param>
        /// <param name="lineStartCluster">First codepoint cluster on this line.</param>
        /// <param name="lineEndCluster">One past the last codepoint cluster on this line.</param>
        /// <param name="lineAdvance">The Y advance for this line. Modify to change spacing.</param>
        public delegate void LineHeightDelegate(int lineIndex, int lineStartCluster, int lineEndCluster, ref float lineAdvance);

        private LayoutSettings settings;

        private float fontAscender;
        private float fontDescender;
        private float fontLineHeight;
        private float fontCapHeight;
        private float glyphScale = 1f;
        private float effectiveFirstLineHeight;
        private float effectiveLastLineHeight;

        /// <summary>
        /// Initializes a new instance of the <see cref="TextLayout"/> class with default settings.
        /// </summary>
        public TextLayout()
        {
            settings = LayoutSettings.Default;
        }

        /// <summary>
        /// Sets font metrics used for line height and baseline calculations.
        /// </summary>
        /// <param name="ascender">Distance from baseline to top of tallest glyph.</param>
        /// <param name="descender">Distance from baseline to bottom of lowest glyph (typically negative).</param>
        /// <param name="lineHeight">Total line height from the font metrics.</param>
        /// <param name="glyphScaleFactor">Scale factor applied to all glyph positions (default 1.0).</param>
        /// <param name="capHeight">Cap height for visual vertical centering (0 to skip correction).</param>
        public void SetFontMetrics(float ascender, float descender, float lineHeight, float glyphScaleFactor = 1f, float capHeight = 0f)
        {
            fontAscender = ascender;
            fontDescender = descender;
            fontLineHeight = lineHeight;
            fontCapHeight = capHeight;
            glyphScale = glyphScaleFactor;
        }

        /// <summary>
        /// Sets the layout settings controlling dimensions and alignment.
        /// </summary>
        /// <param name="newSettings">The new layout settings to apply.</param>
        public void SetLayoutSettings(LayoutSettings newSettings)
        {
            settings = newSettings;
        }

        /// <summary>
        /// Sets the effective line heights after modifier callbacks, used for half-leading calculation.
        /// </summary>
        /// <param name="firstLineHeight">Effective height of the first line (0 = use base metrics).</param>
        /// <param name="lastLineHeight">Effective height of the last line (0 = use base metrics).</param>
        public void SetEffectiveLineHeights(float firstLineHeight, float lastLineHeight)
        {
            effectiveFirstLineHeight = firstLineHeight;
            effectiveLastLineHeight = lastLineHeight;
        }

        /// <summary>
        /// Positions all glyphs from the line breaking results into final screen coordinates.
        /// </summary>
        /// <param name="lines">The lines produced by line breaking.</param>
        /// <param name="runs">The shaped runs referenced by lines.</param>
        /// <param name="glyphs">The shaped glyphs referenced by runs.</param>
        /// <param name="perLineAdvances">Pre-computed Y advances for each line (from TextProcessor).</param>
        /// <param name="totalHeight">Pre-computed total text height (from TextProcessor).</param>
        /// <param name="result">Output array to receive positioned glyphs.</param>
        /// <param name="glyphCount">Returns the number of positioned glyphs written.</param>
        /// <param name="width">Returns the maximum line width encountered.</param>
        /// <param name="height">Returns the total text height.</param>
        /// <remarks>
        /// <para>
        /// The method iterates through all lines, applying horizontal alignment per-line
        /// and accounting for RTL paragraphs. Vertical positioning starts from the top
        /// and advances downward by line height plus spacing.
        /// </para>
        /// <para>
        /// Each glyph's final position combines the line's X offset, the glyph's advance
        /// within the run, and any glyph-specific offsets from shaping (e.g., diacritics).
        /// </para>
        /// </remarks>
        public void Layout(
            ReadOnlySpan<TextLine> lines,
            ReadOnlySpan<ShapedRun> runs,
            ReadOnlySpan<ShapedGlyph> glyphs,
            ReadOnlySpan<float> perLineAdvances,
            float totalHeight,
            PositionedGlyph[] result,
            ref int glyphCount,
            out float width,
            out float height,
            ReadOnlySpan<int> codepoints = default,
            ReadOnlySpan<byte> lineAlignments = default)
        {
            glyphCount = 0;
            width = 0;
            height = 0;

            var lineCount = lines.Length;
            if (lineCount == 0)
                return;

            var computedLineHeight = fontLineHeight;
            if (computedLineHeight <= 0)
                computedLineHeight = fontAscender - fontDescender;
            if (computedLineHeight <= 0)
                computedLineHeight = settings.defaultLineHeight;

            var ascender = fontAscender;
            if (ascender <= 0) ascender = computedLineHeight * 0.8f;

            var contentArea = ascender - fontDescender;
            var firstLeading = MathF.Max(0, effectiveFirstLineHeight - contentArea);
            var topLeading = settings.leadingDistribution switch
            {
                LeadingDistribution.LeadingAbove => firstLeading,
                LeadingDistribution.LeadingBelow => 0f,
                _ => firstLeading * 0.5f
            };

            float topMetric = settings.overEdge switch
            {
                TextOverEdge.CapHeight when fontCapHeight > 0 => fontCapHeight,
                TextOverEdge.HalfLeading => ascender + topLeading,
                _ => ascender
            };

            var trimAmount = ComputeTrimAmount(ascender, fontDescender,
                fontCapHeight, settings.overEdge, settings.underEdge,
                settings.leadingDistribution,
                effectiveFirstLineHeight, effectiveLastLineHeight);

            var effectiveHeight = totalHeight - trimAmount;

            var y = ComputeTextStartY(effectiveHeight, settings) + topMetric;
            float maxLineWidth = 0;

            var availableWidth = settings.maxWidth;
            var baseHAlign = settings.horizontalAlignment;
            var physicalAlign = settings.physicalAlignment;
            var hasFiniteWidth = !float.IsInfinity(availableWidth) && availableWidth > 0;

            for (var i = 0; i < lineCount; i++)
            {
                ref readonly var line = ref lines[i];
                var runStart = line.runStart;
                var runCount = line.runCount;
                var runEnd = runStart + runCount;

                var lineWidth = line.width * glyphScale;

                float x;
                var isRtlLine = (line.paragraphBaseLevel & 1) == 1;
                // Per-line alignment override (TMP <align=...>): the first codepoint of the line carrying
                // an override decides the line's alignment; lines without one use the base alignment.
                var lineHAlign = baseHAlign;
                if (!lineAlignments.IsEmpty)
                {
                    var end = Math.Min(line.range.End, lineAlignments.Length);
                    for (var c = line.range.start; c < end; c++)
                    {
                        var v = lineAlignments[c];
                        if (v == 0) continue;
                        lineHAlign = (HorizontalAlignment)(v - 1);
                        break;
                    }
                }
                // Left/Right are paragraph-relative (start/end) in the engine. Physical alignment
                // (TMP parity) swaps them for RTL paragraphs so Left always means the rect's left edge.
                var hAlign = physicalAlign && isRtlLine ? ToPhysical(lineHAlign) : lineHAlign;
                if (hasFiniteWidth)
                    x = ComputeLineStartX(lineWidth, isRtlLine, availableWidth, hAlign);
                else
                    x = 0;

                if (line.startMargin > 0 && hasFiniteWidth)
                {
                    var margin = line.startMargin * glyphScale;
                    if (isRtlLine)
                    {
                        if (hAlign == HorizontalAlignment.Left)
                            x -= margin;
                        else if (hAlign == HorizontalAlignment.Center) x = (availableWidth - margin - lineWidth) * 0.5f;
                    }
                    else
                    {
                        if (hAlign == HorizontalAlignment.Left)
                            x += margin;
                        else if (hAlign == HorizontalAlignment.Center)
                            x = margin + (availableWidth - margin - lineWidth) * 0.5f;
                    }
                }

                // ---- Justification (Justified/Flush) --------------------------------------------
                // TWO behaviours, selected by settings.tmpJustification:
                //  * OFF (plain UniText, the default): legacy — spread the slack across inter-word
                //    whitespace ONLY, no overrun (the LineBreaker used tolerance 1.0 so lineWidth <=
                //    availableWidth), no character spacing, no compression. Byte-identical to main.
                //  * ON (GlyphMeshPro, opt-in TMP parity): split the slack between word spacing and
                //    character spacing by wordWrappingRatios (TMP 0.4) — spaces take 60%, visible
                //    CLUSTERS take 40%. Slack may be negative (a 5%-overrun line compresses back). A
                //    floor keeps a space >= 70% of its advance. The character share is applied at
                //    GRAPHEME-CLUSTER boundaries (after the last glyph of each cluster), never between a
                //    base and its marks/conjunct parts, so Devanagari/Thai/Khmer/Myanmar marks keep
                //    their offset. On a line containing a JOINING script (Arabic, Syriac, N'Ko,
                //    Mongolian, ...) character spreading is skipped entirely — the whole slack goes to
                //    spaces (with the floor) — because inserting advance between joined letters would
                //    break the cursive join. RTL lines are already excluded below (noted for clarity).
                const float WordWrappingRatio = 0.4f;   // TMP m_wordWrappingRatios default
                const float MinSpaceFraction = 0.70f;   // a space keeps at least 70% of its advance
                bool tmpJustify = settings.tmpJustification;
                float justifyPerSpace = 0f;    // extra advance added at each whitespace glyph
                float justifyPerCluster = 0f;  // extra advance added after each visible cluster (TMP only)
                if ((hAlign == HorizontalAlignment.Justified || hAlign == HorizontalAlignment.Flush)
                    && hasFiniteWidth && !isRtlLine) // RTL lines never justify here (base direction skip)
                {
                    bool isLastLineOfParagraph =
                        i == lineCount - 1 || EndsAtHardBreak(in line, codepoints);
                    bool justifyThisLine = hAlign == HorizontalAlignment.Flush || !isLastLineOfParagraph;
                    if (justifyThisLine)
                    {
                        if (!tmpJustify)
                        {
                            // Legacy: word-gap-only, positive slack only (no overrun on this path).
                            int gaps = CountWhitespaceGapsLegacy(in line, runs, glyphs, codepoints);
                            if (gaps > 0 && lineWidth < availableWidth)
                                justifyPerSpace = (availableWidth - lineWidth) / gaps;
                        }
                        else
                        {
                            CountJustifyTargets(in line, runs, glyphs, codepoints, glyphScale,
                                out int spaces, out int visibleClusters, out float minSpaceAdvance);
                            bool joining = LineHasJoiningScript(in line, codepoints);
                            float gap = availableWidth - lineWidth; // + = spread, - = compress
                            // Joining scripts: no character spreading — all slack to spaces.
                            float ratio = joining ? 0f : WordWrappingRatio;
                            if (spaces > 0 || visibleClusters > 0)
                            {
                                float spaceShare = spaces > 0 ? gap * (1f - ratio) : 0f;
                                float clusterShare = visibleClusters > 0 ? gap * ratio : 0f;
                                if (visibleClusters == 0 || joining) spaceShare = gap;
                                if (spaces == 0) clusterShare = gap;

                                justifyPerSpace = spaces > 0 ? spaceShare / spaces : 0f;
                                justifyPerCluster = (!joining && visibleClusters > 0)
                                    ? clusterShare / visibleClusters : 0f;

                                // Floor: never shrink a space below MinSpaceFraction of its advance.
                                if (justifyPerSpace < 0f && spaces > 0)
                                {
                                    float maxShrink = minSpaceAdvance * (1f - MinSpaceFraction);
                                    if (-justifyPerSpace > maxShrink) justifyPerSpace = -maxShrink;
                                }
                            }
                        }
                    }
                }

                // Track cluster boundaries so the per-cluster character share is applied once, after the
                // LAST glyph of each visible cluster (never between a base and its marks).
                int prevClusterApplied = int.MinValue;

                // TMP <indent> opened mid-line: the pen jumps to the indent position (relative to the
                // line's content origin, i.e. after its start margin). LTR lines only.
                var jumpCp = isRtlLine ? 0 : line.indentJumpCp;
                var lineOriginX = x;

                for (var r = runStart; r < runEnd; r++)
                {
                    ref readonly var run = ref runs[r];
                    var glyphStart = run.glyphStart;
                    var glyphLen = run.glyphCount;

                    var fontId = run.fontId;
                    var glyphEnd = glyphStart + glyphLen;

                    // Phase 2: a glyph carries only a blittable run index. -1 = plain run (no variation,
                    // no real-face styling) so the mesh/modifier fast path skips the run lookup entirely
                    // and the struct stays within 4 bytes of pre-phase2. realBold/realItalic and the
                    // VariationKey are read from the run via this index when it is >= 0.
                    var runRef = (!run.variationKey.IsNone || run.realBold || run.realItalic) ? r : -1;

                    for (var g = glyphStart; g < glyphEnd; g++)
                    {
                        ref readonly var glyph = ref glyphs[g];
                        if (jumpCp > 0 && glyph.cluster == jumpCp)
                        {
                            x = lineOriginX + (line.indentJumpX - line.startMargin) * glyphScale;
                            jumpCp = 0;
                        }
                        var glyphX = x + glyph.offsetX * glyphScale;
                        var advanceScaled = glyph.advanceX * glyphScale;

                        var boundsTop = y - ascender;
                        var boundsBottom = y - fontDescender;

                        result[glyphCount++] = new PositionedGlyph
                        {
                            glyphId = glyph.glyphId,
                            cluster = glyph.cluster,
                            x = glyphX,
                            y = y - glyph.offsetY * glyphScale,
                            fontId = fontId,
                            orderedRunIndex = runRef,
                            shapedGlyphIndex = g,
                            left = x,
                            right = x + advanceScaled,
                            top = boundsTop,
                            bottom = boundsBottom
                        };
                        x += advanceScaled;
                        // Apply the justification offsets:
                        //  * whitespace glyph  -> the word-spacing share (justifyPerSpace).
                        //  * visible cluster    -> the character-spacing share (justifyPerCluster),
                        //    added ONCE after the cluster's LAST glyph. A cluster's last glyph is the
                        //    one whose successor (next glyph in this run, or the run's end) starts a
                        //    different cluster — so marks/conjunct parts that share the base's cluster
                        //    never get an advance inserted between them.
                        if (IsWhitespaceCluster(glyph.cluster, codepoints))
                        {
                            x += justifyPerSpace;
                        }
                        else if (justifyPerCluster != 0f)
                        {
                            bool lastGlyphOfCluster =
                                (g + 1 >= glyphEnd) || glyphs[g + 1].cluster != glyph.cluster;
                            if (lastGlyphOfCluster && glyph.cluster != prevClusterApplied)
                            {
                                x += justifyPerCluster;
                                prevClusterApplied = glyph.cluster;
                            }
                        }
                    }
                }

                if (lineWidth > maxLineWidth)
                    maxLineWidth = lineWidth;

                if (i < perLineAdvances.Length)
                    y += perLineAdvances[i];
            }

            width = maxLineWidth;
            height = effectiveHeight;
        }

        /// <summary>
        /// Computes the total height trim based on edge metrics and leading distribution.
        /// </summary>
        /// <param name="ascender">Font ascender value.</param>
        /// <param name="descender">Font descender value (typically negative).</param>
        /// <param name="capHeight">Font cap height (0 if unavailable).</param>
        /// <param name="overEdge">Top edge metric.</param>
        /// <param name="underEdge">Bottom edge metric.</param>
        /// <param name="distribution">How extra leading is distributed.</param>
        /// <param name="effectiveFirstLineHeight">Effective height of the first line (including modifier adjustments).</param>
        /// <param name="effectiveLastLineHeight">Effective height of the last line (including modifier adjustments).</param>
        /// <returns>The total amount to subtract from raw height to get effective height.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ComputeTrimAmount(
            float ascender, float descender,
            float capHeight, TextOverEdge overEdge, TextUnderEdge underEdge,
            LeadingDistribution distribution,
            float effectiveFirstLineHeight, float effectiveLastLineHeight)
        {
            var contentArea = ascender - descender;
            var firstLeading = MathF.Max(0, effectiveFirstLineHeight - contentArea);
            var lastLeading = MathF.Max(0, effectiveLastLineHeight - contentArea);

            var topLeading = distribution switch
            {
                LeadingDistribution.LeadingAbove => firstLeading,
                LeadingDistribution.LeadingBelow => 0f,
                _ => firstLeading * 0.5f
            };

            var bottomLeading = distribution switch
            {
                LeadingDistribution.LeadingAbove => 0f,
                LeadingDistribution.LeadingBelow => lastLeading,
                _ => lastLeading * 0.5f
            };

            float topTrim = overEdge switch
            {
                TextOverEdge.CapHeight when capHeight > 0 => ascender - capHeight,
                TextOverEdge.HalfLeading => -topLeading,
                _ => 0f
            };

            float bottomTrim = underEdge switch
            {
                TextUnderEdge.Baseline => -descender,
                TextUnderEdge.HalfLeading => -bottomLeading,
                _ => 0f
            };

            return topTrim + bottomTrim;
        }

        /// <summary>Maps a physical Left/Right to the engine's start/end meaning for an RTL paragraph
        /// (physical left = end, physical right = start). Other alignments pass through.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static HorizontalAlignment ToPhysical(HorizontalAlignment alignment) => alignment switch
        {
            HorizontalAlignment.Left => HorizontalAlignment.Right,
            HorizontalAlignment.Right => HorizontalAlignment.Left,
            _ => alignment
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ComputeLineStartX(float lineWidth, bool isRtlLine, float availableWidth,
            HorizontalAlignment alignment)
        {
            return alignment switch
            {
                HorizontalAlignment.Left => isRtlLine ? availableWidth - lineWidth : 0,
                HorizontalAlignment.Right => isRtlLine ? 0 : availableWidth - lineWidth,
                // Justified/Flush start flush with the leading edge; inter-word slack is distributed
                // in the glyph loop so the line fills the width (handled there, not here).
                HorizontalAlignment.Justified => isRtlLine ? availableWidth - lineWidth : 0,
                HorizontalAlignment.Flush => isRtlLine ? availableWidth - lineWidth : 0,
                _ => (availableWidth - lineWidth) * 0.5f
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsWhitespaceCluster(int cluster, ReadOnlySpan<int> codepoints)
        {
            if (codepoints.IsEmpty || (uint)cluster >= (uint)codepoints.Length) return false;
            int cp = codepoints[cluster];
            // Breaking spaces that absorb justification slack. Deliberately excludes NBSP (U+00A0)
            // and newline/tab (not inter-word gaps on a laid-out line).
            return cp == ' ' || cp == 0x2000 || cp == 0x2001 || cp == 0x2002 || cp == 0x2003
                || cp == 0x2004 || cp == 0x2005 || cp == 0x2006 || cp == 0x2008 || cp == 0x2009
                || cp == 0x205F || cp == 0x3000;
        }

        /// <summary>Counts the TMP-justification targets on a line: inter-word whitespace gaps (one per
        /// whitespace glyph, minus a trailing wrap space) and visible GRAPHEME CLUSTERS (distinct
        /// non-whitespace cluster ids, not glyphs — so a base+marks counts once), plus the smallest
        /// whitespace advance (scaled) used to floor how far a space may compress.</summary>
        private static void CountJustifyTargets(in TextLine line, ReadOnlySpan<ShapedRun> runs,
            ReadOnlySpan<ShapedGlyph> glyphs, ReadOnlySpan<int> codepoints, float glyphScale,
            out int spaces, out int visibleClusters, out float minSpaceAdvance)
        {
            spaces = 0; visibleClusters = 0; minSpaceAdvance = float.MaxValue;
            if (codepoints.IsEmpty) { minSpaceAdvance = 0f; return; }
            int runEnd = line.runStart + line.runCount;
            int lastCountedCluster = int.MinValue;
            for (int r = line.runStart; r < runEnd; r++)
            {
                ref readonly var run = ref runs[r];
                int gEnd = run.glyphStart + run.glyphCount;
                for (int g = run.glyphStart; g < gEnd; g++)
                {
                    int cl = glyphs[g].cluster;
                    if (IsWhitespaceCluster(cl, codepoints))
                    {
                        spaces++;
                        float adv = glyphs[g].advanceX * glyphScale;
                        if (adv < minSpaceAdvance) minSpaceAdvance = adv;
                        lastCountedCluster = int.MinValue; // reset so a new visible cluster is counted
                    }
                    else if (cl != lastCountedCluster)
                    {
                        visibleClusters++;
                        lastCountedCluster = cl;
                    }
                }
            }
            // A trailing space at the line's wrap point should not stretch the visible line end; drop
            // one gap when the line's last glyph is whitespace so the last word still lands at the edge.
            if (spaces > 0 && runEnd > line.runStart)
            {
                ref readonly var lastRun = ref runs[runEnd - 1];
                if (lastRun.glyphCount > 0)
                {
                    int lastG = lastRun.glyphStart + lastRun.glyphCount - 1;
                    if (IsWhitespaceCluster(glyphs[lastG].cluster, codepoints)) spaces--;
                }
            }
            // TMP gives the LAST cluster on the line no trailing advance (it lands at the edge).
            if (visibleClusters > 1) visibleClusters -= 1;
            if (minSpaceAdvance == float.MaxValue) minSpaceAdvance = 0f;
        }

        /// <summary>Legacy (non-TMP) count: inter-word whitespace gaps on a line, excluding a trailing
        /// wrap space. This is the exact pre-Round-2.1 behaviour plain UniText keeps.</summary>
        private static int CountWhitespaceGapsLegacy(in TextLine line, ReadOnlySpan<ShapedRun> runs,
            ReadOnlySpan<ShapedGlyph> glyphs, ReadOnlySpan<int> codepoints)
        {
            if (codepoints.IsEmpty) return 0;
            int gaps = 0;
            int runEnd = line.runStart + line.runCount;
            for (int r = line.runStart; r < runEnd; r++)
            {
                ref readonly var run = ref runs[r];
                int gEnd = run.glyphStart + run.glyphCount;
                for (int g = run.glyphStart; g < gEnd; g++)
                    if (IsWhitespaceCluster(glyphs[g].cluster, codepoints)) gaps++;
            }
            if (gaps > 0 && runEnd > line.runStart)
            {
                ref readonly var lastRun = ref runs[runEnd - 1];
                if (lastRun.glyphCount > 0)
                {
                    int lastG = lastRun.glyphStart + lastRun.glyphCount - 1;
                    if (IsWhitespaceCluster(glyphs[lastG].cluster, codepoints)) gaps--;
                }
            }
            return gaps;
        }

        /// <summary>True when any codepoint on the line belongs to a cursive JOINING script (Arabic,
        /// Syriac, N'Ko, Mongolian, Arabic Presentation Forms, Mandaic, ...). On such a line the
        /// character-spacing share is skipped — inserting advance between joined letters would break the
        /// cursive connection — so all slack goes to the inter-word spaces instead.</summary>
        private static bool LineHasJoiningScript(in TextLine line, ReadOnlySpan<int> codepoints)
        {
            if (codepoints.IsEmpty) return false;
            int start = line.range.start;
            int end = line.range.End;
            for (int i = start; i < end; i++)
            {
                if ((uint)i >= (uint)codepoints.Length) break;
                int cp = codepoints[i];
                // Arabic 0600-06FF, Syriac 0700-074F, Arabic Supplement 0750-077F, Thaana 0780-07BF,
                // N'Ko 07C0-07FF, Mandaic/Samaritan 0800-083F, Arabic Extended-A 08A0-08FF,
                // Mongolian 1800-18AF, Arabic Presentation Forms-A FB50-FDFF and -B FE70-FEFF.
                if ((cp >= 0x0600 && cp <= 0x06FF) || (cp >= 0x0700 && cp <= 0x074F)
                    || (cp >= 0x0750 && cp <= 0x077F) || (cp >= 0x07C0 && cp <= 0x07FF)
                    || (cp >= 0x0800 && cp <= 0x083F) || (cp >= 0x08A0 && cp <= 0x08FF)
                    || (cp >= 0x1800 && cp <= 0x18AF)
                    || (cp >= 0xFB50 && cp <= 0xFDFF) || (cp >= 0xFE70 && cp <= 0xFEFF))
                    return true;
            }
            return false;
        }

        /// <summary>True when the line ends at a hard line break (newline) — i.e. it is the last line
        /// of its paragraph, which Justified (but not Flush) leaves un-justified.</summary>
        private static bool EndsAtHardBreak(in TextLine line, ReadOnlySpan<int> codepoints)
        {
            if (codepoints.IsEmpty) return true;
            int end = line.range.End;
            // Scan the codepoint just before the line's end (and the end position itself) for a newline.
            int idx = end - 1;
            if ((uint)idx < (uint)codepoints.Length)
            {
                int cp = codepoints[idx];
                if (cp == '\n' || cp == '\r' || cp == 0x2028 || cp == 0x2029) return true;
            }
            if ((uint)end < (uint)codepoints.Length)
            {
                int cp = codepoints[end];
                if (cp == '\n' || cp == '\r' || cp == 0x2028 || cp == 0x2029) return true;
            }
            // No newline at/after the line end means a soft wrap — not a paragraph end.
            return end >= codepoints.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ComputeTextStartY(float totalTextHeight, LayoutSettings settings)
        {
            var availableHeight = settings.maxHeight;
            if (float.IsInfinity(availableHeight) || availableHeight <= 0)
                return 0;

            return settings.verticalAlignment switch
            {
                VerticalAlignment.Middle => (availableHeight - totalTextHeight) * 0.5f
                    + (settings.overEdge == TextOverEdge.Ascent && fontCapHeight > 0
                        ? (fontCapHeight - fontAscender - fontDescender) * 0.5f : 0f),
                VerticalAlignment.Bottom => availableHeight - totalTextHeight,
                _ => 0
            };
        }
    }
}
