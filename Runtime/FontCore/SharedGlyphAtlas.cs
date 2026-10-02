using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, step 1 integration: a process-wide registry of shared
    /// <see cref="GlyphAtlasArray"/> instances, keyed by (pixel format, page size). Every
    /// <see cref="UniTextFont"/> publishes its packed glyphs into the array for its format, so all
    /// fonts of a given format share ONE <see cref="Texture2DArray"/> — the property the single
    /// draw-group renderer (sub-task 2) relies on.
    /// </summary>
    /// <remarks>
    /// Per decision §5.1 there are at most two live arrays per size: an <see cref="TextureFormat.Alpha8"/>
    /// array (SDF / coverage / pixel) and an <see cref="TextureFormat.RGBA32"/> array (MSDF + COLR
    /// emoji). The per-array page budget is taken from <see cref="UniTextSettings.SharedAtlasPageBudget"/>
    /// (0 ⇒ unbounded; unchanged behaviour) at array-creation time. This facet is ADDITIVE: the legacy
    /// per-font <c>atlasTextures</c> path remains the source of truth until the renderer is switched
    /// over, so publishing here changes no existing behaviour.
    /// </remarks>
    public static class SharedGlyphAtlas
    {
        private readonly struct ArrayKey : System.IEquatable<ArrayKey>
        {
            public readonly TextureFormat format;
            public readonly int size;
            public ArrayKey(TextureFormat f, int s) { format = f; size = s; }
            public bool Equals(ArrayKey o) => format == o.format && size == o.size;
            public override bool Equals(object o) => o is ArrayKey k && Equals(k);
            public override int GetHashCode() => ((int)format * 397) ^ size;
        }

        private static readonly Dictionary<ArrayKey, GlyphAtlasArray> Arrays = new();

        /// <summary>
        /// Normalizes a glyph render mode to the shared-array pixel format: RGBA32 for MSDF and color
        /// (emoji), Alpha8 for everything else (SDF, Smooth/Mono coverage, pixel). Mirrors decision §5.1.
        /// </summary>
        public static TextureFormat FormatFor(UniTextRenderMode mode, bool isColor)
        {
            if (isColor) return TextureFormat.RGBA32;
            return mode == UniTextRenderMode.Msdf ? TextureFormat.RGBA32 : TextureFormat.Alpha8;
        }

        /// <summary>
        /// Gets (creating on first use) the shared array for a format + page size. The page budget is
        /// read from settings at creation; a later settings change does not retroactively resize an
        /// existing array (call <see cref="ApplyBudgetFromSettings"/> to propagate).
        /// </summary>
        public static GlyphAtlasArray Get(TextureFormat format, int size)
        {
            var key = new ArrayKey(format, size);
            if (!Arrays.TryGetValue(key, out var arr))
            {
                arr = new GlyphAtlasArray(size, format, SafeBudget());
                Arrays[key] = arr;
            }
            return arr;
        }

        private static int SafeBudget()
        {
            // Settings may be unavailable in some test contexts; default to unbounded (0).
            return UniTextSettings.IsNull ? 0 : UniTextSettings.SharedAtlasPageBudget;
        }

        /// <summary>Propagates the current <see cref="UniTextSettings.SharedAtlasPageBudget"/> to every live array.</summary>
        public static void ApplyBudgetFromSettings()
        {
            int b = SafeBudget();
            foreach (var a in Arrays.Values) a.PageBudget = b;
        }

        /// <summary>Number of live shared arrays (at most two per size under decision §5.1).</summary>
        public static int ArrayCount => Arrays.Count;

        /// <summary>Advances the LRU clock on every live array (call once per frame if eviction is enabled).</summary>
        public static void BeginFrame()
        {
            foreach (var a in Arrays.Values) a.BeginFrame();
        }

        /// <summary>Destroys every shared array and clears the registry (test teardown / full reset).</summary>
        public static void Clear()
        {
            foreach (var a in Arrays.Values) a.Dispose();
            Arrays.Clear();
        }
    }
}
