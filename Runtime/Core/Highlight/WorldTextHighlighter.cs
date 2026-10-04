using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Link / interactive-range feedback for world-space text (<see cref="UniTextWorld"/>, world
    /// <c>GlyphMeshPro</c>): a soft box behind a hovered range and a flash that fades out on click,
    /// like <see cref="DefaultTextHighlighter"/> on a Canvas. Drawn by one MeshRenderer child (shared unlit
    /// vertex-colour material, sorted just below the text) so it needs no Canvas. The default for world text.
    /// </summary>
    [Serializable]
    public class WorldTextHighlighter : TextHighlighter
    {
        [SerializeField, Tooltip("Colour of the hover box.")]
        private Color hoverColor = new(0.22f, 0.74f, 0.97f, 0.22f);

        [SerializeField, Tooltip("Colour of the click flash.")]
        private Color clickColor = new(0.22f, 0.74f, 0.97f, 0.55f);

        [SerializeField, Tooltip("Seconds for the click flash to fade out.")]
        private float fadeDuration = 0.3f;

        [SerializeField, Tooltip("Padding around each range rectangle, as a fraction of its height.")]
        private float padding = 0.12f;

        private UniTextInputWorldOverlay overlay;
        private readonly List<Rect> hoverRects = new(4);
        private readonly List<Rect> clickRects = new(4);
        private float clickAlpha;

        public Color HoverColor { get => hoverColor; set { hoverColor = value; Redraw(); } }
        public Color ClickColor { get => clickColor; set { clickColor = value; Redraw(); } }
        public float FadeDuration { get => fadeDuration; set => fadeDuration = Mathf.Max(0.01f, value); }
        public float Padding { get => padding; set { padding = Mathf.Max(0f, value); Redraw(); } }

        /// <summary>The rectangles currently drawn (local space), for tests and custom effects.</summary>
        public IReadOnlyList<Rect> DrawnRects => overlay != null ? overlay.Rects : Array.Empty<Rect>();

        /// <summary>True while a range is hovered.</summary>
        public bool IsHovering => hoverRects.Count > 0;

        public override void Initialize(UniText owner)
        {
            base.Initialize(owner);
        }

        private UniTextInputWorldOverlay EnsureOverlay()
        {
            if (overlay != null || owner == null) return overlay;
            var go = new GameObject("Link Highlight", typeof(MeshFilter), typeof(MeshRenderer))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            go.transform.SetParent(owner.transform, false);
            overlay = go.AddComponent<UniTextInputWorldOverlay>();
            return overlay;
        }

        public override void OnRangeEntered(InteractiveRange range, List<Rect> bounds)
        {
            hoverRects.Clear();
            if (bounds != null) hoverRects.AddRange(bounds);
            Redraw();
        }

        public override void OnRangeExited(InteractiveRange range)
        {
            hoverRects.Clear();
            Redraw();
        }

        public override void OnRangeClicked(InteractiveRange range, List<Rect> bounds)
        {
            if (bounds == null || bounds.Count == 0) return;
            clickRects.Clear();
            clickRects.AddRange(bounds);
            clickAlpha = 1f;
            Redraw();
        }

        public override void Update()
        {
            if (clickAlpha <= 0f) return;
            clickAlpha -= Time.unscaledDeltaTime / Mathf.Max(0.01f, fadeDuration);
            if (clickAlpha <= 0f)
            {
                clickAlpha = 0f;
                clickRects.Clear();
            }
            Redraw();
        }

        private void Redraw()
        {
            if (owner == null) return;
            if (hoverRects.Count == 0 && clickRects.Count == 0)
            {
                if (overlay != null)
                {
                    overlay.BeginRects();
                    overlay.Apply();
                    overlay.SetVisible(false);
                }
                return;
            }
            var o = EnsureOverlay();
            if (o == null) return;
            o.BeginRects();
            for (var i = 0; i < hoverRects.Count; i++) o.Add(Pad(hoverRects[i]), ToMesh(hoverColor), false, default);
            if (clickRects.Count > 0)
            {
                var c = clickColor;
                c.a *= clickAlpha;
                for (var i = 0; i < clickRects.Count; i++) o.Add(Pad(clickRects[i]), ToMesh(c), false, default);
            }
            o.Apply();
            o.SetVisible(true);
            var tr = owner.WorldRenderer;
            var r = o.Renderer;
            if (tr != null && r != null)
            {
                r.sortingLayerID = tr.sortingLayerID;
                r.sortingOrder = tr.sortingOrder - 1; // below the glyphs, never over them
            }
        }

        private Rect Pad(Rect r)
        {
            var p = r.height * padding;
            return new Rect(r.xMin - p, r.yMin - p * 0.5f, r.width + 2f * p, r.height + p);
        }

        /// <summary>A MeshRenderer does not convert vertex colours in a linear-space project (a Canvas does).</summary>
        private static Color ToMesh(Color c)
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Linear) return c;
            var l = c.linear;
            l.a = c.a;
            return l;
        }

        public override void Destroy()
        {
            if (overlay != null) ObjectUtils.SafeDestroy(overlay.gameObject);
            overlay = null;
            hoverRects.Clear();
            clickRects.Clear();
            base.Destroy();
        }
    }
}
