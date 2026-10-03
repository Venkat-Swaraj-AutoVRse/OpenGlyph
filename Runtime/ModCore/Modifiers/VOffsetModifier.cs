using System;
using System.Globalization;

namespace LightSide
{
    /// <summary>
    /// Shifts a span vertically by a fixed offset without changing its size or advance. Mirrors
    /// TextMeshPro's <c>&lt;voffset=…&gt;</c> (offset in font-size "em" units; also accepts a plain
    /// pixel number or a percentage of the font size).
    /// </summary>
    /// <remarks>
    /// Positive values raise the span, negative lower it — matching TMP. The offset is applied to the
    /// glyph quad vertices in <see cref="OnGlyph"/>; advance is untouched, so following glyphs keep
    /// their x positions (TMP behaves the same).
    /// </remarks>
    /// <seealso cref="VOffsetParseRule"/>
    [Serializable]
    [TypeGroup("Text Style", 0)]
    public class VOffsetModifier : GlyphModifier<float>
    {
        protected override string AttributeKey => "voffset";

        protected override void DoApply(int start, int end, string parameter)
        {
            float offsetEm = ParseOffset(parameter);
            var cpCount = buffers.codepoints.count;
            var clampedEnd = Math.Min(end, cpCount);
            for (var i = start; i < clampedEnd; i++)
                attribute.buffer[i] = offsetEm;
        }

        /// <summary>Parses the voffset value. "1.5em" / "1.5" => ems; "50%" => fraction of em;
        /// a trailing "px" => pixels converted to ems by the current font size.</summary>
        private float ParseOffset(string param)
        {
            if (string.IsNullOrEmpty(param)) return 0f;
            param = param.Trim();
            if (param.EndsWith("em", StringComparison.OrdinalIgnoreCase))
                return float.TryParse(param.AsSpan(0, param.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var em) ? em : 0f;
            if (param.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                var fs = buffers.shapingFontSize > 0 ? buffers.shapingFontSize : uniText.FontSize;
                return float.TryParse(param.AsSpan(0, param.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var px) && fs > 0 ? px / fs : 0f;
            }
            if (param.EndsWith("%", StringComparison.Ordinal))
                return float.TryParse(param.AsSpan(0, param.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) ? pct / 100f : 0f;
            return float.TryParse(param, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
        }

        protected override Action GetOnGlyphCallback() => OnGlyph;

        private void OnGlyph()
        {
            var gen = UniTextMeshGenerator.Current;
            var cluster = gen.currentCluster;
            if ((uint)cluster >= (uint)attribute.buffer.Capacity) return;
            float offsetEm = attribute.buffer[cluster];
            if (offsetEm == 0f) return;

            // em -> screen units via the font size the generator is drawing at.
            float fs = gen.FontSize;
            float dy = offsetEm * fs;

            var baseIdx = gen.vertexCount - 4;
            var verts = gen.Vertices;
            for (var i = 0; i < 4; i++)
                verts[baseIdx + i].y += dy;
        }
    }
}
