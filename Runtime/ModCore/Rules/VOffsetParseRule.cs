using System;

namespace LightSide
{
    /// <summary>Parses the <![CDATA[<voffset=…>text</voffset>]]> tag (TextMeshPro parity), paired
    /// with <see cref="VOffsetModifier"/>. The value is in em (default), px, or %.</summary>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class VOffsetParseRule : TagParseRule
    {
        protected override string TagName => "voffset";
        protected override bool HasParameter => true;
    }
}
