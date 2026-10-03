using UnityEngine;
using UnityEngine.UI;

namespace LightSide
{
    /// <summary>
    /// UniText partial class implementing Unity UI layout interfaces.
    /// </summary>
    /// <remarks>
    /// Provides <see cref="ILayoutElement"/> for preferred size calculations
    /// and <see cref="ILayoutController"/> for auto-sizing behavior.
    /// </remarks>
    public partial class UniText : ILayoutElement, ILayoutController
    {
        private float cachedEffectiveFontSize;
        private float cachedPreferredWidth;
        private float cachedPreferredHeight;
        private float cachedLayoutWidth;
        private float cachedLayoutHeight;
        private bool hasValidLayoutCache;

        #region ILayoutElement

        void ILayoutElement.CalculateLayoutInputHorizontal()
        {
            UniTextDebug.BeginSample("UniText.CalculateLayoutInputHorizontal");

            cachedPreferredWidth = 0;

            if (!sourceText.IsEmpty && textProcessor != null && textProcessor.HasValidFirstPassData)
            {
                var effectiveFontSize = autoSize ? maxFontSize : fontSize;
                cachedPreferredWidth = textProcessor.GetPreferredWidth(effectiveFontSize) + PositiveMarginsX;
            }

            UniTextDebug.EndSample();
        }

        void ILayoutElement.CalculateLayoutInputVertical()
        {
            UniTextDebug.BeginSample("UniText.CalculateLayoutInputVertical");

            if (sourceText.IsEmpty || textProcessor == null || !textProcessor.HasValidFirstPassData)
            {
                hasValidLayoutCache = false;
                cachedPreferredHeight = 0;
                UniTextDebug.EndSample();
                return;
            }

            var rect = GetLayoutRect(rectTransform.rect);
            if (rect.width <= 0)
            {
                hasValidLayoutCache = false;
                cachedPreferredHeight = 0;
                UniTextDebug.EndSample();
                return;
            }

            var height = (autoSize && !wordWrap)
                ? TextProcessSettings.FloatMax
                : (rect.height > 0 ? rect.height : TextProcessSettings.FloatMax);

            if (hasValidLayoutCache &&
                Mathf.Approximately(cachedLayoutWidth, rect.width) &&
                ((autoSize && !wordWrap) || Mathf.Approximately(cachedLayoutHeight, height)))
            {
                UniTextDebug.EndSample();
                return;
            }

            cachedEffectiveFontSize = GetEffectiveFontSize(rect.width, height);
            cachedLayoutWidth = rect.width;
            cachedLayoutHeight = height;
            hasValidLayoutCache = true;

            textProcessor.EnsureLines(rect.width, cachedEffectiveFontSize, wordWrap, horizontalAlignment, UseTmpJustification);

            cachedPreferredHeight = (autoSize && wordWrap)
                ? textProcessor.GetPreferredHeight(maxFontSize, 0f, overEdge, underEdge, leadingDistribution)
                : textProcessor.GetPreferredHeight(cachedEffectiveFontSize, 0f, overEdge, underEdge, leadingDistribution);
            cachedPreferredHeight += PositiveMarginsY;

            UniTextDebug.EndSample();
        }

        // Use the unlimited size directly to remain compatible with older uGUI versions.
        public float maxWidth => float.PositiveInfinity;
        public float maxHeight => float.PositiveInfinity;

        public float minWidth => contentMinWidth ? GetMinContentWidth() : 0;
        public float preferredWidth => cachedPreferredWidth;
        public float flexibleWidth => -1;

        public float minHeight => 0;
        public float preferredHeight => cachedPreferredHeight;
        public float flexibleHeight => -1;

        public int layoutPriority => 0;

        #endregion

        #region ILayoutController

        void ILayoutController.SetLayoutHorizontal() { }

        void ILayoutController.SetLayoutVertical()
        {
            if (!autoSize) return;
            if (textProcessor == null || !textProcessor.HasValidFirstPassData) return;

            var rect = GetLayoutRect(rectTransform.rect);
            if (rect.width <= 0 || rect.height <= 0) return;

            textProcessor.EnsureLines(rect.width, maxFontSize, wordWrap, horizontalAlignment, UseTmpJustification);
            var preferredH = textProcessor.GetPreferredHeight(maxFontSize, 0f, overEdge, underEdge, leadingDistribution);

            if (rect.height < preferredH - 0.01f)
            {
                var settings = new TextProcessSettings
                {
                    MaxWidth = rect.width,
                    MaxHeight = rect.height,
                    OverEdge = overEdge,
                    UnderEdge = underEdge,
                    LeadingDistribution = leadingDistribution,
                    fontSize = maxFontSize,
                    baseDirection = baseDirection,
                    enableWordWrap = wordWrap,
                    TmpJustification = UseTmpJustification
                };

                cachedEffectiveFontSize = textProcessor.FindOptimalFontSize(
                    minFontSize, maxFontSize, rect.width, rect.height, settings, autoSizeStep);
                textProcessor.EnsureLines(rect.width, cachedEffectiveFontSize, wordWrap, horizontalAlignment, UseTmpJustification);
            }
        }

        #endregion

        #region AutoSize

        private float GetEffectiveFontSize(float width, float height)
        {
            if (!autoSize) return fontSize;
            if (wordWrap) return maxFontSize;

            var settings = new TextProcessSettings
            {
                MaxWidth = width,
                MaxHeight = height,
                OverEdge = overEdge,
                UnderEdge = underEdge,
                fontSize = maxFontSize,
                baseDirection = baseDirection,
                enableWordWrap = false,
                TmpJustification = UseTmpJustification
            };

            return textProcessor.FindOptimalFontSize(
                minFontSize, maxFontSize, width, height, settings, autoSizeStep);
        }

        // TMP adds only POSITIVE margins to the preferred (rendered) size. Padding is never negative.
        private float PositiveMarginsX { get { var m = TextAreaInsets; return Mathf.Max(0f, m.x) + Mathf.Max(0f, m.z); } }
        private float PositiveMarginsY { get { var m = TextAreaInsets; return Mathf.Max(0f, m.y) + Mathf.Max(0f, m.w); } }

        private void InvalidateLayoutCache()
        {
            hasValidLayoutCache = false;
        }

        #endregion
    }

}
