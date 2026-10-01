Shader "Hidden/OpenGlyphArrayPreview"
{
    // TEST-ONLY neutral display shader for the pixel-equivalence harness. Samples the shared
    // Texture2DArray atlas (_MainTexArray) at the per-vertex slice (UV1.y) and reconstructs coverage
    // with the SAME math as the Texture2D neutral shader in EngineRenderHarness: SDF uses .a, MSDF
    // (glyphMode==1 in UV1.z) uses median(rgb); smoothstep around 0.5 for the AA edge. This lets the
    // UNIFIED mesh be compared against the LEGACY mesh through MATCHED neutral shaders, so a pixel
    // diff isolates geometry/UV/atlas-content fidelity rather than production-shader AA constants.
    Properties { }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            UNITY_DECLARE_TEX2DARRAY(_MainTexArray);
            struct appdata { float4 vertex:POSITION; float4 uv0:TEXCOORD0; float4 uv1:TEXCOORD1; float4 color:COLOR; };
            struct v2f { float4 pos:SV_POSITION; float4 uv0:TEXCOORD0; float4 uv1:TEXCOORD1; float4 col:COLOR; };
            v2f vert(appdata v){ v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv0=v.uv0; o.uv1=v.uv1; o.col=v.color; return o; }
            half med3(half3 c){ return max(min(c.r,c.g),min(max(c.r,c.g),c.b)); }
            fixed4 frag(v2f i):SV_Target {
                half4 t = UNITY_SAMPLE_TEX2DARRAY(_MainTexArray, float3(i.uv0.xy, i.uv1.y));
                int mode = (int)round(i.uv1.z);
                half d = (mode == 1) ? med3(t.rgb) : t.a;
                half aa = fwidth(d) + 1e-4;
                half cov = smoothstep(0.5 - aa, 0.5 + aa, d);
                return fixed4(i.col.rgb, i.col.a * cov);
            }
            ENDCG
        }
    }
}
