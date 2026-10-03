Shader "UniText/Uber"
{
    // Render-Architecture Round 2, sub-task 2: ONE shader for a whole UniText component.
    // The three things that used to force a per-font/per-mode/per-style material split are moved
    // into per-glyph DATA this one program reads:
    //   * texture  -> a Texture2DArray (_MainTexArray); per-glyph slice index selects the page.
    //   * mode     -> per-glyph glyphMode (0=SDF, 1=MSDF, 2=bitmap/coverage, 3=COLR/premult color).
    //   * style    -> per-glyph styleIdx into a float-texture style table (_StyleTex).
    // Face + outline + underlay(shadow) are composited in ONE pass from the style record.
    // Target 3.5 keeps it within GLES3.0 / WebGL2 (sampler2DArray is core there; no compute buffers).
    Properties
    {
        [PerRendererData] _MainTexArray ("Atlas Array", 2DArray) = "" {}
        [PerRendererData] _StyleTex ("Style Table", 2D) = "white" {}
        _StyleTexWidth ("Style Table Width", Float) = 8
        _StyleTexHeight ("Style Table Height", Float) = 1

        // Legacy SDF/MSDF coverage constants (ported from UniText/SDF-Face + SDF-Base). These are
        // SHARED defaults (same for every component), so they stay on the shared material without
        // breaking batching; per-component variation rides UV0.z/.w, per-style rides the StyleTable.
        _WeightNormal ("Weight Normal", Float) = 0
        _WeightBold ("Weight Bold", Float) = 1
        _ScaleX ("Scale X", Float) = 1
        _ScaleY ("Scale Y", Float) = 1
        _PerspectiveFilter ("Perspective Correction", Range(0,1)) = 0.875
        _Sharpness ("Sharpness", Range(-1,1)) = 0
        _AtlasSize ("Atlas Slice Size (px)", Float) = 1024

        // UI / masking plumbing (matches UGUI expectations so it works under a CanvasRenderer).
        _Color ("Tint", Color) = (1,1,1,1)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        _ClipRect ("Clip Rect", Vector) = (-32767, -32767, 32767, 32767)
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }

        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "UBER"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            UNITY_DECLARE_TEX2DARRAY(_MainTexArray);
            sampler2D _StyleTex;
            float _StyleTexWidth;
            float _StyleTexHeight;
            fixed4 _Color;
            float4 _ClipRect;
            float _WeightNormal, _WeightBold, _ScaleX, _ScaleY, _PerspectiveFilter, _Sharpness;
            float _AtlasSize;

            struct appdata
            {
                float4 vertex   : POSITION;
                float3 normal   : NORMAL;
                fixed4 color    : COLOR;
                float4 uv0      : TEXCOORD0; // xy = atlas UV, z = gradientScale, w = xScale(signed=bold)
                float4 uv1      : TEXCOORD1; // x = spreadRatio, y = sliceIdx, z = glyphMode, w = styleIdx
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float4 uv0      : TEXCOORD0;
                float4 uv1      : TEXCOORD1;
                float4 worldPos : TEXCOORD2;
                float4 cov      : TEXCOORD3; // x=baseWeight, y=normFactor, z=baseScale (projection-derived, legacy)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.worldPos = v.vertex;
                float4 vPosition = UnityObjectToClipPos(v.vertex);
                o.vertex = vPosition;
                o.color = v.color * _Color;
                o.uv0 = v.uv0;
                o.uv1 = v.uv1;

                // baseWeight + normFactor (legacy coverage).
                float bold = step(v.uv0.w, 0);
                float spreadRatio = v.uv1.x;
                float normFactor = 0.1 /*REFERENCE_SPREAD_RATIO*/ / max(spreadRatio, 0.001);
                float baseWeight = lerp(_WeightNormal, _WeightBold, bold) / 4.0 * 1.0 /*_ScaleRatioA*/ * 0.5;

                // baseScale: the SDF coverage ramp slope in distance-units-per-pixel. Ported VERBATIM
                // from the legacy UniText/SDF-Face vertex shader (TMP's method): derive the true screen
                // pixel size from the clip-space w and the projection matrix, NOT from a fragment
                // ddx/ddy approximation. The old uber port used ddx/ddy(uv0.y)*_AtlasSize*0.75, which
                // UNDER-estimates the scale at small font sizes — the ramp went shallow so thin strokes
                // never saturated to full white (the reported grey/heavier text at ~30pt). This matches
                // the legacy path the unified-vs-legacy pixel-equivalence tests validate against.
                float2 pixelSize = vPosition.w;
                pixelSize /= float2(_ScaleX, _ScaleY) * abs(mul((float2x2)UNITY_MATRIX_P, _ScreenParams.xy));
                float baseScale = rsqrt(dot(pixelSize, pixelSize)) * (_Sharpness + 1);
                if (UNITY_MATRIX_P[3][3] == 0)
                    baseScale = lerp(abs(baseScale) * (1 - _PerspectiveFilter), baseScale,
                        abs(dot(UnityObjectToWorldNormal(v.normal.xyz), normalize(WorldSpaceViewDir(v.vertex)))));

                o.cov = float4(baseWeight, normFactor, baseScale, 0);
                return o;
            }

            // Fetch style texel (row = styleIdx, col = index) from the point-sampled float texture.
            float4 StyleTexel(float styleIdx, int col)
            {
                float u = (col + 0.5) / max(_StyleTexWidth, 1.0);
                float v = (styleIdx + 0.5) / max(_StyleTexHeight, 1.0);
                return tex2Dlod(_StyleTex, float4(u, v, 0, 0));
            }

            half Median3(half3 rgb)
            {
                return max(min(rgb.r, rgb.g), min(max(rgb.r, rgb.g), rgb.b));
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float slice = i.uv1.y;
                int   mode  = (int)round(i.uv1.z);
                float styleIdx = i.uv1.w;

                float3 atlasUVW = float3(i.uv0.xy, slice);
                half4 texel = UNITY_SAMPLE_TEX2DARRAY(_MainTexArray, atlasUVW);

                // --- COLR / premultiplied color emoji (mode 3): sample is the final color. --------
                if (mode == 3)
                {
                    #ifndef UNITY_COLORSPACE_GAMMA
                    // The shared RGBA32 array is linear (it also holds MSDF data), but emoji pixels are
                    // sRGB-encoded like the legacy sRGB page: decode here (un-premultiply first).
                    if (texel.a > 0) texel.rgb = GammaToLinearSpace(texel.rgb / texel.a) * texel.a;
                    #endif
                    half4 c = texel * i.color.a; // modulate by vertex alpha only; emoji keep own color
                    c.rgb *= c.a <= 0 ? 0 : 1;   // guard
                    half2 cp = UnityGet2DClipping(i.worldPos.xy, _ClipRect);
                    c *= cp.x * cp.y;
                    return c;
                }

                // --- Distance / coverage reconstruction -----------------------------------------
                // mode 0 SDF: .a is the signed distance (0.5 == edge)
                // mode 1 MSDF: median(rgb) is the distance
                // mode 2 bitmap/coverage: .a is coverage [0,1], used directly as the face mask
                half dist;
                if (mode == 1)       dist = Median3(texel.rgb);
                else                 dist = texel.a;

                // Style record.
                float4 faceColor     = StyleTexel(styleIdx, 0);
                float4 outlineColor  = StyleTexel(styleIdx, 1);
                float4 underlayColor = StyleTexel(styleIdx, 2);
                float4 scal0         = StyleTexel(styleIdx, 4); // faceDilate, softness, outlineWidth, outlineDilate
                float4 scal1         = StyleTexel(styleIdx, 5); // uOffX, uOffY, uDilate, uSoftness

                half faceDilate   = scal0.x;
                half softness     = max(scal0.y, 0.0);
                half outlineWidth = scal0.z;

                half alpha;      // face coverage
                half outAlpha;   // face+outline extent
                if (mode == 2)
                {
                    // Coverage bitmap: coverage IS the face mask, no SDF ramp, no outline.
                    alpha = dist;
                    outAlpha = 0;
                }
                else
                {
                    // Faithful port of legacy UniText/SDF-Face (face) + SDF-Base (outline):
                    //   d = sample * scale;  coverage = saturate(d - bias)  [linear ramp, SDFLayer]
                    // baseScale is the projection-derived slope computed in the VERTEX shader (legacy
                    // method), NOT a fragment ddx/ddy approximation — the latter went shallow at small
                    // font sizes and left thin strokes grey instead of white.
                    float baseWeight = i.cov.x;
                    float normFactor = i.cov.y;
                    float baseScale = i.cov.z;
                    float xScaleVal = abs(i.uv0.w);
                    float gradientScale = i.uv0.z;
                    float scale = baseScale * xScaleVal * gradientScale;

                    float normFaceEffect = (baseWeight + faceDilate * 0.5) * normFactor;
                    float faceBias = (0.5 - normFaceEffect) * scale - 0.5;
                    alpha = saturate(dist * scale - faceBias);

                    float outScaleSoft = scale / (1.0 + softness * normFactor);
                    float normOutEffect = (baseWeight + (faceDilate + outlineWidth) * 0.5) * normFactor;
                    float outlineBias = (0.5 - normOutEffect) * outScaleSoft - 0.5;
                    outAlpha = saturate(dist * outScaleSoft - outlineBias);

                    // EXTERIOR-FLOOR GATE (fixes the unified "0.5 wall"): the Alpha8/MSDF atlas CLAMPS
                    // the distance field at 0 outside the encoded spread, so where the glyph is fully
                    // outside, `dist == 0`. A dilated bias (e.g. _OutlineDilate/_UnderlayDilate = 1 at
                    // the 0.1 reference spread) makes (0.5 - normEffect) <= 0, so `saturate(0 - bias)`
                    // FLOORS at a positive value — the layer paints solid half-coverage across the
                    // whole padded cell. In the UNIFIED single merged mesh, adjacent glyph quads
                    // (ink + 2*padding wide) overlap, so one glyph's floored layer composites over its
                    // neighbour's ink and darkens it to a hard vertical band (the reported wall). The
                    // field carries NO information below `dist==0`, so coverage there must be 0, not the
                    // bias floor. Gate by field presence: kill only the dist==0 tail (fieldGate->0),
                    // leaving the real ramp (dist>~1/255) and the glyph edge untouched. _AtlasSize*dist
                    // is "sub-texel distance units"; a 0.5-texel knee removes the flat floor without
                    // nibbling the AA edge. Face uses dilate 0 so it never floors, but gating it too
                    // keeps the three layers consistent and costs nothing where dist>0.
                    float fieldGate = saturate(dist * _AtlasSize * 2.0);
                    alpha *= fieldGate;
                    outAlpha *= fieldGate;
                }

                // Composite like legacy (SDF-Base outline, then SDF-Face over via BlendOver):
                // the outline layer fills the FULL outAlpha extent (premultiplied), then the face is
                // composited on top. Previously the outline was only the (outAlpha - alpha) ring,
                // which under-drew the outline (regression). faceColor/outlineColor are straight
                // (non-premultiplied) in the style table; premultiply here to match the One/1-SrcA blend.
                fixed4 face = faceColor; face.rgb *= face.a; face.a *= alpha; face.rgb *= alpha;
                fixed4 outline = outlineColor; outline.rgb *= outline.a; outline.a *= outAlpha; outline.rgb *= outAlpha;
                // BlendOver(dst=outline, src=face): dst.rgb*(1-src.a)+src.rgb ; dst.a saturate(+src.a)
                fixed4 col;
                col.rgb = outline.rgb * (1 - face.a) + face.rgb;
                col.a = saturate(outline.a + face.a);

                // Underlay / drop shadow (offset sample of the SAME slice). Faithful port of legacy
                // UniText/SDF-SSD: the offset uses ComputeUnderlayOffsetFactor (NOT a flat texel step),
                // the ramp is SDFLayer, and the layer is composited BEHIND face+outline via premultiplied
                // BlendOver (NOT a max/lerp). This matches legacy's underlay extent and blend exactly.
                if (underlayColor.a > 0 && mode != 2)
                {
                    float baseWeight = i.cov.x; float normFactor = i.cov.y;
                    float gradientScale = i.uv0.z;
                    float scale = i.cov.z * abs(i.uv0.w) * gradientScale;
                    // Legacy ComputeUnderlayOffsetFactor(gradientScale, normFactor):
                    //   pointSizeApprox = sqrt(72 * gradientScale * normFactor / REFERENCE_SPREAD_RATIO)
                    //   offsetFactor    = pointSizeApprox / 9     (REFERENCE_SPREAD_RATIO = 0.1)
                    // Legacy vertex: offset = -(_UnderlayOffset * _ScaleRatioC) * offsetFactor * texel,
                    // texel = _MainTex_TexelSize = 1/_AtlasSize. _ScaleRatioC = 1 (shared default).
                    float offsetFactor = sqrt(72.0 * gradientScale * normFactor / 0.1) / 9.0;
                    float texel = 1.0 / max(_AtlasSize, 1.0);
                    float2 uOff = float2(scal1.x, scal1.y) * offsetFactor * texel; // scal1.xy = _UnderlayOffsetX/Y
                    half4 ut = UNITY_SAMPLE_TEX2DARRAY(_MainTexArray, float3(i.uv0.xy - uOff, slice));
                    half uDist = (mode == 1) ? Median3(ut.rgb) : ut.a;
                    // Legacy extent: underlaySoftnessFactor = _UnderlaySoftness*_ScaleRatioC*normFactor;
                    // layerScale = scale/(1+that); underlayDilate = _FaceDilate*_ScaleRatioA +
                    // _UnderlayDilate*_ScaleRatioC; normalizedUnderlayEffect = (baseWeight +
                    // underlayDilate*0.5)*normFactor; layerBias = (0.5-that)*layerScale - 0.5.
                    float layerScale = scale / (1.0 + max(scal1.w, 0.0) * normFactor);
                    float underlayDilate = faceDilate + scal1.z; // _FaceDilate + _UnderlayDilate (ratios=1)
                    float normUEffect = (baseWeight + underlayDilate * 0.5) * normFactor;
                    float layerBias = (0.5 - normUEffect) * layerScale - 0.5;
                    half ud = uDist * layerScale;
                    // SDFLayer(ud, layerBias, underlayColor) with PREMULTIPLIED underlay colour, then
                    // BlendOver(dst=underlay, src=col): the underlay sits behind the face+outline.
                    // Exterior-floor gate (see face layer): the underlay samples its OWN offset texel,
                    // so gate by THAT sample (uDist). Where the shadow field is at its clamped-0 floor
                    // the underlay must contribute 0, not the layerBias floor that otherwise paints a
                    // solid half-coverage rectangle over the padded cell (the overlapping-neighbour wall).
                    float uFieldGate = saturate(uDist * _AtlasSize * 2.0);
                    fixed4 uPremult = underlayColor; uPremult.rgb *= uPremult.a;
                    fixed4 uResult = uPremult * (saturate(ud - layerBias) * uFieldGate);
                    fixed4 blended;
                    blended.rgb = uResult.rgb * (1 - col.a) + col.rgb;
                    blended.a = saturate(uResult.a + col.a);
                    col = blended;
                }

                // Vertex tint (gradient / per-vertex <color>). col is already PREMULTIPLIED, so tint
                // rgb by vertex rgb and scale the whole premultiplied value by vertex alpha.
                col.rgb *= i.color.rgb;
                col *= i.color.a;

                // UI clip rect.
                half2 clip = UnityGet2DClipping(i.worldPos.xy, _ClipRect);
                col *= clip.x * clip.y;

                // Already premultiplied (matches Blend One OneMinusSrcAlpha) — no further premultiply.
                return col;
            }
            ENDCG
        }
    }
    Fallback "UI/Default"
}
