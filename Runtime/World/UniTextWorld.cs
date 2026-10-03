using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// World-space text without a Canvas: the full UniText engine (markup, shaping, BiDi, fallback,
    /// emoji, overflow, auto-size, modifiers, animations) drawn by a <see cref="MeshRenderer"/>.
    /// </summary>
    /// <remarks>
    /// <para>The text area is the <see cref="RectTransform"/> rect, in the object's local units (the same
    /// units as <see cref="UniText.FontSize"/>). Scale the transform to size it in the world: at
    /// <c>localScale = 0.01</c> a 200 x 50 rect is a 2 m x 0.5 m label and font size 36 is 36 cm per em.
    /// <see cref="WorldSize"/> reads and writes the size in world units.</para>
    /// <para>With the unified renderer (the default) every label with the same <see cref="Lighting"/>,
    /// <see cref="DepthWrite"/> and <see cref="DoubleSided"/> shares one material per atlas format (one
    /// draw per label, SRP-Batcher compatible). Many labels are much cheaper than one Canvas each.</para>
    /// <para>Pointer events (links, <see cref="UniText.TextClicked"/>) come from the EventSystem through a
    /// <c>PhysicsRaycaster</c> (or XRI's <c>TrackedDevicePhysicsRaycaster</c>) hitting the BoxCollider this
    /// component keeps sized to its rect (<see cref="Collider"/>), or from <see cref="UniText.HitTestRay"/>.</para>
    /// </remarks>
    [AddComponentMenu("OpenGlyph/UniText World")]
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public class UniTextWorld : UniText
    {
        [SerializeField]
        [Tooltip("Lighting, depth write, double-sided and pointer collider options. Components with the same " +
                 "render options share one material.")]
        private WorldTextOptions worldOptions = WorldTextOptions.Default;

        /// <inheritdoc/>
        protected override bool RendersToMeshRenderer => true;

        /// <inheritdoc/>
        protected override WorldTextOptions WorldOptions => worldOptions;

        /// <summary>All world render options at once.</summary>
        public WorldTextOptions Options
        {
            get => worldOptions;
            set
            {
                var collider = value.collider != worldOptions.collider;
                worldOptions = value;
                SetDirty(DirtyFlags.Material);
                if (collider) UpdateWorldCollider();
            }
        }

        /// <summary>Unlit (default) or lit by the main directional light + ambient.</summary>
        public WorldTextLighting Lighting
        {
            get => worldOptions.lighting;
            set { if (worldOptions.lighting == value) return; worldOptions.lighting = value; SetDirty(DirtyFlags.Material); }
        }

        /// <summary>Write depth (alpha-clipped at 0.5 coverage, AlphaTest queue). Default off (transparent).</summary>
        public bool DepthWrite
        {
            get => worldOptions.depthWrite;
            set { if (worldOptions.depthWrite == value) return; worldOptions.depthWrite = value; SetDirty(DirtyFlags.Material); }
        }

        /// <summary>Draw both faces. Default off (front face only).</summary>
        public bool DoubleSided
        {
            get => worldOptions.doubleSided;
            set { if (worldOptions.doubleSided == value) return; worldOptions.doubleSided = value; SetDirty(DirtyFlags.Material); }
        }

        /// <summary>When to keep a BoxCollider sized to the rect for EventSystem pointer events.</summary>
        public WorldTextCollider Collider
        {
            get => worldOptions.collider;
            set { if (worldOptions.collider == value) return; worldOptions.collider = value; UpdateWorldCollider(); }
        }

        /// <summary>Sorting layer of the MeshRenderer (for transparent ordering against sprites/other text).</summary>
        public int SortingLayerID
        {
            get => WorldRenderer != null ? WorldRenderer.sortingLayerID : 0;
            set { if (WorldRenderer != null) WorldRenderer.sortingLayerID = value; }
        }

        /// <summary>Sorting order of the MeshRenderer within its sorting layer.</summary>
        public int SortingOrder
        {
            get => WorldRenderer != null ? WorldRenderer.sortingOrder : 0;
            set { if (WorldRenderer != null) WorldRenderer.sortingOrder = value; }
        }

        /// <summary>
        /// The rect size in world units (rect size x the transform's lossy scale). Setting it resizes the
        /// rect (sizeDelta), keeping the scale.
        /// </summary>
        public Vector2 WorldSize
        {
            get
            {
                var s = transform.lossyScale;
                var r = rectTransform.rect;
                return new Vector2(r.width * Mathf.Abs(s.x), r.height * Mathf.Abs(s.y));
            }
            set
            {
                var s = transform.lossyScale;
                var sx = Mathf.Abs(s.x) > 1e-8f ? Mathf.Abs(s.x) : 1f;
                var sy = Mathf.Abs(s.y) > 1e-8f ? Mathf.Abs(s.y) : 1f;
                rectTransform.sizeDelta = new Vector2(value.x / sx, value.y / sy);
            }
        }

        protected override void Awake()
        {
            base.Awake();
            // The default highlighter draws UI Graphics, which need a Canvas: world text uses the range events.
            if (Highlighter is DefaultTextHighlighter) Highlighter = null;
        }

#if UNITY_EDITOR
        protected override void Reset()
        {
            base.Reset();
            ConfigureNewWorldText(this, rectTransform, WorldRenderer);
        }
#endif

        /// <summary>Defaults for a new world text object: 200 x 50 rect, no shadows, no hover highlight graphics.</summary>
        internal static void ConfigureNewWorldText(UniText t, RectTransform rt, MeshRenderer mr)
        {
            if (rt != null && rt.sizeDelta == new Vector2(100, 100)) rt.sizeDelta = new Vector2(200, 50);
            if (mr != null)
            {
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }
            // The default highlighter draws UI Graphics, which need a Canvas.
            t.Highlighter = null;
        }

#if UNITY_EDITOR
        private static readonly Vector3[] s_gizmoCorners = new Vector3[4];

        private void OnDrawGizmos()
        {
            DrawRectGizmo(this, new Color(1f, 1f, 1f, 0.25f));
        }

        private void OnDrawGizmosSelected()
        {
            DrawRectGizmo(this, new Color(1f, 0.85f, 0.2f, 0.9f));
        }

        /// <summary>Draws the text rect (and the padded/margined text area) in the scene view.</summary>
        internal static void DrawRectGizmo(UniText t, Color color)
        {
            var rt = t.rectTransform;
            if (rt == null) return;
            rt.GetWorldCorners(s_gizmoCorners);
            Gizmos.color = color;
            for (var i = 0; i < 4; i++) Gizmos.DrawLine(s_gizmoCorners[i], s_gizmoCorners[(i + 1) % 4]);
        }
#endif
    }
}
