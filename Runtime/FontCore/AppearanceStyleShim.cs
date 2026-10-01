using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 3: the compatibility shim. Reads an assigned
    /// <see cref="UniTextAppearance"/> / its materials and <b>synthesises the equivalent
    /// <see cref="GlyphStyle"/></b> for the new uber-shader path, so existing scenes that reference
    /// an appearance keep rendering identically with zero asset edits during the deprecation window
    /// (design §3.7).
    /// </summary>
    /// <remarks>
    /// The mapping follows the design doc's property table:
    /// <code>
    ///   _FaceColor / _FaceDilate / _OutlineSoftness        -> face colour, dilate, softness
    ///   _OutlineColor / _OutlineWidth / _OutlineDilate      -> outline
    ///   _UnderlayColor / _UnderlayOffsetX/Y / _UnderlayDilate / _UnderlaySoftness -> underlay (shadow)
    ///   _GlowColor / _GlowOffset / _GlowOuter / _GlowInner / _GlowPower            -> glow
    ///   2-pass pair materials[0]=outline, [1]=face                                -> one multi-layer style
    /// </code>
    /// A null material / missing property falls back to <see cref="GlyphStyle.Default"/>'s value for
    /// that field, so a default appearance maps to the plain opaque-white face the legacy default
    /// renders. Pure read — never mutates the appearance or its materials.
    /// </remarks>
    public static class AppearanceStyleShim
    {
        private static readonly int FaceColor = Shader.PropertyToID("_FaceColor");
        private static readonly int FaceDilate = Shader.PropertyToID("_FaceDilate");
        private static readonly int OutlineSoftness = Shader.PropertyToID("_OutlineSoftness");
        private static readonly int OutlineColor = Shader.PropertyToID("_OutlineColor");
        private static readonly int OutlineWidth = Shader.PropertyToID("_OutlineWidth");
        private static readonly int OutlineDilate = Shader.PropertyToID("_OutlineDilate");
        private static readonly int UnderlayColor = Shader.PropertyToID("_UnderlayColor");
        private static readonly int UnderlayOffsetX = Shader.PropertyToID("_UnderlayOffsetX");
        private static readonly int UnderlayOffsetY = Shader.PropertyToID("_UnderlayOffsetY");
        private static readonly int UnderlayDilate = Shader.PropertyToID("_UnderlayDilate");
        private static readonly int UnderlaySoftness = Shader.PropertyToID("_UnderlaySoftness");
        private static readonly int GlowColor = Shader.PropertyToID("_GlowColor");
        private static readonly int GlowOffset = Shader.PropertyToID("_GlowOffset");
        private static readonly int GlowOuter = Shader.PropertyToID("_GlowOuter");
        private static readonly int GlowInner = Shader.PropertyToID("_GlowInner");
        private static readonly int GlowPower = Shader.PropertyToID("_GlowPower");

        /// <summary>
        /// Synthesises a <see cref="GlyphStyle"/> from an appearance's materials for a given font.
        /// For a 2-pass pair the face layer is <c>materials[1]</c> and the outline layer
        /// <c>materials[0]</c> (the legacy outline-then-face ordering); a single material supplies
        /// both face and outline from its own properties.
        /// </summary>
        public static GlyphStyle StyleFor(UniTextAppearance appearance, UniTextFont font)
        {
            if (appearance == null || font == null)
                return GlyphStyle.Default;
            var mats = appearance.GetMaterials(font);
            return StyleFromMaterials(mats);
        }

        /// <summary>Synthesises a <see cref="GlyphStyle"/> directly from a material array (1 = single pass, 2 = outline+face).</summary>
        public static GlyphStyle StyleFromMaterials(Material[] mats)
        {
            if (mats == null || mats.Length == 0 || mats[0] == null)
                return GlyphStyle.Default;

            // Face comes from the LAST material (face pass in a 2-pass pair, or the only material);
            // outline from the FIRST material (outline pass) when there are two, else the same material.
            Material face = mats[mats.Length - 1] != null ? mats[mats.Length - 1] : mats[0];
            Material outline = mats[0];

            var s = GlyphStyle.Default;

            s.faceColor = GetColor(face, FaceColor, s.faceColor);
            s.faceDilate = GetFloat(face, FaceDilate, s.faceDilate);
            s.softness = GetFloat(face, OutlineSoftness, s.softness);

            s.outlineColor = GetColor(outline, OutlineColor, s.outlineColor);
            s.outlineWidth = GetFloat(outline, OutlineWidth, s.outlineWidth);
            s.outlineDilate = GetFloat(outline, OutlineDilate, s.outlineDilate);

            s.underlayColor = GetColor(face, UnderlayColor, s.underlayColor);
            s.underlayOffsetX = GetFloat(face, UnderlayOffsetX, s.underlayOffsetX);
            s.underlayOffsetY = GetFloat(face, UnderlayOffsetY, s.underlayOffsetY);
            s.underlayDilate = GetFloat(face, UnderlayDilate, s.underlayDilate);
            s.underlaySoftness = GetFloat(face, UnderlaySoftness, s.underlaySoftness);

            s.glowColor = GetColor(face, GlowColor, s.glowColor);
            s.glowOffset = GetFloat(face, GlowOffset, s.glowOffset);
            s.glowOuter = GetFloat(face, GlowOuter, s.glowOuter);
            s.glowInner = GetFloat(face, GlowInner, s.glowInner);
            s.glowPower = GetFloat(face, GlowPower, s.glowPower);

            return s;
        }

        private static Color GetColor(Material m, int id, Color fallback) =>
            m != null && m.HasProperty(id) ? m.GetColor(id) : fallback;

        private static float GetFloat(Material m, int id, float fallback) =>
            m != null && m.HasProperty(id) ? m.GetFloat(id) : fallback;
    }
}
