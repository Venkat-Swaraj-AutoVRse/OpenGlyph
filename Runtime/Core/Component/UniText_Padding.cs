using UnityEngine;

namespace LightSide
{
    // Box-model padding (UniText.Padding): insets the text area like GlyphMeshProUGUI's TMP margin.
    public partial class UniText
    {
        /// <summary>
        /// Inner padding (x = left, y = top, z = right, w = bottom) between the RectTransform edge and the
        /// text. Insets layout and wrapping, the <see cref="TextOverflow.Clip"/> rect and link hit testing,
        /// and is added to <see cref="preferredWidth"/>/<see cref="preferredHeight"/> and the content
        /// measurements. Negative components are clamped to 0. On <c>GlyphMeshProUGUI</c> it adds to the
        /// TMP <c>margin</c>.
        /// </summary>
        public Vector4 Padding
        {
            get => padding;
            set
            {
                value = Vector4.Max(value, Vector4.zero);
                if (padding == value) return;
                padding = value;
                SetDirty(DirtyFlags.Layout);
                RefreshOverflowClip();
            }
        }

        partial void OnValidateLayoutInputs()
        {
            padding = Vector4.Max(padding, Vector4.zero);
            ValidateAutoSizeStep();
        }
    }
}
