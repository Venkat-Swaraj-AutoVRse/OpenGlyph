#ifndef UNITEXT_INCLUDED
#define UNITEXT_INCLUDED

// ============================================
// Distance-field sampling mode (SDF vs MSDF)
// ============================================
// Shaders that define UNITEXT_MSDF before including this header sample the reconstructed
// distance as the median of the RGB atlas channels (multi-channel SDF, corner-preserving);
// otherwise they read the single Alpha8 channel. UNITEXT_SAMPLE_DF is the one place the two
// modes differ, so every shared distance read below routes through it.
#ifndef UNITEXT_DF_SAMPLE_DEFINED
#define UNITEXT_DF_SAMPLE_DEFINED
half UniTextMedian3_(half3 rgb) { return max(min(rgb.r, rgb.g), min(max(rgb.r, rgb.g), rgb.b)); }
#ifdef UNITEXT_MSDF
    #define UNITEXT_SAMPLE_DF(tex, uv) UniTextMedian3_(tex2D(tex, uv).rgb)
#else
    #define UNITEXT_SAMPLE_DF(tex, uv) (tex2D(tex, uv).a)
#endif
#endif // UNITEXT_DF_SAMPLE_DEFINED

// ============================================
// Effect Normalization Constants
// ============================================

// Reference spreadRatio for effect normalization (Padding=9, PointSize=90 -> 9/90 = 0.1)
#define REFERENCE_SPREAD_RATIO 0.1

// Compute underlay UV offset factor (independent of Padding, proportional to PointSize)
// gradientScale ≈ PointSize * Padding / 72
// spreadRatio = Padding / PointSize
// From these: PointSize = sqrt(72 * gradientScale / spreadRatio)
//                       = sqrt(72 * gradientScale * normFactor / REFERENCE_SPREAD_RATIO)
// Returns offset factor that produces consistent visual offset regardless of atlas settings
float ComputeUnderlayOffsetFactor(float gradientScale, float normFactor)
{
	// Derive approximate PointSize from gradientScale and normFactor
	float pointSizeApprox = sqrt(72.0 * gradientScale * normFactor / REFERENCE_SPREAD_RATIO);

	// Scale factor: at reference (PointSize=90, Padding=9), offsetFactor=10
	// This maintains backwards compatibility with existing offset slider values
	// 90 / 9 = 10, so we divide by 9 (reference Padding)
	return pointSizeApprox / 9.0;
}

// ============================================
// Unified SDF Layer rendering functions
// ============================================

// Render a single SDF layer (same as Mobile version)
// d = sampled SDF value * scale
// threshold = bias value
// color = premultiplied alpha color
half4 SDFLayer(half d, float threshold, half4 color)
{
	return color * saturate(d - threshold);
}

// Exterior-floor gate: zeroes a layer where the atlas field is clamped at 0 (outside the encoded
// spread), so a dilation bias cannot floor at positive coverage across the padded quad. Knee is
// 0.5 texel; exactly 1 for rawDist >= 1/(2*atlasSize). See UniText_Uber.shader "EXTERIOR-FLOOR GATE".
// rawDist = unscaled distance sample (MSDF: the median); atlasSize = _MainTex_TexelSize.w.
#ifndef UNITEXT_FIELD_GATE_DEFINED
#define UNITEXT_FIELD_GATE_DEFINED
float UniTextFieldGate(float rawDist, float atlasSize)
{
	return saturate(rawDist * atlasSize * 2.0);
}
#endif // UNITEXT_FIELD_GATE_DEFINED

// Blend layer on top of existing result (premultiplied alpha)
half4 BlendOver(half4 dst, half4 src)
{
	dst.rgb = dst.rgb * (1 - src.a) + src.rgb;
	dst.a = saturate(dst.a + src.a);
	return dst;
}


float3 GetSurfaceNormal(float4 h, float bias, float gradientScale)
{
	bool raisedBevel = step(1, fmod(_ShaderFlags, 2));

	h += bias+_BevelOffset;

	float bevelWidth = max(.01, _OutlineWidth+_BevelWidth);

  // Track outline
	h -= .5;
	h /= bevelWidth;
	h = saturate(h+.5);

	if(raisedBevel) h = 1 - abs(h*2.0 - 1.0);
	h = lerp(h, sin(h*3.141592/2.0), _BevelRoundness);
	h = min(h, 1.0-_BevelClamp);
	h *= _Bevel * bevelWidth * gradientScale * -2.0;

	float3 va = normalize(float3(1.0, 0.0, h.y - h.x));
	float3 vb = normalize(float3(0.0, -1.0, h.w - h.z));

	return cross(va, vb);
}

float3 GetSurfaceNormal(float2 uv, float bias, float3 delta, float gradientScale)
{
	// Read "height field"
  float4 h = {UNITEXT_SAMPLE_DF(_MainTex, uv - delta.xz),
				UNITEXT_SAMPLE_DF(_MainTex, uv + delta.xz),
				UNITEXT_SAMPLE_DF(_MainTex, uv - delta.zy),
				UNITEXT_SAMPLE_DF(_MainTex, uv + delta.zy)};

	return GetSurfaceNormal(h, bias, gradientScale);
}

float3 GetSpecular(float3 n, float3 l)
{
	float spec = pow(max(0.0, dot(n, l)), _Reflectivity);
	return _SpecularColor.rgb * spec * _SpecularPower;
}

float4 GetGlowColor(float d, float effectScale)
{
	float glow = d - (_GlowOffset*_ScaleRatioB) * 0.5 * effectScale;
	float t = lerp(_GlowInner, (_GlowOuter * _ScaleRatioB), step(0.0, glow)) * 0.5 * effectScale;
	glow = saturate(abs(glow/(1.0 + t)));
	glow = 1.0-pow(glow, _GlowPower);
	glow *= sqrt(min(1.0, t)); // Fade off glow thinner than 1 screen pixel
	return float4(_GlowColor.rgb, saturate(_GlowColor.a * glow * 2));
}

#endif // UNITEXT_INCLUDED
