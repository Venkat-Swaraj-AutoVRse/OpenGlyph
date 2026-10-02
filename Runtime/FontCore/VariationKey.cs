using System;
using System.Collections.Generic;

namespace LightSide
{
    /// <summary>
    /// An immutable, hashable key identifying one <em>variation instance</em> of a variable font:
    /// the per-axis design coordinates, quantized so that visually-identical requests collapse to
    /// one atlas/shaper cache entry while distinct instances (e.g. <c>wght</c> 400 vs 700) never do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each axis coordinate is reduced to an integer SLOT by the mapper: a named instance maps to a
    /// reserved exact slot, and a free coordinate buckets at 1% of that axis's (max−min) range by
    /// default (configurable per asset), so animating an axis cannot create unbounded atlas entries.
    /// Equal key ⇒ identical raster, so distinct instances (e.g. <c>wght</c> 400 vs 700) never share
    /// an atlas cell while within-bucket requests collapse to one.
    /// </para>
    /// <para>
    /// <see cref="None"/> is the key for a non-variable face (or the font's default instance); a
    /// static font keeps its original (glyphIndex-only) cache behaviour.
    /// </para>
    /// </remarks>
    public readonly struct VariationKey : IEquatable<VariationKey>
    {
        // Parallel arrays: axis tag (4CC) and its quantized coordinate SLOT (an integer bucket index
        // computed by the mapper per-axis, so a named instance maps to an exact slot and free
        // coordinates bucket at 1% of each axis's range). Null == None.
        private readonly uint[] _tags;
        private readonly int[] _slots;
        private readonly int _hash;

        /// <summary>The key for a non-variable face / default instance.</summary>
        public static readonly VariationKey None = default;

        /// <summary>True when this key carries no variation (a static face / default instance).</summary>
        public bool IsNone => _tags == null || _tags.Length == 0;

        /// <summary>Number of axes captured.</summary>
        public int AxisCount => _tags?.Length ?? 0;

        private VariationKey(uint[] tags, int[] slots)
        {
            _tags = tags;
            _slots = slots;
            unchecked
            {
                int h = -2128831035;
                if (tags != null)
                    for (int i = 0; i < tags.Length; i++)
                    {
                        h = (h ^ (int)tags[i]) * 16777619;
                        h = (h ^ slots[i]) * 16777619;
                    }
                _hash = h;
            }
        }

        /// <summary>
        /// Builds a key from axis tags and their already-quantized integer slots (one per axis).
        /// Tags are sorted so coordinate order never affects identity. The mapper owns quantization
        /// (per-axis 1%-of-range by default, exact for named instances).
        /// </summary>
        public static VariationKey FromQuantized(IReadOnlyList<uint> tags, IReadOnlyList<int> slots)
        {
            if (tags == null || slots == null || tags.Count == 0 || tags.Count != slots.Count)
                return None;

            var idx = new int[tags.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => tags[a].CompareTo(tags[b]));

            var t = new uint[tags.Count];
            var q = new int[tags.Count];
            for (int i = 0; i < idx.Length; i++)
            {
                t[i] = tags[idx[i]];
                q[i] = slots[idx[i]];
            }
            return new VariationKey(t, q);
        }

        /// <summary>Quantized slot at index <paramref name="i"/>.</summary>
        public int SlotAt(int i) => _slots[i];

        /// <summary>Axis tag (4CC) at index <paramref name="i"/>.</summary>
        public uint TagAt(int i) => _tags[i];

        public bool Equals(VariationKey other)
        {
            if (_hash != other._hash) return false;
            if (IsNone) return other.IsNone;
            if (other.IsNone || _tags.Length != other._tags.Length) return false;
            for (int i = 0; i < _tags.Length; i++)
                if (_tags[i] != other._tags[i] || _slots[i] != other._slots[i])
                    return false;
            return true;
        }

        public override bool Equals(object obj) => obj is VariationKey k && Equals(k);
        public override int GetHashCode() => _hash;

        public override string ToString()
        {
            if (IsNone) return "VariationKey.None";
            var sb = new System.Text.StringBuilder("Var{");
            for (int i = 0; i < _tags.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(TagToString(_tags[i])).Append('#').Append(_slots[i]);
            }
            return sb.Append('}').ToString();
        }

        private static string TagToString(uint tag)
        {
            Span<char> c = stackalloc char[4];
            c[0] = (char)((tag >> 24) & 0xFF);
            c[1] = (char)((tag >> 16) & 0xFF);
            c[2] = (char)((tag >> 8) & 0xFF);
            c[3] = (char)(tag & 0xFF);
            return new string(c).Trim();
        }
    }
}
