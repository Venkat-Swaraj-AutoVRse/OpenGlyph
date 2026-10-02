using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 2: the draw-group plan for the single-renderer path. Per
    /// decision §5.1 a canvas batch uses <b>at most two</b> draw groups — one bound to the Alpha8
    /// shared array (SDF / coverage-bitmap / pixel) and one bound to the RGBA32 shared array
    /// (MSDF + COLR emoji) — regardless of how many fonts, spans, or outline/shadow styles are used.
    /// This type maps a glyph's render mode to its uber-shader <see cref="GlyphMode"/> and its shared
    /// array <see cref="TextureFormat"/>, and plans which groups a set of usages needs. It is pure
    /// bookkeeping (no GPU), so the "collapse to ≤2 draw calls" property is unit-testable.
    /// </summary>
    public static class UberDrawGroup
    {
        /// <summary>Per-glyph mode selector packed into the uber-shader vertex data (UV1.z).</summary>
        public enum GlyphMode
        {
            Sdf = 0,
            Msdf = 1,
            Bitmap = 2, // Smooth/Mono coverage + pixel-perfect
            Colr = 3,   // COLR / premultiplied color emoji
        }

        /// <summary>Maps a font's effective render mode (+ color-ness) to the per-glyph shader mode.</summary>
        public static GlyphMode ModeFor(UniTextRenderMode mode, bool isColor)
        {
            if (isColor) return GlyphMode.Colr;
            return mode switch
            {
                UniTextRenderMode.Msdf => GlyphMode.Msdf,
                UniTextRenderMode.SDF => GlyphMode.Sdf,
                _ => GlyphMode.Bitmap, // Smooth / Mono coverage
            };
        }

        /// <summary>
        /// The shared-array pixel format a glyph of this mode lands in: RGBA32 for MSDF + color,
        /// Alpha8 for everything else. Equivalent to <see cref="SharedGlyphAtlas.FormatFor"/>; kept
        /// here so the planner has no dependency ordering with the registry.
        /// </summary>
        public static TextureFormat FormatFor(GlyphMode mode) =>
            mode is GlyphMode.Msdf or GlyphMode.Colr ? TextureFormat.RGBA32 : TextureFormat.Alpha8;

        /// <summary>One (font) usage in a text: its mode and color-ness decide its draw group.</summary>
        public readonly struct Usage
        {
            public readonly int fontId;
            public readonly UniTextRenderMode mode;
            public readonly bool isColor;
            public Usage(int fontId, UniTextRenderMode mode, bool isColor)
            {
                this.fontId = fontId; this.mode = mode; this.isColor = isColor;
            }
        }

        /// <summary>
        /// Plans the draw groups for a set of usages: returns the distinct shared-array formats needed
        /// (1 or 2), each of which is exactly one CanvasRenderer + one array binding in the live path.
        /// The result is at most two entries (Alpha8, RGBA32) by construction — the core guarantee.
        /// </summary>
        public static List<TextureFormat> PlanGroups(IEnumerable<Usage> usages)
        {
            bool alpha8 = false, rgba32 = false;
            foreach (var u in usages)
            {
                var f = FormatFor(ModeFor(u.mode, u.isColor));
                if (f == TextureFormat.Alpha8) alpha8 = true;
                else rgba32 = true;
            }
            var groups = new List<TextureFormat>(2);
            if (alpha8) groups.Add(TextureFormat.Alpha8);
            if (rgba32) groups.Add(TextureFormat.RGBA32);
            return groups;
        }

        /// <summary>Number of draw groups (= CanvasRenderers / draw calls) a set of usages needs; 0..2.</summary>
        public static int DrawGroupCount(IEnumerable<Usage> usages) => PlanGroups(usages).Count;
    }
}
