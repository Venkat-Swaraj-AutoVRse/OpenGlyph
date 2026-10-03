using System;
using System.Globalization;

namespace LightSide
{
    /// <summary>
    /// Culture-invariant numeric parsing for markup-modifier parameters (<c>&lt;size=1.5em&gt;</c>,
    /// <c>&lt;cspace=0.5em&gt;</c>, <c>&lt;line-height=1.5&gt;</c>, ...). Markup is author-facing text
    /// with a '.' decimal separator regardless of the user's OS locale, so these MUST parse with
    /// <see cref="CultureInfo.InvariantCulture"/>. The default <c>float.TryParse</c> uses the ambient
    /// culture, under which (e.g. de-DE) "1.5" is read with '.' as a GROUP separator and yields 15 —
    /// silently mis-sizing text. Centralised here so every modifier parses identically and the
    /// behaviour is unit-testable without the render pipeline.
    /// </summary>
    internal static class ModifierNumberParse
    {
        private const NumberStyles Style = NumberStyles.Float;

        /// <summary>Culture-invariant <c>float.TryParse</c> for a markup numeric token.</summary>
        public static bool TryParseFloat(string s, out float value) =>
            float.TryParse(s, Style, CultureInfo.InvariantCulture, out value);

        /// <summary>Span overload — same invariant semantics, no allocation.</summary>
        public static bool TryParseFloat(ReadOnlySpan<char> s, out float value) =>
            float.TryParse(s, Style, CultureInfo.InvariantCulture, out value);
    }
}
