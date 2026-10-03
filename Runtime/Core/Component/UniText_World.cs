using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace LightSide
{
    /// <summary>How world-space text is shaded.</summary>
    public enum WorldTextLighting
    {
        /// <summary>Unlit: the text colour is drawn as is (default, cheapest).</summary>
        Unlit = 0,
        /// <summary>Wrapped-Lambert main directional light plus the ambient sky colour.</summary>
        Lit = 1,
    }

    /// <summary>When a world-space text component keeps a <c>BoxCollider</c> sized to its rect for pointer events.</summary>
    public enum WorldTextCollider
    {
        /// <summary>Never add a collider (raycast with <see cref="UniText.HitTestRay"/> yourself).</summary>
        None = 0,
        /// <summary>Only while the text has interactive ranges (links) or pointer event subscribers (default).</summary>
        WhenInteractive = 1,
        /// <summary>Always keep a collider (any click on the rect raises <see cref="UniText.TextClicked"/>).</summary>
        Always = 2,
    }

    /// <summary>
    /// Per-component render options of world-space text (<see cref="UniTextWorld"/>, <c>GlyphMeshPro</c>).
    /// Components with the same options share one material per atlas format.
    /// </summary>
    [Serializable]
    public struct WorldTextOptions
    {
        [Tooltip("Unlit (default) or lit by the main directional light + ambient.")]
        public WorldTextLighting lighting;

        [Tooltip("Write depth (with alpha clip at 0.5 coverage) and draw in the AlphaTest queue, so text " +
                 "occludes and is occluded like opaque geometry. Off: transparent, no depth write.")]
        public bool depthWrite;

        [Tooltip("Draw both faces (Cull Off). Off: the text is visible from the front only.")]
        public bool doubleSided;

        [Tooltip("When to keep a BoxCollider sized to the text rect, for EventSystem pointer events " +
                 "(PhysicsRaycaster, XRI TrackedDevicePhysicsRaycaster).")]
        public WorldTextCollider collider;

        /// <summary>The defaults: unlit, transparent, front face only, collider when interactive.</summary>
        public static WorldTextOptions Default => new() { collider = WorldTextCollider.WhenInteractive };

        internal int MaterialKey => (int)lighting | (depthWrite ? 2 : 0) | (doubleSided ? 4 : 0);
    }

    /// <summary>
    /// UniText partial: world-space output. A subclass that returns true from
    /// <see cref="RendersToMeshRenderer"/> (<see cref="UniTextWorld"/>, <c>GlyphMeshPro</c>) draws into the
    /// <see cref="MeshFilter"/> / <see cref="MeshRenderer"/> on its own GameObject instead of CanvasRenderers.
    /// </summary>
    /// <remarks>
    /// Everything before the renderer is unchanged: the same markup, shaping, layout, modifiers,
    /// animations and the same unified (or legacy) mesh build. The merged draw-group meshes are copied
    /// into ONE mesh owned by the component (one sub-mesh per draw group) with
    /// <see cref="Mesh.CombineMeshes(CombineInstance[], bool, bool)"/>. With the unified renderer every
    /// component that uses the same <see cref="WorldTextOptions"/> shares one material per atlas format
    /// (<see cref="UniTextWorldMaterials"/>): all per-glyph data is vertex data, so there is no per-renderer
    /// material state and the SRP Batcher can batch the draws.
    /// </remarks>
    public partial class UniText
    {
        /// <summary>
        /// True for components that render into a <see cref="MeshRenderer"/> on their own GameObject
        /// (world-space text without a Canvas). Plain <c>UniText</c> renders through CanvasRenderers.
        /// </summary>
        protected virtual bool RendersToMeshRenderer => false;

        /// <summary>World render options (material choice, collider). Only read when <see cref="RendersToMeshRenderer"/>.</summary>
        protected virtual WorldTextOptions WorldOptions => WorldTextOptions.Default;

        /// <summary>Whether this component draws into a MeshRenderer (world-space text).</summary>
        public bool IsWorldText => RendersToMeshRenderer;

        private MeshFilter worldFilter;
        private MeshRenderer worldRenderer;
        private Mesh worldMesh;
        private bool worldMeshPopulated;
        private int worldVertexCount;
        private CombineInstance[] worldCombine = Array.Empty<CombineInstance>();
        private Material[] worldMats = Array.Empty<Material>();
        private Material[] appliedWorldMats = Array.Empty<Material>();
        private Texture[] worldTextures = Array.Empty<Texture>();
        private readonly List<int> worldInstanceEntry = new();
        private readonly List<int> worldInstanceVertexStart = new();
        private MaterialPropertyBlock worldBlock;
        private bool worldPerMaterialBlocks;
        private bool worldClipApplied;
        private Rect worldAppliedClip;
        private bool worldLegacyLinearColors;
        private Vector3[] worldNormals = Array.Empty<Vector3>();
        private Vector3[] worldFxVerts = Array.Empty<Vector3>();
        private Color32[] worldFxColors = Array.Empty<Color32>();
        private static readonly List<Color32> s_worldColors = new();
        private static readonly int ClipRectId = Shader.PropertyToID("_ClipRect");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        /// <summary>The mesh this world-space component draws (owned by the component; null for Canvas text).</summary>
        public Mesh WorldMesh => worldMesh;

        /// <summary>The MeshRenderer this world-space component draws with (null for Canvas text).</summary>
        public MeshRenderer WorldRenderer
        {
            get
            {
                if (worldRenderer == null && RendersToMeshRenderer) worldRenderer = GetComponent<MeshRenderer>();
                return worldRenderer;
            }
        }

        /// <summary>TEST INSTRUMENTATION: number of world mesh uploads (CombineMeshes) since start.</summary>
        internal static long WorldMeshUploads;

        // The geometry-identity upload skip may only reuse world geometry drawn with the current options:
        // an option change (lit, depth, sides) must re-bind the materials even when the glyphs are unchanged.
        private bool HasRendererGeometry => RendersToMeshRenderer
            ? worldMeshPopulated && worldAppliedOptionsKey == WorldOptions.MaterialKey
            : subMeshRenderers.Count > 0;
        private int worldAppliedOptionsKey = -1;

        private void EnsureWorldRenderer()
        {
            if (worldFilter == null) worldFilter = GetComponent<MeshFilter>();
            if (worldFilter == null) worldFilter = gameObject.AddComponent<MeshFilter>();
            if (worldRenderer == null) worldRenderer = GetComponent<MeshRenderer>();
            if (worldRenderer == null) worldRenderer = gameObject.AddComponent<MeshRenderer>();
            if (worldMesh == null)
            {
                worldMesh = new Mesh { name = "UniText World Mesh", hideFlags = HideFlags.HideAndDontSave };
                worldMesh.MarkDynamic();
            }
            if (worldFilter.sharedMesh != worldMesh) worldFilter.sharedMesh = worldMesh;
        }

        /// <summary>
        /// MAIN THREAD. Runs the UGUI layout callbacks inline when the layout system has not (world text
        /// has no Canvas, and a Canvas-less RectTransform may never be laid out). Cached: a no-op once
        /// the layout pass already produced valid lines for the current rect.
        /// </summary>
        private void EnsureWorldLayout()
        {
            if (hasValidLayoutCache) return;
            var element = (ILayoutElement)this;
            element.CalculateLayoutInputHorizontal();
            element.CalculateLayoutInputVertical();
            ((ILayoutController)this).SetLayoutVertical();
        }

        /// <summary>Copies the draw-group meshes into this component's own mesh and binds the materials.</summary>
        private void ApplyWorldMeshes(List<UniTextRenderData> data, bool unified)
        {
            EnsureWorldRenderer();
            var options = WorldOptions;

            var n = 0;
            var totalVerts = 0;
            worldInstanceEntry.Clear();
            worldInstanceVertexStart.Clear();
            for (var ei = 0; ei < data.Count; ei++)
            {
                var e = data[ei];
                if (e.mesh == null) continue;
                var vc = e.mesh.vertexCount;
                if (vc == 0) continue;
                var mats = e.materials;
                var mc = mats?.Length ?? 0;
                for (var mi = 0; mi < mc; mi++)
                {
                    if (mats[mi] == null) continue;
                    EnsureWorldCapacity(n + 1);
                    worldMats[n] = unified ? UniTextWorldMaterials.Get(mats[mi], options) : mats[mi];
                    worldTextures[n] = e.texture;
                    worldInstanceEntry.Add(ei);
                    worldInstanceVertexStart.Add(totalVerts);
                    totalVerts += vc;
                    n++;
                }
            }

            if (n == 0)
            {
                ClearWorldMesh();
                return;
            }

            if (worldCombine.Length != n) worldCombine = new CombineInstance[n];
            for (var k = 0; k < n; k++)
                worldCombine[k] = new CombineInstance { mesh = data[worldInstanceEntry[k]].mesh, subMeshIndex = 0 };

            var m = worldMesh;
            m.Clear();
            m.indexFormat = totalVerts > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.CombineMeshes(worldCombine, false, false);
            for (var k = 0; k < n; k++) worldCombine[k].mesh = null; // do not keep the shared meshes alive
            worldVertexCount = totalVerts;
            worldMeshPopulated = true;
            System.Threading.Interlocked.Increment(ref WorldMeshUploads);

            // Legacy materials are the Canvas SDF/MSDF shaders: they read the vertex normal for the
            // perspective filter (a Canvas supplies it, a MeshRenderer does not) and get vertex colours
            // that a Canvas would have converted to linear. Patch both on the copy. The world Uber
            // shader does both itself, so the unified path copies nothing per vertex.
            worldLegacyLinearColors = !unified && QualitySettings.activeColorSpace == ColorSpace.Linear;
            if (!unified)
            {
                if (worldNormals.Length < totalVerts)
                {
                    worldNormals = new Vector3[Mathf.NextPowerOfTwo(totalVerts)];
                    for (var i = 0; i < worldNormals.Length; i++) worldNormals[i] = Vector3.back;
                }
                m.SetNormals(worldNormals, 0, totalVerts);
                if (worldLegacyLinearColors)
                {
                    s_worldColors.Clear();
                    m.GetColors(s_worldColors);
                    for (var i = 0; i < s_worldColors.Count; i++) s_worldColors[i] = GammaToLinear(s_worldColors[i]);
                    m.SetColors(s_worldColors);
                }
            }

            ApplyWorldMaterials(n, unified);
            worldAppliedOptionsKey = options.MaterialKey;
            ApplyWorldClip();
            UpdateWorldCollider();
        }

        private void EnsureWorldCapacity(int n)
        {
            if (worldMats.Length >= n) return;
            var size = Mathf.Max(4, Mathf.NextPowerOfTwo(n));
            Array.Resize(ref worldMats, size);
            Array.Resize(ref worldTextures, size);
        }

        private void ApplyWorldMaterials(int n, bool unified)
        {
            var r = worldRenderer;
            var same = appliedWorldMats.Length == n;
            for (var k = 0; same && k < n; k++) same = appliedWorldMats[k] == worldMats[k];
            if (!same)
            {
                appliedWorldMats = new Material[n];
                Array.Copy(worldMats, appliedWorldMats, n);
                r.sharedMaterials = appliedWorldMats;
            }

            // Legacy pages are bound per draw like CanvasRenderer.SetTexture: a per-material block only
            // where the material's own texture is not the page. Unified binds nothing per renderer.
            var needBlocks = false;
            if (!unified)
                for (var k = 0; k < n; k++)
                    if (worldTextures[k] != null && appliedWorldMats[k].HasProperty(MainTexId) &&
                        appliedWorldMats[k].GetTexture(MainTexId) != worldTextures[k]) { needBlocks = true; break; }

            if (needBlocks)
            {
                worldBlock ??= new MaterialPropertyBlock();
                for (var k = 0; k < n; k++)
                {
                    worldBlock.Clear();
                    if (worldTextures[k] != null) worldBlock.SetTexture(MainTexId, worldTextures[k]);
                    r.SetPropertyBlock(worldBlock, k);
                }
                worldPerMaterialBlocks = true;
            }
            else if (worldPerMaterialBlocks)
            {
                for (var k = 0; k < r.sharedMaterials.Length; k++) r.SetPropertyBlock(null, k);
                worldPerMaterialBlocks = false;
            }
        }

        /// <summary>
        /// Overflow <see cref="TextOverflow.Clip"/> for world text: the world shader clips to <c>_ClipRect</c>
        /// in object space, set on this renderer only (a per-renderer block, so only clipped labels leave
        /// the SRP Batcher). Legacy materials do not clip in world space.
        /// </summary>
        private void ApplyWorldClip()
        {
            var r = worldRenderer;
            if (r == null) return;
            var want = overflow == TextOverflow.Clip && worldMeshPopulated;
            if (want)
            {
                var local = rectTransform.rect;
                var pad = padding;
                if (pad != Vector4.zero)
                    local = Rect.MinMaxRect(local.xMin + pad.x, local.yMin + pad.w,
                        Mathf.Max(local.xMin + pad.x, local.xMax - pad.z), Mathf.Max(local.yMin + pad.w, local.yMax - pad.y));
                if (worldClipApplied && ClipApproximatelyEqual(local, worldAppliedClip)) return;
                worldBlock ??= new MaterialPropertyBlock();
                r.GetPropertyBlock(worldBlock);
                worldBlock.SetVector(ClipRectId, new Vector4(local.xMin, local.yMin, local.xMax, local.yMax));
                r.SetPropertyBlock(worldBlock);
                worldClipApplied = true;
                worldAppliedClip = local;
            }
            else if (worldClipApplied)
            {
                r.SetPropertyBlock(null);
                worldClipApplied = false;
            }
        }

        private void ClearWorldMesh()
        {
            if (worldMesh != null) worldMesh.Clear();
            worldMeshPopulated = false;
            worldVertexCount = 0;
            UpdateWorldCollider();
        }

        private void DestroyWorldMesh()
        {
            if (worldFilter != null && worldFilter.sharedMesh == worldMesh) worldFilter.sharedMesh = null;
            if (worldMesh != null) ObjectUtils.SafeDestroy(worldMesh);
            worldMesh = null;
            worldMeshPopulated = false;
        }

        /// <summary>Per-frame vertex effects for world text: concatenates the effect work buffers into the world mesh.</summary>
        private void UploadWorldVertexEffects(UniTextVertexEffectContext ctx)
        {
            if (worldMesh == null || !worldMeshPopulated) return;
            var total = worldVertexCount;
            if (worldFxVerts.Length < total)
            {
                worldFxVerts = new Vector3[Mathf.NextPowerOfTwo(total)];
                worldFxColors = new Color32[worldFxVerts.Length];
            }

            for (var k = 0; k < worldInstanceEntry.Count; k++)
            {
                var ei = worldInstanceEntry[k];
                if (ei >= ctx.entryCount) return;
                var e = ctx.entries[ei];
                var start = worldInstanceVertexStart[k];
                var count = Mathf.Min(e.count, total - start);
                if (count <= 0) continue;
                Array.Copy(e.verts, 0, worldFxVerts, start, count);
                if (worldLegacyLinearColors)
                    for (var i = 0; i < count; i++) worldFxColors[start + i] = GammaToLinear(e.colors[i]);
                else
                    Array.Copy(e.colors, 0, worldFxColors, start, count);
            }

            worldMesh.SetVertices(worldFxVerts, 0, total);
            worldMesh.SetColors(worldFxColors, 0, total);
            worldMesh.RecalculateBounds(); // culling follows the animated glyphs
        }

        private static Color32 GammaToLinear(Color32 c)
        {
            var l = ((Color)c).linear;
            return new Color32((byte)(l.r * 255f + 0.5f), (byte)(l.g * 255f + 0.5f), (byte)(l.b * 255f + 0.5f), c.a);
        }

        /// <summary>True when any pointer event of this component has a subscriber.</summary>
        internal bool HasPointerSubscribers =>
            TextClicked != null || RangeClicked != null || RangeEntered != null || RangeExited != null || HoverChanged != null;

        /// <summary>Whether the text currently has interactive ranges (links etc.) or pointer subscribers.</summary>
        public bool IsInteractive
        {
            get
            {
                if (HasPointerSubscribers) return true;
                var registry = buffers != null ? InteractiveRangeRegistry.Get(buffers) : null;
                return registry != null && registry.ProviderCount > 0;
            }
        }

        /// <summary>
        /// Keeps the BoxCollider in sync with the rect (world text only), per <see cref="WorldTextOptions.collider"/>.
        /// Called after every mesh apply; call it after subscribing to pointer events from code.
        /// </summary>
        public void UpdateWorldCollider()
        {
#if OPENGLYPH_PHYSICS
            if (!RendersToMeshRenderer) return;
            var mode = WorldOptions.collider;
            var want = isActiveAndEnabled && raycastTarget &&
                       (mode == WorldTextCollider.Always || (mode == WorldTextCollider.WhenInteractive && IsInteractive));
            var box = worldCollider;
            if (box == null) box = worldCollider = FindOwnCollider();
            if (!want)
            {
                if (box != null && box.enabled) box.enabled = false;
                return;
            }
            if (box == null)
            {
                box = worldCollider = gameObject.AddComponent<BoxCollider>();
                box.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
            }
            var rect = rectTransform.rect;
            var depth = Mathf.Max(0.01f, Mathf.Min(rect.width, rect.height) * 0.02f);
            var center = new Vector3(rect.center.x, rect.center.y, 0f);
            var size = new Vector3(Mathf.Max(rect.width, 0.0001f), Mathf.Max(rect.height, 0.0001f), depth);
            if (box.center != center) box.center = center;
            if (box.size != size) box.size = size;
            if (!box.enabled) box.enabled = true;
#endif
        }

#if OPENGLYPH_PHYSICS
        private BoxCollider worldCollider;

        // The collider this component added (DontSave), not one the user placed on the object.
        private BoxCollider FindOwnCollider()
        {
            var boxes = GetComponents<BoxCollider>();
            for (var i = 0; i < boxes.Length; i++)
                if ((boxes[i].hideFlags & HideFlags.DontSave) != 0) return boxes[i];
            return null;
        }

        /// <summary>The BoxCollider this world text keeps for pointer events (null when none).</summary>
        public BoxCollider WorldCollider => worldCollider;

        // OnDestroy: the GameObject may be being destroyed with all its components (destroying a sibling
        // then is an error), so only switch the collider off; it is DontSave, so it is never persisted.
        private void DestroyWorldCollider()
        {
            if (worldCollider != null) worldCollider.enabled = false;
            worldCollider = null;
        }
#else
        private void DestroyWorldCollider() { }
#endif
    }
}
