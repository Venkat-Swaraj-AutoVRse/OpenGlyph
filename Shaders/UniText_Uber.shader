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

            struct appdata
            {
                float4 vertex   : POSITION;
                fixed4 color    : COLOR;
                float4 uv0      : TEXCOORD0; // xy = atlas UV, z = gradientScale, w = xScale
                float4 uv1      : TEXCOORD1; // x = spreadRatio, y = sliceIdx, z = glyphMode, w = styleIdx
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float4 uv0      : TEXCOORD0;
                float4 uv1      : TEXCOORD1;
                float4 worldPos : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.worldPos = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Color;
                o.uv0 = v.uv0;
                o.uv1 = v.uv1;
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
                half softness     = max(scal0.y, 1e-4);
                half outlineWidth = scal0.z;

                half alpha;      // face coverage
                half outAlpha;   // outline coverage
                if (mode == 2)
                {
                    // Coverage bitmap: no SDF thresholding; coverage IS the face mask, no outline.
                    alpha = dist;
                    outAlpha = 0;
                }
                else
                {
                    // SDF/MSDF: the sampled value is a distance field with the edge at 0.5. Use a
                    // SCREEN-SPACE antialias width from fwidth(dist) (one-pixel transition) so the
                    // field is interpreted as a true distance -- a fixed near-zero smoothstep makes
                    // the glyph's padding region (median/alpha > 0.5) opaque and the quad renders as a
                    // solid block (the MSDF bug). `softness`/dilate from the style widen the band.
                    half aa = fwidth(dist) + 1e-4;
                    half edge = 0.5 - faceDilate * 0.5;
                    half band = aa + softness;
                    alpha    = smoothstep(edge - band, edge + band, dist);
                    half oEdge = edge - outlineWidth * 0.5;
                    outAlpha = smoothstep(oEdge - band, oEdge + band, dist);
                }

                // Composite: outline under face; underlay(shadow) under both (same-slice offset sample).
                fixed4 col = faceColor;
                col.a *= alpha;

                // Outline layer (behind face).
                fixed4 outline = outlineColor;
                outline.a *= saturate(outAlpha - alpha);
                col.rgb = lerp(outline.rgb, col.rgb, col.a);
                col.a = max(col.a, outline.a);

                // Underlay / drop shadow (offset sample of the SAME slice).
                if (underlayColor.a > 0 && mode != 2)
                {
                    float2 uOff = float2(scal1.x, scal1.y) * 0.01;
                    half uDist;
                    half4 ut = UNITY_SAMPLE_TEX2DARRAY(_MainTexArray, float3(i.uv0.xy - uOff, slice));
                    uDist = (mode == 1) ? Median3(ut.rgb) : ut.a;
                    half uEdge = 0.5 - scal1.z * 0.5;
                    half uSoft = max(scal1.w, 1e-4);
                    half uA = smoothstep(uEdge - uSoft, uEdge + uSoft, uDist) * underlayColor.a;
                    // Place shadow strictly behind the composited glyph.
                    fixed4 outCol;
                    outCol.rgb = lerp(underlayColor.rgb, col.rgb, col.a);
                    outCol.a = max(col.a, uA);
                    col = outCol;
                }

                // Vertex tint (gradient / per-vertex color) multiplies the whole result.
                col *= i.color;

                // UI clip rect.
                half2 clip = UnityGet2DClipping(i.worldPos.xy, _ClipRect);
                col.a *= clip.x * clip.y;

                // Premultiplied-alpha output (matches Blend One OneMinusSrcAlpha).
                col.rgb *= col.a;
                return col;
            }
            ENDCG
        }
    }
    Fallback "UI/Default"
}
