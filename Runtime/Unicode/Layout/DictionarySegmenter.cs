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
    /// <b>Algorithm.</b> Following ICU's dictionary-based break iteration, each SA run
    /// is segmented by a dynamic program that finds the segmentation minimising the
    /// number of segments, preferring longer dictionary matches and treating any
    /// codepoint not covered by a dictionary word as a single-codepoint fallback
    /// segment. This is equivalent to maximal matching with lookahead but is
    /// deterministic and order-independent. The dictionary is a minimized DAWG
    /// (<see cref="DictionaryTrie"/>) walked with zero per-lookup allocation.
    /// </para>
    /// <para>
    /// <b>Grapheme safety.</b> A boundary is only emitted where the caller-supplied
    /// grapheme-cluster boundary flag is set, so a break is never placed inside a
    /// cluster (e.g. between a base and its combining marks / vowel signs).
    /// </para>
    /// <para>
    /// <b>Lazy loading.</b> Each script's dictionary binary is loaded from
    /// <c>Resources/Segmentation/&lt;Script&gt;Dict.bytes</c> only the first time that
    /// script is actually seen. Missing dictionaries degrade gracefully (no injected
    /// breaks for that script — behaviour identical to pre-segmentation).
    /// </para>
    /// </remarks>
    /// <seealso cref="DictionaryTrie"/>
    /// <seealso cref="LineBreakAlgorithm"/>
    internal sealed class DictionarySegmenter
    {
        // A single-codepoint fallback segment costs more than any dictionary word so
        // the DP prefers dictionary coverage; among dictionary-only paths it minimises
        // segment count (favouring longer words), matching ICU behaviour.
        private const int DictWordCost = 1;
        private const long FallbackCost = 1L << 16;

        private readonly UnicodeDataProvider _provider;
        private readonly GraphemeBreaker _graphemeBreaker;

        // Lazily-loaded per-script dictionaries. Indexed by an internal small enum.
        private DictionaryTrie _thai, _lao, _khmer, _myanmar;
        private bool _thaiTried, _laoTried, _khmerTried, _myanmarTried;

        // Reusable DP scratch (grows as needed). Not thread-shared: the segmenter is
        // held per line-break-algorithm instance, which is used single-threaded.
        private long[] _cost = Array.Empty<long>();
        private int[] _prev = Array.Empty<int>();

        // Reusable grapheme-boundary scratch for the whole codepoint buffer.
        private bool[] _graphemeScratch = Array.Empty<bool>();

        public DictionarySegmenter(UnicodeDataProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _graphemeBreaker = new GraphemeBreaker(provider);
        }

        /// <summary>True if the codepoint's line-break class is SA (complex-context).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsComplexContext(int codepoint)
        {
            return _provider.GetLineBreakClass(codepoint) == LineBreakClass.SA;
        }

        /// <summary>
        /// Scans <paramref name="codepoints"/> for maximal runs of SA characters and,
        /// for each run whose script has a dictionary, sets <c>breaks[i] = Optional</c>
        /// at interior word boundaries. Positions outside SA runs are never touched, so
        /// line breaking for every other script is unchanged. Grapheme-cluster boundaries
        /// (UAX #29) are computed internally so a break is never placed inside a cluster.
        /// </summary>
        /// <param name="codepoints">The full codepoint buffer.</param>
        /// <param name="breaks">
        /// UAX #14 break-opportunity buffer (length codepoints.Length + 1); modified in place.
        /// </param>
        public void InjectBreaks(
            ReadOnlySpan<int> codepoints,
            Span<LineBreakType> breaks)
        {
            int n = codepoints.Length;
            if (n < 2) return;

            // Grapheme-cluster boundaries for the whole buffer (UAX #29). Computed once,
            // into reusable scratch. length n + 1: index i is the boundary BEFORE cp i.
            if (_graphemeScratch.Length < n + 1)
                _graphemeScratch = new bool[n + 1];
            var graphemeBoundaries = _graphemeScratch.AsSpan(0, n + 1);
            _graphemeBreaker.GetBreakOpportunities(codepoints, graphemeBoundaries);

            int i = 0;
            while (i < n)
            {
                if (_provider.GetLineBreakClass(codepoints[i]) != LineBreakClass.SA)
                {
                    i++;
                    continue;
                }

                // Extend a maximal SA run.
                int runStart = i;
                int j = i + 1;
                while (j < n && _provider.GetLineBreakClass(codepoints[j]) == LineBreakClass.SA)
                    j++;
                int runEnd = j; // exclusive

                if (runEnd - runStart >= 2)
                    SegmentRun(codepoints, runStart, runEnd, breaks, graphemeBoundaries);

                i = runEnd;
            }
        }

        private void SegmentRun(
            ReadOnlySpan<int> codepoints,
            int start,
            int end,
            Span<LineBreakType> breaks,
            ReadOnlySpan<bool> graphemeBoundaries)
        {
            var trie = ResolveTrie(codepoints[start]);
            if (trie == null)
                return; // unknown / unsupported script — leave the run intact

            int len = end - start;
            EnsureScratch(len + 1);
            var cost = _cost;
            var prev = _prev;

            // DP over run-relative positions 0..len. cost[k] = min cost to segment [start, start+k).
            cost[0] = 0;
            prev[0] = -1;
            for (int k = 1; k <= len; k++)
            {
                cost[k] = long.MaxValue;
                prev[k] = -1;
            }

            for (int p = 0; p < len; p++)
            {
                if (cost[p] == long.MaxValue)
                    continue;

                // 1) All dictionary words starting at run position p.
                int node = DictionaryTrie.Root;
                bool anyWord = false;
                for (int q = p; q < len; q++)
                {
                    node = trie.Step(node, codepoints[start + q]);
                    if (node < 0)
                        break;
                    if (trie.IsWord(node))
                    {
                        int wlen = q + 1 - p;
                        long c = cost[p] + DictWordCost;
                        int endPos = p + wlen;
                        if (c < cost[endPos])
                        {
                            cost[endPos] = c;
                            prev[endPos] = p;
                        }
                        anyWord = true;
                    }
                }

                // 2) Single-codepoint fallback (always available) so unknown text still
                //    advances and the DP can never get stuck.
                long fc = cost[p] + FallbackCost;
                if (fc < cost[p + 1])
                {
                    cost[p + 1] = fc;
                    prev[p + 1] = p;
                }

                _ = anyWord;
            }

            // Back-track the optimal path and mark interior boundaries.
            // A boundary at run position b (0 < b < len) becomes an Optional break at
            // absolute index start + b, but only if it is a grapheme-cluster boundary
            // and the UAX #14 pass left it as None (we only ADD opportunities).
            int cur = len;
            while (cur > 0)
            {
                int pcur = prev[cur];
                if (pcur > 0) // interior boundary (pcur == 0 is the run's own start)
                {
                    int abs = start + pcur;
                    if (graphemeBoundaries[abs] && breaks[abs] == LineBreakType.None)
                        breaks[abs] = LineBreakType.Optional;
                }
                cur = pcur;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureScratch(int required)
        {
            if (_cost.Length < required)
            {
                _cost = new long[required];
                _prev = new int[required];
            }
        }

        private DictionaryTrie ResolveTrie(int codepoint)
        {
            switch (_provider.GetScript(codepoint))
            {
                case UnicodeScript.Thai:
                    if (!_thaiTried) { _thai = Load("ThaiDict"); _thaiTried = true; }
                    return _thai;
                case UnicodeScript.Lao:
                    if (!_laoTried) { _lao = Load("LaoDict"); _laoTried = true; }
                    return _lao;
                case UnicodeScript.Khmer:
                    if (!_khmerTried) { _khmer = Load("KhmerDict"); _khmerTried = true; }
                    return _khmer;
                case UnicodeScript.Myanmar:
                    if (!_myanmarTried) { _myanmar = Load("MyanmarDict"); _myanmarTried = true; }
                    return _myanmar;
                default:
                    return null;
            }
        }

        private static DictionaryTrie Load(string name)
        {
            var asset = Resources.Load<TextAsset>("Segmentation/" + name);
            if (asset == null)
            {
                Debug.LogWarning($"[DictionarySegmenter] Missing dictionary Resources/Segmentation/{name}.bytes — " +
                                 "SA runs of this script will not receive word-boundary breaks.");
                return null;
            }

            try
            {
                return new DictionaryTrie(asset.bytes);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DictionarySegmenter] Failed to load {name}: {ex.Message}");
                return null;
            }
        }
    }
}
