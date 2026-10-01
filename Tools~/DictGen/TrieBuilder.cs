using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenGlyph.DictGen
{
    /// <summary>
    /// Builds a minimized DAWG (deterministic acyclic word graph) from a word list
    /// and serializes it to the OpenGlyph segmentation binary format ("OGSD" v2).
    ///
    /// Approach: build the full trie, then minimize bottom-up (post-order) by merging
    /// structurally identical sub-tries. Two nodes are equivalent iff they share the
    /// same isWord flag and the same ordered list of (label, canonical-child) edges.
    /// Because children are canonicalized before their parents, a single pass in
    /// post-order suffices and the result is the unique minimal DAWG.
    ///
    /// ---------------------------------------------------------------------------
    /// BINARY FORMAT ("OGSD" v2) — little-endian throughout
    /// ---------------------------------------------------------------------------
    ///   uint32  magic       = 0x4453474F  ('O','G','S','D')
    ///   uint32  version     = 2
    ///   uint32  scriptId    = UnicodeScript enum value (informational)
    ///   uint32  wordCount   = number of words encoded (informational)
    ///   int32   cpBase      = codepoint subtracted from every edge label before packing
    ///   uint32  nodeCount   = N   (node 0 is the root)
    ///   uint32  edgeCount   = M
    ///   // Node table: N records (12 bytes each)
    ///   //   uint32 firstEdge : 31   index into edge table
    ///   //          isWord    : 1    packed into the high bit
    ///   //   uint16 edgeCount        outgoing edges (sorted by codepoint asc)
    ///   //   uint16 pad
    ///   // Edge table: M records (8 bytes each), grouped per node, codepoint-sorted
    ///   //   uint16 label            (codepoint - cpBase)
    ///   //   uint16 pad
    ///   //   uint32 targetNode
    /// ---------------------------------------------------------------------------
    /// Fixed-width records keep the runtime loader allocation-free (flat int arrays)
    /// and every match step a binary search over a node's contiguous edge slice.
    /// </summary>
    internal sealed class TrieBuilder
    {
        public const uint Magic = 0x4453474F; // 'OGSD'
        public const uint Version = 2;

        private sealed class Node
        {
            public readonly SortedDictionary<int, Node> Children = new SortedDictionary<int, Node>();
            public bool IsWord;
            public int CanonicalId = -1; // assigned during minimization
        }

        private readonly Node _root = new Node();
        private int _wordCount;

        public int WordCount => _wordCount;

        public void Insert(int[] word)
        {
            if (word.Length == 0)
                return;

            var node = _root;
            foreach (var cp in word)
            {
                if (!node.Children.TryGetValue(cp, out var next))
                {
                    next = new Node();
                    node.Children[cp] = next;
                }
                node = next;
            }

            if (!node.IsWord)
            {
                node.IsWord = true;
                _wordCount++;
            }
        }

        public byte[] Serialize(int scriptId, int cpBase)
        {
            // ---- Minimize: post-order canonicalization ----
            var register = new Dictionary<string, Node>();     // structural key -> canonical node
            var canonicalNodes = new List<Node>();             // index == CanonicalId

            Node Canonicalize(Node n)
            {
                // Canonicalize children first (post-order), rewriting edges in place.
                foreach (var label in new List<int>(n.Children.Keys))
                    n.Children[label] = Canonicalize(n.Children[label]);

                var key = StructuralKey(n);
                if (register.TryGetValue(key, out var existing))
                    return existing;

                n.CanonicalId = canonicalNodes.Count;
                canonicalNodes.Add(n);
                register[key] = n;
                return n;
            }

            // Ensure root ends up at index 0: canonicalize its children, then place root first.
            foreach (var label in new List<int>(_root.Children.Keys))
                _root.Children[label] = Canonicalize(_root.Children[label]);

            // Root goes first (index 0). It is never merged with another node because
            // no other node is reachable "as the whole graph", but guard anyway.
            var reordered = new List<Node>(canonicalNodes.Count + 1) { _root };
            _root.CanonicalId = 0;
            foreach (var n in canonicalNodes)
            {
                n.CanonicalId = reordered.Count;
                reordered.Add(n);
            }

            int nodeCount = reordered.Count;
            int edgeCount = 0;
            foreach (var n in reordered)
                edgeCount += n.Children.Count;

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            w.Write(Magic);
            w.Write(Version);
            w.Write((uint)scriptId);
            w.Write((uint)_wordCount);
            w.Write(cpBase);
            w.Write((uint)nodeCount);
            w.Write((uint)edgeCount);

            uint runningEdge = 0;
            foreach (var n in reordered)
            {
                uint packed = runningEdge & 0x7FFFFFFF;
                if (n.IsWord) packed |= 0x80000000;
                w.Write(packed);
                w.Write((ushort)n.Children.Count);
                w.Write((ushort)0);
                runningEdge += (uint)n.Children.Count;
            }

            foreach (var n in reordered)
            {
                foreach (var kv in n.Children) // SortedDictionary => ascending codepoint
                {
                    int label = kv.Key - cpBase;
                    if (label < 0 || label > 0xFFFF)
                        throw new InvalidOperationException(
                            $"edge label {kv.Key} out of 16-bit range from base {cpBase}");
                    w.Write((ushort)label);
                    w.Write((ushort)0);
                    w.Write((uint)kv.Value.CanonicalId);
                }
            }

            w.Flush();
            return ms.ToArray();
        }

        private static string StructuralKey(Node n)
        {
            var sb = new StringBuilder(32);
            sb.Append(n.IsWord ? '1' : '0');
            foreach (var kv in n.Children) // canonicalized children have stable CanonicalId
            {
                sb.Append('|').Append(kv.Key).Append(':').Append(kv.Value.CanonicalId);
            }
            return sb.ToString();
        }
    }
}
