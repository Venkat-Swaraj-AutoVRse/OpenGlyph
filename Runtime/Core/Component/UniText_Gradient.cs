using System.Collections.Generic;
using UnityEngine;

namespace LightSide
{
    /// <summary>Wave 2: whole-text gradient fill (linear, radial, angular).</summary>
    public partial class UniText
    {
        [SerializeField]
        [Tooltip("Gradient over the whole text (Linear, Radial or Angular). <gradient> and <color> spans override it on their range. Vertex colours: works with both renderers.")]
        private UniTextGradientFill gradientFill = UniTextGradientFill.None;

        /// <summary>
        /// Gradient over the whole text. Span <c>&lt;gradient&gt;</c> / <c>&lt;color&gt;</c> override it on their
        /// range. Set <see cref="UniTextGradientFill.mode"/> to <see cref="GradientFillMode.None"/> to turn it off.
        /// Reassign the struct after editing its <see cref="Gradient"/> stops so the text recolours.
        /// </summary>
        public UniTextGradientFill GradientFill
        {
            get => gradientFill;
            set
            {
                gradientFill = value;
                SetDirty(DirtyFlags.Color);
            }
        }

        private bool fillHooked;
        private UniTextMeshGenerator fillHookedGenerator;
        private System.Action fillOnGlyph;
        private System.Action fillOnRebuildStart;
        private GradientFrame fillFrame;
        private bool fillHasFrame;
        private readonly List<Rect> fillBounds = new();

        // MAIN THREAD before generation: subscribe the base-fill hook only while a fill is set.
        private void SyncGradientFillHook()
        {
            fillOnGlyph ??= OnFillGlyph;
            fillOnRebuildStart ??= OnFillRebuildStart;
            var want = gradientFill.mode != GradientFillMode.None && gradientFill.gradient != null && meshGenerator != null;
            if (fillHooked && (!want || fillHookedGenerator != meshGenerator))
            {
                if (fillHookedGenerator != null)
                {
                    fillHookedGenerator.OnGlyphBase -= fillOnGlyph;
                    fillHookedGenerator.OnRebuildStart -= fillOnRebuildStart;
                }
                fillHooked = false;
                fillHookedGenerator = null;
            }
            if (want && !fillHooked)
            {
                meshGenerator.OnGlyphBase += fillOnGlyph;
                meshGenerator.OnRebuildStart += fillOnRebuildStart;
                fillHooked = true;
                fillHookedGenerator = meshGenerator;
            }
        }

        private void OnFillRebuildStart()
        {
            GetRangeBounds(0, int.MaxValue, fillBounds);
            fillHasFrame = fillBounds.Count > 0;
            if (!fillHasFrame) return;
            var b = fillBounds[0];
            for (var i = 1; i < fillBounds.Count; i++)
            {
                var r = fillBounds[i];
                b = Rect.MinMaxRect(Mathf.Min(b.xMin, r.xMin), Mathf.Min(b.yMin, r.yMin), Mathf.Max(b.xMax, r.xMax), Mathf.Max(b.yMax, r.yMax));
            }
            var shape = gradientFill.mode == GradientFillMode.Radial ? GradientShape.Radial
                : gradientFill.mode == GradientFillMode.Angular ? GradientShape.Angular : GradientShape.Linear;
            fillFrame = GradientFrame.Create(shape, b, gradientFill.angle, gradientFill.center, gradientFill.radius);
        }

        private void OnFillGlyph()
        {
            if (!fillHasFrame) return;
            var gen = UniTextMeshGenerator.Current;
            if (gen == null || gen.font == null || gen.font.IsColor) return;
            fillFrame.ColorQuad(gradientFill.gradient, gen.Vertices, gen.Colors, gen.currentGlyphVertexStart, gen.defaultColor.a);
        }
    }
}
