using System;

namespace LightSide
{
    /// <summary>Parses the <![CDATA[<sup>text</sup>]]> superscript tag (TextMeshPro parity),
    /// paired with <see cref="SuperSubscriptModifier"/> (Superscript kind).</summary>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class SuperscriptParseRule : TagParseRule
    {
        protected override string TagName => "sup";
        protected override bool HasParameter => false;
    }
}
