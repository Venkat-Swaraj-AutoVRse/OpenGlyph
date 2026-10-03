// UniText world-space pass body, shared by the URP and Built-in SubShaders of UniText/World/Uber.
// Unlit by default; _UNITEXT_LIT adds a wrapped-Lambert main directional light + flat ambient,
// _UNITEXT_ALPHACLIP discards low-coverage pixels (used with depth write). Fog via the standard
// FOG_* keywords and unity_FogParams/unity_FogColor (set by both pipelines).
//
// No render-pipeline include is used, so the shader compiles in projects with or without URP. The
// URP pass defines UNITEXT_URP and reads URP's main-light globals (_MainLightPosition,
// _MainLightColor); the Built-in pass reads _WorldSpaceLightPos0/_LightColor0 (ForwardBase).

#ifndef UNITEXT_UBER_WORLD_INCLUDED
#define UNITEXT_UBER_WORLD_INCLUDED

#define UNITEXT_UBER_WORLD 1
#define UNITEXT_UBER_EXTRA_PROPS float _LightWrap; float _AlphaClipThreshold;
#include "UniText_UberCore.cginc"

#if defined(UNITEXT_URP)
    float4 _MainLightPosition;
    half4 _MainLightColor;
    #define UNITEXT_MAIN_LIGHT_DIR   (_MainLightPosition.xyz)
    #define UNITEXT_MAIN_LIGHT_COLOR (_MainLightColor.rgb)
#else
    #include "UnityLightingCommon.cginc"
    #define UNITEXT_MAIN_LIGHT_DIR   (_WorldSpaceLightPos0.xyz)
    #define UNITEXT_MAIN_LIGHT_COLOR (_LightColor0.rgb)
#endif

// Text quads face -Z in object space (the UGUI convention the generator follows).
static const float3 UNITEXT_TEXT_NORMAL = float3(0, 0, -1);

v2f vert(appdata v)
{
    v2f o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_OUTPUT(v2f, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    UberVertex(v, UNITEXT_TEXT_NORMAL, o);

    // A Canvas converts vertex colours to linear in a Linear project before the UI shader sees them;
    // a MeshRenderer does not, so do it here to match the Canvas look. _Color is already linear.
    #ifndef UNITY_COLORSPACE_GAMMA
    o.color = fixed4(GammaToLinearSpace(v.color.rgb), v.color.a) * _Color;
    #endif

    float3 light = float3(1, 1, 1);
    #if defined(_UNITEXT_LIT)
    float3 nWS = UnityObjectToWorldNormal(UNITEXT_TEXT_NORMAL);
    float3 posWS = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1)).xyz;
    // Light the side facing the camera (double-sided text is lit on both faces).
    nWS *= dot(nWS, _WorldSpaceCameraPos.xyz - posWS) < 0 ? -1.0 : 1.0;
    float ndl = dot(nWS, normalize(UNITEXT_MAIN_LIGHT_DIR));
    float diffuse = saturate((ndl + _LightWrap) / (1.0 + _LightWrap));
    light = unity_AmbientSky.rgb + UNITEXT_MAIN_LIGHT_COLOR * diffuse;
    #endif

    float fog = 1;
    #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
    UNITY_CALC_FOG_FACTOR(o.vertex.z);
    fog = saturate(unityFogFactor);
    #endif

    o.worldFx = float4(light, fog);
    return o;
}

fixed4 frag(v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    fixed4 col = UberFragment(i);
    col.rgb *= i.worldFx.rgb;
    // Premultiplied: fog blends toward fogColour * alpha.
    col.rgb = lerp(unity_FogColor.rgb * col.a, col.rgb, i.worldFx.a);

    // Overflow Clip: _ClipRect is the text rect in object space (set per renderer only when used).
    float2 inside = step(_ClipRect.xy, i.worldPos.xy) * step(i.worldPos.xy, _ClipRect.zw);
    col *= inside.x * inside.y;

    #if defined(_UNITEXT_ALPHACLIP)
    clip(col.a - _AlphaClipThreshold);
    #endif
    return col;
}

#endif // UNITEXT_UBER_WORLD_INCLUDED
