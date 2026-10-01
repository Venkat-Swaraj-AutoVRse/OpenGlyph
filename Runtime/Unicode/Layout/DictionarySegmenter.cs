using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Dictionary-based word segmentation for scripts that write without inter-word
    /// spaces — Thai, Lao, Khmer and Myanmar (Unicode line-break class SA). Produces
    /// break opportunities inside SA runs at word boundaries, so UAX #14 line breaking
    /// can wrap these scripts correctly instead of treating a run as one long token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Algorithm.</b> A faithful re-implementation of ICU's dictionary break iteration
    /// (ICU4C <c>dictbe.cpp</c>, the Thai/Lao/Khmer/Burmese family): a forward scan that,
    /// at each position, gathers candidate dictionary words and, when more than one match,
    /// chooses the candidate that lets the following one or two words also match (a
    /// 3-word lookahead). Runs of characters not covered by the dictionary are resynced
    /// into a single unknown segment using per-script end-of-word / begin-of-word sets and
    /// root/prefix "combine" thresholds, and a break is never emitted before a combining
    /// mark. The dictionary is a minimized DAWG (<see cref="DictionaryTrie"/>) walked with
    /// zero per-lookup allocation.
    /// </para>
    /// <para>
    /// <b>Grapheme safety.</b> Emitted boundaries are additionally filtered to UAX #29
    /// grapheme-cluster boundaries, so a break is never placed inside a cluster (between a
    /// base and its combining marks / vowel signs, or inside a Khmer coeng stack).
    /// </para>
    /// <para>
    /// <b>Opt-in loading.</b> Each script's dictionary is loaded ONLY if one is assigned in
    /// <see cref="UniTextSettings"/> (a per-script <see cref="SegmentationDictionaryEntry"/>).
    /// The dictionary assets live outside any Resources folder, so a project that assigns
    /// none ships no dictionary bytes and behaves exactly as before (no interior breaks),
    /// with a single one-time warning per unconfigured script it encounters.
    /// </para>
    /// </remarks>
    /// <seealso cref="DictionaryTrie"/>
    /// <seealso cref="LineBreakAlgorithm"/>
    internal sealed class DictionarySegmenter
    {
        // ICU tuning constants (from ICU4C dictbe.cpp), shared by all four engines.
        private const int Lookahead = 3;             // *_LOOKAHEAD
        private const int RootCombineThreshold = 3;  // *_ROOT_COMBINE_THRESHOLD
        private const int PrefixCombineThreshold = 3; // *_PREFIX_COMBINE_THRESHOLD
        private const int MinWord = 2;               // *_MIN_WORD

        private readonly UnicodeDataProvider _provider;
        private readonly GraphemeBreaker _graphemeBreaker;

        // Lazily-loaded per-script dictionaries.
        private DictionaryTrie _thai, _lao, _khmer, _myanmar;
        private bool _thaiTried, _laoTried, _khmerTried, _myanmarTried;

        // Reusable candidate-length scratch for the 3-word lookahead (word lengths are
        // small; POSSIBLE_WORD_LIST_MAX in ICU is 20).
        private readonly int[] _cand0 = new int[64];
        private readonly int[] _cand1 = new int[64];

        // Reusable grapheme-boundary scratch for the whole codepoint buffer.
        private bool[] _graphemeScratch = Array.Empty<bool>();

        public DictionarySegmenter(UnicodeDataProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _graphemeBreaker = new GraphemeBreaker(provider);
            // Re-resolve dictionaries if the project's segmentation assignment changes at
            // runtime (e.g. UniTextSettings.SetInstance, or an edit in Project Settings). Without
            // this the first per-script resolution would be cached forever, so assigning a
            // dictionary after a script had already fallen back — or clearing one — would have
            // no effect until domain reload.
            UniTextSettings.Changed += ResetDictionaries;
        }

        /// <summary>
        /// Drops the cached per-script dictionaries so the next SA run re-resolves them from
        /// <see cref="UniTextSettings"/> (re-emitting the one-time warning for a script that is
        /// still unassigned). Invoked automatically on <see cref="UniTextSettings.Changed"/>.
        /// </summary>
        public void ResetDictionaries()
        {
            _thai = _lao = _khmer = _myanmar = null;
            _thaiTried = _laoTried = _khmerTried = _myanmarTried = false;
        }

        /// <summary>True if the codepoint's line-break class is SA (complex-context).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsComplexContext(int codepoint)
        {
            return _provider.GetLineBreakClass(codepoint) == LineBreakClass.SA;
        }

        /// <summary>
        /// Scans <paramref name="codepoints"/> for maximal runs of SA characters and,
        /// for each run whose script has an assigned dictionary, sets <c>breaks[i] =
        /// Optional</c> at interior word boundaries. Positions outside SA runs are never
        /// touched, so line breaking for every other script is unchanged. Grapheme-cluster
        /// boundaries (UAX #29) are computed internally so a break is never placed inside a
        /// cluster.
        /// </summary>
        /// <param name="codepoints">The full codepoint buffer.</param>
        /// <param name="breaks">
        /// UAX #14 break-opportunity buffer (length codepoints.Length + 1); modified in place.
        /// </param>
        public void InjectBreaks(ReadOnlySpan<int> codepoints, Span<LineBreakType> breaks)
        {
            int n = codepoints.Length;
            if (n < 2) return;

            bool graphemeReady = false;
            Span<bool> graphemeBoundaries = default;

            int i = 0;
            while (i < n)
            {
                if (_provider.GetLineBreakClass(codepoints[i]) != LineBreakClass.SA)
                {
                    i++;
                    continue;
                }

                int runStart = i;
                int j = i + 1;
                while (j < n && _provider.GetLineBreakClass(codepoints[j]) == LineBreakClass.SA)
                    j++;
                int runEnd = j;

                if (runEnd - runStart >= MinWord * 2 && ResolveTrie(codepoints[runStart]) != null)
                {
                    // Compute grapheme boundaries once, lazily, only if a real SA run with
                    // a dictionary is present (avoids the O(n) pass for pure non-SA text).
                    if (!graphemeReady)
                    {
                        if (_graphemeScratch.Length < n + 1)
                            _graphemeScratch = new bool[n + 1];
                        graphemeBoundaries = _graphemeScratch.AsSpan(0, n + 1);
                        _graphemeBreaker.GetBreakOpportunities(codepoints, graphemeBoundaries);
                        graphemeReady = true;
                    }

                    SegmentRun(codepoints, runStart, runEnd, breaks, graphemeBoundaries);
                }

                i = runEnd;
            }
        }

        /// <summary>
        /// Segments one SA run via ICU's forward-scan-with-lookahead algorithm and marks
        /// interior word boundaries as Optional breaks (grapheme-safe, additive only).
        /// </summary>
        private void SegmentRun(
            ReadOnlySpan<int> codepoints,
            int start,
            int end,
            Span<LineBreakType> breaks,
            ReadOnlySpan<bool> graphemeBoundaries)
        {
            var trie = ResolveTrie(codepoints[start]);
            if (trie == null)
                return;

            int rangeEnd = end - start;               // run-relative length
            int current = 0;                          // run-relative scan position

            while (current < rangeEnd)
            {
                int cpWordLength = 0;
                int candidates = Candidates(trie, codepoints, start, current, rangeEnd, _cand0, out _);

                if (candidates == 1)
                {
                    cpWordLength = _cand0[0];
                }
                else if (candidates > 1)
                {
                    // Choose the candidate that lets the following word(s) also match.
                    int marked = _cand0[candidates - 1]; // default: longest
                    bool decided = false;
                    for (int a = candidates - 1; a >= 0 && !decided; a--)
                    {
                        int lenA = _cand0[a];
                        if (current + lenA >= rangeEnd) { marked = lenA; break; }
                        int c1 = Candidates(trie, codepoints, start, current + lenA, rangeEnd, _cand1, out _);
                        if (c1 > 0)
                        {
                            for (int b = c1 - 1; b >= 0; b--)
                            {
                                int lenB = _cand1[b];
                                if (current + lenA + lenB >= rangeEnd) { marked = lenA; decided = true; break; }
                                // If a third word can start after A+B, A is a good split.
                                int nodeC = DictionaryTrie.Root, pos = current + lenA + lenB, cnt = 0;
                                for (int q = pos; q < rangeEnd; q++)
                                {
                                    nodeC = trie.Step(nodeC, codepoints[start + q]);
                                    if (nodeC < 0) break;
                                    if (trie.IsWord(nodeC)) { cnt++; break; }
                                }
                                if (cnt > 0) { marked = lenA; decided = true; break; }
                            }
                        }
                    }
                    cpWordLength = marked;
                }

                // Resync unknown run: if what follows is not a dictionary word (and the
                // current word is short / shares little prefix), scan forward to a plausible
                // boundary and fold the passed-over characters into one segment.
                if (current + cpWordLength < rangeEnd && cpWordLength < RootCombineThreshold)
                {
                    int nextCandidates = Candidates(trie, codepoints, start, current + cpWordLength, rangeEnd, _cand1, out int nextPrefix);
                    if (nextCandidates <= 0 && (cpWordLength == 0 || nextPrefix < PrefixCombineThreshold))
                    {
                        int remaining = rangeEnd - (current + cpWordLength);
                        int chars = 0;
                        int scan = current + cpWordLength;
                        for (;;)
                        {
                            int pc = codepoints[start + scan];
                            scan++; chars++; remaining--;
                            if (remaining <= 0) break;
                            int uc = codepoints[start + scan];
                            if (IsEndWord(trie, pc) && IsBeginWord(pc, uc))
                            {
                                int nc = Candidates(trie, codepoints, start, scan, rangeEnd, _cand1, out _);
                                if (nc > 0) break;
                            }
                        }
                        cpWordLength += chars;
                    }
                }

                // Never stop before a combining mark.
                while (current + cpWordLength < rangeEnd && IsMark(codepoints[start + current + cpWordLength]))
                    cpWordLength++;

                if (cpWordLength <= 0)
                    cpWordLength = 1; // safety: always advance

                int boundary = current + cpWordLength;
                if (boundary < rangeEnd)
                {
                    int abs = start + boundary;
                    if (graphemeBoundaries[abs] && breaks[abs] == LineBreakType.None)
                        breaks[abs] = LineBreakType.Optional;
                }
                current = boundary;
            }
        }

        /// <summary>
        /// Fills <paramref name="lens"/> with the ascending lengths (in codepoints) of all
        /// dictionary words starting at run position <paramref name="p"/>, and returns the
        /// count. <paramref name="prefix"/> receives the longest matched prefix length
        /// (whether or not it ends on a word) — ICU's <c>longestPrefix</c>.
        /// </summary>
        private int Candidates(DictionaryTrie trie, ReadOnlySpan<int> cps, int start, int p, int rangeEnd,
            int[] lens, out int prefix)
        {
            int count = 0;
            int node = DictionaryTrie.Root;
            prefix = 0;
            for (int q = p; q < rangeEnd; q++)
            {
                node = trie.Step(node, cps[start + q]);
                if (node < 0) break;
                prefix = q + 1 - p;
                if (trie.IsWord(node) && count < lens.Length)
                    lens[count++] = q + 1 - p;
            }
            return count;
        }

        // ---- Per-script character-class predicates (from ICU4C dictbe.cpp) ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsMark(int cp)
        {
            var gc = _provider.GetGeneralCategory(cp);
            return gc == GeneralCategory.Mn || gc == GeneralCategory.Mc || gc == GeneralCategory.Me;
        }

        /// <summary>
        /// ICU fEndWordSet: characters allowed to end a word. Default is the whole SA
        /// word-set, minus per-script exceptions (Thai MAI HAN-AKAT and leading vowels,
        /// Lao leading vowels, Khmer COENG).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsEndWord(DictionaryTrie trie, int cp)
        {
            switch (trie.Script)
            {
                case UnicodeScript.Thai:
                    return cp != 0x0E31 && !(cp >= 0x0E40 && cp <= 0x0E44);
                case UnicodeScript.Lao:
                    return !(cp >= 0x0EC0 && cp <= 0x0EC4);
                case UnicodeScript.Khmer:
                    return cp != 0x17D2; // COENG must not end a word (keeps coeng stacks intact)
                default:
                    return true; // Myanmar: whole SA set
            }
        }

        /// <summary>ICU fBeginWordSet: characters allowed to begin a word.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsBeginWord(int prevCp, int cp)
        {
            // Dispatch on the script of the candidate begin character.
            switch (_provider.GetScript(cp))
            {
                case UnicodeScript.Thai:
                    return (cp >= 0x0E01 && cp <= 0x0E2E) || (cp >= 0x0E40 && cp <= 0x0E44);
                case UnicodeScript.Lao:
                    return (cp >= 0x0E81 && cp <= 0x0EAE) || (cp >= 0x0EDC && cp <= 0x0EDD) || (cp >= 0x0EC0 && cp <= 0x0EC4);
                case UnicodeScript.Khmer:
                    return cp >= 0x1780 && cp <= 0x17B3;
                case UnicodeScript.Myanmar:
                    return cp >= 0x1000 && cp <= 0x102A;
                default:
                    return false;
            }
        }

        private DictionaryTrie ResolveTrie(int codepoint)
        {
            switch (_provider.GetScript(codepoint))
            {
                case UnicodeScript.Thai:
                    if (!_thaiTried) { _thai = Load(SegmentationScript.Thai); _thaiTried = true; }
                    return _thai;
                case UnicodeScript.Lao:
                    if (!_laoTried) { _lao = Load(SegmentationScript.Lao); _laoTried = true; }
                    return _lao;
                case UnicodeScript.Khmer:
                    if (!_khmerTried) { _khmer = Load(SegmentationScript.Khmer); _khmerTried = true; }
                    return _khmer;
                case UnicodeScript.Myanmar:
                    if (!_myanmarTried) { _myanmar = Load(SegmentationScript.Myanmar); _myanmarTried = true; }
                    return _myanmar;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Loads the dictionary assigned for a script in <see cref="UniTextSettings"/>.
        /// Returns <see langword="null"/> (with a single one-time warning) when none is
        /// assigned, so the script falls back to default line breaking.
        /// </summary>
        private static DictionaryTrie Load(SegmentationScript script)
        {
            var asset = UniTextSettings.GetSegmentationDictionary(script);
            if (asset == null)
            {
                Debug.LogWarning(
                    $"[DictionarySegmenter] No segmentation dictionary assigned for {script}. " +
                    $"Text in this script will not receive dictionary word-boundary line breaks. " +
                    $"Assign a dictionary in Project Settings \u2192 UniText \u2192 Word Segmentation Dictionaries " +
                    $"to enable it. See Documentation/GettingStarted.md (\u201cWord segmentation\u201d).");
                return null;
            }

            try
            {
                return new DictionaryTrie(asset.bytes);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DictionarySegmenter] Failed to load {script} dictionary: {ex.Message}");
                return null;
            }
        }
    }
}
