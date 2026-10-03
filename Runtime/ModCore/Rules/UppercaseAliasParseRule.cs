using System;

namespace LightSide
{
    /// <summary>
    /// Parses the TMP-style <![CDATA[<uppercase>text</uppercase>]]> tag — a NAME ALIAS of the
    /// engine's <c>&lt;upper&gt;</c> rule (<see cref="UppercaseParseRule"/>), paired with the same
    /// <see cref="UppercaseModifier"/>. Added for TextMeshPro tag parity so migrated markup and
    /// GlyphMeshPro's <c>fontStyle</c> UpperCase wrapping resolve to the uppercase transform.
    /// </summary>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class UppercaseAliasParseRule : TagParseRule
    {
        protected override string TagName => "uppercase";
        protected override bool HasParameter => false;
    }
}
