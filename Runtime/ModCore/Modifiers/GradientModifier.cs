using System;
using System.Globalization;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Applies gradient coloring to text ranges using named gradients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage:
    /// <list type="bullet">
    /// <item><c>&lt;gradient=name&gt;</c> - Visual mode, horizontal (angle=0)</item>
    /// <item><c>&lt;gradient=name,angle&gt;</c> - Visual mode with angle (0=→, 90=↑)</item>
    /// <item><c>&lt;gradient=name,L&gt;</c> - Logical mode (by character position)</item>
    /// <item><c>&lt;gradient=name,radial[,cx,cy[,radius]]&gt;</c> - Radial from a centre (normalised to the span bounds, default 0.5,0.5; radius 1 = farthest corner)</item>
    /// <item><c>&lt;gradient=name,angular[,startDeg[,cx,cy]]&gt;</c> - Angular (conic) sweep around a centre, counter-clockwise from the start angle</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Visual mode:</b> Gradient based on visual X/Y coordinates. Multi-line text
    /// may restart gradient on each line since X resets.
    /// </para>
    /// <para>
    /// <b>Logical mode:</b> Gradient based on character index within the range.
    /// Provides smooth color transition regardless of line breaks.
    /// </para>
    /// <para>
    /// <b>Radial / angular:</b> evaluated at each glyph quad corner over the span's bounding box (all its
    /// lines). Being vertex colours, they render in both the unified and the legacy renderer.
    /// </para>
    /// <para>
    /// Gradients are defined in <see cref="UniTextGradients"/> ScriptableObject referenced by <see cref="UniTextSettings"/>.
    /// </para>
    /// </remarks>
    /// <seealso cref="UniTextGradients"/>
    /// <seealso cref="GradientParseRule"/>
    [Serializable]
    [TypeGroup("Appearance", 2)]
    public sealed class GradientModifier : BaseModifier
    {
        private enum GradientMode : byte
        {
            Visual,
            Logical,
            Radial,
            Angular,
        }

        private struct GradientDef
        {
            public int startCluster;
            public int endCluster;
            public Gradient gradient;
            public float angleDeg;
            public float minProj;
            public float maxProj;
            public float cosAngle;
            public float sinAngle;
            public GradientMode mode;
            public Vector2 center;
            public float radius;
            public GradientFrame frame;
            public bool hasFrame;
        }
        
        private PooledArrayAttribute<byte> attribute;
        private readonly PooledList<GradientDef> gradientDefs = new();
        private readonly PooledList<Rect> boundsCache = new();

        protected override void OnEnable()
        {
            attribute = buffers.GetOrCreateAttributeData<PooledArrayAttribute<byte>>(AttributeKeys.Gradient);
            attribute.EnsureCountAndClear(buffers.codepoints.count);

            gradientDefs.Clear();
            
            uniText.TextProcessor.LayoutComplete += OnLayoutComplete;
            uniText.MeshGenerator.OnGlyph += OnGlyph;
        }

        protected override void OnDisable()
        {
            uniText.TextProcessor.LayoutComplete -= OnLayoutComplete;
            uniText.MeshGenerator.OnGlyph -= OnGlyph;
        }

        protected override void OnDestroy()
        {
            buffers.ReleaseAttributeData(AttributeKeys.Gradient);
            attribute = null;
            gradientDefs.Return();
            boundsCache.Return();
        }

        protected override void OnApply(int start, int end, string parameter)
        {
            if (string.IsNullOrEmpty(parameter))
                return;

            if (!TryParse(parameter, out var gradientName, out var angle, out var mode, out var center, out var radius))
                return;

            var gradientsAsset = UniTextSettings.Gradients;
            if (gradientsAsset == null)
            {
                Debug.LogWarning("[GradientModifier] UniTextSettings.Gradients is not assigned");
                return;
            }

            if (!gradientsAsset.TryGetGradient(gradientName, out var gradient))
            {
                Debug.LogWarning($"[GradientModifier] Gradient '{gradientName}' not found");
                return;
            }

            var buffer = attribute.buffer.data;

            var index = gradientDefs.Count;
            gradientDefs.Add(new GradientDef
            {
                startCluster = start,
                endCluster = end,
                gradient = gradient,
                angleDeg = angle,
                mode = mode,
                center = center,
                radius = radius,
            });

            var gradientIndex = (byte)(index + 1);
            var cpCount = buffers.codepoints.count;
            var actualEnd = Math.Min(end, cpCount);

            for (var i = start; i < actualEnd; i++)
                buffer[i] = gradientIndex;
        }

        private void OnLayoutComplete()
        {
            if (gradientDefs.Count == 0) return;

            for (var i = 0; i < gradientDefs.Count; i++)
            {
                ref var g = ref gradientDefs[i];

                if (g.mode == GradientMode.Logical)
                    continue;

                uniText.GetRangeBounds(g.startCluster, g.endCluster, boundsCache);

                if (g.mode == GradientMode.Radial || g.mode == GradientMode.Angular)
                {
                    g.hasFrame = boundsCache.Count > 0;
                    if (!g.hasFrame) continue;
                    var b = boundsCache[0];
                    for (var j = 1; j < boundsCache.Count; j++)
                    {
                        ref readonly var r = ref boundsCache[j];
                        b = Rect.MinMaxRect(Mathf.Min(b.xMin, r.xMin), Mathf.Min(b.yMin, r.yMin),
                            Mathf.Max(b.xMax, r.xMax), Mathf.Max(b.yMax, r.yMax));
                    }
                    g.frame = GradientFrame.Create(g.mode == GradientMode.Radial ? GradientShape.Radial : GradientShape.Angular,
                        b, g.angleDeg, g.center, g.radius);
                    continue;
                }

                var rad = g.angleDeg * Mathf.Deg2Rad;
                g.cosAngle = Mathf.Cos(rad);
                g.sinAngle = Mathf.Sin(rad);

                if (boundsCache.Count == 0)
                {
                    g.minProj = 0;
                    g.maxProj = 0;
                    continue;
                }

                g.minProj = float.MaxValue;
                g.maxProj = float.MinValue;

                for (var j = 0; j < boundsCache.Count; j++)
                {
                    ref readonly var rect = ref boundsCache[j];
                    UpdateProj(rect.xMin, rect.yMin, ref g);
                    UpdateProj(rect.xMax, rect.yMin, ref g);
                    UpdateProj(rect.xMin, rect.yMax, ref g);
                    UpdateProj(rect.xMax, rect.yMax, ref g);
                }
            }
        }

        private static void UpdateProj(float x, float y, ref GradientDef g)
        {
            var proj = x * g.cosAngle + y * g.sinAngle;
            if (proj < g.minProj) g.minProj = proj;
            if (proj > g.maxProj) g.maxProj = proj;
        }

        private void OnGlyph()
        {
            var gen = UniTextMeshGenerator.Current;
            if (gen.font.IsColor) return;

            var buffer = attribute.buffer.data;
            var cluster = gen.currentCluster;
            
            var gradientIndex = buffer[cluster];
            if (gradientIndex == 0) return;

            ref readonly var g = ref gradientDefs[gradientIndex - 1];

            var baseIdx = gen.currentGlyphVertexStart;
            var colors = gen.Colors;
            var alpha = gen.defaultColor.a;

            if (g.mode == GradientMode.Radial || g.mode == GradientMode.Angular)
            {
                if (g.hasFrame) g.frame.ColorQuad(g.gradient, gen.Vertices, colors, baseIdx, alpha);
                return;
            }

            if (g.mode == GradientMode.Logical)
            {
                var clusterRange = g.endCluster - g.startCluster - 1;
                var t = clusterRange > 0 ? (float)(cluster - g.startCluster) / clusterRange : 0f;

                var color = g.gradient.Evaluate(t);
                var c = new Color32(
                    (byte)(color.r * 255),
                    (byte)(color.g * 255),
                    (byte)(color.b * 255),
                    alpha
                );

                colors[baseIdx] = c;
                colors[baseIdx + 1] = c;
                colors[baseIdx + 2] = c;
                colors[baseIdx + 3] = c;
            }
            else
            {
                var range = g.maxProj - g.minProj;
                if (range <= 0) return;

                var verts = gen.Vertices;

                for (var i = 0; i < 4; i++)
                {
                    ref readonly var v = ref verts[baseIdx + i];
                    var proj = v.x * g.cosAngle + v.y * g.sinAngle;
                    var t = Mathf.Clamp01((proj - g.minProj) / range);

                    var color = g.gradient.Evaluate(t);
                    colors[baseIdx + i] = new Color32(
                        (byte)(color.r * 255),
                        (byte)(color.g * 255),
                        (byte)(color.b * 255),
                        alpha
                    );
                }
            }
        }

        private static bool TryParse(ReadOnlySpan<char> param, out string name, out float angle, out GradientMode mode,
            out Vector2 center, out float radius)
        {
            name = null;
            angle = 0f;
            mode = GradientMode.Visual;
            center = new Vector2(0.5f, 0.5f);
            radius = 1f;

            if (param.IsEmpty) return false;

            var commaIndex = param.IndexOf(',');

            ReadOnlySpan<char> nameSpan;
            ReadOnlySpan<char> rest;

            if (commaIndex < 0)
            {
                nameSpan = param.Trim();
                rest = default;
            }
            else
            {
                nameSpan = param[..commaIndex].Trim();
                rest = param[(commaIndex + 1)..];
            }

            if (nameSpan.IsEmpty) return false;

            name = nameSpan.ToString();

            var second = NextToken(ref rest).Trim();
            if (second.IsEmpty) return true;

            if (second.Length == 1 && (second[0] == 'L' || second[0] == 'l'))
            {
                mode = GradientMode.Logical;
            }
            else if (second.Equals("radial".AsSpan(), StringComparison.OrdinalIgnoreCase) || second.Equals("r".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                mode = GradientMode.Radial;
                if (TryNextFloat(ref rest, out var cx)) center.x = cx;
                if (TryNextFloat(ref rest, out var cy)) center.y = cy;
                if (TryNextFloat(ref rest, out var r)) radius = Mathf.Max(1e-3f, r);
            }
            else if (second.Equals("angular".AsSpan(), StringComparison.OrdinalIgnoreCase) || second.Equals("conic".AsSpan(), StringComparison.OrdinalIgnoreCase)
                     || second.Equals("a".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                mode = GradientMode.Angular;
                if (TryNextFloat(ref rest, out var start)) angle = start;
                if (TryNextFloat(ref rest, out var cx)) center.x = cx;
                if (TryNextFloat(ref rest, out var cy)) center.y = cy;
            }
            else
            {
                float.TryParse(second, NumberStyles.Float, CultureInfo.InvariantCulture, out angle);
            }

            return true;
        }

        private static ReadOnlySpan<char> NextToken(ref ReadOnlySpan<char> rest)
        {
            if (rest.IsEmpty) return default;
            var c = rest.IndexOf(',');
            ReadOnlySpan<char> tok;
            if (c < 0) { tok = rest; rest = default; }
            else { tok = rest[..c]; rest = rest[(c + 1)..]; }
            return tok;
        }

        private static bool TryNextFloat(ref ReadOnlySpan<char> rest, out float v)
        {
            v = 0f;
            var tok = NextToken(ref rest).Trim();
            return !tok.IsEmpty && float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
