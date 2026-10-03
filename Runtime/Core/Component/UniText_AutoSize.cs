using UnityEngine;

namespace LightSide
{
    // Auto Size fit steps (UniText.AutoSizeStep).
    public partial class UniText
    {
        /// <summary>
        /// Auto Size fit step in points. 0 (default) = continuous: any size between
        /// <see cref="MinFontSize"/> and <see cref="MaxFontSize"/>. With a step, the fitted size is the largest
        /// multiple of the step that fits (e.g. 1 → 13 pt rather than 13.37 pt), so labels of similar length
        /// get identical sizes. <see cref="MaxFontSize"/> is kept when the text fits at it;
        /// <see cref="MinFontSize"/> is used when no multiple fits.
        /// </summary>
        public float AutoSizeStep
        {
            get => autoSizeStep;
            set
            {
                value = float.IsNaN(value) || value < 0f ? 0f : value;
                if (Mathf.Approximately(autoSizeStep, value)) return;
                autoSizeStep = value;
                if (autoSize) SetDirty(DirtyFlags.Layout);
            }
        }

        private void ValidateAutoSizeStep()
        {
            if (autoSizeStep < 0f || float.IsNaN(autoSizeStep)) autoSizeStep = 0f;
        }
    }
}
