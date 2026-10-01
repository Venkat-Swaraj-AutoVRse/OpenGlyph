using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 2 (live path): collapses the legacy per-segment
    /// <see cref="UniTextRenderData"/> list (one entry per font/atlas-page/pass) into AT MOST TWO
    /// merged entries — one per shared-array format (Alpha8 SDF/coverage, RGBA32 MSDF/color) — each a
    /// single mesh bound to one <see cref="GlyphAtlasArray"/> and the <c>UniText/Uber</c> material.
    /// This is what makes a <c>UniText</c> component draw in ≤2 CanvasRenderers. One instance is owned
    /// per component so its meshes/materials/style table persist across rebuilds.
    /// </summary>
    /// <remarks>
    /// <para><b>Pages-as-slices.</b> Reuses the existing segment meshes/UVs wholesale: each legacy
    /// atlas PAGE is published as a slice of the shared array (<see cref="GlyphAtlasArray.AddPage"/>),
    /// so a vertex's existing page-local UV0.xy plus the per-glyph slice index addresses the same
    /// texel — no re-layout, no re-rasterization. Per-glyph <c>(spreadRatio, sliceIdx, glyphMode,
    /// styleIdx)</c> is written into UV1 for the uber-shader.</para>
    /// <para><b>glyphMode</b> comes from the segment's atlas TEXTURE FORMAT: Alpha8 ⇒ SDF (coverage
    /// samples .a identically), RGB24 ⇒ MSDF, RGBA32 ⇒ COLR color.</para>
    /// <para><b>Style.</b> A single component-wide <see cref="GlyphStyle"/> (from
    /// <see cref="AppearanceStyleShim"/>) occupies row 0; per-span styles are a later step but the
    /// per-vertex styleIdx is already plumbed.</para>
    /// </remarks>
    public sealed class UnifiedRenderBuilder : IDisposable
    {
        private static readonly int MainTexArray = Shader.PropertyToID("_MainTexArray");
        private static readonly int StyleTex = Shader.PropertyToID("_StyleTex");
        private static readonly int StyleTexWidth = Shader.PropertyToID("_StyleTexWidth");
        private static readonly int StyleTexHeight = Shader.PropertyToID("_StyleTexHeight");

        private static Shader _uberShader;
        private static Shader UberShader => _uberShader != null ? _uberShader : (_uberShader = Shader.Find("UniText/Uber"));

        private sealed class Group
        {
            public readonly List<Vector3> verts = new();
            public readonly List<Color32> colors = new();
            public readonly List<Vector4> uv0 = new();
            public readonly List<Vector4> uv1 = new();
            public readonly List<int> tris = new();
            public Mesh mesh;
            public Material material;
            public int pageSize;
            public void ClearBuffers() { verts.Clear(); colors.Clear(); uv0.Clear(); uv1.Clear(); tris.Clear(); pageSize = 0; }
        }

        private readonly Dictionary<TextureFormat, Group> _groups = new();
        private readonly StyleTable _styleTable = new();
        private readonly List<Vector4> _tmpUv = new();

        private static UberDrawGroup.GlyphMode ModeFromFormat(TextureFormat f) => f switch
        {
            TextureFormat.RGB24 => UberDrawGroup.GlyphMode.Msdf,
            TextureFormat.RGBA32 => UberDrawGroup.GlyphMode.Colr,
            _ => UberDrawGroup.GlyphMode.Sdf,
        };

        /// <summary>
        /// Builds the merged render data (≤2 entries) into <paramref name="output"/> from the legacy
        /// per-segment <paramref name="segments"/>, shading every glyph with the component-wide
        /// <paramref name="style"/> (styleIdx 0). Main-thread only.
        /// </summary>
        public void Build(List<UniTextRenderData> segments, in GlyphStyle style, List<UniTextRenderData> output)
        {
            output.Clear();
            if (segments == null || segments.Count == 0) return;

            _styleTable.Reset();
            int styleIdx = _styleTable.GetOrAdd(style);
            var styleTex = _styleTable.Apply();

            foreach (var kv in _groups) kv.Value.ClearBuffers();

            foreach (var seg in segments)
            {
                if (seg.mesh == null || seg.mesh.vertexCount == 0 || seg.texture is not Texture2D page) continue;

                var mode = ModeFromFormat(page.format);
                var dstFormat = UberDrawGroup.FormatFor(mode);
                var arr = SharedGlyphAtlas.Get(dstFormat, page.width);
                if (!arr.AddPage(page.GetInstanceID(), page, out int slice))
                    continue;

                if (!_groups.TryGetValue(dstFormat, out var g)) { g = new Group(); _groups[dstFormat] = g; }
                g.pageSize = page.width;
                AppendSegment(g, seg.mesh, slice, (int)mode, styleIdx);
            }

            foreach (var kv in _groups)
            {
                var g = kv.Value;
                if (g.verts.Count == 0) continue;
                var arr = SharedGlyphAtlas.Get(kv.Key, g.pageSize > 0 ? g.pageSize : 1024);
                arr.Apply(false);

                if (g.mesh == null) g.mesh = NewMesh();
                var m = g.mesh;
                m.Clear();
                m.SetVertices(g.verts);
                m.SetColors(g.colors);
                m.SetUVs(0, g.uv0);
                m.SetUVs(1, g.uv1);
                m.SetTriangles(g.tris, 0);

                if (g.material == null) g.material = NewUberMaterial();
                g.material.SetTexture(MainTexArray, arr.Texture);
                g.material.SetTexture(StyleTex, styleTex);
                g.material.SetFloat(StyleTexWidth, _styleTable.Width);
                g.material.SetFloat(StyleTexHeight, Mathf.Max(1, _styleTable.Count));

                // IMPORTANT: the atlas is a Texture2DArray bound via the material's _MainTexArray.
                // Do NOT pass it as the CanvasRenderer main texture (SetTexture expects a 2D texture
                // and asserts otherwise). The render-data texture is null; the material carries the array.
                output.Add(new UniTextRenderData(m, g.material, (Texture)null, 0));
            }
        }

        private void AppendSegment(Group g, Mesh src, int slice, int glyphMode, int styleIdx)
        {
            int baseIndex = g.verts.Count;
            var v = src.vertices;
            var c = src.colors32;
            _tmpUv.Clear();
            src.GetUVs(0, _tmpUv);
            var tri = src.triangles;

            for (int i = 0; i < v.Length; i++)
            {
                g.verts.Add(v[i]);
                g.colors.Add(c != null && c.Length == v.Length ? c[i] : (Color32)Color.white);
                var uv0 = i < _tmpUv.Count ? _tmpUv[i] : Vector4.zero;
                g.uv0.Add(uv0);
                g.uv1.Add(new Vector4(uv0.z, slice, glyphMode, styleIdx));
            }
            for (int i = 0; i < tri.Length; i++)
                g.tris.Add(baseIndex + tri[i]);
        }

        private static Mesh NewMesh()
        {
            var m = new Mesh { name = "UniText Uber Mesh", hideFlags = HideFlags.DontSave };
            m.MarkDynamic();
            return m;
        }

        private static Material NewUberMaterial()
        {
            var sh = UberShader;
            return new Material(sh != null ? sh : Shader.Find("UI/Default"))
            {
                name = "UniText Uber (runtime)",
                hideFlags = HideFlags.DontSave,
            };
        }

        public void Dispose()
        {
            foreach (var kv in _groups)
            {
                if (kv.Value.mesh != null) UnityEngine.Object.DestroyImmediate(kv.Value.mesh);
                if (kv.Value.material != null) UnityEngine.Object.DestroyImmediate(kv.Value.material);
            }
            _groups.Clear();
            _styleTable.Dispose();
        }
    }
}
