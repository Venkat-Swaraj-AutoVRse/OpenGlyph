using System;
using System.Runtime.CompilerServices;
using System.Threading;
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

        /// <summary>
        /// Immutable, atomically-swappable set of the four per-script dictionaries. A layout pass
        /// captures ONE reference to the current snapshot at its start and reads only that reference
        /// for its whole duration, so a settings change on another thread (which only flips a dirty
        /// flag) can never null or swap a trie mid-pass — no torn reads, no NRE on a worker thread.
        /// </summary>
        private sealed class DictSnapshot
        {
            public readonly DictionaryTrie Thai, Lao, Khmer, Myanmar;
            public DictSnapshot(DictionaryTrie thai, DictionaryTrie lao, DictionaryTrie khmer, DictionaryTrie myanmar)
            { Thai = thai; Lao = lao; Khmer = khmer; Myanmar = myanmar; }
        }

        // The live snapshot (volatile: readers see a fully-constructed snapshot, never a partially
        // written field set). Built on the MAIN thread — at construction and whenever UniTextSettings
        // changes — because resolving a dictionary reads TextAsset.bytes, which Unity permits only on
        // the main thread. A worker-thread layout pass only READS this reference; it never builds.
        private volatile DictSnapshot _snapshot;
        private readonly object _snapshotLock = new object();

        // Bit per script (1 << (int)SegmentationScript) whose "no dictionary" warning has already
        // been logged. Reset whenever the settings change. Updated with Interlocked because
        // InjectBreaks can run on layout worker threads.
        private int _missingWarnedMask;

        private void WarnMissingDictionaryOnce(UnicodeScript unicodeScript)
        {
            SegmentationScript script;
            switch (unicodeScript)
            {
                case UnicodeScript.Thai: script = SegmentationScript.Thai; break;
                case UnicodeScript.Lao: script = SegmentationScript.Lao; break;
                case UnicodeScript.Khmer: script = SegmentationScript.Khmer; break;
                case UnicodeScript.Myanmar: script = SegmentationScript.Myanmar; break;
                default: return;
            }

            int bit = 1 << (int)script;
            while (true)
            {
                int seen = Volatile.Read(ref _missingWarnedMask);
                if ((seen & bit) != 0) return;
                if (Interlocked.CompareExchange(ref _missingWarnedMask, seen | bit, seen) == seen) break;
            }

            Debug.LogWarning(
                $"[DictionarySegmenter] No segmentation dictionary assigned for {script}. " +
                $"Text in this script will not receive dictionary word-boundary line breaks. " +
                $"Assign a dictionary in Project Settings \u2192 UniText \u2192 Word Segmentation Dictionaries " +
                $"to enable it. See Documentation/GettingStarted.md (\u201cWord segmentation\u201d).");
        }

        // Reusable candidate-length scratch for the 3-word lookahead (word lengths are small;
        // POSSIBLE_WORD_LIST_MAX in ICU is 20). THREAD-STATIC: the one shared DictionarySegmenter
        // (held by the single static LineBreakAlgorithm in SharedPipelineComponents) is used
        // concurrently by parallel DoFirstPass layout passes on worker threads, so this scratch must
        // be per-thread or two passes would stomp each other's candidate lists mid-scan. Each worker
        // lazily allocates its own on first use via the accessors below.
        [ThreadStatic] private static int[] _cand0Ts;
        [ThreadStatic] private static int[] _cand1Ts;

        // Reusable grapheme-boundary scratch for the whole codepoint buffer — also per-thread for the
        // same reason (a parallel pass writes the full buffer's grapheme boundaries into it).
        [ThreadStatic] private static bool[] _graphemeScratchTs;

        private static int[] Cand0 => _cand0Ts ??= new int[64];
        private static int[] Cand1 => _cand1Ts ??= new int[64];

        public DictionarySegmenter(UnicodeDataProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _graphemeBreaker = new GraphemeBreaker(provider);
            // Build the initial snapshot now (constructor runs on the main thread). A later settings
            // change rebuilds it via RebuildSnapshot, invoked on UniTextSettings.Changed — which fires
            // synchronously inside UniTextSettings.SetInstance, i.e. on the (main) thread that changed
            // the setting. The rebuilt snapshot is published atomically through the volatile field, so
            // a worker-thread pass that captured the PREVIOUS snapshot keeps using it to completion and
            // the NEXT pass picks up the new one — the reload applies strictly between passes.
            RebuildSnapshot();
            UniTextSettings.Changed += RebuildSnapshot;
        }

        /// <summary>
        /// Resolves all four dictionaries and publishes a fresh immutable snapshot. MAIN-THREAD ONLY
        /// (reads TextAsset.bytes). Serialised so concurrent Changed callbacks resolve once.
        /// </summary>
        private void RebuildSnapshot()
        {
            lock (_snapshotLock)
            {
                // A settings change may assign or remove dictionaries: allow each still-missing
                // script to warn once more, the next time text in it is actually laid out.
                Interlocked.Exchange(ref _missingWarnedMask, 0);
                _snapshot = new DictSnapshot(
                    Load(SegmentationScript.Thai),
                    Load(SegmentationScript.Lao),
                    Load(SegmentationScript.Khmer),
                    Load(SegmentationScript.Myanmar));
            }
        }

        /// <summary>
        /// Back-compat alias retained for callers/tests that forced a re-resolution. Rebuilds the
        /// snapshot on the calling (main) thread; the new snapshot is used by the NEXT pass.
        /// </summary>
        public void ResetDictionaries() => RebuildSnapshot();

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

            // Capture ONE immutable snapshot at the start of the pass. The snapshot is only ever
            // replaced (atomically, via the volatile field) by RebuildSnapshot on the main thread
            // when UniTextSettings changes; a change arriving while this pass runs publishes a NEW
            // snapshot that this pass does not see — it keeps using `snap` to completion, and the
            // next pass captures the new one. Hence no torn reads mid-pass.
            DictSnapshot snap = _snapshot;

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

                DictionaryTrie trie = runEnd - runStart >= MinWord * 2 ? ResolveTrie(snap, codepoints[runStart]) : null;
                if (trie == null && runEnd - runStart >= MinWord * 2)
                    WarnMissingDictionaryOnce(_provider.GetScript(codepoints[runStart]));

                if (trie != null)
                {
                    // Compute grapheme boundaries once, lazily, only if a real SA run with
                    // a dictionary is present (avoids the O(n) pass for pure non-SA text).
                    if (!graphemeReady)
                    {
                        if (_graphemeScratchTs == null || _graphemeScratchTs.Length < n + 1)
                            _graphemeScratchTs = new bool[n + 1];
                        graphemeBoundaries = _graphemeScratchTs.AsSpan(0, n + 1);
                        _graphemeBreaker.GetBreakOpportunities(codepoints, graphemeBoundaries);
                        graphemeReady = true;
                    }

                    SegmentRun(snap, codepoints, runStart, runEnd, breaks, graphemeBoundaries);
                }

                i = runEnd;
            }
        }

        /// <summary>
        /// Segments one SA run via ICU's forward-scan-with-lookahead algorithm and marks
        /// interior word boundaries as Optional breaks (grapheme-safe, additive only).
        /// </summary>
        private void SegmentRun(
            DictSnapshot snap,
            ReadOnlySpan<int> codepoints,
            int start,
            int end,
            Span<LineBreakType> breaks,
            ReadOnlySpan<bool> graphemeBoundaries)
        {
            var trie = ResolveTrie(snap, codepoints[start]);
            if (trie == null)
                return;

            // Per-thread candidate scratch (see the [ThreadStatic] fields): captured once here so the
            // rest of this method reads/writes its OWN thread's buffers, never a sibling worker's.
            var _cand0 = Cand0;
            var _cand1 = Cand1;

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

        private DictionaryTrie ResolveTrie(DictSnapshot snap, int codepoint)
        {
            switch (_provider.GetScript(codepoint))
            {
                case UnicodeScript.Thai: return snap.Thai;
                case UnicodeScript.Lao: return snap.Lao;
                case UnicodeScript.Khmer: return snap.Khmer;
                case UnicodeScript.Myanmar: return snap.Myanmar;
                default: return null;
            }
        }

        /// <summary>
        /// Loads the dictionary assigned for a script in <see cref="UniTextSettings"/>.
        /// Returns <see langword="null"/> (with a single one-time warning) when none is
        /// assigned, so the script falls back to default line breaking.
        /// </summary>
        private static DictionaryTrie Load(SegmentationScript script)
        {
            // No warning here: this runs for all four scripts whenever the snapshot is built, so
            // warning here would fire for scripts the project never displays. The warning is
            // emitted lazily by WarnMissingDictionaryOnce, only when text in that script is laid out.
            var asset = UniTextSettings.GetSegmentationDictionary(script);
            if (asset == null)
                return null;

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
