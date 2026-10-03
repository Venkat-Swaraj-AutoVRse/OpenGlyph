using System;

namespace LightSide
{
    /// <summary>Parses the <![CDATA[<sub>text</sub>]]> subscript tag (TextMeshPro parity),
    /// paired with <see cref="SuperSubscriptModifier"/> (Subscript kind).</summary>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class SubscriptParseRule : TagParseRule
    {
        protected override string TagName => "sub";
        protected override bool HasParameter => false;
    }
}
