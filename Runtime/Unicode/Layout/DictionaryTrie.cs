using System;
using System.IO;

namespace LightSide
{
    /// <summary>
    /// Immutable, allocation-free reader for an OpenGlyph segmentation dictionary
    /// ("OGSD" v2) — a minimized DAWG of dictionary words for one script.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The binary is produced offline by the <c>openglyph-dictgen</c> tool (see
    /// <c>Tools~/DictGen</c>) from the pinned ICU break-iterator dictionaries. It is
    /// loaded once per script (lazily, only when that script appears) and then walked
    /// with zero per-lookup allocation: node/edge tables live in flat arrays and each
    /// step is a binary search over a node's contiguous, codepoint-sorted edge slice.
    /// </para>
    /// <para>
    /// FORMAT (little-endian): magic 'OGSD', version 2, scriptId, wordCount, cpBase,
    /// nodeCount N, edgeCount M, then N node records (uint32 firstEdge|isWord-high-bit,
    /// uint16 edgeCount, uint16 pad) and M edge records (uint16 label=cp-cpBase, uint16
    /// pad, uint32 target).
    /// </para>
    /// </remarks>
    /// <seealso cref="DictionarySegmenter"/>
    internal sealed class DictionaryTrie
    {
        private const uint Magic = 0x4453474F; // 'OGSD'
        private const uint ExpectedVersion = 2;

        // Node table (parallel arrays).
        private readonly int[] _nodeFirstEdge;
        private readonly ushort[] _nodeEdgeCount;
        private readonly bool[] _nodeIsWord;

        // Edge table (parallel arrays, grouped per node, codepoint-sorted).
        private readonly int[] _edgeLabel;   // already un-based (real codepoint)
        private readonly int[] _edgeTarget;

        /// <summary>Unicode script id recorded in the file header (informational).</summary>
        public int ScriptId { get; }

        /// <summary>Number of words encoded (informational).</summary>
        public int WordCount { get; }

        /// <summary>Root node index (always 0).</summary>
        public const int Root = 0;

        public DictionaryTrie(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            using var ms = new MemoryStream(data, false);
            using var r = new BinaryReader(ms);

            if (r.ReadUInt32() != Magic)
                throw new InvalidDataException("OGSD: bad magic.");
            var version = r.ReadUInt32();
            if (version != ExpectedVersion)
                throw new InvalidDataException($"OGSD: unsupported version {version} (expected {ExpectedVersion}).");

            ScriptId = unchecked((int)r.ReadUInt32());
            WordCount = unchecked((int)r.ReadUInt32());
            int cpBase = r.ReadInt32();
            int nodeCount = unchecked((int)r.ReadUInt32());
            int edgeCount = unchecked((int)r.ReadUInt32());

            _nodeFirstEdge = new int[nodeCount];
            _nodeEdgeCount = new ushort[nodeCount];
            _nodeIsWord = new bool[nodeCount];

            for (int i = 0; i < nodeCount; i++)
            {
                uint packed = r.ReadUInt32();
                _nodeFirstEdge[i] = (int)(packed & 0x7FFFFFFF);
                _nodeIsWord[i] = (packed & 0x80000000) != 0;
                _nodeEdgeCount[i] = r.ReadUInt16();
                r.ReadUInt16(); // pad
            }

            _edgeLabel = new int[edgeCount];
            _edgeTarget = new int[edgeCount];
            for (int i = 0; i < edgeCount; i++)
            {
                int label = r.ReadUInt16();
                r.ReadUInt16(); // pad
                _edgeTarget[i] = unchecked((int)r.ReadUInt32());
                _edgeLabel[i] = label + cpBase;
            }
        }

        /// <summary>True if a word terminates at this node.</summary>
        public bool IsWord(int node) => _nodeIsWord[node];

        /// <summary>
        /// Follows the edge labelled <paramref name="codepoint"/> from <paramref name="node"/>.
        /// Returns the target node, or -1 if there is no such edge. Zero allocation.
        /// </summary>
        public int Step(int node, int codepoint)
        {
            int lo = _nodeFirstEdge[node];
            int hi = lo + _nodeEdgeCount[node] - 1;
            var labels = _edgeLabel;
            while (lo <= hi)
            {
                int mid = (int)(((uint)lo + (uint)hi) >> 1);
                int lbl = labels[mid];
                if (lbl == codepoint) return _edgeTarget[mid];
                if (lbl < codepoint) lo = mid + 1;
                else hi = mid - 1;
            }
            return -1;
        }
    }
}
