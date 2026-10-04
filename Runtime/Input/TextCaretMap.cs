using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Caret geometry of a laid-out <see cref="UniText"/>: per line, the grapheme boxes in visual order and
    /// the caret stops at their edges. Positions are UTF-16 indices into the laid-out (clean) text; every
    /// position is a grapheme boundary. Coordinates are layout space: x from the left of the text area,
    /// y down from its top (as in <see cref="PositionedGlyph"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>BiDi.</b> A logical position sits at the trailing edge of the grapheme before it
    /// (<c>upstream</c> affinity) or at the leading edge of the grapheme after it (downstream). In a run
    /// of one direction both are the same x; at a direction boundary they are two different places on the
    /// line, so a caret is (position, affinity). Left/Right arrows walk the stops in visual order, so the
    /// caret moves the way the arrow points through mixed Hebrew/Arabic and Latin text.</para>
    /// <para>Ligatures (one glyph for several graphemes, e.g. "fi") are split into equal parts so every
    /// grapheme has a caret stop. Lines without glyphs (empty lines, the line after a trailing newline,
    /// empty text) get their y from the neighbouring lines, or from the font metrics.</para>
    /// </remarks>
    internal sealed class TextCaretMap
    {
        internal struct Line
        {
            public int start, end;     // [start, end) excluding the line's '\n'
            public bool hardBreak;     // ends with '\n'
            public bool rtl;           // paragraph direction
            public float top, bottom, baseline;
            public bool hasGeometry;
            public int boxStart, boxCount;   // visual order
            public int stopStart, stopCount; // visual order
            public float emptyX;
        }

        internal struct Box
        {
            public int start, end;
            public float left, right;
            public bool rtl;
            internal bool tail; // build-time: glyphless ligature tail
        }

        internal struct Stop
        {
            public float x;
            public int pos;
            public bool upstream;
            public bool rightEdge; // the stop is the right edge of its box (else the left edge / empty line)
            public int box;        // index in boxes, -1 for an empty line
        }

        public readonly struct Caret
        {
            public readonly int pos;
            public readonly bool upstream;
            public Caret(int pos, bool upstream) { this.pos = pos; this.upstream = upstream; }
        }

        private readonly List<Line> lines = new();
        private readonly List<Box> boxes = new();
        private readonly List<Stop> stops = new();
        private readonly TextSegments segments = new();
        private int[] cpUtf16 = Array.Empty<int>();
        private float[] gLeft = Array.Empty<float>(), gRight = Array.Empty<float>(), gTop = Array.Empty<float>(),
            gBottom = Array.Empty<float>(), gBase = Array.Empty<float>();
        private sbyte[] gDir = Array.Empty<sbyte>(); // per UTF-16 grapheme start: 0 none, 1 ltr, 2 rtl
        private readonly List<Box> lineScratch = new();

        public string Text { get; private set; } = string.Empty;
        public int LineCount => lines.Count;
        public IReadOnlyList<Line> Lines => lines;
        public IReadOnlyList<Box> Boxes => boxes;
        public IReadOnlyList<Stop> Stops => stops;
        public float LineHeight { get; private set; } = 20f;
        public float Width { get; private set; }
        public float Height { get; private set; }
        /// <summary>True when the map was built from the component's current layout (not synthesised).</summary>
        public bool FromLayout { get; private set; }

        /// <summary>
        /// Builds the map for <paramref name="layoutText"/> as laid out by <paramref name="t"/>. When the
        /// component's layout does not match the text (not rebuilt yet, or empty) the lines are synthesised
        /// from the text and the font metrics.
        /// </summary>
        public void Build(UniText t, string layoutText, Rect area)
        {
            layoutText ??= string.Empty;
            Text = layoutText;
            lines.Clear();
            boxes.Clear();
            stops.Clear();
            Width = area.width;
            Height = area.height;
            segments.Build(layoutText);

            var n = layoutText.Length;
            EnsureCapacity(n + 1);

            // Codepoint -> UTF-16 start.
            var cpCount = 0;
            for (var i = 0; i < n; i++)
            {
                cpUtf16[cpCount++] = i;
                if (char.IsHighSurrogate(layoutText[i]) && i + 1 < n && char.IsLowSurrogate(layoutText[i + 1])) i++;
            }
            cpUtf16[cpCount] = n;

            FontLineMetrics(t, out var asc, out var desc, out var pitch);
            LineHeight = asc - desc > 0 ? asc - desc : pitch;

            var buffers = t != null ? t.Buffers : null;
            var glyphs = t != null ? t.ResultGlyphs : ReadOnlySpan<PositionedGlyph>.Empty;
            var layoutMatches = buffers != null && buffers.codepoints.count == cpCount && cpCount > 0 && buffers.lines.count > 0;
            FromLayout = layoutMatches;

            var baseRtl = t != null && t.BaseDirection == TextDirection.RightToLeft;

            // ---- logical lines ----
            if (layoutMatches)
            {
                for (var i = 0; i < buffers.lines.count; i++)
                {
                    ref readonly var tl = ref buffers.lines.data[i];
                    var s = tl.range.start;
                    var e = Math.Min(tl.range.start + tl.range.length, cpCount);
                    if (s > cpCount) break;
                    var su = cpUtf16[s];
                    var eu = cpUtf16[e];
                    var hard = eu > su && layoutText[eu - 1] == '\n';
                    lines.Add(new Line
                    {
                        start = su, end = hard ? eu - 1 : eu, hardBreak = hard,
                        rtl = (tl.paragraphBaseLevel & 1) == 1, boxStart = 0, boxCount = 0,
                    });
                }
                // A layout that dropped trailing lines (should not happen with Overflow) - cover the rest.
                var covered = lines.Count > 0 ? (lines[lines.Count - 1].hardBreak ? lines[lines.Count - 1].end + 1 : lines[lines.Count - 1].end) : 0;
                if (covered < n) AddSplitLines(layoutText, covered, n, baseRtl);
            }
            else
            {
                AddSplitLines(layoutText, 0, n, baseRtl);
            }
            if (n == 0 || layoutText[n - 1] == '\n' || lines.Count == 0)
            {
                var prevRtl = lines.Count > 0 ? lines[lines.Count - 1].rtl : baseRtl || FirstStrongRtl(layoutText, 0);
                lines.Add(new Line { start = n, end = n, hardBreak = false, rtl = prevRtl });
            }

            // ---- grapheme boxes from the glyphs ----
            for (var i = 0; i <= n; i++) { gDir[i] = 0; }
            if (layoutMatches)
            {
                var runs = buffers.orderedRuns;
                var runCursor = 0;
                for (var gi = 0; gi < glyphs.Length; gi++)
                {
                    ref readonly var g = ref glyphs[gi];
                    if (g.cluster < 0 || g.cluster >= cpCount) continue;
                    var u = segments.Floor(cpUtf16[g.cluster]);
                    var rtl = GlyphRtl(runs, g.shapedGlyphIndex, ref runCursor);
                    if (gDir[u] == 0)
                    {
                        gDir[u] = (sbyte)(rtl ? 2 : 1);
                        gLeft[u] = g.left; gRight[u] = g.right; gTop[u] = g.top; gBottom[u] = g.bottom; gBase[u] = g.y;
                    }
                    else
                    {
                        if (g.left < gLeft[u]) gLeft[u] = g.left;
                        if (g.right > gRight[u]) gRight[u] = g.right;
                    }
                }
            }

            // ---- per line: boxes (split ligatures), geometry ----
            for (var li = 0; li < lines.Count; li++)
            {
                var line = lines[li];
                lineScratch.Clear();
                var lastWithBox = -1;
                var pos = line.start;
                while (pos < line.end)
                {
                    var next = segments.Next(pos);
                    if (gDir[pos] != 0)
                    {
                        lineScratch.Add(new Box { start = pos, end = next, left = gLeft[pos], right = gRight[pos], rtl = gDir[pos] == 2 });
                        lastWithBox = lineScratch.Count - 1;
                        if (!line.hasGeometry)
                        {
                            line.hasGeometry = true;
                            line.top = gTop[pos]; line.bottom = gBottom[pos]; line.baseline = gBase[pos];
                        }
                    }
                    else
                    {
                        // Glyphless: a ligature tail when it follows a box and is not a control/space
                        // (the glyph of the previous grapheme covers it), else zero width.
                        var c = layoutText[pos];
                        var tail = lastWithBox >= 0 && !char.IsControl(c) && !char.IsWhiteSpace(c);
                        lineScratch.Add(new Box { start = pos, end = next, rtl = lastWithBox >= 0 ? lineScratch[lastWithBox].rtl : line.rtl,
                            left = float.NaN, right = float.NaN, tail = tail });
                    }
                    pos = next;
                }
                SplitLigatures(ref line);
                lines[li] = line;
            }

            // ---- y for lines without glyphs ----
            FillLineGeometry(t, asc, desc, pitch, area.height);

            // ---- visual order, stops ----
            for (var li = 0; li < lines.Count; li++)
            {
                var line = lines[li];
                line.emptyX = EmptyLineX(t, line.rtl, area.width);
                CollectLineBoxes(line);
                SortByLeft(lineScratch);
                line.boxStart = boxes.Count;
                line.boxCount = lineScratch.Count;
                for (var i = 0; i < lineScratch.Count; i++) boxes.Add(lineScratch[i]);
                line.stopStart = stops.Count;
                if (line.boxCount == 0)
                {
                    stops.Add(new Stop { x = line.emptyX, pos = line.start, upstream = false, box = -1 });
                }
                else
                {
                    for (var b = line.boxStart; b < line.boxStart + line.boxCount; b++)
                    {
                        var box = boxes[b];
                        stops.Add(new Stop { x = box.left, pos = box.rtl ? box.end : box.start, upstream = box.rtl, rightEdge = false, box = b });
                        stops.Add(new Stop { x = box.right, pos = box.rtl ? box.start : box.end, upstream = !box.rtl, rightEdge = true, box = b });
                    }
                }
                line.stopCount = stops.Count - line.stopStart;
                lines[li] = line;
            }
        }

        // Per-line boxes from the first pass (logical order), before visual sorting.
        private readonly List<Box> pendingBoxes = new();

        /// <summary>
        /// Resolves glyphless graphemes in <see cref="lineScratch"/>: ligature tails share the box of the
        /// grapheme whose glyph covers them (split into equal parts in the run direction); other glyphless
        /// graphemes (controls) are zero width at the logical end of the content before them. Moves the
        /// result to <see cref="pendingBoxes"/> and records the range on the line.
        /// </summary>
        private void SplitLigatures(ref Line line)
        {
            var list = lineScratch;
            var k = 0;
            while (k < list.Count)
            {
                if (float.IsNaN(list[k].left)) { k++; continue; }
                var tails = 0;
                while (k + 1 + tails < list.Count && list[k + 1 + tails].tail) tails++;
                if (tails == 0) { k++; continue; }
                var head = list[k];
                var parts = tails + 1;
                var w = (head.right - head.left) / parts;
                for (var p = 0; p < parts; p++)
                {
                    var b = list[k + p];
                    var idx = head.rtl ? parts - 1 - p : p;
                    b.left = head.left + w * idx;
                    b.right = b.left + w;
                    b.rtl = head.rtl;
                    b.tail = false;
                    list[k + p] = b;
                }
                k += parts;
            }
            var endX = 0f;
            var haveEnd = false;
            for (var i = 0; i < list.Count; i++)
            {
                var b = list[i];
                if (!float.IsNaN(b.left)) { endX = b.rtl ? b.left : b.right; haveEnd = true; continue; }
                b.left = b.right = haveEnd ? endX : float.NaN;
                b.tail = false;
                list[i] = b;
            }
            line.boxStart = pendingBoxes.Count;
            line.boxCount = list.Count;
            for (var i = 0; i < list.Count; i++) pendingBoxes.Add(list[i]);
        }

        private void CollectLineBoxes(Line line)
        {
            lineScratch.Clear();
            for (var i = line.boxStart; i < line.boxStart + line.boxCount; i++)
            {
                var b = pendingBoxes[i];
                if (float.IsNaN(b.left)) { b.left = b.right = line.emptyX; }
                lineScratch.Add(b);
            }
        }
        private void FillLineGeometry(UniText t, float asc, float desc, float pitch, float areaHeight)
        {
            var first = -1; var last = -1;
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].hasGeometry) { if (first < 0) first = i; last = i; }
            if (first >= 0 && last > first)
                pitch = (lines[last].top - lines[first].top) / (last - first);
            else if (first >= 0)
            {
                var h = lines[first].bottom - lines[first].top;
                if (h > 0) pitch = Mathf.Max(pitch, h);
            }
            if (pitch <= 0) pitch = 20f;
            LineHeight = first >= 0 ? lines[first].bottom - lines[first].top : asc - desc;
            if (LineHeight <= 0) LineHeight = pitch;

            if (first < 0)
            {
                var total = pitch * (lines.Count - 1) + LineHeight;
                var top0 = 0f;
                var va = t != null ? t.VerticalAlignment : VerticalAlignment.Top;
                if (va == VerticalAlignment.Middle) top0 = (areaHeight - total) * 0.5f;
                else if (va == VerticalAlignment.Bottom) top0 = areaHeight - total;
                for (var i = 0; i < lines.Count; i++)
                {
                    var l = lines[i];
                    l.top = top0 + pitch * i;
                    l.bottom = l.top + LineHeight;
                    l.baseline = l.top + asc;
                    lines[i] = l;
                }
                return;
            }
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].hasGeometry) continue;
                // Nearest line with geometry.
                var j = -1;
                for (var d = 1; d < lines.Count; d++)
                {
                    if (i - d >= 0 && lines[i - d].hasGeometry) { j = i - d; break; }
                    if (i + d < lines.Count && lines[i + d].hasGeometry) { j = i + d; break; }
                }
                var l = lines[i];
                var r = lines[j];
                l.top = r.top + pitch * (i - j);
                l.bottom = l.top + (r.bottom - r.top);
                l.baseline = l.top + (r.baseline - r.top);
                lines[i] = l;
            }
        }

        private static void FontLineMetrics(UniText t, out float asc, out float desc, out float pitch)
        {
            asc = 16f; desc = -4f; pitch = 24f;
            if (t == null) return;
            var size = t.CurrentFontSize;
            var font = t.MainFont;
            if (font == null) { asc = size * 0.8f; desc = -size * 0.2f; pitch = size * 1.2f; return; }
            var fi = font.FaceInfo;
            var scale = size * font.FontScale / font.UnitsPerEm;
            asc = fi.ascentLine * scale;
            desc = fi.descentLine * scale;
            pitch = fi.lineHeight * scale;
            if (asc - desc <= 0) { asc = size * 0.8f; desc = -size * 0.2f; }
            if (pitch <= 0) pitch = (asc - desc) * 1.2f;
        }

        private float EmptyLineX(UniText t, bool rtl, float width)
        {
            if (t == null) return 0f;
            var ha = t.HorizontalAlignment;
            var physical = t.UsesPhysicalAlignmentForInput;
            switch (ha)
            {
                case HorizontalAlignment.Center: return width * 0.5f;
                case HorizontalAlignment.Right: return physical ? width : rtl ? 0f : width;
                default: return physical ? 0f : rtl ? width : 0f;
            }
        }

        private void AddSplitLines(string s, int from, int to, bool baseRtl)
        {
            var start = from;
            for (var i = from; i <= to; i++)
            {
                if (i == to || s[i] == '\n')
                {
                    if (i == to && start == to && to > from && s[to - 1] == '\n') break;
                    if (i == to && start == to && from == to) break;
                    lines.Add(new Line { start = start, end = i, hardBreak = i < to, rtl = baseRtl || FirstStrongRtl(s, start) });
                    start = i + 1;
                }
            }
        }

        private static bool FirstStrongRtl(string s, int from)
        {
            for (var i = from; i < s.Length && s[i] != '\n'; i++)
            {
                var c = s[i];
                if (c >= 0x0590 && c <= 0x08FF || c >= 0xFB1D && c <= 0xFDFF || c >= 0xFE70 && c <= 0xFEFF) return true;
                if (char.IsLetter(c)) return false;
            }
            return false;
        }

        private static bool GlyphRtl(PooledBuffer<ShapedRun> runs, int shapedIndex, ref int cursor)
        {
            if (runs.count == 0) return false;
            if (cursor >= runs.count) cursor = 0;
            for (var k = 0; k < runs.count; k++)
            {
                var r = (cursor + k) % runs.count;
                ref readonly var run = ref runs.data[r];
                if (shapedIndex >= run.glyphStart && shapedIndex < run.glyphStart + run.glyphCount)
                {
                    cursor = r;
                    return run.direction == TextDirection.RightToLeft;
                }
            }
            return false;
        }

        private static void SortByLeft(List<Box> list)
        {
            for (var i = 1; i < list.Count; i++)
            {
                var v = list[i];
                var j = i - 1;
                while (j >= 0 && (list[j].left > v.left || list[j].left == v.left && list[j].right > v.right)) { list[j + 1] = list[j]; j--; }
                list[j + 1] = v;
            }
        }

        private void EnsureCapacity(int n)
        {
            pendingBoxes.Clear();
            if (gDir.Length >= n && cpUtf16.Length >= n) return;
            var size = Mathf.NextPowerOfTwo(Math.Max(16, n));
            cpUtf16 = new int[size];
            gLeft = new float[size]; gRight = new float[size]; gTop = new float[size]; gBottom = new float[size]; gBase = new float[size];
            gDir = new sbyte[size];
        }

        // =============================================================================================
        // Queries
        // =============================================================================================

        /// <summary>Index of the line the caret is on (a soft line break belongs to the line above when upstream).</summary>
        public int LineOf(int pos, bool upstream)
        {
            var found = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (pos < l.start) break;
                if (pos <= l.end)
                {
                    if (found < 0) found = i;
                    if (!upstream) found = i; // later line wins downstream
                    if (upstream) break;
                }
            }
            if (found < 0) found = pos <= 0 || lines.Count == 0 ? 0 : lines.Count - 1;
            return Mathf.Clamp(found, 0, Math.Max(0, lines.Count - 1));
        }

        /// <summary>The stop index for a caret on its line.</summary>
        public int StopOf(int pos, bool upstream, out int line)
        {
            line = LineOf(pos, upstream);
            if (lines.Count == 0) return -1;
            var l = lines[line];
            int exact = -1, any = -1, nearest = -1, bestDist = int.MaxValue;
            for (var s = l.stopStart; s < l.stopStart + l.stopCount; s++)
            {
                var st = stops[s];
                if (st.pos == pos)
                {
                    if (st.upstream == upstream) { exact = s; break; }
                    if (any < 0) any = s;
                }
                var d = Math.Abs(st.pos - pos);
                if (d < bestDist) { bestDist = d; nearest = s; }
            }
            return exact >= 0 ? exact : any >= 0 ? any : nearest;
        }

        /// <summary>Caret x and the line box (layout space).</summary>
        public bool CaretGeometry(int pos, bool upstream, out float x, out float top, out float bottom, out int line)
        {
            var s = StopOf(pos, upstream, out line);
            if (s < 0) { x = top = bottom = 0; return false; }
            x = stops[s].x;
            top = lines[line].top;
            bottom = lines[line].bottom;
            return true;
        }

        /// <summary>The grapheme box that starts at the caret (for block/underline carets), else a box of default width.</summary>
        public void NextGraphemeBox(int pos, bool upstream, out float left, out float right)
        {
            var s = StopOf(pos, upstream, out var line);
            left = right = s >= 0 ? stops[s].x : 0f;
            var l = lines.Count > 0 ? lines[line] : default;
            for (var b = l.boxStart; b < l.boxStart + l.boxCount; b++)
            {
                if (boxes[b].start != pos) continue;
                left = boxes[b].left; right = boxes[b].right;
                return;
            }
            var w = LineHeight * 0.5f;
            if (l.rtl) left -= w; else right += w;
        }

        private float StopX(int s) => stops[s].x;

        /// <summary>Visual move by one stop group: +1 right, -1 left. Crosses to the adjacent line at a line's end.</summary>
        public Caret MoveVisual(int pos, bool upstream, int dir)
        {
            var s = StopOf(pos, upstream, out var li);
            if (s < 0) return new Caret(pos, upstream);
            var l = lines[li];
            var end = l.stopStart + l.stopCount;
            var x = StopX(s);
            var k = s;
            if (dir > 0)
            {
                while (k < end && Mathf.Abs(StopX(k) - x) < 0.01f) k++;
                if (k >= end) return CrossLine(li, l.rtl ? -1 : +1, pos, upstream);
                var gx = StopX(k);
                var pick = k;
                for (var j = k; j < end && Mathf.Abs(StopX(j) - gx) < 0.01f; j++)
                    if (stops[j].rightEdge) { pick = j; break; }
                return new Caret(stops[pick].pos, stops[pick].upstream);
            }
            else
            {
                while (k >= l.stopStart && Mathf.Abs(StopX(k) - x) < 0.01f) k--;
                if (k < l.stopStart) return CrossLine(li, l.rtl ? +1 : -1, pos, upstream);
                var gx = StopX(k);
                var pick = k;
                for (var j = k; j >= l.stopStart && Mathf.Abs(StopX(j) - gx) < 0.01f; j--)
                    if (!stops[j].rightEdge) { pick = j; break; }
                return new Caret(stops[pick].pos, stops[pick].upstream);
            }
        }

        // Logical neighbour line: +1 = next line's start, -1 = previous line's end (upstream, so a soft
        // wrap shows the caret at the end of the line above).
        private Caret CrossLine(int li, int logicalDir, int pos, bool upstream)
        {
            if (logicalDir > 0)
                return li + 1 < lines.Count ? new Caret(lines[li + 1].start, false) : new Caret(pos, upstream);
            return li > 0 ? new Caret(lines[li - 1].end, true) : new Caret(pos, upstream);
        }
        /// <summary>Line home / end (logical start / end of the visual line).</summary>
        public Caret LineEdge(int pos, bool upstream, bool end)
        {
            var li = LineOf(pos, upstream);
            if (lines.Count == 0) return new Caret(0, false);
            var l = lines[li];
            return end ? new Caret(l.end, !l.hardBreak && li + 1 < lines.Count) : new Caret(l.start, false);
        }

        /// <summary>Caret on line <paramref name="line"/> nearest to <paramref name="x"/>.</summary>
        public Caret AtLineX(int line, float x)
        {
            line = Mathf.Clamp(line, 0, lines.Count - 1);
            var l = lines[line];
            if (l.boxCount == 0) return new Caret(l.start, false);
            // Inside a box: its nearer edge.
            for (var b = l.boxStart; b < l.boxStart + l.boxCount; b++)
            {
                var box = boxes[b];
                if (x < box.left || x > box.right) continue;
                var leftHalf = x < (box.left + box.right) * 0.5f;
                return EdgeCaret(b, leftHalf ? false : true);
            }
            // Outside: nearest stop.
            var best = l.stopStart;
            var bd = float.MaxValue;
            for (var s = l.stopStart; s < l.stopStart + l.stopCount; s++)
            {
                var d = Mathf.Abs(stops[s].x - x);
                if (d < bd) { bd = d; best = s; }
            }
            return new Caret(stops[best].pos, stops[best].upstream);
        }

        private Caret EdgeCaret(int boxIndex, bool rightEdge)
        {
            var box = boxes[boxIndex];
            if (!rightEdge) return new Caret(box.rtl ? box.end : box.start, box.rtl);
            return new Caret(box.rtl ? box.start : box.end, !box.rtl);
        }

        /// <summary>Hit test in layout space: the nearest caret position.</summary>
        public Caret HitTest(float x, float y)
        {
            if (lines.Count == 0) return new Caret(0, false);
            var line = 0;
            var best = float.MaxValue;
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                float d = y < l.top ? l.top - y : y > l.bottom ? y - l.bottom : 0f;
                if (d < best - 0.001f) { best = d; line = i; }
                if (d == 0f) break;
            }
            return AtLineX(line, x);
        }

        /// <summary>Selection rectangles (layout space) for [a, b).</summary>
        public void SelectionRects(int a, int b, List<Rect> output, bool includeNewlines = true)
        {
            output.Clear();
            if (b < a) (a, b) = (b, a);
            if (a == b) return;
            for (var li = 0; li < lines.Count; li++)
            {
                var l = lines[li];
                if (l.end < a && !(l.hardBreak && l.end >= a)) continue;
                if (l.start >= b) break;
                var inRun = false;
                float rx0 = 0, rx1 = 0;
                for (var bi = l.boxStart; bi < l.boxStart + l.boxCount; bi++)
                {
                    var box = boxes[bi];
                    var sel = box.start >= a && box.end <= b;
                    if (sel)
                    {
                        if (!inRun) { rx0 = box.left; rx1 = box.right; inRun = true; }
                        else { rx0 = Mathf.Min(rx0, box.left); rx1 = Mathf.Max(rx1, box.right); }
                    }
                    else if (inRun)
                    {
                        output.Add(Rect.MinMaxRect(rx0, l.top, rx1, l.bottom));
                        inRun = false;
                    }
                }
                if (inRun) output.Add(Rect.MinMaxRect(rx0, l.top, rx1, l.bottom));
                // The selected newline (or an empty line) shows as a small box at the line's logical end.
                if (includeNewlines && l.hardBreak && l.end >= a && l.end < b)
                {
                    var w = LineHeight * 0.25f;
                    float ex;
                    if (l.boxCount == 0) ex = l.emptyX;
                    else ex = l.rtl ? boxes[l.boxStart].left : boxes[l.boxStart + l.boxCount - 1].right;
                    output.Add(l.rtl ? Rect.MinMaxRect(ex - w, l.top, ex, l.bottom) : Rect.MinMaxRect(ex, l.top, ex + w, l.bottom));
                }
            }
        }

        /// <summary>Underline rectangles (layout space) under [a, b) - the IME composition.</summary>
        public void UnderlineRects(int a, int b, List<Rect> output)
        {
            output.Clear();
            if (b <= a) return;
            var thick = Mathf.Max(1f, LineHeight * 0.05f);
            for (var li = 0; li < lines.Count; li++)
            {
                var l = lines[li];
                if (l.end <= a || l.start >= b) continue;
                var y = l.baseline + LineHeight * 0.08f;
                var inRun = false;
                float rx0 = 0, rx1 = 0;
                for (var bi = l.boxStart; bi < l.boxStart + l.boxCount; bi++)
                {
                    var box = boxes[bi];
                    var sel = box.start >= a && box.end <= b;
                    if (sel)
                    {
                        if (!inRun) { rx0 = box.left; rx1 = box.right; inRun = true; }
                        else { rx0 = Mathf.Min(rx0, box.left); rx1 = Mathf.Max(rx1, box.right); }
                    }
                    else if (inRun) { output.Add(Rect.MinMaxRect(rx0, y, rx1, y + thick)); inRun = false; }
                }
                if (inRun) output.Add(Rect.MinMaxRect(rx0, y, rx1, y + thick));
            }
        }

        /// <summary>Bounds of all content (layout space): x over boxes/empty lines, y from the first line top to the last line bottom.</summary>
        public Rect ContentBounds()
        {
            if (lines.Count == 0) return default;
            float x0 = float.MaxValue, x1 = float.MinValue;
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.boxCount == 0) { x0 = Mathf.Min(x0, l.emptyX); x1 = Mathf.Max(x1, l.emptyX); continue; }
                x0 = Mathf.Min(x0, boxes[l.boxStart].left);
                x1 = Mathf.Max(x1, boxes[l.boxStart + l.boxCount - 1].right);
            }
            return Rect.MinMaxRect(x0, lines[0].top, x1, lines[lines.Count - 1].bottom);
        }

        public TextSegments Segments => segments;
    }
}
