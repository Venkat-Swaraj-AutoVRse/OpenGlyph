using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// A per-frame vertex effect on a <see cref="UniText"/>'s finished mesh: span animations
    /// (<see cref="TextAnimationModifier"/>), the typewriter (<see cref="UniTextReveal"/>), or your own.
    /// </summary>
    /// <remarks>
    /// <para>Effects never re-shape or re-lay out text. When a component that has effects rebuilds, its
    /// final mesh (positions, colours, UVs, triangles) is captured once together with a glyph table
    /// (one <see cref="AnimatedGlyph"/> per glyph quad). Every frame the component copies the captured
    /// positions and colours into work buffers, runs each effect's <see cref="Apply"/> on them, and
    /// uploads only positions and colours to its own mesh. A component without effects does none of this.</para>
    /// <para>All calls happen on the main thread from <c>Canvas.willRenderCanvases</c>, after the
    /// frame's text rebuilds.</para>
    /// </remarks>
    public interface IUniTextVertexEffect
    {
        /// <summary>Execution order; lower runs first. Span animations use 0, <see cref="UniTextReveal"/> 1000
        /// (it multiplies alpha last).</summary>
        int Order { get; }

        /// <summary>Called after every capture (the glyph table was rebuilt: new text, layout or colour).</summary>
        void OnCaptured(UniTextVertexEffectContext context);

        /// <summary>
        /// Called every tick before <see cref="Apply"/>. Advance internal state here. Return true when
        /// the output differs from the last tick (the component then re-applies all effects and uploads);
        /// return false when this effect is static this frame.
        /// </summary>
        bool Prepare(UniTextVertexEffectContext context);

        /// <summary>Modifies the work positions / colours through <paramref name="context"/>.</summary>
        void Apply(UniTextVertexEffectContext context);
    }

    /// <summary>One glyph quad of a captured mesh.</summary>
    public struct AnimatedGlyph
    {
        /// <summary>Logical codepoint index of the glyph's cluster (the same value modifiers see as <c>cluster</c>).</summary>
        public int cluster;
        /// <summary>Render entry (draw group / sub-mesh renderer) holding the quad.</summary>
        public int entry;
        /// <summary>First of the quad's 4 vertices within the entry.</summary>
        public int vertexStart;
        /// <summary>Centre of the captured quad, mesh-local units.</summary>
        public Vector2 center;
        /// <summary>Height of the captured quad (includes the SDF padding), mesh-local units.</summary>
        public float height;
    }

    /// <summary>
    /// Per-component state handed to <see cref="IUniTextVertexEffect"/>: the glyph table, the time, and
    /// helpers that edit a glyph's 4 work vertices. Reused across frames (no per-frame allocation).
    /// </summary>
    public sealed class UniTextVertexEffectContext
    {
        internal sealed class Entry
        {
            public Mesh mesh;
            public int count;
            public Vector3[] baseVerts = System.Array.Empty<Vector3>();
            public Vector3[] verts = System.Array.Empty<Vector3>();
            public Color32[] baseColors = System.Array.Empty<Color32>();
            public Color32[] colors = System.Array.Empty<Color32>();

            public void Ensure(int n)
            {
                if (baseVerts.Length >= n) return;
                var cap = Mathf.NextPowerOfTwo(Mathf.Max(n, 64));
                baseVerts = new Vector3[cap];
                verts = new Vector3[cap];
                baseColors = new Color32[cap];
                colors = new Color32[cap];
            }
        }

        /// <summary>A run of captured vertices that belongs to no glyph (underline, strikethrough, mark, ellipsis…).</summary>
        public struct DecorationRange { public int entry, start, count; }

        internal readonly List<Entry> entries = new();
        internal int entryCount;
        internal AnimatedGlyph[] glyphs = new AnimatedGlyph[64];
        internal int glyphCount;
        internal DecorationRange[] decorations = new DecorationRange[8];
        internal int decorationCount;

        internal UniTextVertexEffectContext(UniText owner) { Owner = owner; }

        /// <summary>The component this context belongs to.</summary>
        public UniText Owner { get; }

        /// <summary>Component animation time in seconds (integrated clock × <see cref="UniText.AnimationTimeScale"/>
        /// + <see cref="UniText.AnimationPhase"/>).</summary>
        public float Time { get; internal set; }

        /// <summary>Scaled time advanced since the previous tick (0 on the first tick).</summary>
        public float DeltaTime { get; internal set; }

        /// <summary>Number of glyph quads in the capture.</summary>
        public int GlyphCount => glyphCount;

        /// <summary>The glyph quad at <paramref name="index"/>.</summary>
        public ref readonly AnimatedGlyph Glyph(int index) => ref glyphs[index];

        /// <summary>Number of non-glyph vertex runs in the capture.</summary>
        public int DecorationCount => decorationCount;

        /// <summary>A non-glyph vertex run.</summary>
        public DecorationRange Decoration(int index) => decorations[index];

        /// <summary>Codepoints of the component's current text (logical order).</summary>
        public System.ReadOnlySpan<int> Codepoints
        {
            get
            {
                var b = Owner != null ? Owner.Buffers : null;
                return b == null ? System.ReadOnlySpan<int>.Empty : new System.ReadOnlySpan<int>(b.codepoints.data, 0, b.codepoints.count);
            }
        }

        /// <summary>Moves glyph <paramref name="glyph"/> by <paramref name="delta"/> (mesh-local units).</summary>
        public void Offset(int glyph, Vector2 delta)
        {
            ref readonly var g = ref glyphs[glyph];
            var v = entries[g.entry].verts;
            for (var k = 0; k < 4; k++)
            {
                ref var p = ref v[g.vertexStart + k];
                p.x += delta.x; p.y += delta.y;
            }
        }

        /// <summary>Scales glyph <paramref name="glyph"/> about its captured centre.</summary>
        public void Scale(int glyph, float scale)
        {
            ref readonly var g = ref glyphs[glyph];
            var v = entries[g.entry].verts;
            for (var k = 0; k < 4; k++)
            {
                ref var p = ref v[g.vertexStart + k];
                p.x = g.center.x + (p.x - g.center.x) * scale;
                p.y = g.center.y + (p.y - g.center.y) * scale;
            }
        }

        /// <summary>Multiplies the alpha of glyph <paramref name="glyph"/> by <paramref name="factor"/> (0..1).</summary>
        public void MultiplyAlpha(int glyph, float factor)
        {
            ref readonly var g = ref glyphs[glyph];
            MultiplyAlpha(entries[g.entry].colors, g.vertexStart, 4, factor);
        }

        /// <summary>Multiplies the alpha of every decoration vertex by <paramref name="factor"/>.</summary>
        public void MultiplyDecorationAlpha(float factor)
        {
            for (var i = 0; i < decorationCount; i++)
            {
                var d = decorations[i];
                MultiplyAlpha(entries[d.entry].colors, d.start, d.count, factor);
            }
        }

        /// <summary>Replaces the RGB of glyph <paramref name="glyph"/>'s vertices, keeping their alpha.</summary>
        public void SetRgb(int glyph, Color32 rgb)
        {
            ref readonly var g = ref glyphs[glyph];
            var c = entries[g.entry].colors;
            for (var k = 0; k < 4; k++)
            {
                ref var col = ref c[g.vertexStart + k];
                col.r = rgb.r; col.g = rgb.g; col.b = rgb.b;
            }
        }

        /// <summary>Work position of vertex <paramref name="corner"/> (0..3: BL, TL, TR, BR) of a glyph.</summary>
        public ref Vector3 Vertex(int glyph, int corner)
        {
            ref readonly var g = ref glyphs[glyph];
            return ref entries[g.entry].verts[g.vertexStart + corner];
        }

        /// <summary>Work colour of vertex <paramref name="corner"/> (0..3) of a glyph.</summary>
        public ref Color32 Color(int glyph, int corner)
        {
            ref readonly var g = ref glyphs[glyph];
            return ref entries[g.entry].colors[g.vertexStart + corner];
        }

        private static void MultiplyAlpha(Color32[] c, int start, int count, float factor)
        {
            if (factor >= 1f) return;
            if (factor < 0f) factor = 0f;
            for (var k = 0; k < count; k++)
            {
                ref var col = ref c[start + k];
                col.a = (byte)(col.a * factor + 0.5f);
            }
        }

        // ---------------------------------------------------------------- capture / frame plumbing

        internal bool HasCapture => entryCount > 0;

        internal Entry NextEntry()
        {
            if (entryCount == entries.Count) entries.Add(new Entry());
            return entries[entryCount++];
        }

        internal void BeginCapture()
        {
            entryCount = 0;
            glyphCount = 0;
            decorationCount = 0;
        }

        internal void AddGlyph(in AnimatedGlyph g)
        {
            if (glyphCount == glyphs.Length) System.Array.Resize(ref glyphs, glyphs.Length * 2);
            glyphs[glyphCount++] = g;
        }

        internal void AddDecorationVertex(int entry, int index)
        {
            if (decorationCount > 0)
            {
                ref var last = ref decorations[decorationCount - 1];
                if (last.entry == entry && last.start + last.count == index) { last.count++; return; }
            }
            if (decorationCount == decorations.Length) System.Array.Resize(ref decorations, decorations.Length * 2);
            decorations[decorationCount++] = new DecorationRange { entry = entry, start = index, count = 1 };
        }

        internal void ResetWork()
        {
            for (var i = 0; i < entryCount; i++)
            {
                var e = entries[i];
                if (e.count == 0) continue;
                System.Array.Copy(e.baseVerts, e.verts, e.count);
                System.Array.Copy(e.baseColors, e.colors, e.count);
            }
        }

        internal void ReleaseMeshes()
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.mesh != null) ObjectUtils.SafeDestroy(e.mesh);
                e.mesh = null;
                e.count = 0;
            }
            entryCount = 0;
            glyphCount = 0;
            decorationCount = 0;
        }
    }
}
