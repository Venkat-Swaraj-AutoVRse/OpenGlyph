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
        _StyleTexWidth ("Style Table Width", Float) = 12
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
            // Shared program (also used by UniText/World/Uber): UNITY_VERTEX_INPUT_INSTANCE_ID,
            // UNITY_VERTEX_OUTPUT_STEREO live in the appdata / v2f structs there.
            #include "UniText_UberCore.cginc"

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                UberVertex(v, v.normal, o);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = UberFragment(i);
                // UI clip rect (RectMask2D / overflow Clip), in canvas space.
                half2 clip = UnityGet2DClipping(i.worldPos.xy, _ClipRect);
                col *= clip.x * clip.y;
                // Already premultiplied (matches Blend One OneMinusSrcAlpha).
                return col;
            }
            ENDCG
        }
    }
    Fallback "UI/Default"
}
