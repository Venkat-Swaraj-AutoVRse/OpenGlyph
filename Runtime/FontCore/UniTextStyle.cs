using System;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 1: a <b>serializable, component-authored</b> text style
    /// with the same fields as a <see cref="GlyphStyle"/> (a <see cref="StyleTable"/> row): face
    /// colour / dilate / softness, outline colour / width / dilate, underlay (shadow) colour / offset /
    /// dilate / softness, and glow.
    /// </summary>
    /// <remarks>
    /// <para>This is the user requirement's replacement for a <see cref="UniTextAppearance"/> + material
    /// asset: the style lives <b>on the component</b> and is edited in the Inspector. When a component
    /// marks its style active (<c>UniText.OverrideStyle</c>), the unified render path shades every glyph
    /// from this style instead of synthesising one from a legacy appearance/material via
    /// <see cref="AppearanceStyleShim"/>. The shim remains the fallback for components that still
    /// reference a legacy appearance, so old assets keep rendering during the deprecation window.</para>
    /// <para>It is a plain <see cref="Serializable"/> struct (not a <c>ScriptableObject</c>): no external
    /// asset, no material. <see cref="ToGlyphStyle"/> / <see cref="FromGlyphStyle"/> convert losslessly
    /// to and from the runtime <see cref="GlyphStyle"/> the <see cref="StyleTable"/> packs.</para>
    /// </remarks>
    [Serializable]
    public struct UniTextStyle : IEquatable<UniTextStyle>
    {
        [Tooltip("Face (fill) colour of the glyph.")]
        public Color faceColor;
        [Tooltip("Face dilation (edge weight bias), v[-1,1]. 0 = unchanged.")]
        [Range(-1f, 1f)] public float faceDilate;
        [Tooltip("Edge softness / anti-aliasing, v[0,1]. 0 = crisp.")]
        [Range(0f, 1f)] public float softness;

        [Tooltip("Outline colour. Alpha 0 = no outline.")]
        public Color outlineColor;
        [Tooltip("Outline width, v[0,1].")]
        [Range(0f, 1f)] public float outlineWidth;
        [Tooltip("Outline dilation, v[-1,1].")]
        [Range(-1f, 1f)] public float outlineDilate;

        [Tooltip("Underlay (drop-shadow) colour. Alpha 0 = no shadow.")]
        public Color underlayColor;
        [Tooltip("Underlay horizontal offset.")]
        public float underlayOffsetX;
        [Tooltip("Underlay vertical offset.")]
        public float underlayOffsetY;
        [Tooltip("Underlay dilation.")]
        public float underlayDilate;
        [Tooltip("Underlay softness.")]
        public float underlaySoftness;

        [Tooltip("Glow colour. Alpha 0 = no glow. Alpha above 1 brightens the glow (intensity).")]
        public Color glowColor;
        [Tooltip("Shifts where the glow starts (positive = further out).")]
        public float glowOffset;
        [Tooltip("Glow size outside the edge, v[0,1] of the atlas spread.")]
        public float glowOuter;
        [Tooltip("Legacy glow inner extent (desktop legacy SDF/MSDF shaders only).")]
        public float glowInner;
        [Tooltip("Glow falloff exponent: 1 = linear, larger = softer (fades faster), smaller = harder.")]
        public float glowPower;

        [Tooltip("Second outline band (outside the outline). Alpha 0 = none. Unified renderer only.")]
        public Color outline2Color;
        [Tooltip("Second outline width, v[0,1], added outside Outline Width.")]
        [Range(0f, 1f)] public float outline2Width;
        [Tooltip("Second outline edge softness, v[0,1].")]
        [Range(0f, 1f)] public float outline2Softness;

        [Tooltip("Inner shadow colour (drawn inside the face). Alpha 0 = none. Unified renderer only.")]
        public Color innerShadowColor;
        [Tooltip("Inner shadow horizontal offset (same units as the underlay offset).")]
        public float innerShadowOffsetX;
        [Tooltip("Inner shadow vertical offset.")]
        public float innerShadowOffsetY;
        [Tooltip("Inner shadow dilation.")]
        public float innerShadowDilate;
        [Tooltip("Inner shadow softness.")]
        public float innerShadowSoftness;

        /// <summary>A plain opaque-white face with no outline/underlay/glow — matches <see cref="GlyphStyle.Default"/>.</summary>
        public static UniTextStyle Default => FromGlyphStyle(GlyphStyle.Default);

        /// <summary>Converts this authored style to the runtime <see cref="GlyphStyle"/> the style table packs.</summary>
        public GlyphStyle ToGlyphStyle() => new()
        {
            faceColor = faceColor,
            faceDilate = faceDilate,
            softness = softness,
            outlineColor = outlineColor,
            outlineWidth = outlineWidth,
            outlineDilate = outlineDilate,
            underlayColor = underlayColor,
            underlayOffsetX = underlayOffsetX,
            underlayOffsetY = underlayOffsetY,
            underlayDilate = underlayDilate,
            underlaySoftness = underlaySoftness,
            glowColor = glowColor,
            glowOffset = glowOffset,
            glowOuter = glowOuter,
            glowInner = glowInner,
            glowPower = glowPower,
            outline2Color = outline2Color,
            outline2Width = outline2Width,
            outline2Softness = outline2Softness,
            innerShadowColor = innerShadowColor,
            innerShadowOffsetX = innerShadowOffsetX,
            innerShadowOffsetY = innerShadowOffsetY,
            innerShadowDilate = innerShadowDilate,
            innerShadowSoftness = innerShadowSoftness,
        };

        /// <summary>Builds an authored style from a runtime <see cref="GlyphStyle"/> (e.g. a shim-synthesised one, for migration).</summary>
        public static UniTextStyle FromGlyphStyle(in GlyphStyle g) => new()
        {
            faceColor = g.faceColor,
            faceDilate = g.faceDilate,
            softness = g.softness,
            outlineColor = g.outlineColor,
            outlineWidth = g.outlineWidth,
            outlineDilate = g.outlineDilate,
            underlayColor = g.underlayColor,
            underlayOffsetX = g.underlayOffsetX,
            underlayOffsetY = g.underlayOffsetY,
            underlayDilate = g.underlayDilate,
            underlaySoftness = g.underlaySoftness,
            glowColor = g.glowColor,
            glowOffset = g.glowOffset,
            glowOuter = g.glowOuter,
            glowInner = g.glowInner,
            glowPower = g.glowPower,
            outline2Color = g.outline2Color,
            outline2Width = g.outline2Width,
            outline2Softness = g.outline2Softness,
            innerShadowColor = g.innerShadowColor,
            innerShadowOffsetX = g.innerShadowOffsetX,
            innerShadowOffsetY = g.innerShadowOffsetY,
            innerShadowDilate = g.innerShadowDilate,
            innerShadowSoftness = g.innerShadowSoftness,
        };

        public bool Equals(UniTextStyle o) => ToGlyphStyle().Equals(o.ToGlyphStyle());
        public override bool Equals(object obj) => obj is UniTextStyle u && Equals(u);
        public override int GetHashCode() => ToGlyphStyle().GetHashCode();
    }
}
