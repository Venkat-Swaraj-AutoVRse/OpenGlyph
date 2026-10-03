using UnityEngine;
using UnityEngine.UI;

namespace LightSide
{
    // Content measurement: min-/max-content width and height for a width, without side effects.
    public partial class UniText
    {
        /// <summary>
        /// When true, <see cref="minWidth"/> (<see cref="ILayoutElement"/>) reports
        /// <see cref="GetMinContentWidth"/> instead of 0, so a layout group never makes the text narrower
        /// than its widest word. Default false (unchanged layout-group behaviour).
        /// </summary>
        public bool ContentMinWidth
        {
            get => contentMinWidth;
            set
            {
                if (contentMinWidth == value) return;
                contentMinWidth = value;
                LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
            }
        }

        private bool EnsureMeasurable()
        {
            if (sourceText.IsEmpty) return false;
            if (textProcessor == null || !textProcessor.HasValidFirstPassData) RunFirstPassNow();
            return textProcessor != null && textProcessor.HasValidFirstPassData;
        }

        private float InsetsX { get { var m = TextAreaInsets; return Mathf.Max(0f, m.x) + Mathf.Max(0f, m.z); } }
        private float InsetsY { get { var m = TextAreaInsets; return Mathf.Max(0f, m.y) + Mathf.Max(0f, m.w); } }

        /// <summary>
        /// The narrowest width (including <see cref="Padding"/> and margins) this text can have without
        /// breaking inside a word: the widest unbreakable segment. With Auto Size it is measured at
        /// <see cref="MinFontSize"/> (the size the text can shrink to); without word wrap it is the widest
        /// line between hard breaks. Runs the first pass if needed; never changes the RectTransform or the
        /// current layout. 0 for empty text.
        /// </summary>
        public float GetMinContentWidth()
        {
            if (!EnsureMeasurable()) return 0f;
            var fs = autoSize ? minFontSize : fontSize;
            return textProcessor.GetMinContentWidth(fs, wordWrap) + InsetsX;
        }

        /// <summary>
        /// The width (including <see cref="Padding"/> and margins) of the widest line between hard breaks,
        /// i.e. the width at which no soft wrapping happens. With Auto Size it is measured at
        /// <see cref="MaxFontSize"/>. Equals <see cref="preferredWidth"/>. 0 for empty text.
        /// </summary>
        public float GetMaxContentWidth()
        {
            if (!EnsureMeasurable()) return 0f;
            var fs = autoSize ? maxFontSize : fontSize;
            return textProcessor.GetPreferredWidth(fs) + InsetsX;
        }

        /// <summary>
        /// Height (including <see cref="Padding"/> and margins) of the text when the component is
        /// <paramref name="width"/> wide: wrapped at that width with this component's line breaking, line
        /// heights and edge trimming. With Auto Size and word wrap the text is measured at
        /// <see cref="MaxFontSize"/> (no height limit to shrink into); with Auto Size and no wrap at the size
        /// that fits the width. Never changes the RectTransform or the current layout. 0 for empty text.
        /// </summary>
        public float GetHeightForWidth(float width)
        {
            if (!EnsureMeasurable()) return 0f;
            var inner = Mathf.Max(0f, width - InsetsX);
            var fs = fontSize;
            if (autoSize)
                fs = wordWrap ? maxFontSize : textProcessor.GetWidthLimitedFontSize(minFontSize, maxFontSize, inner, autoSizeStep);
            return textProcessor.MeasureHeightForWidth(inner, fs, wordWrap, 0f, overEdge, underEdge, leadingDistribution) + InsetsY;
        }
    }
}
