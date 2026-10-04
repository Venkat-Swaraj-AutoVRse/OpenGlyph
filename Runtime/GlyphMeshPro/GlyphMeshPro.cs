// OpenGlyph GlyphMeshPro — world-space (MeshRenderer) text with the TextMeshPro API.
//
// The TMP `TextMeshPro` counterpart. It is the same TMP-parity adapter as GlyphMeshProUGUI (same
// properties, tags, layout and styling semantics) over the same engine, rendering through the
// MeshFilter/MeshRenderer on its GameObject instead of a CanvasRenderer, exactly like UniTextWorld
// does for UniText. See Documentation/WorldText.md and Documentation/GlyphMeshPro-Parity.md.
//
// Units: the RectTransform rect and the font size are in the object's local units, as in
// GlyphMeshProUGUI. TMP's TextMeshPro scales its glyphs by 0.1 (fontSize 36 in a 20 x 5 rect); the
// GlyphMeshPro equivalent is localScale 0.1 with a 200 x 50 rect (or any scale/rect pair).

using UnityEngine;
using LightSide;

namespace OpenGlyph
{
    /// <summary>
    /// World-space text renderer with a TextMeshPro-parity API (the counterpart of TMP's
    /// <c>TextMeshPro</c>), backed by the OpenGlyph engine and drawn by a <see cref="MeshRenderer"/>
    /// without a Canvas.
    /// </summary>
    /// <remarks>
    /// Inherits every TMP-named property and method of <see cref="GlyphMeshProUGUI"/>; adds the
    /// world render options (<see cref="Options"/>) and the renderer's sorting layer/order, as TMP's
    /// <c>TextMeshPro.sortingLayerID</c>/<c>sortingOrder</c>.
    /// </remarks>
    [AddComponentMenu("")] // created from GameObject > 3D Object > OpenGlyph
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    [DisallowMultipleComponent]
    public class GlyphMeshPro : GlyphMeshProUGUI
    {
        [SerializeField]
        [Tooltip("Lighting, depth write, double-sided and pointer collider options. Components with the same " +
                 "render options share one material.")]
        private WorldTextOptions m_worldOptions = WorldTextOptions.Default;

        /// <inheritdoc/>
        protected override bool RendersToMeshRenderer => true;

        /// <inheritdoc/>
        protected override WorldTextOptions WorldOptions => m_worldOptions;

        /// <summary>World render options (lighting, depth write, double-sided, collider).</summary>
        public WorldTextOptions Options
        {
            get => m_worldOptions;
            set
            {
                var collider = value.collider != m_worldOptions.collider;
                m_worldOptions = value;
                SetDirty(DirtyFlags.Material);
                if (collider) UpdateWorldCollider();
            }
        }

        /// <summary>Mirrors <c>TextMeshPro.renderer</c>: the MeshRenderer that draws the text.</summary>
        public new MeshRenderer renderer => WorldRenderer;

        /// <summary>Mirrors <c>TextMeshPro.sortingLayerID</c>.</summary>
        public int sortingLayerID
        {
            get => WorldRenderer != null ? WorldRenderer.sortingLayerID : 0;
            set { if (WorldRenderer != null) WorldRenderer.sortingLayerID = value; }
        }

        /// <summary>Mirrors <c>TextMeshPro.sortingOrder</c>.</summary>
        public int sortingOrder
        {
            get => WorldRenderer != null ? WorldRenderer.sortingOrder : 0;
            set { if (WorldRenderer != null) WorldRenderer.sortingOrder = value; }
        }

        /// <summary>Mirrors <c>TMP_Text.mesh</c>: the mesh the text is drawn with.</summary>
        public Mesh mesh => WorldMesh;

        protected override void Awake()
        {
            base.Awake();
            // The default highlighter draws UI Graphics, which need a Canvas.
            if (Highlighter is DefaultTextHighlighter) Highlighter = new WorldTextHighlighter();
        }

#if UNITY_EDITOR
        protected override void Reset()
        {
            base.Reset();
            UniTextWorld.ConfigureNewWorldText(this, rectTransform, WorldRenderer);
        }

        private void OnDrawGizmos() => UniTextWorld.DrawRectGizmo(this, new Color(1f, 1f, 1f, 0.25f));

        private void OnDrawGizmosSelected() => UniTextWorld.DrawRectGizmo(this, new Color(1f, 0.85f, 0.2f, 0.9f));
#endif
    }
}
