using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace LightSide
{
    /// <summary>
    /// Solid rectangles in an input field's text space: the selection highlight, the caret and the IME
    /// composition underline. One instance per layer; created and owned by <see cref="UniTextInputField"/>
    /// (not saved with the scene). On a Canvas it is a <see cref="MaskableGraphic"/>, so a
    /// <see cref="RectMask2D"/> on the field's viewport clips it like the text. For world-space fields
    /// <see cref="UniTextInputWorldOverlay"/> draws the same rectangles with a MeshRenderer.
    /// </summary>
    [AddComponentMenu("")]
    [ExecuteAlways]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class UniTextInputOverlay : MaskableGraphic
    {
        private readonly List<Rect> rects = new();
        private readonly List<Color32> colors = new();

        /// <summary>Number of rectangles currently drawn.</summary>
        public int RectCount => rects.Count;

        /// <summary>The rectangles currently drawn (local space).</summary>
        public IReadOnlyList<Rect> Rects => rects;

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
        }

        internal void Clear() => rects.Clear();

        internal void Add(Rect r, Color c)
        {
            rects.Add(r);
            colors.Add(c);
        }

        internal void BeginRects()
        {
            rects.Clear();
            colors.Clear();
        }

        /// <summary>Uploads the rectangles to the CanvasRenderer now (no wait for the canvas rebuild).</summary>
        internal void Apply()
        {
            if (!IsActive()) return;
            UpdateGeometry();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            for (var i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                var c = colors[i];
                var v = vh.currentVertCount;
                vh.AddVert(new Vector3(r.xMin, r.yMin), c, Vector4.zero);
                vh.AddVert(new Vector3(r.xMin, r.yMax), c, Vector4.zero);
                vh.AddVert(new Vector3(r.xMax, r.yMax), c, Vector4.zero);
                vh.AddVert(new Vector3(r.xMax, r.yMin), c, Vector4.zero);
                vh.AddTriangle(v, v + 1, v + 2);
                vh.AddTriangle(v + 2, v + 3, v);
            }
        }

        /// <summary>Shows or hides without rebuilding (caret blink): CanvasRenderer alpha.</summary>
        internal void SetVisible(bool visible)
        {
            canvasRenderer.SetAlpha(visible ? 1f : 0f);
        }

        public override bool Raycast(Vector2 sp, Camera eventCamera) => false;
    }

    /// <summary>
    /// World-space counterpart of <see cref="UniTextInputOverlay"/>: the same rectangles drawn by a
    /// MeshRenderer with an unlit vertex-colour material, clipped on the CPU to the field's viewport.
    /// </summary>
    [AddComponentMenu("")]
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class UniTextInputWorldOverlay : MonoBehaviour
    {
        private static Material s_material;
        private Mesh mesh;
        private MeshRenderer meshRenderer;
        private readonly List<Vector3> verts = new();
        private readonly List<Color32> vcolors = new();
        private readonly List<int> tris = new();
        private readonly List<Rect> rects = new();

        /// <summary>The rectangles currently drawn (local space, after clipping).</summary>
        public IReadOnlyList<Rect> Rects => rects;
        public int RectCount => rects.Count;
        public MeshRenderer Renderer => meshRenderer != null ? meshRenderer : meshRenderer = GetComponent<MeshRenderer>();

        internal static Material SharedMaterial
        {
            get
            {
                if (s_material != null) return s_material;
                var shader = Shader.Find("UI/Default");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                s_material = new Material(shader) { name = "UniText Input Overlay", hideFlags = HideFlags.HideAndDontSave };
                return s_material;
            }
        }

        private void EnsureMesh()
        {
            if (mesh != null) return;
            mesh = new Mesh { name = "UniText Input Overlay", hideFlags = HideFlags.HideAndDontSave };
            mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = Renderer;
            r.sharedMaterial = SharedMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        internal void BeginRects()
        {
            rects.Clear();
            verts.Clear();
            vcolors.Clear();
            tris.Clear();
        }

        internal void Add(Rect r, Color c, bool clip, Rect clipRect)
        {
            if (clip)
            {
                var xMin = Mathf.Max(r.xMin, clipRect.xMin);
                var yMin = Mathf.Max(r.yMin, clipRect.yMin);
                var xMax = Mathf.Min(r.xMax, clipRect.xMax);
                var yMax = Mathf.Min(r.yMax, clipRect.yMax);
                if (xMax <= xMin || yMax <= yMin) return;
                r = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            }
            rects.Add(r);
            var v = verts.Count;
            verts.Add(new Vector3(r.xMin, r.yMin));
            verts.Add(new Vector3(r.xMin, r.yMax));
            verts.Add(new Vector3(r.xMax, r.yMax));
            verts.Add(new Vector3(r.xMax, r.yMin));
            Color32 c32 = c;
            vcolors.Add(c32); vcolors.Add(c32); vcolors.Add(c32); vcolors.Add(c32);
            tris.Add(v); tris.Add(v + 1); tris.Add(v + 2);
            tris.Add(v + 2); tris.Add(v + 3); tris.Add(v);
        }

        internal void Apply()
        {
            EnsureMesh();
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetColors(vcolors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
        }

        internal void SetVisible(bool visible)
        {
            var r = Renderer;
            if (r != null && r.enabled != visible) r.enabled = visible;
        }

        private void OnDestroy()
        {
            if (mesh != null)
            {
                if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
            }
        }
    }
}
