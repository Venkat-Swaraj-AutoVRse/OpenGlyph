using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Reads a variable font's <c>fvar</c> axes/named-instances (via the existing <see cref="FTVar"/>
    /// native exports) and maps a <see cref="FontStyleSpec"/> onto design-space axis coordinates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Standard registered axes are mapped from the request: <c>wght</c>←weight, <c>wdth</c>←width,
    /// <c>ital</c>←italic (0/1), <c>slnt</c>←oblique slant (degrees), <c>opsz</c>←optical size
    /// (render point size). Each is clamped to that axis's min/max. Axes the font does not expose are
    /// skipped; axes not driven by the request keep their default.
    /// </para>
    /// <para>
    /// Coordinates are DESIGN-space values handed to <see cref="FTVar.SetDesignCoordinates"/>, which
    /// applies the font's <c>avar</c> segment map inside FreeType; the same user-space values go to
    /// HarfBuzz via <see cref="FTVar.SetHbVariations"/> so shaping advances match the rasterized face.
    /// </para>
    /// </remarks>
    public sealed class VariationMapper
    {
        /// <summary>Registered OpenType axis tags.</summary>
        public static readonly uint WGHT = FTVar.Tag("wght");
        public static readonly uint WDTH = FTVar.Tag("wdth");
        public static readonly uint ITAL = FTVar.Tag("ital");
        public static readonly uint SLNT = FTVar.Tag("slnt");
        public static readonly uint OPSZ = FTVar.Tag("opsz");

        public readonly bool isVariable;
        internal readonly FTVar.VarAxis[] axes;
        public readonly int namedInstanceCount;

        /// <summary>Number of fvar axes (0 for a static font).</summary>
        public int AxisCount => axes?.Length ?? 0;

        private VariationMapper(bool isVariable, FTVar.VarAxis[] axes, int namedInstanceCount)
        {
            this.isVariable = isVariable;
            this.axes = axes ?? Array.Empty<FTVar.VarAxis>();
            this.namedInstanceCount = namedInstanceCount;
        }

        /// <summary>
        /// Reads the fvar table of a loaded FreeType face. Returns a mapper describing its axes;
        /// <see cref="isVariable"/> is false for a static font.
        /// </summary>
        public static VariationMapper Read(IntPtr face)
        {
            if (!FTVar.TryGetVariationInfo(face, out int axisCount, out int namedCount) || axisCount == 0)
                return new VariationMapper(false, null, 0);

            var axes = new FTVar.VarAxis[axisCount];
            for (int i = 0; i < axisCount; i++)
                FTVar.TryGetAxis(face, i, out axes[i]);

            return new VariationMapper(true, axes, namedCount);
        }

        /// <summary>Returns the index of <paramref name="tag"/> in <see cref="axes"/>, or -1.</summary>
        public int AxisIndex(uint tag)
        {
            for (int i = 0; i < axes.Length; i++)
                if (axes[i].tag == tag) return i;
            return -1;
        }

        private float Clamp(int axisIndex, float v) =>
            Mathf.Clamp(v, axes[axisIndex].min, axes[axisIndex].max);

        /// <summary>Default number of quantization buckets across each axis range (1% granularity).</summary>
        public const int DefaultBuckets = 100;

        // Quantizes a clamped coordinate on axis i into an integer slot in [0, buckets]. A zero-range
        // axis (min==max) always yields slot 0. This is what caps atlas entries when an axis is
        // swept continuously.
        private int Slot(int i, float clamped, int buckets)
        {
            float range = axes[i].max - axes[i].min;
            if (range <= 0f || buckets <= 0) return 0;
            float t = (clamped - axes[i].min) / range;        // 0..1
            return Mathf.Clamp(Mathf.RoundToInt(t * buckets), 0, buckets);
        }

        /// <summary>
        /// Maps <paramref name="spec"/> (and optical size) to the full design-coordinate vector plus
        /// a quantized <see cref="VariationKey"/>. Free coordinates bucket at 1/<paramref name="buckets"/>
        /// of each axis range (default 1%).
        /// </summary>
        /// <param name="spec">Requested weight/width/style.</param>
        /// <param name="opticalSize">
        /// Render point size used to auto-drive <c>opsz</c> (CSS font-optical-sizing: auto). Pass ≤0
        /// to leave <c>opsz</c> at its default (optical sizing off). Ignored when
        /// <paramref name="opszExplicit"/> is true.
        /// </param>
        /// <param name="tags">Output: axis tags in axis order.</param>
        /// <param name="coords">Output: design coords in axis order (for FreeType).</param>
        /// <param name="buckets">Quantization buckets per axis (default 100 == 1%).</param>
        /// <param name="opszExplicit">
        /// True when the caller set <c>opsz</c> explicitly (via <paramref name="explicitOpsz"/>); then
        /// the explicit value wins and <paramref name="opticalSize"/> is NOT auto-applied.
        /// </param>
        /// <param name="explicitOpsz">The explicit opsz value, used only when <paramref name="opszExplicit"/>.</param>
        /// <returns>The variation key, or <see cref="VariationKey.None"/> for a static font.</returns>
        public VariationKey Map(FontStyleSpec spec, float opticalSize, out uint[] tags, out float[] coords,
            int buckets = DefaultBuckets, bool opszExplicit = false, float explicitOpsz = 0f)
        {
            tags = null; coords = null;
            if (!isVariable || axes.Length == 0)
                return VariationKey.None;

            tags = new uint[axes.Length];
            coords = new float[axes.Length];
            var slots = new int[axes.Length];

            for (int i = 0; i < axes.Length; i++)
            {
                tags[i] = axes[i].tag;
                float v = axes[i].def; // start at the axis default

                if (axes[i].tag == WGHT) v = spec.weight;
                else if (axes[i].tag == WDTH) v = spec.width;
                else if (axes[i].tag == ITAL) v = spec.style == StyleAxis.Italic ? 1f : 0f;
                else if (axes[i].tag == SLNT) v = spec.style == StyleAxis.Oblique ? spec.slant : axes[i].def;
                else if (axes[i].tag == OPSZ)
                {
                    if (opszExplicit) v = explicitOpsz;              // explicit wins
                    else if (opticalSize > 0f) v = opticalSize;      // auto from rendered size
                    // else leave at default (optical sizing off)
                }

                coords[i] = Clamp(i, v);
                slots[i] = Slot(i, coords[i], buckets);
            }

            return VariationKey.FromQuantized(tags, slots);
        }
    }
}
