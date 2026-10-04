using System;
using System.Collections.Generic;

namespace LightSide
{
    /// <summary>
    /// Grapheme-cluster and word boundaries of a string, in UTF-16 indices. The caret and every edit stay on
    /// grapheme boundaries (UAX #29, the engine's <see cref="GraphemeBreaker"/>), so a caret is never placed
    /// inside a surrogate pair, a combining sequence, an emoji ZWJ sequence or a flag.
    /// </summary>
    /// <remarks>
    /// Words follow the UAX #29 word rules that matter for caret movement: letters, marks, digits and
    /// connector punctuation form words; an apostrophe, period, colon or middle dot between letters
    /// (WB6/7) and a comma, period or semicolon between digits (WB11/12) stay inside the word; runs of
    /// spaces and runs of other punctuation are segments of their own; each Han or Hiragana grapheme and
    /// each emoji is its own word; a Katakana run is one word. Scripts written without spaces (Thai, Lao,
    /// Khmer, Myanmar) are one word per run here (no dictionary).
    /// </remarks>
    internal sealed class TextSegments
    {
        internal enum Kind : byte { Word, Space, Newline, Punct, Ideo, Emoji }

        internal struct Segment
        {
            public int start, end;
            public Kind kind;
        }

        private static GraphemeBreaker s_breaker;
        private static UnicodeDataProvider s_provider;

        private string text = string.Empty;
        private bool[] boundary = new bool[1] { true };
        private int[] cps = Array.Empty<int>();
        private int[] cpStart = Array.Empty<int>();
        private bool[] cpBreaks = Array.Empty<bool>();
        private readonly List<Segment> segments = new();

        public string Text => text;
        public int Length => text.Length;
        public IReadOnlyList<Segment> Segments => segments;

        public void Build(string s)
        {
            s ??= string.Empty;
            if (ReferenceEquals(s, text) && boundary.Length == s.Length + 1) return;
            text = s;
            var n = s.Length;
            if (boundary.Length < n + 1) boundary = new bool[n + 1];
            Array.Clear(boundary, 0, n + 1);

            // Codepoints and their UTF-16 starts.
            if (cps.Length < n) { cps = new int[n]; cpStart = new int[n + 1]; }
            var cpCount = 0;
            for (var i = 0; i < n; i++)
            {
                cpStart[cpCount] = i;
                var c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(s[i + 1]))
                {
                    cps[cpCount++] = char.ConvertToUtf32(c, s[i + 1]);
                    i++;
                }
                else cps[cpCount++] = c;
            }
            cpStart[cpCount] = n;

            if (cpBreaks.Length < cpCount + 1) cpBreaks = new bool[cpCount + 1];
            var breaker = Breaker();
            if (breaker != null)
                breaker.GetBreakOpportunities(new ReadOnlySpan<int>(cps, 0, cpCount), new Span<bool>(cpBreaks, 0, cpCount + 1));
            else
                for (var i = 0; i <= cpCount; i++) cpBreaks[i] = true;

            for (var i = 0; i <= cpCount; i++)
                if (cpBreaks[i]) boundary[cpStart[i]] = true;
            boundary[0] = true;
            boundary[n] = true;

            BuildWords(cpCount);
        }

        private static GraphemeBreaker Breaker()
        {
            if (s_breaker != null && s_provider == UnicodeData.Provider) return s_breaker;
            UnicodeData.EnsureInitialized();
            var p = UnicodeData.Provider;
            if (p == null) return null;
            s_provider = p;
            s_breaker = new GraphemeBreaker(p);
            return s_breaker;
        }

        public bool IsBoundary(int i) => i <= 0 || i >= text.Length || boundary[i];

        /// <summary>The nearest grapheme boundary at or before <paramref name="i"/>.</summary>
        public int Floor(int i)
        {
            if (i <= 0) return 0;
            if (i >= text.Length) return text.Length;
            while (i > 0 && !boundary[i]) i--;
            return i;
        }

        /// <summary>The nearest grapheme boundary at or after <paramref name="i"/>.</summary>
        public int Ceil(int i)
        {
            if (i <= 0) return 0;
            if (i >= text.Length) return text.Length;
            while (i < text.Length && !boundary[i]) i++;
            return i;
        }

        public int Next(int i)
        {
            if (i >= text.Length) return text.Length;
            i++;
            while (i < text.Length && !boundary[i]) i++;
            return i;
        }

        public int Prev(int i)
        {
            if (i <= 0) return 0;
            if (i > text.Length) return text.Length;
            i--;
            while (i > 0 && !boundary[i]) i--;
            return i;
        }

        /// <summary>Number of grapheme clusters.</summary>
        public int GraphemeCount
        {
            get
            {
                var count = 0;
                for (var i = 1; i <= text.Length; i++) if (boundary[i]) count++;
                return count;
            }
        }

        // ---- words ---------------------------------------------------------------------------------

        private void BuildWords(int cpCount)
        {
            segments.Clear();
            var n = text.Length;
            if (n == 0) return;
            var p = UnicodeData.Provider;

            // Classify every grapheme by its first codepoint.
            var gStart = 0;
            var cpIndex = 0;
            Segment cur = default;
            var haveCur = false;
            var pendingMidStart = -1; // a MidLetter/MidNum grapheme waiting for the next grapheme
            Kind pendingMidKind = Kind.Punct;
            var lastWordIsNumeric = false;

            while (gStart < n)
            {
                var gEnd = Next(gStart);
                while (cpIndex < cpCount && cpStart[cpIndex] < gStart) cpIndex++;
                var cp = cpIndex < cpCount ? cps[cpIndex] : text[gStart];
                var kind = Classify(p, cp, out var numeric, out var midLetter, out var midNum);

                if (pendingMidStart >= 0)
                {
                    // WB6/7, WB11/12: Mid* between two word graphemes of the right kind keeps the word.
                    var joins = kind == Kind.Word && (numeric ? pendingMidKind == Kind.Ideo /*num*/ : pendingMidKind == Kind.Word);
                    if (joins)
                    {
                        cur.end = gEnd;
                        lastWordIsNumeric = numeric;
                        pendingMidStart = -1;
                        gStart = gEnd;
                        continue;
                    }
                    // Not joined: the pending Mid grapheme becomes its own punctuation segment.
                    segments.Add(cur);
                    cur = new Segment { start = pendingMidStart, end = gStart, kind = Kind.Punct };
                    haveCur = true;
                    pendingMidStart = -1;
                }

                if (haveCur && cur.kind == Kind.Word && (midLetter && !lastWordIsNumeric || midNum && lastWordIsNumeric))
                {
                    pendingMidStart = gStart;
                    pendingMidKind = lastWordIsNumeric ? Kind.Ideo : Kind.Word;
                    gStart = gEnd;
                    continue;
                }

                var merge = haveCur && cur.kind == kind &&
                            (kind == Kind.Word || kind == Kind.Space || kind == Kind.Punct ||
                             kind == Kind.Ideo && IsKatakana(p, cp) && IsKatakana(p, FirstCp(cur.start)));
                if (merge)
                {
                    cur.end = gEnd;
                }
                else
                {
                    if (haveCur) segments.Add(cur);
                    cur = new Segment { start = gStart, end = gEnd, kind = kind };
                    haveCur = true;
                }
                if (kind == Kind.Word) lastWordIsNumeric = numeric;
                gStart = gEnd;
            }

            if (pendingMidStart >= 0)
            {
                segments.Add(cur);
                cur = new Segment { start = pendingMidStart, end = n, kind = Kind.Punct };
            }
            if (haveCur) segments.Add(cur);
        }

        private int FirstCp(int utf16)
        {
            var c = text[utf16];
            if (char.IsHighSurrogate(c) && utf16 + 1 < text.Length && char.IsLowSurrogate(text[utf16 + 1]))
                return char.ConvertToUtf32(c, text[utf16 + 1]);
            return c;
        }

        private static bool IsKatakana(UnicodeDataProvider p, int cp) =>
            p != null ? p.GetScript(cp) == UnicodeScript.Katakana : cp >= 0x30A0 && cp <= 0x30FF;

        private static Kind Classify(UnicodeDataProvider p, int cp, out bool numeric, out bool midLetter, out bool midNum)
        {
            numeric = false;
            midLetter = cp == '\'' || cp == 0x2019 || cp == '.' || cp == ':' || cp == 0x00B7 || cp == 0x2027 || cp == 0xFE13 || cp == 0xFF0E;
            midNum = cp == ',' || cp == '.' || cp == ';' || cp == 0x066C || cp == 0xFE50 || cp == 0xFF0C;
            if (cp == '\n' || cp == '\r' || cp == 0x2028 || cp == 0x2029) return Kind.Newline;
            if (cp == ' ' || cp == '\t' || cp == 0x00A0 || cp == 0x3000 || (cp >= 0x2000 && cp <= 0x200A) || cp == 0x202F || cp == 0x205F)
                return Kind.Space;
            if (p == null)
            {
                if (char.IsLetterOrDigit((char)Math.Min(cp, 0xFFFF)) || cp == '_') { numeric = cp >= '0' && cp <= '9'; return Kind.Word; }
                return cp > 0xFFFF ? Kind.Emoji : Kind.Punct;
            }

            if (p.IsExtendedPictographic(cp) && cp > 0xFF) return Kind.Emoji;
            var gc = p.GetGeneralCategory(cp);
            switch (gc)
            {
                case GeneralCategory.Nd:
                case GeneralCategory.Nl:
                case GeneralCategory.No:
                    numeric = true;
                    return Kind.Word;
                case GeneralCategory.Lu:
                case GeneralCategory.Ll:
                case GeneralCategory.Lt:
                case GeneralCategory.Lm:
                case GeneralCategory.Lo:
                case GeneralCategory.Mn:
                case GeneralCategory.Mc:
                case GeneralCategory.Me:
                {
                    var script = p.GetScript(cp);
                    if (script == UnicodeScript.Han || script == UnicodeScript.Hiragana || script == UnicodeScript.Katakana)
                        return Kind.Ideo;
                    return Kind.Word;
                }
                case GeneralCategory.Pc:
                    return Kind.Word;
                case GeneralCategory.Zs:
                    return Kind.Space;
                default:
                    return Kind.Punct;
            }
        }

        private int SegmentIndexAt(int i)
        {
            for (var s = 0; s < segments.Count; s++)
                if (i >= segments[s].start && i < segments[s].end) return s;
            return segments.Count;
        }

        private static bool IsWordLike(Kind k) => k != Kind.Space && k != Kind.Newline;

        /// <summary>Ctrl+Right (Windows style): the start of the next word, after the current word and the spaces after it.</summary>
        public int NextWordStart(int i)
        {
            var n = text.Length;
            if (i >= n) return n;
            var s = SegmentIndexAt(i);
            if (s >= segments.Count) return n;
            // Leave the current segment (word, punctuation or spaces).
            var pos = segments[s].end;
            s++;
            // Skip spaces (not newlines: a line end is a stop).
            while (s < segments.Count && segments[s].kind == Kind.Space) { pos = segments[s].end; s++; }
            return s < segments.Count ? segments[s].start : n;
        }

        /// <summary>Ctrl+Left: the start of the word before the caret (spaces before the caret are skipped).</summary>
        public int PrevWordStart(int i)
        {
            if (i <= 0) return 0;
            var s = SegmentIndexAt(i - 1);
            if (s >= segments.Count) return 0;
            while (s > 0 && segments[s].kind == Kind.Space) s--;
            if (segments[s].kind == Kind.Space) return segments[s].start;
            return segments[s].start;
        }

        /// <summary>The end of the word at or after <paramref name="i"/> (for word-wise extend, mac style).</summary>
        public int NextWordEnd(int i)
        {
            var n = text.Length;
            if (i >= n) return n;
            var s = SegmentIndexAt(i);
            while (s < segments.Count && !IsWordLike(segments[s].kind)) s++;
            return s < segments.Count ? segments[s].end : n;
        }

        /// <summary>The segment (word, run of spaces, punctuation) that contains <paramref name="i"/>; at a
        /// word end the word before it (so a double-click just after a word selects that word).</summary>
        public void SegmentAt(int i, out int start, out int end)
        {
            var n = text.Length;
            start = end = Math.Max(0, Math.Min(i, n));
            if (segments.Count == 0) return;
            var s = SegmentIndexAt(i);
            if (s >= segments.Count || (segments[s].kind == Kind.Space || segments[s].kind == Kind.Newline) && i > 0)
            {
                var b = SegmentIndexAt(i - 1);
                if (b < segments.Count && IsWordLike(segments[b].kind)) s = b;
            }
            if (s >= segments.Count) s = segments.Count - 1;
            start = segments[s].start;
            end = segments[s].end;
            if (segments[s].kind == Kind.Newline) end = start;
        }
    }
}
