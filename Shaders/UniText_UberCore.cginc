// UniText Uber core: the per-glyph data-driven SDF / MSDF / bitmap / colour-emoji program shared by
// the Canvas shader (UniText/Uber) and the world-space MeshRenderer shader (UniText/World/Uber).
//
// Per-glyph data (no material split):
//   * texture -> Texture2DArray (_MainTexArray); per-glyph slice index selects the page.
//   * mode    -> per-glyph glyphMode (0=SDF, 1=MSDF, 2=bitmap/coverage, 3=COLR/premult color).
//   * style   -> per-glyph styleIdx into a float-texture style table (_StyleTex).
// Face + outline + underlay(shadow) + wave-2 layers are composited in ONE pass from the style record.
//
// The including pass defines the entry points; it calls UberVertex() and UberFragment(). The
// fragment result is PREMULTIPLIED (Blend One OneMinusSrcAlpha) and not yet clipped.
// Define UNITEXT_UBER_WORLD before including to add the world-space interpolator (lighting + fog).
// Define UNITEXT_UBER_EXTRA_PROPS to add material properties to the UnityPerMaterial buffer.

#ifndef UNITEXT_UBER_CORE_INCLUDED
#define UNITEXT_UBER_CORE_INCLUDED

#include "UnityCG.cginc"

UNITY_DECLARE_TEX2DARRAY(_MainTexArray);
sampler2D _StyleTex;

// Every material property lives in UnityPerMaterial so the world shader is SRP-Batcher compatible
// (one buffer layout for every material of this shader). Harmless for the Canvas shader.
CBUFFER_START(UnityPerMaterial)
    float _StyleTexWidth;
    float _StyleTexHeight;
    fixed4 _Color;
    float4 _ClipRect;
    float _WeightNormal, _WeightBold, _ScaleX, _ScaleY, _PerspectiveFilter, _Sharpness;
    float _AtlasSize;
#ifdef UNITEXT_UBER_EXTRA_PROPS
    UNITEXT_UBER_EXTRA_PROPS
#endif
CBUFFER_END

struct appdata
{
    float4 vertex   : POSITION;
    float3 normal   : NORMAL;
    fixed4 color    : COLOR;
    float4 uv0      : TEXCOORD0; // xy = atlas UV, z = gradientScale, w = xScale(signed=bold)
    float4 uv1      : TEXCOORD1; // x = spreadRatio, y = sliceIdx, z = glyphMode, w = styleIdx
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct v2f
{
    float4 vertex   : SV_POSITION;
    fixed4 color    : COLOR;
    float4 uv0      : TEXCOORD0;
    float4 uv1      : TEXCOORD1;
    float4 worldPos : TEXCOORD2; // object-space position (UI clip rect space)
    float4 cov      : TEXCOORD3; // x=baseWeight, y=normFactor, z=baseScale (projection-derived, legacy), w=outline2Softness
    // Wave 2 effect layers, fetched from the style row ONCE per vertex (the style is constant
    // over a glyph quad) so a glyph without them costs no extra fragment texture reads.
    float4 glowColor : TEXCOORD4;  // rgba (a may exceed 1 = intensity)
    float4 glowP     : TEXCOORD5;  // glowOffset, glowOuter, glowPower, outline2Width
    float4 o2Color   : TEXCOORD6;  // outline2 rgba
    float4 isColor   : TEXCOORD7;  // inner shadow rgba
    float4 isP       : TEXCOORD8;  // inner shadow offX, offY, dilate, softness
#ifdef UNITEXT_UBER_WORLD
    float4 worldFx   : TEXCOORD9;  // rgb = lighting multiplier, a = fog factor (1 = no fog)
#endif
    UNITY_VERTEX_OUTPUT_STEREO // single-pass instanced / multiview (Quest)
};

// Fetch style texel (row = styleIdx, col = index) from the point-sampled float texture.
float4 StyleTexel(float styleIdx, int col)
{
    float u = (col + 0.5) / max(_StyleTexWidth, 1.0);
    float v = (styleIdx + 0.5) / max(_StyleTexHeight, 1.0);
    return tex2Dlod(_StyleTex, float4(u, v, 0, 0));
}

// Fills every v2f field except the stereo/instance plumbing (the caller initialises those) and
// worldFx. objNormal is the object-space normal used by the perspective filter.
void UberVertex(appdata v, float3 objNormal, inout v2f o)
{
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
    // ddx/ddy approximation (which went shallow at small sizes: grey, heavy strokes).
    float2 pixelSize = vPosition.w;
    pixelSize /= float2(_ScaleX, _ScaleY) * abs(mul((float2x2)UNITY_MATRIX_P, _ScreenParams.xy));
    float baseScale = rsqrt(dot(pixelSize, pixelSize)) * (_Sharpness + 1);
    if (UNITY_MATRIX_P[3][3] == 0)
        baseScale = lerp(abs(baseScale) * (1 - _PerspectiveFilter), baseScale,
            abs(dot(UnityObjectToWorldNormal(objNormal), normalize(WorldSpaceViewDir(v.vertex)))));

    o.cov = float4(baseWeight, normFactor, baseScale, 0);

    // Wave 2: effect layer parameters (glow, second outline, inner shadow) per glyph.
    float styleIdxV = v.uv1.w;
    float4 o2Scal = StyleTexel(styleIdxV, 9);   // outline2Width, outline2Softness
    float4 glowRaw = StyleTexel(styleIdxV, 6);  // glowOffset, glowOuter, glowInner, glowPower
    o.glowColor = StyleTexel(styleIdxV, 3);
    o.glowP = float4(glowRaw.x, glowRaw.y, glowRaw.w, o2Scal.x);
    o.o2Color = StyleTexel(styleIdxV, 7);
    o.isColor = StyleTexel(styleIdxV, 8);
    o.isP = StyleTexel(styleIdxV, 10);
    o.cov.w = o2Scal.y;
}

half Median3(half3 rgb)
{
    return max(min(rgb.r, rgb.g), min(max(rgb.r, rgb.g), rgb.b));
}

// Premultiplied glyph colour (face, outlines, effects, underlay, vertex alpha). No clip.
fixed4 UberFragment(v2f i)
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

        // EXTERIOR-FLOOR GATE (fixes the unified "0.5 wall"): the atlas clamps the distance at 0
        // outside the encoded spread; a dilated bias would floor coverage there and paint the whole
        // padded cell over overlapping neighbours. Kill only the dist==0 tail.
        float fieldGate = saturate(dist * _AtlasSize * 2.0);
        alpha *= fieldGate;
        outAlpha *= fieldGate;
    }

    // Composite like legacy (SDF-Base outline, then SDF-Face over via BlendOver). Style colours are
    // straight (non-premultiplied); premultiply here to match the One/1-SrcA blend.
    fixed4 face = faceColor; face.rgb *= face.a; face.a *= alpha; face.rgb *= alpha;
    fixed4 outline = outlineColor; outline.rgb *= outline.a; outline.a *= outAlpha; outline.rgb *= outAlpha;
    fixed4 col;
    col.rgb = outline.rgb * (1 - face.a) + face.rgb;
    col.a = saturate(outline.a + face.a);

    // ---- Wave 2 layers (each costs nothing unless its colour alpha > 0) ----------------
    float fxGate = 1.0;
    float fxNorm = i.cov.y;
    float fxBaseW = i.cov.x;
    float fxScale = i.cov.z * abs(i.uv0.w) * i.uv0.z;
    if (mode != 2) fxGate = saturate(dist * _AtlasSize * 2.0);

    // Inner shadow: the face minus a shifted copy of the glyph, drawn ON the face.
    if (i.isColor.a > 0 && mode != 2)
    {
        float offsetFactorI = sqrt(72.0 * i.uv0.z * fxNorm / 0.1) / 9.0;
        float2 iOff = i.isP.xy * offsetFactorI / max(_AtlasSize, 1.0);
        half4 it = UNITY_SAMPLE_TEX2DARRAY(_MainTexArray, float3(i.uv0.xy - iOff, slice));
        half iDist = (mode == 1) ? Median3(it.rgb) : it.a;
        float iScale = fxScale / (1.0 + max(i.isP.w, 0.0) * fxNorm);
        float iNorm = (fxBaseW + (faceDilate + i.isP.z) * 0.5) * fxNorm;
        float iBias = (0.5 - iNorm) * iScale - 0.5;
        half shifted = saturate(iDist * iScale - iBias) * saturate(iDist * _AtlasSize * 2.0);
        fixed4 s = i.isColor; s.rgb *= s.a;
        s *= (1 - shifted) * alpha;
        col.rgb = col.rgb * (1 - s.a) + s.rgb;
    }

    // Second outline band, outside the outline (same SDF ramp, wider bias).
    if (i.o2Color.a > 0 && mode != 2)
    {
        float o2Scale = fxScale / (1.0 + max(i.cov.w, 0.0) * fxNorm);
        float o2Norm = (fxBaseW + (faceDilate + outlineWidth + i.glowP.w) * 0.5) * fxNorm;
        float o2Bias = (0.5 - o2Norm) * o2Scale - 0.5;
        half o2A = saturate(dist * o2Scale - o2Bias) * fxGate;
        fixed4 o2 = i.o2Color; o2.rgb *= o2.a; o2 *= o2A;
        col.rgb = o2.rgb * (1 - col.a) + col.rgb;
        col.a = saturate(o2.a + col.a);
    }

    // Vertex tint (gradient / per-vertex <color>) on face + outlines; the glow below keeps
    // its own colour. col is PREMULTIPLIED, so tint rgb only (alpha is applied at the end).
    col.rgb *= i.color.rgb;

    // Soft outer glow, behind the text.
    if (i.glowColor.a > 0 && mode != 2)
    {
        float edge = 0.5 - (fxBaseW + faceDilate * 0.5) * fxNorm - i.glowP.x * 0.5 * fxNorm;
        float range = max(i.glowP.y * 0.5 * fxNorm, 1e-4);
        float tg = saturate((edge - dist) / range);
        float g = pow(1.0 - tg, max(i.glowP.z, 0.01));
        g *= saturate(dist * 16.0); // fade out at the clamped field floor instead of a hard cut
        float ga = saturate(i.glowColor.a * g);
        fixed4 gl = fixed4(i.glowColor.rgb * ga, ga);
        col.rgb = gl.rgb * (1 - col.a) + col.rgb;
        col.a = saturate(gl.a + col.a);
    }

    // Underlay / drop shadow (offset sample of the SAME slice), faithful port of legacy
    // UniText/SDF-SSD, composited BEHIND face+outline via premultiplied BlendOver.
    if (underlayColor.a > 0 && mode != 2)
    {
        float baseWeight = i.cov.x; float normFactor = i.cov.y;
        float gradientScale = i.uv0.z;
        float scale = i.cov.z * abs(i.uv0.w) * gradientScale;
        float offsetFactor = sqrt(72.0 * gradientScale * normFactor / 0.1) / 9.0;
        float texel = 1.0 / max(_AtlasSize, 1.0);
        float2 uOff = float2(scal1.x, scal1.y) * offsetFactor * texel; // scal1.xy = _UnderlayOffsetX/Y
        half4 ut = UNITY_SAMPLE_TEX2DARRAY(_MainTexArray, float3(i.uv0.xy - uOff, slice));
        half uDist = (mode == 1) ? Median3(ut.rgb) : ut.a;
        float layerScale = scale / (1.0 + max(scal1.w, 0.0) * normFactor);
        float underlayDilate = faceDilate + scal1.z; // _FaceDilate + _UnderlayDilate (ratios=1)
        float normUEffect = (baseWeight + underlayDilate * 0.5) * normFactor;
        float layerBias = (0.5 - normUEffect) * layerScale - 0.5;
        half ud = uDist * layerScale;
        float uFieldGate = saturate(uDist * _AtlasSize * 2.0);
        fixed4 uPremult = underlayColor; uPremult.rgb *= uPremult.a;
        fixed4 uResult = uPremult * (saturate(ud - layerBias) * uFieldGate);
        uResult.rgb *= i.color.rgb; // tinted like the face (col was tinted above)
        fixed4 blended;
        blended.rgb = uResult.rgb * (1 - col.a) + col.rgb;
        blended.a = saturate(uResult.a + col.a);
        col = blended;
    }

    // Vertex alpha scales the whole premultiplied result (reveal / fade / CanvasGroup).
    col *= i.color.a;
    return col;
}

#endif // UNITEXT_UBER_CORE_INCLUDED
