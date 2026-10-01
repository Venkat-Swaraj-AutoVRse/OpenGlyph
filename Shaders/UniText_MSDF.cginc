#ifndef UNITEXT_MSDF_INCLUDED
#define UNITEXT_MSDF_INCLUDED

// ============================================
// Multi-channel SDF (MSDF) sampling helpers
// ============================================
//
// The MSDF atlas stores a per-channel signed distance in RGB (RGB24). The reconstructed
// distance at a texel is the MEDIAN of the three channels — this is what recovers sharp
// corners that a single-channel SDF rounds off. Everything downstream of the sample
// (bias/scale, outline, glow, underlay) is identical to the SDF path, so these helpers are
// drop-in replacements for the SDF shaders' `tex2D(_MainTex, uv).a`.

// Median of three components (msdfgen reconstruction).
half UniTextMedian3(half3 rgb)
{
	return max(min(rgb.r, rgb.g), min(max(rgb.r, rgb.g), rgb.b));
}

// Sample the reconstructed distance-field value in [0,1] at a UV (equivalent to the SDF
// atlas's .a channel, but from the RGB median).
half UniTextSampleMSDF(sampler2D tex, float2 uv)
{
	return UniTextMedian3(tex2D(tex, uv).rgb);
}

// Screen-space pixel range: matches the SDF pipeline's `scale` derivation. The SDF shaders
// already compute `scale = baseScale * xScaleVal * gradientScale` and multiply the sampled
// distance by it; MSDF reuses that exact term, so no separate pxRange path is required here —
// the median simply replaces the alpha sample. This helper is provided for shaders that want
// an explicit screen-space anti-alias width instead.
half UniTextScreenPxRange(float gradientScale, float2 texelSize, float2 unitRange)
{
	// unitRange = pxRange / atlasSize; screenTexSize = 1 / fwidth(uv).
	// Callers on platforms without derivatives should use the SDF `scale` term instead.
	return max(0.5 * dot(unitRange, float2(1.0, 1.0)) * gradientScale, 1.0);
}

#endif // UNITEXT_MSDF_INCLUDED
