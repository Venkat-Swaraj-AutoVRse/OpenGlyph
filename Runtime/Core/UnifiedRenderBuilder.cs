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
        private static readonly int AtlasSize = Shader.PropertyToID("_AtlasSize");

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
            StencilCopies.Clear();
        }

        // Mask (stencil) copies of the shared materials. StencilMaterial.Add copies a material ONCE and
        // caches it, but the shared material's array/style bindings change on every Build (the array is
        // reallocated as pages are added, the style texture as rows are added). Without re-syncing, a
        // masked component samples a stale/destroyed array and draws nothing.
        private static readonly List<(Material shared, Material copy)> StencilCopies = new();

        /// <summary>True when <paramref name="mat"/> is one of the shared unified (Uber) materials.</summary>
        public static bool IsSharedMaterial(Material mat)
        {
            if (mat == null) return false;
            foreach (var m in SharedMaterials.Values) if (m == mat) return true;
            return false;
        }

        /// <summary>Registers a stencil copy of a shared material so later Builds keep its bindings current.</summary>
        public static void TrackStencilCopy(Material shared, Material copy)
        {
            if (shared == null || copy == null || shared == copy) return;
            CopyBindings(shared, copy);
            foreach (var (_, c) in StencilCopies) if (c == copy) return;
            StencilCopies.Add((shared, copy));
        }

        private static void SyncStencilCopies(Material shared)
        {
            for (int i = StencilCopies.Count - 1; i >= 0; i--)
            {
                var (s, c) = StencilCopies[i];
                if (s == null || c == null) { StencilCopies.RemoveAt(i); continue; } // released by StencilMaterial
                if (s == shared) CopyBindings(s, c);
            }
        }

        private static void CopyBindings(Material src, Material dst)
        {
            dst.SetTexture(MainTexArray, src.GetTexture(MainTexArray));
            dst.SetTexture(StyleTex, src.GetTexture(StyleTex));
            dst.SetFloat(StyleTexWidth, src.GetFloat(StyleTexWidth));
            dst.SetFloat(StyleTexHeight, src.GetFloat(StyleTexHeight));
            dst.SetFloat(AtlasSize, src.GetFloat(AtlasSize));
        }

        // Per-format merged geometry, kept in plain arrays that are reused across Builds (grown, never
        // shrunk). Bulk channels are block-copied and the mesh is set from array ranges. The earlier
        // List<T>-per-vertex Add loop cost ~0.35 ms per 7.6k-vertex component on Windows, the largest
        // part of a unified rebuild.
        private sealed class Group
        {
            public Vector3[] verts = Array.Empty<Vector3>();
            public Vector3[] normals = Array.Empty<Vector3>();
            public Color32[] colors = Array.Empty<Color32>();
            public Vector4[] uv0 = Array.Empty<Vector4>();
            public Vector4[] uv1 = Array.Empty<Vector4>();
            public int[] tris = Array.Empty<int>();
            public int vertCount;
            public int triCount;
            // normals[0..constNormals) are known to hold the UGUI forward normal (0,0,-1).
            public int constNormals;
            public Mesh mesh;
            public int pageSize;

            public void ClearBuffers() { vertCount = 0; triCount = 0; pageSize = 0; }

            public void EnsureVerts(int required)
            {
                if (verts.Length >= required) return;
                var cap = Math.Max(required, Math.Max(256, verts.Length * 2));
                Array.Resize(ref verts, cap);
                Array.Resize(ref normals, cap);
                Array.Resize(ref colors, cap);
                Array.Resize(ref uv0, cap);
                Array.Resize(ref uv1, cap);
            }

            public void EnsureTris(int required)
            {
                if (tris.Length >= required) return;
                Array.Resize(ref tris, Math.Max(required, Math.Max(384, tris.Length * 2)));
            }

            /// <summary>Writes the forward normal into [start, end) unless it is already there.</summary>
            public void FillConstNormals(int start, int end)
            {
                if (end <= constNormals) return;
                var from = Math.Max(start, constNormals);
                var n = new Vector3(0, 0, -1);
                for (var i = from; i < end; i++) normals[i] = n;
                if (start <= constNormals) constNormals = end;
            }
        }

        private readonly Dictionary<TextureFormat, Group> _groups = new();
        private readonly List<Vector4> _tmpUv = new();
        private readonly List<Vector4> _tmpUv1 = new();
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

        /// <summary>
        /// TEST INSTRUMENTATION: number of segments the last builds merged by reading the Unity mesh
        /// back (<c>Mesh.GetVertices</c> etc.) instead of from the generator's own buffers. The
        /// component rebuild path must never read back; tests reset this and assert it stays 0.
        /// </summary>
        internal static long MeshReadbackSegments;

        public void Build(List<UniTextRenderData> segments, in GlyphStyle style, List<UniTextRenderData> output)
            => Build(segments, null, style, null, output);

        /// <summary>
        /// R2 sub-task 2 overload: when a per-component <paramref name="spanStyles"/> collector is
        /// supplied, each glyph's LOCAL style id (written into source UV1.w by the component's span-style
        /// coordinator) is mapped to the collector's composed <see cref="GlyphStyle"/> and then to a
        /// shared <see cref="StyleTable"/> row — so distinct per-span styles each get their own deduped
        /// row chosen per vertex, still one renderer. Local id 0 (no span) uses the base <paramref name="style"/>.
        /// </summary>
        public void Build(List<UniTextRenderData> segments, in GlyphStyle style, SpanStyleCollector spanStyles, List<UniTextRenderData> output)
            => Build(segments, null, style, spanStyles, output);

        /// <summary>
        /// Same as the public overloads, but when <paramref name="source"/> is the generator that just
        /// produced <paramref name="segments"/> (its buffers not yet returned, one generated segment per
        /// entry), the geometry is read straight from the generator's managed buffers instead of being
        /// read back out of the Unity meshes. Falls back to the mesh readback otherwise.
        /// </summary>
        internal void Build(List<UniTextRenderData> segments, UniTextMeshGenerator source, in GlyphStyle style,
            SpanStyleCollector spanStyles, List<UniTextRenderData> output)
        {
            using var _ = s_BuildMarker.Auto();
            output.Clear();
            if (segments == null || segments.Count == 0) return;

            var genSegs = source != null && source.HasGeneratedData ? source.GeneratedSegments : null;
            if (genSegs != null && genSegs.Count != segments.Count) genSegs = null;

            // Shared style table (dedups identical styles across all components) -> stable styleIdx.
            int baseStyleIdx = SharedStyles.GetOrAdd(style);

            foreach (var kv in _groups) kv.Value.ClearBuffers();

            for (var si = 0; si < segments.Count; si++)
            {
                var seg = segments[si];
                if (seg.mesh == null || seg.texture is not Texture2D page) continue;
                var vertexCount = genSegs != null ? genSegs.buffer[si].vertexCount : seg.mesh.vertexCount;
                if (vertexCount == 0) continue;

                var mode = ModeFromFormat(page.format);
                var dstFormat = UberDrawGroup.FormatFor(mode);
                if (DiagLog)
                    Debug.Log($"[UnifiedRenderBuilder DIAG] seg fontId={seg.fontId} pageFormat={page.format} -> glyphMode={mode} dstArray={dstFormat} verts={vertexCount}");
                var arr = SharedGlyphAtlas.Get(dstFormat, page.width);
                // Pass the page's current revision so a page mutated IN PLACE after its first copy
                // (glyphs added later) is re-copied into its existing slice rather than drawing the
                // stale (blank) copy. The revision is bumped by UniTextFont on every glyph upload.
                int pageRev = UniTextFont.AtlasPageRevision(page);
                if (!arr.AddPage(page.GetInstanceID(), page, pageRev, out int slice))
                    continue;

                if (!_groups.TryGetValue(dstFormat, out var g)) { g = new Group(); _groups[dstFormat] = g; }
                g.pageSize = page.width;
                if (genSegs != null)
                    AppendFromGenerator(g, source, in genSegs.buffer[si], slice, (int)mode, baseStyleIdx, spanStyles);
                else
                    AppendSegment(g, seg.mesh, slice, (int)mode, baseStyleIdx, spanStyles);
            }

            // Build/refresh the shared style texture AFTER span rows have been added above.
            var styleTex = SharedStyles.Apply();

            foreach (var kv in _groups)
            {
                var g = kv.Value;
                if (g.vertCount == 0) continue;
                var arr = SharedGlyphAtlas.Get(kv.Key, g.pageSize > 0 ? g.pageSize : 1024);
                arr.Apply(false);

                if (g.mesh == null) g.mesh = NewMesh();
                var m = g.mesh;
                m.Clear();
                m.SetVertices(g.verts, 0, g.vertCount);
                m.SetNormals(g.normals, 0, g.vertCount);
                m.SetColors(g.colors, 0, g.vertCount);
                m.SetUVs(0, g.uv0, 0, g.vertCount);
                m.SetUVs(1, g.uv1, 0, g.vertCount);
                m.SetTriangles(g.tris, 0, g.triCount, 0);

                // SHARED material (batches across components) carrying the shared array + style texture.
                var mat = MaterialFor(kv.Key);
                mat.SetTexture(MainTexArray, arr.Texture);
                mat.SetTexture(StyleTex, styleTex);
                mat.SetFloat(StyleTexWidth, SharedStyles.Width);
                mat.SetFloat(StyleTexHeight, Mathf.Max(1, SharedStyles.Count));
                mat.SetFloat(AtlasSize, g.pageSize > 0 ? g.pageSize : 1024);
                SyncStencilCopies(mat);

                // Array bound via material _MainTexArray; CanvasRenderer texture MUST be null (a
                // Texture2DArray trips a native kTexDim2D assert in CanvasRenderer.SetTexture).
                output.Add(new UniTextRenderData(m, mat, (Texture)null, 0));
            }
        }

        /// <summary>
        /// Appends one generated segment from the generator's buffers: positions, colours and UV0 are
        /// block-copied; only UV1 (slice / glyph mode / shared style row) and the rebased indices are
        /// written per element. The generator emits no normals, so the UGUI forward normal is used —
        /// the same value the readback path substitutes for a mesh without normals.
        /// </summary>
        private void AppendFromGenerator(Group g, UniTextMeshGenerator src, in GeneratedMeshSegment seg,
            int slice, int glyphMode, int baseStyleIdx, SpanStyleCollector spanStyles)
        {
            var count = seg.vertexCount;
            var start = seg.vertexStart;
            var baseIndex = g.vertCount;
            g.EnsureVerts(baseIndex + count);

            Array.Copy(src.Vertices, start, g.verts, baseIndex, count);
            Array.Copy(src.Colors, start, g.colors, baseIndex, count);
            Array.Copy(src.Uvs0, start, g.uv0, baseIndex, count);
            g.FillConstNormals(baseIndex, baseIndex + count);

            var srcUv1 = src.Uvs1;
            var dstUv1 = g.uv1;
            int lastLocal = -1, lastShared = baseStyleIdx;
            for (int i = 0; i < count; i++)
            {
                var u = srcUv1[start + i];
                // UV1.x is the real spreadRatio (Padding/PointSize), UV1.w the per-glyph LOCAL
                // span-style id (0 = base); see AppendSegment.
                int styleIdx = baseStyleIdx;
                if (spanStyles != null)
                {
                    int local = (int)(u.w + 0.5f);
                    if (local == lastLocal) styleIdx = lastShared;
                    else if (local <= 0) { lastLocal = local; lastShared = baseStyleIdx; }
                    else
                    {
                        styleIdx = SharedStyles.GetOrAdd(spanStyles.StyleAt(local));
                        lastLocal = local; lastShared = styleIdx;
                    }
                }
                dstUv1[baseIndex + i] = new Vector4(u.x, slice, glyphMode, styleIdx);
            }

            var triCount = seg.triangleCount;
            var triBase = g.triCount;
            g.EnsureTris(triBase + triCount);
            var srcTris = src.Triangles;
            var dstTris = g.tris;
            var triStart = seg.triangleStart;
            for (int i = 0; i < triCount; i++)
                dstTris[triBase + i] = baseIndex + srcTris[triStart + i];

            g.vertCount = baseIndex + count;
            g.triCount = triBase + triCount;
        }

        private void AppendSegment(Group g, Mesh src, int slice, int glyphMode, int baseStyleIdx, SpanStyleCollector spanStyles)
        {
            System.Threading.Interlocked.Increment(ref MeshReadbackSegments);
            int baseIndex = g.vertCount;
            // Non-allocating reads into reusable scratch lists (the .vertices/.colors32/.triangles
            // PROPERTIES allocate a fresh array every call — the per-frame GC the benchmark flagged).
            _tmpV.Clear(); src.GetVertices(_tmpV);
            _tmpN.Clear(); src.GetNormals(_tmpN);
            _tmpC.Clear(); src.GetColors(_tmpC);
            _tmpUv.Clear(); src.GetUVs(0, _tmpUv);
            _tmpUv1.Clear(); src.GetUVs(1, _tmpUv1);
            _tmpTri.Clear(); src.GetTriangles(_tmpTri, 0);

            var count = _tmpV.Count;
            bool haveColors = _tmpC.Count == count;
            bool haveNormals = _tmpN.Count == count;
            bool haveUv1 = _tmpUv1.Count == count;
            g.EnsureVerts(baseIndex + count);
            if (haveNormals)
            {
                for (int i = 0; i < count; i++) g.normals[baseIndex + i] = _tmpN[i];
                if (g.constNormals > baseIndex) g.constNormals = baseIndex;
            }
            else g.FillConstNormals(baseIndex, baseIndex + count);

            // Small cache so repeated local ids within a segment don't re-walk the shared table.
            int lastLocal = -1, lastShared = baseStyleIdx;
            for (int i = 0; i < count; i++)
            {
                g.verts[baseIndex + i] = _tmpV[i];
                g.colors[baseIndex + i] = haveColors ? _tmpC[i] : (Color32)Color.white;
                g.uv0[baseIndex + i] = i < _tmpUv.Count ? _tmpUv[i] : Vector4.zero;
                // UV1.x MUST be the real spreadRatio (Padding/PointSize) from the source mesh's
                // TEXCOORD1.x — NOT uv0.z (gradientScale). normFactor = 0.1/spreadRatio, and getting
                // this wrong (≈0.01 instead of ≈1) makes the outline/underlay offset ~100x too small.
                float spreadRatio = haveUv1 ? _tmpUv1[i].x : 0.1f;
                // UV1.w carries the per-glyph LOCAL span-style id (0 = base) written by the component
                // coordinator. Map it to a shared StyleTable row; 0 maps straight to baseStyleIdx.
                int styleIdx = baseStyleIdx;
                if (spanStyles != null && haveUv1)
                {
                    int local = (int)(_tmpUv1[i].w + 0.5f);
                    if (local == lastLocal) styleIdx = lastShared;
                    else if (local <= 0) { styleIdx = baseStyleIdx; lastLocal = local; lastShared = baseStyleIdx; }
                    else
                    {
                        styleIdx = SharedStyles.GetOrAdd(spanStyles.StyleAt(local));
                        lastLocal = local; lastShared = styleIdx;
                    }
                }
                g.uv1[baseIndex + i] = new Vector4(spreadRatio, slice, glyphMode, styleIdx);
            }

            var triBase = g.triCount;
            g.EnsureTris(triBase + _tmpTri.Count);
            for (int i = 0; i < _tmpTri.Count; i++)
                g.tris[triBase + i] = baseIndex + _tmpTri[i];

            g.vertCount = baseIndex + count;
            g.triCount = triBase + _tmpTri.Count;
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
