using System;

namespace LightSide
{
    /// <summary>
    /// Parses the <![CDATA[<lowercase>text</lowercase>]]> tag, paired with
    /// <see cref="LowercaseModifier"/>. TextMeshPro tag parity.
    /// </summary>
    /// <seealso cref="LowercaseModifier"/>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class LowercaseParseRule : TagParseRule
    {
        protected override string TagName => "lowercase";
        protected override bool HasParameter => false;
    }
}
