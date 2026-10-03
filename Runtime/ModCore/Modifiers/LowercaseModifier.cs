using System;

namespace LightSide
{
    /// <summary>
    /// Transforms text to lowercase within marked ranges. Mirror of <see cref="UppercaseModifier"/>.
    /// </summary>
    /// <remarks>
    /// Usage: <c>&lt;lowercase&gt;text&lt;/lowercase&gt;</c>.
    /// The transformation happens during Apply, after parsing but before shaping, so the shaper and
    /// renderer see the already-cased codepoints.
    /// </remarks>
    /// <seealso cref="LowercaseParseRule"/>
    [Serializable]
    [TypeGroup("Text Style", 0)]
    public class LowercaseModifier : BaseModifier
    {
        protected override void OnEnable() { }
        protected override void OnDisable() { }
        protected override void OnDestroy() { }

        protected override void OnApply(int start, int end, string parameter)
        {
            var codepoints = buffers.codepoints.data;
            var cpCount = buffers.codepoints.count;
            var clampedEnd = Math.Min(end, cpCount);

            for (var i = start; i < clampedEnd; i++)
                codepoints[i] = ToLowerCodepoint(codepoints[i]);
        }

        private static int ToLowerCodepoint(int codepoint)
        {
            if (codepoint <= UnicodeData.MaxBmp)
                return char.ToLowerInvariant((char)codepoint);
            return codepoint;
        }
    }
}
