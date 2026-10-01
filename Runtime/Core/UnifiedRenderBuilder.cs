using System;
using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Render-Architecture Round 2, sub-task 2 (live path): collapses the legacy per-segment
    /// <see cref="UniTextRenderData"/> list into AT MOST TWO merged entries — one per shared-array
    /// format (Alpha8 SDF/coverage, RGBA32 MSDF/color) — each a single mesh bound to one
    /// <see cref="GlyphAtlasArray"/> and the <c>UniText/Uber</c> material.
    /// </summary>
    /// <remarks>
    /// <para><b>Batching (Round-2 §8 fix).</b> The uber <see cref="Material"/> and the
    /// <see cref="StyleTable"/> are PROCESS-WIDE shared per draw-group format, not per component. A
    /// first cut created a material per component, and distinct material instances do not batch, so
    /// 50 identical components cost ~50× the draws and lost to the legacy path's cross-component UI
    /// batching. Sharing one material + one array + one style texture across components lets UGUI
    /// batch them again, so the renderer-count win (n→1 per component) is not paid back on the GPU.
    /// The merged MESH stays per component (its geometry is the component's text); only the GPU state
    /// the batcher keys on (material, textures) is shared.</para>
    /// <para><b>Pages-as-slices.</b> Each legacy atlas PAGE is published as a slice of the shared
    /// array (<see cref="GlyphAtlasArray.AddPage"/>); a vertex's existing page-local UV0.xy plus the
    /// per-glyph slice index addresses the same texel. Per-glyph <c>(spreadRatio, sliceIdx, glyphMode,
    /// styleIdx)</c> is written into UV1.</para>
    /// <para><b>Style.</b> The shared style table dedups identical <see cref="GlyphStyle"/>s across
    /// ALL components, so the default appearance occupies one shared row and every component's styleIdx
    /// points at it — keeping the shared material's bound style texture identical for batching.</para>
    /// </remarks>
    public sealed class UnifiedRenderBuilder : IDisposable
    {
        private static readonly int MainTexArray = Shader.PropertyToID("_MainTexArray");
        private static readonly int StyleTex = Shader.PropertyToID("_StyleTex");
        private static readonly int StyleTexWidth = Shader.PropertyToID("_StyleTexWidth");
        private static readonly int StyleTexHeight = Shader.PropertyToID("_StyleTexHeight");

        private static Shader _uberShader;
        private static Shader UberShader => _uberShader != null ? _uberShader : (_uberShader = Shader.Find("UniText/Uber"));

        // ---- Process-wide shared state (keyed by draw-group format) so components batch together ---
        private static readonly Dictionary<TextureFormat, Material> SharedMaterials = new();
        private static readonly StyleTable SharedStyles = new();

        /// <summary>Shared uber material for a draw-group format, created once and reused by every component.</summary>
        private static Material MaterialFor(TextureFormat format)
        {
            if (SharedMaterials.TryGetValue(format, out var m) && m != null) return m;
            var sh = UberShader;
            if (sh == null)
                Cat.MeowWarnFormat("[UniText] UniText/Uber shader not found at runtime (stripped from the build?). " +
                    "The unified renderer is falling back to UI/Default and will NOT render text correctly. " +
                    "Ensure it is in Always-Included Shaders (the build processor adds it).");
            m = new Material(sh != null ? sh : Shader.Find("UI/Default"))
            {
                name = $"UniText Uber Shared [{format}]",
                hideFlags = HideFlags.DontSave,
            };
            SharedMaterials[format] = m;
            return m;
        }

        /// <summary>Drops the shared material + style table (test reset). Does not touch the arrays (SharedGlyphAtlas owns those).</summary>
        public static void ResetShared()
        {
            foreach (var m in SharedMaterials.Values) if (m != null) UnityEngine.Object.DestroyImmediate(m);
            SharedMaterials.Clear();
            SharedStyles.Reset();
        }

        private sealed class Group
        {
            public readonly List<Vector3> verts = new();
            public readonly List<Vector3> normals = new();
            public readonly List<Color32> colors = new();
            public readonly List<Vector4> uv0 = new();
            public readonly List<Vector4> uv1 = new();
            public readonly List<int> tris = new();
            public Mesh mesh;
            public int pageSize;
            public void ClearBuffers() { verts.Clear(); normals.Clear(); colors.Clear(); uv0.Clear(); uv1.Clear(); tris.Clear(); pageSize = 0; }
        }

        private readonly Dictionary<TextureFormat, Group> _groups = new();
        private readonly List<Vector4> _tmpUv = new();
        private readonly List<Vector3> _tmpV = new();
        private readonly List<Vector3> _tmpN = new();
        private readonly List<Color32> _tmpC = new();
        private readonly List<int> _tmpTri = new();

        private static UberDrawGroup.GlyphMode ModeFromFormat(TextureFormat f) => f switch
        {
            TextureFormat.RGB24 => UberDrawGroup.GlyphMode.Msdf,
            TextureFormat.RGBA32 => UberDrawGroup.GlyphMode.Colr,
            _ => UberDrawGroup.GlyphMode.Sdf,
        };

        /// <summary>
        /// Builds the merged render data (≤2 entries) into <paramref name="output"/> from the legacy
        /// per-segment <paramref name="segments"/>, shading every glyph with the component-wide
        /// <paramref name="style"/> (resolved to a shared style-table row). Main-thread only.
        /// </summary>
        private static readonly ProfilerMarker s_BuildMarker = new("UniText.Unified.Build");

        /// <summary>Diagnostics: when true, logs each segment's page format -> glyphMode -> dst array.</summary>
        public static bool DiagLog = false;

        public void Build(List<UniTextRenderData> segments, in GlyphStyle style, List<UniTextRenderData> output)
        {
            using var _ = s_BuildMarker.Auto();
            output.Clear();
            if (segments == null || segments.Count == 0) return;

            // Shared style table (dedups identical styles across all components) -> stable styleIdx.
            int styleIdx = SharedStyles.GetOrAdd(style);
            var styleTex = SharedStyles.Apply();

            foreach (var kv in _groups) kv.Value.ClearBuffers();

            foreach (var seg in segments)
            {
                if (seg.mesh == null || seg.mesh.vertexCount == 0 || seg.texture is not Texture2D page) continue;

                var mode = ModeFromFormat(page.format);
                var dstFormat = UberDrawGroup.FormatFor(mode);
                if (DiagLog)
                    Debug.Log($"[UnifiedRenderBuilder DIAG] seg fontId={seg.fontId} pageFormat={page.format} -> glyphMode={mode} dstArray={dstFormat} verts={seg.mesh.vertexCount}");
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
                m.SetNormals(g.normals);
                m.SetColors(g.colors);
                m.SetUVs(0, g.uv0);
                m.SetUVs(1, g.uv1);
                m.SetTriangles(g.tris, 0);

                // SHARED material (batches across components) carrying the shared array + style texture.
                var mat = MaterialFor(kv.Key);
                mat.SetTexture(MainTexArray, arr.Texture);
                mat.SetTexture(StyleTex, styleTex);
                mat.SetFloat(StyleTexWidth, SharedStyles.Width);
                mat.SetFloat(StyleTexHeight, Mathf.Max(1, SharedStyles.Count));

                // Array bound via material _MainTexArray; CanvasRenderer texture MUST be null (a
                // Texture2DArray trips a native kTexDim2D assert in CanvasRenderer.SetTexture).
                output.Add(new UniTextRenderData(m, mat, (Texture)null, 0));
            }
        }

        private void AppendSegment(Group g, Mesh src, int slice, int glyphMode, int styleIdx)
        {
            int baseIndex = g.verts.Count;
            // Non-allocating reads into reusable scratch lists (the .vertices/.colors32/.triangles
            // PROPERTIES allocate a fresh array every call — the per-frame GC the benchmark flagged).
            _tmpV.Clear(); src.GetVertices(_tmpV);
            _tmpN.Clear(); src.GetNormals(_tmpN);
            _tmpC.Clear(); src.GetColors(_tmpC);
            _tmpUv.Clear(); src.GetUVs(0, _tmpUv);
            _tmpTri.Clear(); src.GetTriangles(_tmpTri, 0);

            bool haveColors = _tmpC.Count == _tmpV.Count;
            bool haveNormals = _tmpN.Count == _tmpV.Count;
            for (int i = 0; i < _tmpV.Count; i++)
            {
                g.verts.Add(_tmpV[i]);
                g.normals.Add(haveNormals ? _tmpN[i] : new Vector3(0, 0, -1)); // UGUI forward normal
                g.colors.Add(haveColors ? _tmpC[i] : (Color32)Color.white);
                var uv0 = i < _tmpUv.Count ? _tmpUv[i] : Vector4.zero;
                g.uv0.Add(uv0);
                g.uv1.Add(new Vector4(uv0.z, slice, glyphMode, styleIdx));
            }
            for (int i = 0; i < _tmpTri.Count; i++)
                g.tris.Add(baseIndex + _tmpTri[i]);
        }

        private static Mesh NewMesh()
        {
            var m = new Mesh { name = "UniText Uber Mesh", hideFlags = HideFlags.DontSave };
            m.MarkDynamic();
            return m;
        }

        /// <summary>Disposes this component's per-component meshes. The shared material/style table are process-wide (freed via <see cref="ResetShared"/>).</summary>
        public void Dispose()
        {
            foreach (var kv in _groups)
                if (kv.Value.mesh != null) UnityEngine.Object.DestroyImmediate(kv.Value.mesh);
            _groups.Clear();
        }
    }
}
