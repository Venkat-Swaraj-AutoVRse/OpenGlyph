Shader "UniText/World/Uber"
{
    // World-space (MeshRenderer) variant of UniText/Uber, used by UniTextWorld and GlyphMeshPro.
    // Same per-glyph data model (Texture2DArray page, glyph mode and style row in UV1), so one shared
    // material draws every world label that uses the same options: one draw per renderer, and the
    // SRP Batcher can batch them (all properties are in UnityPerMaterial; no per-renderer data).
    //
    // Options are material state, chosen per component and shared by every component that picks
    // the same combination: lit/unlit, depth write (with alpha clip), double-sided (Cull Off).
    // SubShader 1 runs under URP (UniversalForward), SubShader 2 under the Built-in pipeline.
    Properties
    {
        [PerRendererData] _MainTexArray ("Atlas Array", 2DArray) = "" {}
        [PerRendererData] _StyleTex ("Style Table", 2D) = "white" {}
        _StyleTexWidth ("Style Table Width", Float) = 12
        _StyleTexHeight ("Style Table Height", Float) = 1

        _WeightNormal ("Weight Normal", Float) = 0
        _WeightBold ("Weight Bold", Float) = 1
        _ScaleX ("Scale X", Float) = 1
        _ScaleY ("Scale Y", Float) = 1
        _PerspectiveFilter ("Perspective Correction", Range(0,1)) = 0.875
        _Sharpness ("Sharpness", Range(-1,1)) = 0
        _AtlasSize ("Atlas Slice Size (px)", Float) = 1024
        _Color ("Tint", Color) = (1,1,1,1)
        _ClipRect ("Clip Rect (object space)", Vector) = (-32767, -32767, 32767, 32767)

        [Toggle(_UNITEXT_LIT)] _Lit ("Lit (main light + ambient)", Float) = 0
        _LightWrap ("Light Wrap (0 = Lambert, 1 = half-Lambert)", Range(0,1)) = 0.5
        [Toggle(_UNITEXT_ALPHACLIP)] _AlphaClip ("Alpha Clip", Float) = 0
        _AlphaClipThreshold ("Alpha Clip Threshold", Range(0,1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Enum(Off,0,On,1)] _ZWrite ("Depth Write", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Depth Test", Float) = 4
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }

        Pass
        {
            Name "UniTextWorld"
            Tags { "LightMode"="UniversalForward" }
            Cull [_Cull]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma shader_feature_local _ _UNITEXT_LIT
            #pragma shader_feature_local _ _UNITEXT_ALPHACLIP
            #define UNITEXT_URP 1
            // Stereo (UNITY_VERTEX_INPUT_INSTANCE_ID / UNITY_VERTEX_OUTPUT_STEREO) is declared in the
            // appdata / v2f structs of UniText_UberCore.cginc; vert/frag below set them up:
            // UNITY_SETUP_INSTANCE_ID, UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO (UniText_UberWorld.cginc).
            #include "UniText_UberWorld.cginc"
            ENDCG
        }
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }

        Pass
        {
            Name "UniTextWorld"
            Tags { "LightMode"="ForwardBase" }
            Cull [_Cull]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma shader_feature_local _ _UNITEXT_LIT
            #pragma shader_feature_local _ _UNITEXT_ALPHACLIP
            #include "UniText_UberWorld.cginc"
            ENDCG
        }
    }
    Fallback Off
}
