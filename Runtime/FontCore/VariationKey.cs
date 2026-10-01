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
    /// Coordinates are stored as fixed-point integers at <see cref="Quantum"/> granularity. The SAME
    /// quantization is used when the coordinates are pushed to FreeType
    /// (<see cref="FTVar.SetDesignCoordinates"/>), so "equal key ⇒ identical raster" holds: two
    /// requests that round to the same key produce byte-identical glyphs, and the atlas is shared.
    /// </para>
    /// <para>
    /// <see cref="None"/> is the key for a non-variable face (or the font's default instance); it is
    /// deliberately distinct from any explicit all-defaults key so a static font keeps its original
    /// (glyphIndex-only) cache behaviour.
    /// </para>
    /// </remarks>
    public readonly struct VariationKey : IEquatable<VariationKey>
    {
        /// <summary>Quantization granularity in design units (coords rounded to this step).</summary>
        public const float Quantum = 1f;

        // Parallel arrays: axis tag (4CC) and its quantized coordinate. Null == None.
        private readonly uint[] _tags;
        private readonly int[] _quant;
        private readonly int _hash;

        /// <summary>The key for a non-variable face / default instance.</summary>
        public static readonly VariationKey None = default;

        /// <summary>True when this key carries no variation (a static face / default instance).</summary>
        public bool IsNone => _tags == null || _tags.Length == 0;

        /// <summary>Number of axes captured.</summary>
        public int AxisCount => _tags?.Length ?? 0;

        private VariationKey(uint[] tags, int[] quant)
        {
            _tags = tags;
            _quant = quant;
            unchecked
            {
                int h = -2128831035;
                if (tags != null)
                    for (int i = 0; i < tags.Length; i++)
                    {
                        h = (h ^ (int)tags[i]) * 16777619;
                        h = (h ^ quant[i]) * 16777619;
                    }
                _hash = h;
            }
        }

        /// <summary>
        /// Builds a key from axis tags and their design-space coordinates (floats), quantizing each
        /// coordinate. Tags are sorted so coordinate order never affects identity.
        /// </summary>
        public static VariationKey FromCoords(IReadOnlyList<uint> tags, IReadOnlyList<float> coords)
        {
            if (tags == null || coords == null || tags.Count == 0 || tags.Count != coords.Count)
                return None;

            var idx = new int[tags.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => tags[a].CompareTo(tags[b]));

            var t = new uint[tags.Count];
            var q = new int[tags.Count];
            for (int i = 0; i < idx.Length; i++)
            {
                t[i] = tags[idx[i]];
                q[i] = Quantize(coords[idx[i]]);
            }
            return new VariationKey(t, q);
        }

        /// <summary>Rounds a design coordinate to the <see cref="Quantum"/> grid.</summary>
        public static int Quantize(float coord) =>
            (int)Math.Round(coord / Quantum, MidpointRounding.AwayFromZero);

        /// <summary>Dequantized coordinate for axis <paramref name="i"/> (for pushing to FreeType).</summary>
        public float CoordAt(int i) => _quant[i] * Quantum;

        /// <summary>Axis tag (4CC) at index <paramref name="i"/>.</summary>
        public uint TagAt(int i) => _tags[i];

        public bool Equals(VariationKey other)
        {
            if (_hash != other._hash) return false;
            if (IsNone) return other.IsNone;
            if (other.IsNone || _tags.Length != other._tags.Length) return false;
            for (int i = 0; i < _tags.Length; i++)
                if (_tags[i] != other._tags[i] || _quant[i] != other._quant[i])
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
                sb.Append(TagToString(_tags[i])).Append('=').Append(CoordAt(i).ToString("0.#"));
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
