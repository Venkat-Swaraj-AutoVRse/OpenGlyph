using System;
using System.Collections.Generic;
using System.Threading;

namespace LightSide
{
    /// <summary>
    /// Process-wide registry of decompressed font files, so every <see cref="UniTextFont"/> asset whose
    /// stored bytes are the same UTFZ container (e.g. Regular / Bold / Italic assets built from one
    /// variable or multi-style file) shares ONE decompressed array instead of each decoding its own.
    /// </summary>
    /// <remarks>
    /// Entries are keyed by a 128-bit content hash of the compressed container plus its length and
    /// hold the decoded array weakly: once no font (and no pinned FreeType/HarfBuzz handle) references
    /// it, the array is collectable and the entry is pruned on the next lookup. The decoded array is
    /// treated as immutable by every consumer (FreeType and HarfBuzz both read it in place).
    /// </remarks>
    internal static class SharedFontData
    {
        private readonly struct Key : IEquatable<Key>
        {
            private readonly ulong _h1, _h2;
            private readonly int _length;

            public Key(byte[] data)
            {
                _length = data.Length;
                unchecked
                {
                    // Two independent 64-bit FNV-1a style passes over every byte (the container is the
                    // COMPRESSED form, so this is cheaper than the decode it saves).
                    ulong h1 = 14695981039346656037UL, h2 = 0x9E3779B97F4A7C15UL;
                    for (var i = 0; i < data.Length; i++)
                    {
                        h1 = (h1 ^ data[i]) * 1099511628211UL;
                        h2 = (h2 ^ data[i]) * 0x100000001B3UL + 0x632BE59BD9B4E019UL;
                        h2 ^= h2 >> 29;
                    }
                    _h1 = h1;
                    _h2 = h2;
                }
            }

            public bool Equals(Key other) => _h1 == other._h1 && _h2 == other._h2 && _length == other._length;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode() => unchecked((int)_h1 ^ (int)(_h1 >> 32) ^ _length);
        }

        private static readonly Dictionary<Key, WeakReference<byte[]>> entries = new();
        private static readonly object gate = new();
        private static int decodeCount;

        /// <summary>Number of actual decompressions performed (diagnostics/tests).</summary>
        internal static int DecodeCount => Volatile.Read(ref decodeCount);

        /// <summary>
        /// Returns the decompressed bytes for <paramref name="compressed"/>, reusing a live array already
        /// decoded from an identical container. Throws whatever <see cref="FontCompression.Decompress"/>
        /// throws for a corrupt container.
        /// </summary>
        public static byte[] GetOrDecompress(byte[] compressed)
        {
            var key = new Key(compressed);
            lock (gate)
            {
                if (entries.TryGetValue(key, out var weak) && weak.TryGetTarget(out var existing))
                    return existing;
            }

            var decoded = FontCompression.Decompress(compressed);
            Interlocked.Increment(ref decodeCount);

            lock (gate)
            {
                // Another thread may have decoded the same container meanwhile: keep the first.
                if (entries.TryGetValue(key, out var weak) && weak.TryGetTarget(out var existing))
                    return existing;
                PruneDead();
                entries[key] = new WeakReference<byte[]>(decoded);
                return decoded;
            }
        }

        private static void PruneDead()
        {
            List<Key> dead = null;
            foreach (var kv in entries)
                if (!kv.Value.TryGetTarget(out _))
                    (dead ??= new List<Key>()).Add(kv.Key);
            if (dead != null)
                foreach (var k in dead) entries.Remove(k);
        }

        /// <summary>Drops every registry entry (the arrays stay alive while fonts reference them).</summary>
        internal static void Clear()
        {
            lock (gate) entries.Clear();
        }
    }
}
