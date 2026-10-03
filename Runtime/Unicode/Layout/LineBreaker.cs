using System;
using System.Runtime.CompilerServices;

namespace LightSide
{
    /// <summary>
    /// Performs word wrapping by breaking shaped text into lines based on available width.
    /// </summary>
    /// <remarks>
    /// Uses break opportunities from <see cref="LineBreakAlgorithm"/> to determine where
    /// lines can be split. Handles BiDi reordering of runs within each line according to
    /// the Unicode Bidirectional Algorithm (UAX #9).
    /// </remarks>
    /// <seealso cref="LineBreakAlgorithm"/>
    /// <seealso cref="TextLine"/>
    internal sealed class LineBreaker
    {
        private TextLine[] tempLines;
        private int tempLineCount;
        private ShapedRun[] tempOrderedRuns;
        private int tempOrderedRunCount;
        private int searchStartRunIdx;

        public void BreakLines(
            ReadOnlySpan<int> codepoints,
            ReadOnlySpan<ShapedRun> runs,
            ReadOnlySpan<ShapedGlyph> glyphs,
            ReadOnlySpan<float> cpWidths,
            ReadOnlySpan<LineBreakType> breakTypes,
            float maxWidth,
            ReadOnlySpan<BidiParagraph> paragraphs,
            ref TextLine[] linesOut,
            ref int lineCount,
            ref ShapedRun[] orderedRunsOut,
            ref int orderedRunCount,
            ReadOnlySpan<float> startMargins,
            float widthTolerance = 1f)
        {
            tempLines = linesOut;
            tempLineCount = 0;
            tempOrderedRuns = orderedRunsOut;
            tempOrderedRunCount = 0;

            if (runs.IsEmpty)
            {
                lineCount = 0;
                orderedRunCount = 0;
                return;
            }

            WrapLines(codepoints, runs, glyphs, cpWidths, breakTypes, maxWidth, startMargins, widthTolerance);
            ReorderRunsPerLine(paragraphs);

            linesOut = tempLines;
            orderedRunsOut = tempOrderedRuns;
            lineCount = tempLineCount;
            orderedRunCount = tempOrderedRunCount;
        }

        /// <summary>
        /// Gets the break type after the specified codepoint index.
        /// </summary>
        /// <remarks>
        /// breakTypes[i+1] represents the break type between codepoint[i] and codepoint[i+1].
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static LineBreakType GetBreakTypeAfter(ReadOnlySpan<LineBreakType> breakTypes, int index)
        {
            var breakIndex = index + 1;
            return (uint)breakIndex < (uint)breakTypes.Length ? breakTypes[breakIndex] : LineBreakType.None;
        }

        private void WrapLines(
            ReadOnlySpan<int> codepoints,
            ReadOnlySpan<ShapedRun> runs,
            ReadOnlySpan<ShapedGlyph> glyphs,
            ReadOnlySpan<float> cpWidths,
            ReadOnlySpan<LineBreakType> breakTypes,
            float maxWidth,
            ReadOnlySpan<float> startMargins,
            float widthTolerance = 1f)
        {
            searchStartRunIdx = 0;
            ResetRunClusterOrder(runs.Length);

            var cpCount = codepoints.Length;

            // TMP parity: in justified/flush modes a line may EXCEED the box by a small factor
            // (TMP uses 1.05) before it wraps, so a word that nearly fits stays on the line and is
            // pulled back by inter-word justification — rather than wrapping to the next line. For
            // all other alignments widthTolerance is 1 (identical behaviour to before). Infinite
            // widths stay infinite. See Documentation/GlyphMeshPro-Parity.md (justification).
            var tolerance = widthTolerance > 0f ? widthTolerance : 1f;
            var toleranceWidth = float.IsInfinity(maxWidth) ? maxWidth : maxWidth * tolerance;

            var lineStartCp = 0;
            float lineWidth = 0;
            var lastBreakCp = -1;
            float widthAtLastBreak = 0;

            var rawMargin = (uint)lineStartCp < (uint)startMargins.Length ? startMargins[lineStartCp] : 0f;
            var effectiveMaxWidth = toleranceWidth - rawMargin;

            for (var cpIdx = 0; cpIdx < cpCount; cpIdx++)
            {
                lineWidth += cpWidths[cpIdx];

                var breakType = GetBreakTypeAfter(breakTypes, cpIdx);

                while (lineWidth > effectiveMaxWidth)
                    if (lastBreakCp >= 0 && lastBreakCp >= lineStartCp)
                    {
                        CreateLineFromCodepoints(runs, glyphs, lineStartCp, lastBreakCp, rawMargin);
                        lineStartCp = lastBreakCp + 1;
                        lineWidth -= widthAtLastBreak;
                        lastBreakCp = -1;
                        widthAtLastBreak = 0;
                        rawMargin = (uint)lineStartCp < (uint)startMargins.Length ? startMargins[lineStartCp] : 0f;
                        effectiveMaxWidth = toleranceWidth - rawMargin;
                    }
                    else if (cpIdx > lineStartCp)
                    {
                        CreateLineFromCodepoints(runs, glyphs, lineStartCp, cpIdx - 1, rawMargin);
                        lineStartCp = cpIdx;
                        lineWidth = cpWidths[cpIdx];
                        lastBreakCp = -1;
                        widthAtLastBreak = 0;
                        rawMargin = (uint)lineStartCp < (uint)startMargins.Length ? startMargins[lineStartCp] : 0f;
                        effectiveMaxWidth = toleranceWidth - rawMargin;
                    }
                    else
                    {
                        break;
                    }

                if (breakType == LineBreakType.Mandatory)
                {
                    CreateLineFromCodepoints(runs, glyphs, lineStartCp, cpIdx, rawMargin);
                    lineStartCp = cpIdx + 1;
                    lineWidth = 0;
                    lastBreakCp = -1;
                    widthAtLastBreak = 0;
                    rawMargin = (uint)lineStartCp < (uint)startMargins.Length ? startMargins[lineStartCp] : 0f;
                    effectiveMaxWidth = toleranceWidth - rawMargin;
                    continue;
                }

                if (breakType == LineBreakType.Optional)
                {
                    lastBreakCp = cpIdx;
                    widthAtLastBreak = lineWidth;
                }
            }

            if (lineStartCp < cpCount)
                CreateLineFromCodepoints(runs, glyphs, lineStartCp, cpCount - 1, rawMargin);
        }

        private void CreateLineFromCodepoints(
            ReadOnlySpan<ShapedRun> runs,
            ReadOnlySpan<ShapedGlyph> glyphs,
            int startCp, int endCp, float startMargin = 0f)
        {
            if (startCp > endCp) return;

            var lineRunStart = tempOrderedRunCount;
            var lineRunCount = 0;

            for (var runIdx = searchStartRunIdx; runIdx < runs.Length; runIdx++)
            {
                var run = runs[runIdx];
                var runStart = run.range.start;
                var runEnd = run.range.End - 1;

                if (runEnd < startCp)
                {
                    searchStartRunIdx = runIdx + 1;
                    continue;
                }

                if (runStart > endCp)
                    break;

                int glyphFirst, glyphLast;
                FindGlyphRangeInLine(glyphs, run, runIdx, startCp, endCp, out glyphFirst, out glyphLast);

                if (glyphFirst < 0) continue;

                var glyphCount = glyphLast - glyphFirst + 1;

                float partialWidth = 0;
                for (var g = glyphFirst; g <= glyphLast; g++) partialWidth += glyphs[run.glyphStart + g].advanceX;

                EnsureOrderedRunCapacity(tempOrderedRunCount + 1);
                tempOrderedRuns[tempOrderedRunCount++] = new ShapedRun
                {
                    range = run.range,
                    glyphStart = run.glyphStart + glyphFirst,
                    glyphCount = glyphCount,
                    width = partialWidth,
                    direction = run.direction,
                    bidiLevel = run.bidiLevel,
                    fontId = run.fontId,
                    variationKey = run.variationKey,
                    styleSpec = run.styleSpec,
                    realBold = run.realBold,
                    realItalic = run.realItalic
                };
                lineRunCount++;
            }

            float actualLineWidth = 0;
            for (var i = lineRunStart; i < tempOrderedRunCount; i++) actualLineWidth += tempOrderedRuns[i].width;

            EnsureLineCapacity(tempLineCount + 1);
            tempLines[tempLineCount++] = new TextLine
            {
                range = new TextRange(startCp, endCp - startCp + 1),
                runStart = lineRunStart,
                runCount = lineRunCount,
                width = actualLineWidth,
                startMargin = startMargin
            };
        }

        // Per-run cluster order, computed lazily once per BreakLines call (see FindGlyphRangeInLine).
        private const byte OrderUnknown = 0, OrderAscending = 1, OrderDescending = 2, OrderUnordered = 3;
        private byte[] runClusterOrder;

        /// <summary>
        /// Finds the first/last glyph (relative to the run) whose cluster lies in [startCp, endCp].
        /// </summary>
        /// <remarks>
        /// This used to scan every glyph of the run for every line the run touches. A single long run
        /// (one font/script paragraph) touches every line, so wrapping was O(lines × glyphs) =
        /// O(n²) in text length. Shaped clusters are monotonic within a run (ascending for LTR,
        /// descending for RTL), so the in-range glyphs are one contiguous block found by binary
        /// search. A run whose clusters are not monotonic falls back to the original full scan, so
        /// the result is identical in every case.
        /// </remarks>
        private void FindGlyphRangeInLine(ReadOnlySpan<ShapedGlyph> glyphs, in ShapedRun run, int runIdx,
            int startCp, int endCp, out int glyphFirst, out int glyphLast)
        {
            glyphFirst = -1;
            glyphLast = -1;
            var count = run.glyphCount;
            if (count <= 0) return;
            var runGlyphs = glyphs.Slice(run.glyphStart, count);

            var order = runClusterOrder[runIdx];
            if (order == OrderUnknown)
                runClusterOrder[runIdx] = order = ClassifyClusterOrder(runGlyphs);

            if (order == OrderAscending)
            {
                // First glyph with cluster >= startCp, last glyph with cluster <= endCp.
                var first = LowerBoundAscending(runGlyphs, startCp);
                var last = LowerBoundAscending(runGlyphs, endCp + 1) - 1;
                if (first <= last) { glyphFirst = first; glyphLast = last; }
                return;
            }

            if (order == OrderDescending)
            {
                // First glyph with cluster <= endCp, last glyph with cluster >= startCp.
                var first = FirstAtOrBelowDescending(runGlyphs, endCp);
                var last = FirstAtOrBelowDescending(runGlyphs, startCp - 1) - 1;
                if (first <= last) { glyphFirst = first; glyphLast = last; }
                return;
            }

            for (var g = 0; g < count; g++)
            {
                var cpIdx = runGlyphs[g].cluster;
                if (cpIdx >= startCp && cpIdx <= endCp)
                {
                    if (glyphFirst < 0) glyphFirst = g;
                    glyphLast = g;
                }
            }
        }

        private static byte ClassifyClusterOrder(ReadOnlySpan<ShapedGlyph> runGlyphs)
        {
            bool asc = true, desc = true;
            for (var g = 1; g < runGlyphs.Length && (asc || desc); g++)
            {
                var prev = runGlyphs[g - 1].cluster;
                var cur = runGlyphs[g].cluster;
                if (cur < prev) asc = false;
                if (cur > prev) desc = false;
            }
            return asc ? OrderAscending : desc ? OrderDescending : OrderUnordered;
        }

        /// <summary>Index of the first glyph with cluster &gt;= value (clusters non-decreasing).</summary>
        private static int LowerBoundAscending(ReadOnlySpan<ShapedGlyph> runGlyphs, int value)
        {
            int lo = 0, hi = runGlyphs.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (runGlyphs[mid].cluster < value) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>Index of the first glyph with cluster &lt;= value (clusters non-increasing).</summary>
        private static int FirstAtOrBelowDescending(ReadOnlySpan<ShapedGlyph> runGlyphs, int value)
        {
            int lo = 0, hi = runGlyphs.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (runGlyphs[mid].cluster > value) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private void ResetRunClusterOrder(int runCount)
        {
            if (runClusterOrder == null || runClusterOrder.Length < runCount)
                runClusterOrder = new byte[Math.Max(runCount, runClusterOrder == null ? 64 : runClusterOrder.Length * 2)];
            else
                Array.Clear(runClusterOrder, 0, runCount);
        }

        private void ReorderRunsPerLine(ReadOnlySpan<BidiParagraph> paragraphs)
        {
            for (var i = 0; i < tempLineCount; i++)
            {
                var line = tempLines[i];

                var paragraphBaseLevel = FindParagraphBaseLevel(paragraphs, line.range.start);

                ReorderRunsInLine(line.runStart, line.runCount, paragraphBaseLevel);

                line.paragraphBaseLevel = paragraphBaseLevel;
                tempLines[i] = line;
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte FindParagraphBaseLevel(ReadOnlySpan<BidiParagraph> paragraphs, int codepointIndex)
        {
            if (paragraphs.IsEmpty)
                return 0;

            if (paragraphs.Length == 1)
                return paragraphs[0].baseLevel;

            for (var i = 0; i < paragraphs.Length; i++)
            {
                var para = paragraphs[i];
                if (codepointIndex >= para.startIndex && codepointIndex <= para.endIndex)
                    return para.baseLevel;
            }

            return paragraphs[0].baseLevel;
        }

        private void ReorderRunsInLine(int start, int count, byte paragraphBaseLevel)
        {
            if (count <= 1) return;

            var maxLevel = paragraphBaseLevel;
            var minLevel = paragraphBaseLevel;

            for (var i = 0; i < count; i++)
            {
                var level = tempOrderedRuns[start + i].bidiLevel;
                if (level > maxLevel) maxLevel = level;
                if (level < minLevel) minLevel = level;
            }

            var lowestOddLevel = (minLevel & 1) == 1 ? minLevel : (byte)(minLevel + 1);
            if (lowestOddLevel > maxLevel) return;

            for (var level = maxLevel; level >= lowestOddLevel; level--)
            {
                var runStart = -1;

                for (var i = 0; i <= count; i++)
                {
                    var inSequence = i < count && tempOrderedRuns[start + i].bidiLevel >= level;

                    if (inSequence && runStart < 0)
                    {
                        runStart = i;
                    }
                    else if (!inSequence && runStart >= 0)
                    {
                        ReverseRuns(start + runStart, i - runStart);
                        runStart = -1;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReverseRuns(int start, int count)
        {
            var arr = tempOrderedRuns;
            var end = start + count - 1;
            while (start < end)
            {
                (arr[start], arr[end]) = (arr[end], arr[start]);
                start++;
                end--;
            }
        }

        private void EnsureLineCapacity(int required)
        {
            if (tempLines != null && tempLines.Length >= required) return;

            var newSize = Math.Max(required, tempLines?.Length * 2 ?? 128);
            var newBuffer = UniTextArrayPool<TextLine>.Rent(newSize);

            if (tempLines != null)
            {
                tempLines.AsSpan(0, tempLineCount).CopyTo(newBuffer);
                UniTextArrayPool<TextLine>.Return(tempLines);
            }

            tempLines = newBuffer;
        }

        private void EnsureOrderedRunCapacity(int required)
        {
            if (tempOrderedRuns != null && tempOrderedRuns.Length >= required) return;

            var newSize = Math.Max(required, tempOrderedRuns?.Length * 2 ?? 512);
            var newBuffer = UniTextArrayPool<ShapedRun>.Rent(newSize);

            if (tempOrderedRuns != null)
            {
                tempOrderedRuns.AsSpan(0, tempOrderedRunCount).CopyTo(newBuffer);
                UniTextArrayPool<ShapedRun>.Return(tempOrderedRuns);
            }

            tempOrderedRuns = newBuffer;
        }
    }

}
