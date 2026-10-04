using System;
using System.Runtime.CompilerServices;

namespace LightSide
{
    // TextMeshPro-parity layout features used by GlyphMeshPro (characterSpacing / wordSpacing,
    // lineSpacing / paragraphSpacing, <indent>/<line-indent>, <font>, smcp small caps, Page overflow).
    // Every input defaults to zero/off, so plain UniText layout is unchanged.
    public sealed partial class TextProcessor
    {
        private static readonly uint SmcpTag = HB.Tag('s', 'm', 'c', 'p');
        private static readonly uint LigaTag = HB.Tag('l', 'i', 'g', 'a');
        private static readonly uint CligTag = HB.Tag('c', 'l', 'i', 'g');

        /// <summary>
        /// TMP parity: shape Latin/Greek/Cyrillic/Common runs with the standard and contextual ligatures
        /// (<c>liga</c>, <c>clig</c>) turned off, as TextMesh Pro does with its default font features
        /// (kerning only). Required ligatures (<c>rlig</c>) and complex scripts are unaffected. Off for plain
        /// UniText; a change requires a first-pass rebuild.
        /// </summary>
        public bool DisableLatinLigatures;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsLigatureFreeScript(UnicodeScript s) =>
            s == UnicodeScript.Latin || s == UnicodeScript.Greek || s == UnicodeScript.Cyrillic ||
            s == UnicodeScript.Common || s == UnicodeScript.Inherited;

        private static int AppendNoLigatureFeatures(int count, TextRange range)
        {
            featureScratch ??= new HBFeature[8];
            if (count + 2 > featureScratch.Length) Array.Resize(ref featureScratch, Math.Max(8, (count + 2) * 2));
            featureScratch[count++] = new HBFeature { tag = LigaTag, value = 0, start = (uint)range.start, end = (uint)range.End };
            featureScratch[count++] = new HBFeature { tag = CligTag, value = 0, start = (uint)range.start, end = (uint)range.End };
            return count;
        }

        [ThreadStatic] private static HBFeature[] featureScratch;

        /// <summary>TEST INSTRUMENTATION: TMP-parity passes that did work (spacing, indent margins).</summary>
        internal static long TmpParityPassCount;

        private float[] indentMarginScratch = Array.Empty<float>();
        private float[] indentJumpScratch = Array.Empty<float>();
        private float[] indentValueScratch = Array.Empty<float>();
        private int[] itemizeFontOverride;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsTmpWhitespace(int cp) =>
            cp == 0x200B || (cp <= 0xFFFF && char.IsWhiteSpace((char)cp));

        /// <summary>
        /// Adds TMP characterSpacing to every character and wordSpacing to whitespace (em/100 of the
        /// shaping font size). Applied once per character, after the cluster's last glyph, so marks and
        /// conjunct parts never get space inserted between them.
        /// </summary>
        private void ApplyTmpSpacing()
        {
            if (CharacterSpacingEm == 0f && WordSpacingEm == 0f) return;
            System.Threading.Interlocked.Increment(ref TmpParityPassCount);

            var em = buf.shapingFontSize * 0.01f;
            var cs = CharacterSpacingEm * em;
            var ws = WordSpacingEm * em;
            var glyphs = buf.shapedGlyphs.data;
            var runs = buf.shapedRuns.data;
            var runCount = buf.shapedRuns.count;
            var cps = buf.codepoints.data;
            var cpCount = buf.codepoints.count;

            for (var r = 0; r < runCount; r++)
            {
                ref var run = ref runs[r];
                var end = run.glyphStart + run.glyphCount;
                var width = 0f;
                for (var g = run.glyphStart; g < end; g++)
                {
                    var cl = glyphs[g].cluster;
                    var lastOfCluster = g + 1 >= end || glyphs[g + 1].cluster != cl;
                    if (lastOfCluster && (uint)cl < (uint)cpCount)
                    {
                        glyphs[g].advanceX += cs;
                        if (ws != 0f && IsTmpWhitespace(cps[cl])) glyphs[g].advanceX += ws;
                    }
                    width += glyphs[g].advanceX;
                }
                run.width = width;
            }
        }

        /// <summary>True when the line ends with a paragraph break (TMP: U+000A or U+2029).</summary>
        private bool LineEndsParagraph(in TextLine line)
        {
            var idx = line.range.End - 1;
            if ((uint)idx >= (uint)buf.codepoints.count) return false;
            var cp = buf.codepoints.data[idx];
            return cp == '\n' || cp == 0x2029;
        }

        /// <summary>
        /// Folds the indent spans into per-codepoint line-start margins and mid-line pen jumps, in
        /// shaping units. Nested spans: the innermost (latest-starting) span wins inside its range, as
        /// with TMP's indent stack. List margins (startMargins) combine with the indent by max.
        /// </summary>
        private void BuildIndentMargins(int cpCount, float glyphScale, float layoutWidth,
            out Span<float> margins, out ReadOnlySpan<float> jumps)
        {
            System.Threading.Interlocked.Increment(ref TmpParityPassCount);
            if (indentMarginScratch.Length < cpCount)
            {
                var n = Math.Max(cpCount, indentMarginScratch.Length * 2);
                indentMarginScratch = new float[n];
                indentJumpScratch = new float[n];
                indentValueScratch = new float[n];
            }

            var baseMargins = buf.startMargins.data;
            var m = indentMarginScratch;
            var j = indentJumpScratch;
            var v = indentValueScratch;
            for (var c = 0; c < cpCount; c++)
            {
                m[c] = c < baseMargins.Length ? baseMargins[c] : 0f;
                j[c] = -1f;
                v[c] = -1f;
            }

            var cps = buf.codepoints.data;
            if (glyphScale <= 0f) glyphScale = 1f;

            // <indent>: innermost wins -> apply in ascending start order, later spans overwrite.
            indentSpans.Sort(static (a, b) => a.start.CompareTo(b.start));
            for (var i = 0; i < indentSpans.Count; i++)
            {
                var span = indentSpans[i];
                var value = span.unit switch
                {
                    1 => span.value * buf.shapingFontSize,
                    2 => layoutWidth > 0f && layoutWidth < TextProcessSettings.FloatMax * 0.5f
                        ? layoutWidth * span.value * 0.01f : 0f,
                    _ => span.value / glyphScale
                };
                var s = Math.Max(0, span.start);
                var e = Math.Min(cpCount, span.end);
                if (s >= cpCount) continue;

                if (span.lineIndent)
                {
                    // First line of each paragraph inside the span (and the line the tag starts).
                    m[s] += value;
                    for (var c = s + 1; c < e; c++)
                        if (cps[c - 1] == '\n' || cps[c - 1] == 0x2029) m[c] += value;
                    continue;
                }

                for (var c = s; c < e; c++) v[c] = value;
                j[s] = value;
            }

            for (var c = 0; c < cpCount; c++)
                if (v[c] >= 0f && v[c] > m[c]) m[c] = v[c];

            margins = m.AsSpan(0, cpCount);
            jumps = j.AsSpan(0, cpCount);
        }

        /// <summary>
        /// Builds the HarfBuzz feature list for one run from the per-codepoint smcp flags (contiguous
        /// flagged codepoints become one ranged feature). Returns the number of features written.
        /// </summary>
        private static int BuildSmallCapsFeatures(byte[] flags, TextRange range)
        {
            if (flags == null) return 0;
            var count = 0;
            var end = Math.Min(range.End, flags.Length);
            var i = range.start;
            while (i < end)
            {
                if (flags[i] == 0) { i++; continue; }
                var s = i;
                while (i < end && flags[i] != 0) i++;
                featureScratch ??= new HBFeature[8];
                if (count >= featureScratch.Length) Array.Resize(ref featureScratch, featureScratch.Length * 2);
                featureScratch[count++] = new HBFeature { tag = SmcpTag, value = 1, start = (uint)s, end = (uint)i };
            }
            return count;
        }

        /// <summary><c>&lt;font&gt;</c> span override for a cluster: the span font when it has the glyph.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryGetSpanFont(Span<int> cpSpan, int start, UniTextFontProvider fp, out int fontId)
        {
            fontId = 0;
            var table = itemizeFontOverride;
            if (table == null || (uint)start >= (uint)table.Length) return false;
            var idx = table[start];
            if (idx <= 0 || idx > spanFonts.Count) return false;
            var font = spanFonts[idx - 1];
            if (font == null || Shaper.GetGlyphIndex(font, (uint)cpSpan[start]) == 0) return false;
            fontId = UniTextFontProvider.GetFontId(font);
            fp.RegisterFontAsset(fontId, font);
            return fontId != 0;
        }

        /// <summary>
        /// Groups lines into pages that fit <paramref name="maxHeight"/> and returns the first line and
        /// line count of the 1-based <paramref name="page"/> (clamped), plus its layout height.
        /// </summary>
        private void ResolvePage(in TextProcessSettings settings, int page, out int firstLine, out int count,
            out float layoutHeight, out float firstLineHeight, out float lastLineHeight)
        {
            var lineCount = buf.lines.count;
            firstLine = 0;
            count = lineCount;
            layoutHeight = cachedRawHeight;
            firstLineHeight = cachedEffectiveFirstLineHeight;
            lastLineHeight = cachedEffectiveLastLineHeight;
            PageCount = 1;

            var maxHeight = settings.MaxHeight;
            if (lineCount == 0 || !(maxHeight > 0f) || maxHeight >= TextProcessSettings.FloatMax) return;

            var target = Math.Max(1, page) - 1;
            var start = 0;
            var pageIndex = 0;
            while (start < lineCount)
            {
                var n = FitLinesFrom(start, settings, out var h, out var fh, out var lh);
                if (pageIndex == target || start + n >= lineCount)
                {
                    // The requested page, or the last page when the request is past the end (clamped).
                    if (pageIndex <= target)
                    {
                        firstLine = start; count = n; layoutHeight = h; firstLineHeight = fh; lastLineHeight = lh;
                    }
                }
                pageIndex++;
                start += n;
            }
            PageCount = pageIndex;
        }

        /// <summary>How many lines starting at <paramref name="startLine"/> fit the layout height (at least
        /// one), with the raw height of exactly those lines.</summary>
        private int FitLinesFrom(int startLine, in TextProcessSettings settings, out float layoutHeight,
            out float firstLineHeight, out float lastLineHeight)
        {
            var lineCount = buf.lines.count;
            var advances = buf.perLineAdvances.data;
            var capHeight = fontProvider?.GetCapHeight(settings.fontSize) ?? 0f;
            var baseHeight = cachedMainAscender - cachedMainDescender;
            firstLineHeight = startLine == 0 ? cachedEffectiveFirstLineHeight
                : EffectiveLineHeight(startLine, settings.LineSpacing);
            layoutHeight = baseHeight;
            lastLineHeight = firstLineHeight;

            var visible = 1;
            var accumulated = 0f;
            for (var k = 1; startLine + k <= lineCount; k++)
            {
                var lastIdx = startLine + k - 1;
                var raw = baseHeight + accumulated;
                var lastH = lastIdx == lineCount - 1
                    ? cachedEffectiveLastLineHeight
                    : EffectiveLineHeight(lastIdx, settings.LineSpacing);
                var trim = TextLayout.ComputeTrimAmount(cachedMainAscender, cachedMainDescender, capHeight,
                    settings.OverEdge, settings.UnderEdge, settings.LeadingDistribution, firstLineHeight, lastH);

                if (k > 1 && raw - trim > settings.MaxHeight + OverflowEpsilon) break;

                visible = k;
                layoutHeight = raw;
                lastLineHeight = lastH;
                if (lastIdx < lineCount - 1) accumulated += advances[lastIdx];
            }
            return visible;
        }
    }
}
