using System;

namespace LightSide
{
    /// <summary>
    /// Renders a span as superscript or subscript: the glyphs are scaled down and shifted vertically
    /// (up for superscript, down for subscript). Mirrors TextMeshPro's <c>&lt;sup&gt;</c>/<c>&lt;sub&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Modeled on <see cref="SizeModifier"/>: the per-cluster scale is applied to the shaped advance
    /// (so following glyphs reflow for the smaller size) in <see cref="OnShaped"/>, and to the glyph
    /// quad vertices in <see cref="OnGlyph"/>, where a baseline offset is also added. The scale and
    /// offset fractions mirror TMP's defaults (0.5 size; +0.35 / -0.125 of the ascent-ish height).
    /// </remarks>
    /// <seealso cref="SuperscriptParseRule"/>
    /// <seealso cref="SubscriptParseRule"/>
    [Serializable]
    [TypeGroup("Text Style", 0)]
    public class SuperSubscriptModifier : BaseModifier
    {
        public enum Kind { Superscript, Subscript }

        private readonly Kind kind;
        private PooledArrayAttribute<byte> flags; // 1 where this modifier's span applies

        // TMP defaults: subscript/superscript render at half size.
        private const float ScriptScale = 0.5f;
        // Vertical shift as a fraction of the font ascent; superscript up, subscript down.
        private const float SuperRaiseFrac = 0.35f;
        private const float SubLowerFrac = 0.125f;

        private readonly string key;

        public SuperSubscriptModifier() : this(Kind.Superscript) { }

        public SuperSubscriptModifier(Kind kind)
        {
            this.kind = kind;
            key = kind == Kind.Superscript ? "superscript" : "subscript";
        }

        protected override void OnEnable()
        {
            flags ??= buffers.GetOrCreateAttributeData<PooledArrayAttribute<byte>>(key);
            flags.EnsureCountAndClear(buffers.codepoints.count);
            uniText.TextProcessor.Shaped += OnShaped;
            uniText.MeshGenerator.OnGlyph += OnGlyph;
        }

        protected override void OnDisable()
        {
            uniText.TextProcessor.Shaped -= OnShaped;
            uniText.MeshGenerator.OnGlyph -= OnGlyph;
        }

        protected override void OnDestroy()
        {
            buffers?.ReleaseAttributeData(key);
            flags = null;
        }

        protected override void OnApply(int start, int end, string parameter)
        {
            var cpCount = buffers.codepoints.count;
            var clampedEnd = Math.Min(end, cpCount);
            for (var i = start; i < clampedEnd; i++)
                flags.buffer[i] = 1;
        }

        private void OnShaped()
        {
            var buf = buffers;
            var glyphs = buf.shapedGlyphs.data;
            var runs = buf.shapedRuns.data;
            var runCount = buf.shapedRuns.count;
            var cap = flags.buffer.Capacity;

            for (var r = 0; r < runCount; r++)
            {
                ref var run = ref runs[r];
                var glyphEnd = run.glyphStart + run.glyphCount;
                float width = 0f;
                for (var g = run.glyphStart; g < glyphEnd; g++)
                {
                    var cluster = glyphs[g].cluster;
                    if ((uint)cluster < (uint)cap && flags.buffer[cluster] != 0)
                        glyphs[g].advanceX *= ScriptScale;
                    width += glyphs[g].advanceX;
                }
                run.width = width;
            }
        }

        private void OnGlyph()
        {
            var gen = UniTextMeshGenerator.Current;
            var cluster = gen.currentCluster;
            if ((uint)cluster >= (uint)flags.buffer.Capacity) return;
            if (flags.buffer[cluster] == 0) return;

            var baseIdx = gen.vertexCount - 4;
            var verts = gen.Vertices;
            var baselineY = gen.baselineY;

            // Approximate ascent in screen units from the current scale; shift relative to it.
            var font = gen.font;
            float ascent = font != null ? font.FaceInfo.ascentLine * gen.scale : (gen.FontSize * 0.8f);
            float yShift = kind == Kind.Superscript ? ascent * SuperRaiseFrac : -ascent * SubLowerFrac;

            var leftX = verts[baseIdx].x;
            for (var i = 0; i < 4; i++)
            {
                ref var v = ref verts[baseIdx + i];
                v.x = leftX + (v.x - leftX) * ScriptScale;
                v.y = baselineY + (v.y - baselineY) * ScriptScale + yShift;
            }
        }
    }
}
