// =============================================================================
// THROWAWAY SPIKE CODE — Round 1 feasibility proof. NOT production.
// Proves: one shader renders SDF + MSDF + bitmap(coverage) + COLR(color) glyphs
// from a single Texture2DArray, with per-glyph mode + per-span style selected
// from a StructuredBuffer, in ONE draw call. See Documentation/Design/
// RenderArchitecture.md §3. Delete after the architecture is validated.
// Clean-room: authored from public SDF/MSDF shading knowledge + this repo's
// UniText_Properties.cginc / UniText_MSDF.cginc; nothing from UniText 2.0.
// =============================================================================
Shader "OpenGlyphSpike/Uber"
{
    Properties
    {
        _MainTexArray ("Glyph Atlas Array", 2DArray) = "" {}
        _ClipRect ("Clip", Vector) = (-32767,-32767,32767,32767)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" }
        Lighting Off Cull Off ZTest [unity_GUIZTestMode] ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma require 2darray
            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2DARRAY(_MainTexArray);
            float4 _ClipRect;

            // Per-span / per-glyph style record. One upload per text; styleIdx in UV1.w.
            // Mirrors the TMP-style set in UniText_Properties.cginc, trimmed to what the
            // spike shades: face, outline, underlay(shadow). (glow/bevel/gradient fold in
            // the same way — omitted here only to keep the proof small.)
            struct GlyphStyle
            {
                float4 faceColor;
                float4 outlineColor;
                float4 underlayColor;
                float  outlineWidth;   // 0..1 in distance units
                float  faceDilate;     // -1..1
                float  underlayOffX;   // in UV units
                float  underlayOffY;
            };
            StructuredBuffer<GlyphStyle> _Styles;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float4 uv0    : TEXCOORD0; // atlasU, atlasV, gradientScale, xScale
                float4 uv1    : TEXCOORD1; // spreadRatio, sliceIdx, glyphMode, styleIdx
            };
            struct v2f
            {
                float4 pos    : SV_POSITION;
                fixed4 color  : COLOR;
                float4 uv0    : TEXCOORD0;
                float4 uv1    : TEXCOORD1;
                float2 wpos   : TEXCOORD2; // for clip rect
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv0 = v.uv0;
                o.uv1 = v.uv1;
                o.wpos = v.vertex.xy;
                return o;
            }

            half median3(half3 c){ return max(min(c.r,c.g), min(max(c.r,c.g), c.b)); }

            fixed4 frag(v2f i) : SV_Target
            {
                float slice     = i.uv1.y;
                int   glyphMode  = (int)round(i.uv1.z);  // 0 SDF, 1 MSDF, 2 bitmap, 3 COLR
                int   styleIdx   = (int)round(i.uv1.w);
                GlyphStyle s = _Styles[styleIdx];

                float3 uvw = float3(i.uv0.xy, slice);
                half4 texel = UNITY_SAMPLE_TEX2DARRAY(_MainTexArray, uvw);

                fixed4 outCol;
                if (glyphMode == 3)
                {
                    // COLR / color emoji: texel is premultiplied-ish RGBA, drawn as-is * tint.
                    outCol = texel * i.color;
                }
                else if (glyphMode == 2)
                {
                    // Bitmap / coverage: alpha coverage * face colour.
                    outCol = fixed4(s.faceColor.rgb, texel.a * s.faceColor.a) * i.color;
                }
                else
                {
                    // Distance field: SDF uses .a, MSDF uses median(rgb). Single branch, one line.
                    half d = (glyphMode == 1) ? median3(texel.rgb) : texel.a;
                    half scale = i.uv0.z * i.uv0.w; // gradientScale * xScale (as in current SDF path)
                    scale = max(scale, 1.0);

                    half face = saturate((d - (0.5 - s.faceDilate)) * scale + 0.5);
                    // Outline: widen the threshold inward by outlineWidth.
                    half outer = saturate((d - (0.5 - s.faceDilate - s.outlineWidth)) * scale + 0.5);
                    fixed4 faceC = s.faceColor * i.color;
                    fixed4 col = lerp(s.outlineColor, faceC, face);
                    col.a = outer * (s.outlineWidth > 0 ? 1 : face);
                    // Underlay / shadow: offset sample, composited behind.
                    if (s.underlayColor.a > 0)
                    {
                        half dU = (glyphMode == 1)
                            ? median3(UNITY_SAMPLE_TEX2DARRAY(_MainTexArray, float3(i.uv0.xy + float2(s.underlayOffX, s.underlayOffY), slice)).rgb)
                            : UNITY_SAMPLE_TEX2DARRAY(_MainTexArray, float3(i.uv0.xy + float2(s.underlayOffX, s.underlayOffY), slice)).a;
                        half shadow = saturate((dU - 0.5) * scale + 0.5) * s.underlayColor.a;
                        col = lerp(fixed4(s.underlayColor.rgb, shadow), col, col.a);
                        col.a = max(col.a, shadow);
                    }
                    outCol = col;
                }

                // UI clip rect (both Overlay and World canvases set _ClipRect via material).
                float2 inside = step(_ClipRect.xy, i.wpos) * step(i.wpos, _ClipRect.zw);
                outCol.a *= inside.x * inside.y;
                return outCol;
            }
            ENDCG
        }
    }
    Fallback Off
}
