using System;
using System.Globalization;

namespace LightSide
{
    /// <summary>
    /// <![CDATA[<pause=0.5>]]>: a zero-width marker that holds a <see cref="UniTextReveal"/> for the given
    /// number of seconds before the unit that follows it. Adds no character to the text.
    /// </summary>
    [Serializable]
    [TypeGroup("Animation", 0)]
    public sealed class PauseParseRule : TagParseRule
    {
        protected override string TagName => "pause";
        protected override bool HasParameter => true;
        protected override bool IsSelfClosing => true;
        protected override string InsertString => string.Empty;
    }

    /// <summary>
    /// Stores <c>&lt;pause=seconds&gt;</c> markers per codepoint (key <see cref="AttributeKeys.RevealPause"/>);
    /// read by <see cref="UniTextReveal"/>. Index <c>codepointCount</c> holds a pause at the very end.
    /// </summary>
    [Serializable]
    [TypeGroup("Animation", 0)]
    public sealed class RevealPauseModifier : BaseModifier
    {
        private PooledArrayAttribute<float> attribute;

        protected override void OnEnable()
        {
            attribute = buffers.GetOrCreateAttributeData<PooledArrayAttribute<float>>(AttributeKeys.RevealPause);
            attribute.EnsureCountAndClear(buffers.codepoints.count + 1);
        }

        protected override void OnDisable()
        {
            // The reveal reads this buffer without knowing whether the modifier ran for the current
            // text: leave no stale pauses behind when the text changes.
            attribute?.buffer.ClearData();
        }

        protected override void OnDestroy()
        {
            buffers?.ReleaseAttributeData(AttributeKeys.RevealPause);
            attribute = null;
        }

        protected override void OnApply(int start, int end, string parameter)
        {
            if (attribute == null || string.IsNullOrEmpty(parameter)) return;
            if (!float.TryParse(parameter.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return;
            if (seconds <= 0f) return;
            var data = attribute.buffer.data;
            if (data == null || (uint)start >= (uint)data.Length) return;
            data[start] += seconds;
        }
    }
}
