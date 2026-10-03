using System;
using UnityEngine;

namespace LightSide
{
    /// <summary>Geometry of a gradient fill.</summary>
    public enum GradientShape : byte
    {
        /// <summary>Along a direction (angle in degrees, 0 = left→right, 90 = bottom→top).</summary>
        Linear,
        /// <summary>Out from a centre: 0 at the centre, 1 at <c>radius</c> × the distance to the farthest corner.</summary>
        Radial,
        /// <summary>Around a centre (conic sweep): 0 at the start angle, increasing counter-clockwise to 1.</summary>
        Angular,
    }

    /// <summary>Whole-text gradient fill mode (<see cref="UniText.GradientFill"/>).</summary>
    public enum GradientFillMode : byte { None, Linear, Radial, Angular }

    /// <summary>
    /// A gradient over the whole text of a <see cref="UniText"/> (<see cref="UniText.GradientFill"/>). Span
    /// <c>&lt;gradient&gt;</c> and <c>&lt;color&gt;</c> tags override it on their range.
    /// </summary>
    [Serializable]
    public struct UniTextGradientFill
    {
        [Tooltip("None, Linear, Radial or Angular.")]
        public GradientFillMode mode;
        [Tooltip("Colour stops. Alpha is ignored (vertex alpha follows the component colour).")]
        public Gradient gradient;
        [Tooltip("Linear: direction in degrees (0 = left to right, 90 = bottom to top). Angular: start angle.")]
        public float angle;
        [Tooltip("Radial / Angular centre, normalised to the text bounds (0.5, 0.5 = middle).")]
        public Vector2 center;
        [Tooltip("Radial: 1 = the gradient ends at the farthest corner of the text bounds.")]
        public float radius;

        /// <summary>No gradient.</summary>
        public static UniTextGradientFill None => new() { mode = GradientFillMode.None, center = new Vector2(0.5f, 0.5f), radius = 1f };

        public static UniTextGradientFill Linear(Gradient g, float angleDeg = 0f) =>
            new() { mode = GradientFillMode.Linear, gradient = g, angle = angleDeg, center = new Vector2(0.5f, 0.5f), radius = 1f };

        public static UniTextGradientFill Radial(Gradient g, Vector2 center, float radius = 1f) =>
            new() { mode = GradientFillMode.Radial, gradient = g, center = center, radius = radius };

        public static UniTextGradientFill Angular(Gradient g, Vector2 center, float startAngleDeg = 0f) =>
            new() { mode = GradientFillMode.Angular, gradient = g, center = center, angle = startAngleDeg, radius = 1f };
    }

    /// <summary>
    /// The resolved geometry of one gradient over a rectangle (text or span bounds, mesh-local units):
    /// maps a point to the gradient parameter t in [0,1]. Used by <see cref="GradientModifier"/> and the
    /// whole-text <see cref="UniText.GradientFill"/>. Gradients are vertex colours, so they work with the
    /// unified and the legacy renderer; a glyph quad is coloured at its 4 corners.
    /// </summary>
    public struct GradientFrame
    {
        public GradientShape shape;
        // Linear
        public float cos, sin, minProj, invRange;
        // Radial / angular
        public float cx, cy, invRadius, startRad;

        /// <summary>Builds the frame for <paramref name="bounds"/>.</summary>
        public static GradientFrame Create(GradientShape shape, Rect bounds, float angleDeg, Vector2 center, float radius)
        {
            var f = new GradientFrame { shape = shape };
            switch (shape)
            {
                case GradientShape.Linear:
                {
                    var rad = angleDeg * Mathf.Deg2Rad;
                    f.cos = Mathf.Cos(rad);
                    f.sin = Mathf.Sin(rad);
                    float min = float.MaxValue, max = float.MinValue;
                    Proj(bounds.xMin, bounds.yMin, ref f, ref min, ref max);
                    Proj(bounds.xMax, bounds.yMin, ref f, ref min, ref max);
                    Proj(bounds.xMin, bounds.yMax, ref f, ref min, ref max);
                    Proj(bounds.xMax, bounds.yMax, ref f, ref min, ref max);
                    f.minProj = min;
                    f.invRange = max > min ? 1f / (max - min) : 0f;
                    break;
                }
                default:
                {
                    f.cx = bounds.xMin + center.x * bounds.width;
                    f.cy = bounds.yMin + center.y * bounds.height;
                    var dx = Mathf.Max(Mathf.Abs(bounds.xMin - f.cx), Mathf.Abs(bounds.xMax - f.cx));
                    var dy = Mathf.Max(Mathf.Abs(bounds.yMin - f.cy), Mathf.Abs(bounds.yMax - f.cy));
                    var far = Mathf.Sqrt(dx * dx + dy * dy) * Mathf.Max(1e-4f, radius);
                    f.invRadius = far > 0f ? 1f / far : 0f;
                    f.startRad = angleDeg * Mathf.Deg2Rad;
                    break;
                }
            }
            return f;
        }

        private static void Proj(float x, float y, ref GradientFrame f, ref float min, ref float max)
        {
            var p = x * f.cos + y * f.sin;
            if (p < min) min = p;
            if (p > max) max = p;
        }

        /// <summary>Gradient parameter at (x, y). Angular returns [0,1) with the seam at the start angle.</summary>
        public float Evaluate(float x, float y)
        {
            switch (shape)
            {
                case GradientShape.Linear:
                    return Mathf.Clamp01((x * cos + y * sin - minProj) * invRange);
                case GradientShape.Radial:
                {
                    var dx = x - cx; var dy = y - cy;
                    return Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) * invRadius);
                }
                default:
                {
                    var a = (Mathf.Atan2(y - cy, x - cx) - startRad) / (Mathf.PI * 2f);
                    return a - Mathf.Floor(a);
                }
            }
        }

        /// <summary>
        /// Colours the 4 vertices of the quad at <paramref name="baseIdx"/>. For angular gradients the
        /// corners are unwrapped around the quad centre, so a glyph on the seam is not interpolated
        /// through the whole gradient.
        /// </summary>
        public void ColorQuad(Gradient gradient, Vector3[] verts, Color32[] colors, int baseIdx, byte alpha)
        {
            if (gradient == null) return;
            float tc = 0f;
            if (shape == GradientShape.Angular)
            {
                var mx = 0f; var my = 0f;
                for (var k = 0; k < 4; k++) { mx += verts[baseIdx + k].x; my += verts[baseIdx + k].y; }
                tc = Evaluate(mx * 0.25f, my * 0.25f);
            }
            for (var k = 0; k < 4; k++)
            {
                ref readonly var v = ref verts[baseIdx + k];
                var t = Evaluate(v.x, v.y);
                if (shape == GradientShape.Angular)
                {
                    if (t - tc > 0.5f) t -= 1f;
                    else if (tc - t > 0.5f) t += 1f;
                    t = Mathf.Clamp01(t);
                }
                var c = gradient.Evaluate(t);
                colors[baseIdx + k] = new Color32((byte)(c.r * 255f + 0.5f), (byte)(c.g * 255f + 0.5f), (byte)(c.b * 255f + 0.5f), alpha);
            }
        }
    }
}
